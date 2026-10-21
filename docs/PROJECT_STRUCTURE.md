# 项目结构与依赖规则

本文档定义 Glimpse 的目录职责与依赖方向。新增代码前先对照"落位判断"一节，避免功能随手堆进最方便的文件里。

## 设计取向

这个项目要保持可维护，靠的不是目录数量，而是三件可被构建系统强制执行的事：

1. **依赖方向由工程引用物理约束**，而不是靠口头约定。
2. **版本与编译设置集中管理**，不允许各工程各写一套。
3. **命名与风格在构建期检查**，不依赖代码评审时的个人习惯。

因此只拆三个工程。继续细分会让编译变慢、引用变乱，反而更容易长出循环依赖。

## 目录树

```
Glimpse/
├── Glimpse.sln                 解决方案，只包含正式工程，不含 spikes
├── Directory.Build.props       全局编译设置（TFM、x64、可空、分析器）
├── Directory.Packages.props    集中包版本管理（CPM）
├── .editorconfig               命名与代码风格，构建期生效
├── docs/
│   └── PROJECT_STRUCTURE.md    本文档
├── src/
│   ├── Glimpse.Abstractions/   契约层：接口与共享模型
│   │   ├── Capture/            IFrameSource、CapturedFrame
│   │   ├── Configuration/      AppSettings、PreviewPlacement
│   │   ├── Diagnostics/        IFrameClock
│   │   ├── Display/            IMonitorEnumerator、MonitorDescriptor
│   │   └── Preview/            IPreviewHost
│   └── Glimpse/                主程序
│       ├── App/                组合根与生命周期
│       ├── Capture/            WGC 采集实现
│       ├── Configuration/      设置读写
│       ├── Diagnostics/        时钟、帧率与延迟统计
│       ├── Interop/            P/Invoke、COM 接口、原生结构体
│       ├── Platform/           托盘、全局热键、窗口捕获排除
│       ├── Render/             D3D11 呈现与 DirectComposition
│       └── Ui/                 窗口、布局、交互
├── tests/
│   └── Glimpse.Tests/          单元测试与可自动化验证
└── spikes/
    └── CaptureProbe/           基线验证程序，已冻结，不参与正式构建
```

## 各层职责

| 目录 | 只放什么 | 明确不放 |
|---|---|---|
| `Abstractions/` | 接口、不可变数据模型、枚举 | 任何实现、窗口类型、P/Invoke |
| `Interop/` | `DllImport`、`ComImport` 接口、与原生结构体一一对应的 struct | 业务逻辑、状态机、错误提示文案 |
| `Capture/` | `IFrameSource` 的实现、捕获会话生命周期 | 窗口、绘制、设置读写 |
| `Render/` | `IPreviewHost` 的实现、交换链与合成器 | 采集逻辑、显示器枚举 |
| `Ui/` | 窗口消息处理、鼠标键盘交互、排版 | 直接调用 `DllImport`、直接 `new` 采集或渲染对象 |
| `Platform/` | 托盘、热键注册、`SetWindowDisplayAffinity` 等系统集成 | 画面处理 |
| `Diagnostics/` | 时钟、帧率、延迟统计与输出 | 业务决策 |
| `Configuration/` | 设置序列化与迁移 | 界面控件 |
| `App/` | 组合根、启动与关闭顺序 | 具体算法与渲染细节 |

## 依赖规则

允许的引用（箭头表示"可以引用"）：

```
App → Ui / Capture / Render / Platform / Configuration / Diagnostics
Ui  → Abstractions
Capture → Abstractions / Interop
Render  → Abstractions / Interop
Platform → Interop
Configuration → Abstractions
Diagnostics → Abstractions
Abstractions → （仅 Vortice 的纹理与格式类型）
```

三条硬性禁令：

1. **`Capture` 与 `Render` 不得互相引用。** 两者只通过 `Abstractions` 里的 `IFrameSource` / `IPreviewHost` 通信。这条是后续替换实现（跨设备接收、其他呈现方式）时不返工的前提。
2. **`Ui` 不得直接创建 `Capture` 或 `Render` 的具体类型。** 依赖由 `App/CompositionRoot` 注入。界面代码一旦能 `new WgcFrameSource()`，替换实现就开始牵一发动全身。
3. **`Interop` 不得被 `Ui` 直接调用。** 原生调用必须先被 `Capture` / `Render` / `Platform` 包装成有语义的方法。散落的 `DllImport` 是这类项目最早失控的地方。

## 落位判断

新增一个功能时先问它属于哪一类，再决定放哪：

- 新的画面来源（局域网接收、摄像头、窗口捕获）→ `Capture/`，实现 `IFrameSource`
- 新的呈现方式（全屏、多预览窗、录制输出）→ `Render/`，实现 `IPreviewHost`
- 新的系统集成（开机自启、任务栏缩略图、显示器热插拔）→ `Platform/`
- 新的度量指标（丢帧、GPU 耗时、内存）→ `Diagnostics/`
- 新的用户可调项 → 模型放 `Abstractions/Configuration`，持久化放 `Configuration/`，控件放 `Ui/`
- 只是给已有流程加分支 → 先考虑是否是职责错位，不要因为方便就塞进当前文件

## 构建与验证

需要 .NET 8 SDK（当前开发机只有运行时，尚未安装）。

```powershell
dotnet build Glimpse.sln -c Debug
dotnet run --project src/Glimpse
dotnet test tests/Glimpse.Tests
```

`spikes/CaptureProbe` 不参与解决方案构建。它已通过 `ManagePackageVersionsCentrally=false` 与全局配置隔离，仅作为基线数据出处保留。

## 当前基线

`spikes/CaptureProbe` 在 1080x1920 副屏 → 562x1000 预览窗的配置下实测：

| 指标 | 数值 |
|---|---|
| 捕获帧率 | 60–68 fps |
| 显示帧率 | 24–32 fps |
| 端到端延迟 | avg 26–42 ms，p95 39–57 ms |
| CPU | 约一个逻辑核满载 |

瓶颈在 CPU 读回与 GDI 绘制，不在捕获本身。正式实现改为 GPU 零拷贝后，应以同一脚本复测并与此表对比。
