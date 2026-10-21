# Glimpse 开发文档

本文档是编码阶段的实施依据。目录职责与依赖禁令见 [PROJECT_STRUCTURE.md](PROJECT_STRUCTURE.md)，两者不重复：那份讲"代码放哪"，这份讲"怎么实现"。

## 1. 范围

**要做的**

- 同机双屏：把副屏画面以置顶画中画显示在主屏，或反向
- 仅监看：预览窗只显示画面，不转发鼠标键盘
- 舞台可用：低延迟、可点击穿透、不干扰被控软件的前台焦点
- 预览窗自身不得出现在任何屏幕捕获结果中

**当前不做**

- 反向控制（在预览窗上操作被控屏）
- 录制、推流、多预览窗
- 跨设备传输（架构预留 `IFrameSource` 扩展点，但本期不实现）

## 2. 技术路线

| 环节 | 方案 | 选它的原因 |
|---|---|---|
| 采集 | Windows.Graphics.Capture（WGC） | 官方支持按显示器捕获、可关隐私边框、可带光标；DXGI Desktop Duplication 在混合 DPI 和 HDR 下更麻烦且已非推荐方案 |
| GPU | D3D11 | WGC 的 `frame.Surface` 可直接转为 `ID3D11Texture2D`，是零拷贝的前提 |
| 呈现 | DXGI flip 交换链 + DirectComposition | 延迟最低；配合 `WS_EX_NOREDIRECTIONBITMAP` 绕开 DWM 重定向 |
| 外壳 | WinForms | 穿透、置顶、托盘都已验证；不需要 XAML 运行时 |
| 时间基准 | QueryPerformanceCounter | 与 WGC 帧时间戳同源，可计算帧年龄 |

**明确不走的路**：CPU 读回 + GDI 绘制。基线程序已证明该路径在 1080×1920 源、562×1000 预览窗下只有 24–32 fps，端到端 p95 39–57 ms，且一个逻辑核满载。正式实现必须让画面全程停留在显存。

## 3. 管线设计

```
被捕获屏
   │  DWM 合成结果
   ▼
WGC FramePool ──frame.Surface──► ID3D11Texture2D（系统持有）
                                      │ CopyResource
                                      ▼
                            自有纹理槽 A / B（双缓冲）
                                      │ 无锁交换索引
                                      ▼
                              渲染线程取最新槽
                                      │
                                      ▼
                        DXGI flip 交换链 ──► DirectComposition visual
                                                        │
                                                        ▼
                                                  预览窗（置顶）
```

关键点：整条路径没有任何一次 `Map`、`LockBits` 或字节数组拷贝。像素只在 GPU 内部流动。

## 4. 线程模型

| 线程 | 职责 | 禁止 |
|---|---|---|
| WGC 回调线程（线程池） | 取出 frame、`CopyResource` 到空闲槽、更新时间戳、交换槽索引 | 任何阻塞调用、任何 UI 操作 |
| 渲染线程（独立） | 轮询最新槽、`Present`、统计刷新 | 访问 WinForms 控件 |
| UI 线程 | 窗口消息、拖动、热键、设置界面 | 逐帧绘制 |

**为什么不用 WinForms Timer**：`System.Windows.Forms.Timer` 精度受消息循环影响，实测在 16ms 周期下仍只能跑出 28 fps。渲染节拍必须由独立线程掌握：

```
while (running)
{
    if (帧源有新帧) host.Present(frame);
    else Thread.Sleep(1);   // 静止画面不空转
}
```

**双缓冲是必须的，不是优化**。WGC 的 frame 一旦 Dispose，其 Surface 内容不再可靠。如果捕获线程直接覆盖渲染线程正在读的纹理，会出现撕裂或花屏。两个槽位加 `Interlocked.Exchange` 交换索引即可，不需要锁。

## 5. 模块设计

### Interop

| 文件 | 职责 |
|---|---|
| `MonitorInterop.cs` | `EnumDisplayMonitors` / `GetMonitorInfoW`，产出 `MonitorDescriptor` |
| `WgcInterop.cs` | `IGraphicsCaptureItemInterop` 的 `CreateForMonitor`，走 `RoGetActivationFactory` |
| `D3D11Interop.cs` | `CreateDirect3D11DeviceFromDXGIDevice`、设备创建与特性级别降级 |
| `GpuDevice.cs` | 采集与呈现共享的设备/立即上下文与上下文锁 |
| `CompositionInterop.cs` | `DCompositionCreateDevice`、DirectComposition COM 接口定义 |
| `WindowInterop.cs` | 扩展样式、`SetWindowDisplayAffinity`、`RegisterHotKey` |
| `PerformanceCounterInterop.cs` | QPC 计时，供帧年龄统计使用 |
| `ConsoleInterop.cs` | `AttachConsole`，让 WinExe 从终端启动时能输出诊断 |

### Capture

`WgcFrameSource : IFrameSource`

- 构造时接收 `MonitorDescriptor` 与共享的 `ID3D11Device`
- `FramePool` 用 `CreateFreeThreaded`，避免回调被 UI 线程的消息泵拖住
- 帧池 `NumberOfBuffers` 取 2，更多只会增加延迟
- `FrameSize` 变化时（改分辨率、切换缩放）重建帧池与会话
- `Stop()` 只停会话，`Dispose()` 才释放帧池与纹理

### Render

`D3DPreviewHost : IPreviewHost`

- 持有交换链、两个渲染纹理、DirectComposition target/visual
- 只做 `CopyResource` 或 `Draw` 到交换链后备缓冲，然后 `Present`
- 等比缩放交给 GPU：按源画面比例计算 viewport，剩余区域填黑
- 窗口尺寸变化时 `ResizeBuffers`，不要重建整个交换链

### Ui

`PreviewWindow : Form`

- 无边框、置顶、不显示在任务栏、不进 Alt+Tab（`WS_EX_TOOLWINDOW`）
- 承载 `D3DPreviewHost` 的 HWND 子类化，或直接把整个窗口交给交换链
- 鼠标拖动、边缘缩放手柄；`WM_SIZING` 中按源画面宽高比约束窗口，避免出现黑边
- 延迟叠加层不要画在交换链上，用独立的半透明子窗口，避免污染渲染路径

### Platform

- `TrayIconHost`：托盘菜单、退出、设置入口
- `GlobalHotKey`：`RegisterHotKey`，默认 `Ctrl+Alt+P` 切换穿透
- `WindowCaptureExclusion`：`SetWindowDisplayAffinity(hwnd, 0x11)`

### Diagnostics

- `FrameClock`：QPC 秒数，同时作为帧时间戳基准
- `LatencyMonitor`：滑动窗口算 avg / p95 / max，输出到控制台或日志
- `FrameStats`：捕获帧数、呈现帧数、丢弃帧数

### Configuration

- `SettingsStore`：`%LOCALAPPDATA%\Glimpse\settings.json`，`System.Text.Json`
- 写入用临时文件 + 原子替换，避免崩溃时留下半截配置
- 读不到或损坏时回退默认值，不抛异常

## 6. 关键实现要点

**6.1 纹理零拷贝**
`frame.Surface` 用 `QueryInterface<ID3D11Texture2D>()` 取出原生纹理，再 `CopyResource` 到自有纹理。不要调用 `SoftwareBitmap.CreateCopyFromSurfaceAsync`。

**6.2 交换链参数**
`CreateSwapChainForComposition` + `DXGI_SWAP_EFFECT_FLIP_DISCARD` + `BufferCount = 2` + `DXGI_SCALING_STRETCH`。帧延迟在设备级设置：`IDXGIDevice1::SetMaximumFrameLatency(1)`。合成交换链不支持 `IDXGISwapChain2::SetMaximumFrameLatency`，调用会返回 `DXGI_ERROR_INVALID_CALL`。`Present` 用 `PresentFlags.DoNotWait`，返回 `DXGI_ERROR_WAS_STILL_DRAWING` 时直接丢弃该帧。

**6.3 窗口样式组合**
```
WS_POPUP
WS_EX_NOREDIRECTIONBITMAP   // 必须，配合 DirectComposition
WS_EX_TOOLWINDOW            // 不进 Alt+Tab
WS_EX_NOACTIVATE            // 被点击也不抢焦点
WS_EX_TRANSPARENT           // 点击穿透（运行时切换）
```
注意 `WS_EX_NOREDIRECTIONBITMAP` 与 `WS_EX_LAYERED` 互斥，不要同时设。

这些样式必须作用在顶层窗口上：重写 `CreateParams` 或直接 `SetWindowLongPtr`，`Form.SetStyle` 只到控件层，管不到它们。`WS_EX_TRANSPARENT` 运行时切换后还要 `SetWindowPos` 带 `SWP_FRAMECHANGED` 才会生效。

带 `WS_EX_NOREDIRECTIONBITMAP` 的窗口没有 DWM 重定向表面，Form 的 GDI 绘制不会显示。这类窗口上不能放任何 WinForms 控件，`OnPaint` 也不得产生可见输出，否则表现为黑块、白块或闪烁。全部可见像素只能来自交换链。

**6.4 预览窗必须隐身**
`SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)`，值 `0x11`，要求 Windows 10 2004 以上。不做这一步，预览窗会被 WGC 拍进画面，观众在大屏上直接看到画中画，反向预览时还会形成递归。

**6.5 特性级别降级**
先请求 `Level_11_1`，`E_INVALIDARG` 时去掉重试 `Level_11_0`，再退 `Level_10_1`。桌面窗口合成场景 11_0 完全够用，不必强求 11_1。

**6.6 设备丢失**
任何 `Present` 或 `CopyResource` 返回 `DXGI_ERROR_DEVICE_REMOVED` / `DEVICE_RESET`，都必须释放整条 D3D 栈并重建，不能重试。重建后重新创建帧池与会话。

**6.7 显示器热插拔**
处理 `WM_DISPLAYCHANGE`：重新枚举显示器，若目标 HMONITOR 失效则停止捕获、重建捕获项。HMONITOR 不是稳定标识，不要长期缓存。

**6.8 DPI**
进程已声明 PerMonitorV2。计算预览窗位置时用物理像素，不要混用 `Screen.Bounds`（DIP）与 Win32 坐标（物理像素）。跨不同缩放的屏拖动时，按目标屏的 DPI 重新换算尺寸。

**6.9 HDR**
WGC 对 HDR 源可能给出 `R16G16B16A16_FLOAT`。交换链格式需与之匹配，否则要显式做色调映射。验证阶段允许强制 SDR，但代码里不要写死 `B8G8R8A8`。

**6.10 多 GPU**
采集与呈现共用一个 D3D11 设备时，纹理不会跨设备，`CopyResource` 不需要共享纹理；但 DirectComposition 的内容必须能被预览输出合成。混合显卡机器上按显示器选择适配器可能落到无法合成的核显，实测表现为预览窗全黑；应优先使用 `IDXGIFactory6.EnumAdapterByGpuPreference(HighPerformance)` 返回的适配器，其次预览屏适配器，最后退回适配器 0。跨适配器采集由 WGC 处理，能工作但帧年龄与端到端延迟更高；只有把采集与呈现拆成两个设备并使用共享纹理，才能同时保证低延迟与任意输出组合。

## 7. 生命周期

```
启动
 ├─ 单实例检查（Mutex）
 ├─ 读配置 → 若无效则按当前显示器重新推导
 ├─ 枚举显示器
 ├─ 创建 D3D11 设备
 ├─ 创建帧源（目标显示器）
 ├─ 创建预览窗 + 呈现器
 ├─ 启动捕获 → 启动渲染线程
 └─ 注册热键 / 托盘

退出
 ├─ 停渲染线程（join，超时保护）
 ├─ 停捕获、释放帧池
 ├─ 释放交换链与合成器
 ├─ 释放 D3D 设备
 └─ 保存配置
```

关闭顺序必须与创建相反，否则容易出现退出时挂死或设备泄漏。

## 8. 性能目标

| 指标 | 目标 | 基线（CPU 路径） |
|---|---|---|
| 呈现帧率 | ≥ 58 fps（源 60 fps） | 24–32 fps |
| 端到端延迟 p95 | ≤ 20 ms | 39–57 ms |
| 端到端延迟 max | ≤ 35 ms | 70 ms |
| CPU 占用 | ≤ 20% 单核 | 100% 单核 |
| 内存 | 1 小时长稳无增长 | 未测 |

验收必须在同一台机器、同一分辨率组合下与基线对比，否则数字没有意义。

### 8.1 0.0.2 实测

测试条件：Windows 11；主屏 2560×1440@180（Intel 核显），副屏 1080×1920@60（NVIDIA 独显）。正向捕获副屏、预览主屏（预览窗 446×792），反向反之。设备统一使用高性能 NVIDIA 适配器。

| 指标 | 0.0.2 正向（主屏看副屏） | 0.0.2 反向（副屏看主屏） | 基线（CPU 路径） |
|---|---|---|---|
| 捕获 → 回调年龄 | p95 5–27 ms | p95 16–33 ms | — |
| 端到端延迟 | avg 7–20 ms，p95 18–38 ms | avg 15–24 ms，p95 28–42 ms | avg 26–42 ms，p95 39–57 ms |
| 呈现帧率 | 32–47 fps，与捕获持平 | 32–39 fps，与捕获持平 | 24–32 fps |
| 丢帧 | 0 | 0 | 约一半 |
| CPU | 约 0.16 个逻辑核 | 约 0.16 个逻辑核 | 约 1 个逻辑核 |

正向为同适配器采集；反向源屏在核显、设备与预览在独显，WGC 需跨适配器出帧，因此延迟更高。

同一时刻运行冻结的 `CaptureProbe` 对照：其累计捕获约 30–40 fps，个别秒会补帧到 60+。本机 WGC 显示器捕获稳定在 32–47 fps，因此 58 fps 目标未达成；`cap == show` 且 `drop == 0`，说明瓶颈在 WGC 出帧而非呈现管线。

## 9. 里程碑

**M0 骨架**（已完成）
三工程分层、契约接口、集中构建配置。

**M1 GPU 直通**（0.0.2 已完成）
`WgcInterop` + `WgcFrameSource` + `D3DPreviewHost`，实现采集到呈现全显存路径。产出：呈现帧数与捕获帧数一致，不因管线丢帧；端到端 p95 进入 20ms 内。帧率上限受 WGC 出帧限制，实测见第 8 节。具体拆解见第 10 节。

**M2 交互与集成**
点击穿透、全局热键、托盘、设置持久化、窗口拖动与缩放。

**M3 健壮性**
热插拔、设备丢失恢复、DPI 换算、HDR 兼容、长期稳定性。

**M4 跨设备**（另行评估）
`IFrameSource` 新增网络实现，配合硬件编码。不影响前三期结构。

每个里程碑结束都要跑一次基准脚本并记录数字，不允许"感觉快了"。

## 10. 开发步骤

### 10.1 总原则

里程碑是验收节点，不是工作顺序。实际开发按"每步都能看见结果"切分，禁止把多个未验证模块留到最后一次性集成。

1. **呈现先于采集。** 先让呈现侧画一个已知图案，证明 D3D 设备、交换链、DirectComposition、窗口样式这条链路是通的。之后接入采集若黑屏，故障范围只剩纹理拷贝与槽位交换，不必二分排查。
2. **风险前置。** DirectComposition 挂 WinForms 窗口、`WS_EX_NOREDIRECTIONBITMAP` 下的 GDI 失效、WGC Surface 转 D3D11 纹理，这三处是 M1 最可能卡住的地方，全部排进前三步。
3. **每步单独可运行。** 任何一步做完都能启动程序并观察到该步的产出，不依赖后续步骤。

### 10.2 M1 拆解

#### 步骤 1：显示器与 D3D 设备

- 落位：`Interop/MonitorInterop.cs`、`Display/MonitorEnumerator.cs`、`Interop/D3D11Interop.cs`、`Diagnostics/FrameClock.cs`
- 内容：
  - `EnumDisplayMonitors` + `GetMonitorInfoW` 产出 `MonitorDescriptor`，按 X 再按 Y 排序，保证同一台机器上序号稳定
  - 设备创建按 `Level_11_1 → Level_11_0 → Level_10_1` 降级（见 6.5）
  - **显式选择适配器**：优先枚举 `IDXGIFactory6` 的 `HighPerformance` 适配器，失败时退回预览屏所在显示器的适配器，再退适配器 0。不要用 `null` 走默认适配器；混合显卡上按显示器选择可能选到无法合成 DirectComposition 内容的核显，表现为预览窗全黑
  - `FrameClock` 用 QPC，与 WGC 的 `SystemRelativeTime` 同源
- 完成判据：控制台列出的显示器尺寸与坐标和系统显示设置一致；设备创建成功并打印实际特性级别；连续启动两次序号一致
- 失败模式：`GetMonitorInfoW` 返回物理像素，`Screen.Bounds` 返回 DIP，两者不可混算（见 6.8）

#### 步骤 2：呈现器打底

风险最高的一步，先做。

- 落位：`Interop/CompositionInterop.cs`、`Render/D3DPreviewHost.cs`、`Ui/PreviewWindow.cs`
- 内容：
  - 窗口样式按 6.3 组合，不外挂任何 WinForms 控件
  - `D3DPreviewHost` 只画测试图案（GPU 生成的棋盘格或渐变），刻意不接采集
  - 交换链按 6.2 参数创建，创建后立刻 `SetMaximumFrameLatency(1)`
  - 渲染线程独立于 UI 线程，按第 4 节模型轮询并 `Present`
- 完成判据：无边框置顶窗口显示 GPU 绘制的测试图案；拖动改变尺寸后画面随 `ResizeBuffers` 正确重绘；静止时渲染线程不空转
- 失败模式：`WS_EX_NOREDIRECTIONBITMAP` 与 Form 默认绘制冲突（见 6.3）；交换链建在错误的适配器上导致 `CreateSwapChainForComposition` 失败

#### 步骤 3：隐身与穿透

- 落位：`Platform/WindowCaptureExclusion.cs`、`Platform/GlobalHotKey.cs`
- 内容：
  - `SetWindowDisplayAffinity(hwnd, 0x11)`，见 6.4
  - `WS_EX_TRANSPARENT` 运行时切换，改完 ExStyle 必须 `SetWindowPos` 带 `SWP_FRAMECHANGED`
  - `RegisterHotKey` 注册 `Ctrl+Alt+P`
- 完成判据：
  - 系统截图工具截全屏，预览窗区域不出现窗口内容
  - 开启穿透后 `WindowFromPoint` 在预览窗中心返回的不是自身 HWND
  - 点击预览窗后 `GetForegroundWindow` 不变
- 为什么提前：`SetWindowDisplayAffinity` 会影响窗口的合成行为，与交换链和样式组合存在耦合。等到 M3 才验证，返工面覆盖窗口与呈现器两层

#### 步骤 4：WGC 采集到纹理

- 落位：`Interop/WgcInterop.cs`、`Capture/WgcFrameSource.cs`
- 内容：
  - `RoGetActivationFactory` + `CreateForMonitor` 直接从 `spikes/CaptureProbe/CaptureInterop.cs` 移植，该路径已验证，不重写
  - 帧池用 `CreateFreeThreaded`，`NumberOfBuffers = 2`
  - `frame.Surface` 取 `ID3D11Texture2D` 走 `IDirect3DDxgiInterfaceAccess`（IID `A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1`）的 `GetInterface`，不要用 `SoftwareBitmap`
  - 回调全程同步：`TryGetNextFrame` → 取纹理 → 拷贝 → `Dispose`，不出现 `await`
- 完成判据：每秒输出捕获帧率与帧年龄，帧年龄明显低于基线表；全程 0 次 `Map` / `LockBits` / 字节数组
- 失败模式：忘记 `Dispose` frame 导致帧池耗尽、`TryGetNextFrame` 持续返回 `null`；在回调里碰 UI 造成跨线程异常或消息泵阻塞

#### 步骤 5：双缓冲接通

- 落位：`Capture/WgcFrameSource.cs`（补槽位）、`Render/D3DPreviewHost.cs`（接 `Present`）
- 内容：
  - 两个自有纹理槽，回调线程 `CopyResource` 到空闲槽后 `Interlocked.Exchange` 交换索引（见第 4 节）
  - 等比缩放按源画面比例算 viewport，剩余区域填黑
  - 渲染线程无新帧时 `Thread.Sleep(1)`，有新帧立即 `Present`
- 完成判据：画面出现并跟随源屏变化；≥ 58 fps；p95 ≤ 20 ms；CPU ≤ 20% 单核
- 失败模式：只有一个槽或拷贝与呈现共用纹理导致撕裂；没有丢弃语义导致延迟随运行时长累积

#### 步骤 6：指标闭环

- 落位：`Diagnostics/LatencyMonitor.cs`、`Diagnostics/FrameStats.cs`
- 内容：滑动窗口算 avg / p95 / max，每秒输出一行
- 完成判据：同一台机器、同一分辨率组合下与 [PROJECT_STRUCTURE.md](PROJECT_STRUCTURE.md) 的基线表逐项对照，把新旧数字写进文档。没有对照数字不算通过

### 10.3 M2 顺序

1. 点击穿透与不抢焦点一起验证。只加 `WS_EX_TRANSPARENT` 而不加 `WS_EX_NOACTIVATE`，会出现穿透了但焦点仍被抢的情况
2. 窗口拖动与边缘缩放，缩放路径必须走 `ResizeBuffers`，并在 `WM_SIZING` 中按源宽高比锁定窗口
3. 托盘菜单与退出
4. 设置持久化。放在交互定型之后，否则字段设计会返工
5. 延迟叠加层。用独立半透明子窗口，最后做，避免污染交换链渲染路径

### 10.4 M3 顺序

1. 设备丢失恢复（见 6.6）。长稳测试最先暴露，早做能减少后续返工
2. 显示器热插拔（见 6.7）
3. 跨屏 DPI 重算（见 6.8）
4. HDR 格式匹配（见 6.9）
5. 1 小时长稳，观察内存与句柄数

### 10.5 实现纪律

1. WGC 回调里禁止 `await`、禁止 UI 操作、禁止阻塞等待。基线程序的 `async void` + `SoftwareBitmap` 正是 CPU 路径的根源
2. 第一步就显式选适配器。跨适配器（见 6.10）在设备创建阶段就要能明确报错，不拖到呈现失败才查
3. 纯函数必须单测，不允许只靠肉眼验证

## 11. 验证方法

- **帧率与延迟**：控制台输出每秒一行，包含捕获 fps、呈现 fps、avg/p95/max
- **穿透**：`WindowFromPoint` 检查开启穿透后是否还能命中自身
- **捕获隐身**：把预览窗移到被捕获屏上，截图确认画面中不出现预览窗
- **长稳**：连续运行 1 小时，观察 `Process.PrivateMemorySize64` 与句柄数是否单调增长
- **静止画面**：源画面不动时确认 WGC 不产生新帧，渲染线程不空转吃 CPU
- **单元测试**：等比缩放 viewport 计算、`SettingsStore` 损坏回退、`LatencyMonitor` 分位数。三项都是纯函数且容易算错，放进 `tests/Glimpse.Tests`，不依赖真实显示器

## 12. 风险清单

| 风险 | 影响 | 应对 |
|---|---|---|
| 受保护内容（DRM）黑屏 | 舞台播片时预览全黑 | 无法绕过，需在界面明确提示 |
| 独占全屏 | WGC 可能抓不到 | 提示用户改用无边框窗口 |
| 多 GPU 跨适配器 | 拷贝失败 | 共享纹理或明确报错 |
| 高刷新率源（144Hz） | 呈现策略不匹配 | 按源刷新率动态调整，允许丢帧但不得累积延迟 |
| 舞台软件置顶全屏 | 预览窗被盖住 | 预览窗自身置顶 + 用户可设"始终最前"级别 |
| 系统缩放非 100% | 尺寸与位置错乱 | 全链路用物理像素，按目标屏 DPI 换算 |
