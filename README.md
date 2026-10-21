# Glimpse

在主/副屏用置顶画中画低延迟预览另一块屏幕的轻量监看工具。适用于舞台表演投屏这类主副屏物理位置不在一起、需要边操作边确认输出的场景。

目标形态：同机零拷贝、跨设备局域网、点击穿透、全局快捷键。

## 当前状态

0.0.2 已完成 M1：WGC → 自有 D3D11 纹理 → DXGI 合成交换链 → DirectComposition 的 GPU 全显存链路，画面全程不经过 CPU 读回。

实测环境：Windows 11；主屏 2560×1440@180（Intel 核显，预览窗 446×792），副屏 1080×1920@60（NVIDIA，捕获源）。

| 指标 | 0.0.2 正向（主屏看副屏） | 0.0.2 反向（副屏看主屏） | 基线（CPU 路径） |
|---|---|---|---|
| 捕获 → 回调年龄 | p95 5–27 ms | p95 16–33 ms | — |
| 端到端延迟 | avg 7–20 ms，p95 18–38 ms | avg 15–24 ms，p95 28–42 ms | avg 26–42 ms，p95 39–57 ms |
| 呈现帧率 | 32–47 fps，与捕获持平 | 32–39 fps，与捕获持平 | 24–32 fps |
| 丢帧 | 0 | 0 | 约一半 |
| CPU | 约 0.16 个逻辑核 | 约 0.16 个逻辑核 | 约 1 个逻辑核 |
| 点击穿透 | 通过 | 通过 | 通过 |
| 捕获隐身 | 通过 | 通过 | 未实现 |

说明：同一时刻运行冻结的 `CaptureProbe`，其累计捕获约 30–40 fps（个别秒补帧到 60+）。本机 WGC 显示器捕获稳定在 32–47 fps，因此未达到 58 fps 目标；`cap == show` 且 `drop == 0`，瓶颈在 WGC 出帧，不在呈现管线。反向模式下源屏在核显、设备与预览在独显，WGC 需跨适配器出帧，延迟高于正向。

设备统一选择系统高性能适配器。按显示器选适配器在混合显卡机器上可能选到无法合成 DirectComposition 内容的核显，表现为预览窗全黑。

## 使用

启动后自动把第一块非主屏作为捕获源，在主屏右下角显示置顶预览窗。显示器序号见日志中的 `[显示器] #0/#1`。

反向使用（在副屏看主屏）：

```powershell
# 源码运行
dotnet run --project src/Glimpse -- --source 0 --preview 1

# 直接运行
src\Glimpse\bin\x64\Debug\net8.0-windows10.0.22621.0\Glimpse.exe --source 0 --preview 1
```

只指定一侧时，另一侧会自动选主屏或另一块屏。

- `Ctrl+Alt+P`：切换点击穿透；被其他软件占用时自动改用 `Ctrl+Shift+P`
- `Ctrl+Alt+Q`：退出；备选 `Ctrl+Shift+Q`
- 预览窗内部可拖动，边缘可缩放；缩放时按源画面宽高比锁定，画面始终填满窗口，不出现黑边
- 诊断日志：`%LOCALAPPDATA%\Glimpse\logs\glimpse.log`

当前限制：源屏/预览屏可用启动参数指定，但尚无设置界面与托盘；HDR 源会先按 SDR 输出；设备丢失与显示器热插拔留到 M3。

## 文档

- [项目结构与依赖规则](docs/PROJECT_STRUCTURE.md)
- [开发文档](docs/DEVELOPMENT.md)

## 构建

需要 .NET 8 SDK。

```powershell
dotnet build Glimpse.sln -c Debug
dotnet run --project src/Glimpse
dotnet test tests/Glimpse.Tests
```

## 许可证

见 [LICENSE](LICENSE)。
