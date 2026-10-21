# Glimpse

在主/副屏用置顶画中画低延迟预览另一块屏幕的轻量监看工具。适用于舞台表演投屏这类主副屏物理位置不在一起、需要边操作边确认输出的场景。

目标形态：同机零拷贝、跨设备局域网、点击穿透、全局快捷键。

## 当前状态

技术验证阶段。基线程序位于 `spikes/CaptureProbe`（已冻结，不参与正式构建）。

实测环境：Windows 11，主屏 2560×1440，副屏 1080×1920 竖屏，预览窗 562×1000。

| 指标 | 数值 |
|---|---|
| 捕获帧率 | 60–68 fps |
| 显示帧率 | 24–32 fps |
| 端到端延迟 | avg 26–42 ms，p95 39–57 ms，max 70 ms |
| CPU | 约一个逻辑核满载（全机 5% × 20 核） |
| 点击穿透 | 通过 |
| 关闭捕获边框 | 通过 |

结论：捕获侧正常，瓶颈在 CPU 读回显存与 GDI 绘制，导致约一半帧被丢弃。正式实现将改为 D3D11 纹理直通 + DirectComposition 呈现。

## 文档

- [项目结构与依赖规则](docs/PROJECT_STRUCTURE.md)
- [开发文档](docs/DEVELOPMENT.md)

## 构建

需要 .NET 8 SDK。

```powershell
dotnet build Glimpse.sln -c Debug
dotnet run --project src/Glimpse
```

## 许可证

见 [LICENSE](LICENSE)。
