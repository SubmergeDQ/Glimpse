namespace Glimpse.App;

/// <summary>
/// 组合根：全应用唯一的对象装配入口。
/// 除这里之外，任何层都不得直接创建其他层的具体实现，这是依赖方向不失控的关键约束。
/// </summary>
internal static class CompositionRoot
{
    public static void Run()
    {
        // 骨架阶段尚未接入采集与呈现，先以空窗口验证工程可启动。
        // 接入顺序：Interop → Capture → Render → Ui，在此处完成装配。
        Application.Run(new Form
        {
            Text = "Glimpse",
            Width = 640,
            Height = 360,
            StartPosition = FormStartPosition.CenterScreen,
        });
    }
}
