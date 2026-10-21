using Glimpse.App;

namespace Glimpse;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // DPI 模式由 csproj 的 ApplicationHighDpiMode 指定，此处不再手动设置
        ApplicationConfiguration.Initialize();

        // 入口只负责启动组合根，不在这里装配任何具体实现
        CompositionRoot.Run(args);
    }
}
