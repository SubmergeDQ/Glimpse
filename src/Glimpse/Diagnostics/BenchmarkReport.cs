namespace Glimpse.Diagnostics;

/// <summary>基准测试结果导出。Markdown 字段与验收指标保持同名，便于和基线表对照。</summary>
internal static class BenchmarkReport
{
    public static string ToMarkdown(BenchmarkResult result)
    {
        double dropRate = result.Captured == 0 ? 0 : result.Dropped / (double)result.Captured;

        return string.Join(
            '\n',
            [
                "# Glimpse 基准测试",
                "",
                $"- 开始时间：{result.StartedAt:yyyy-MM-dd HH:mm:ss}",
                $"- 时长：{result.Duration.TotalSeconds:0} 秒",
                $"- 捕获：{result.Captured} 帧（{result.CaptureFps:F1} fps）",
                $"- 呈现：{result.Presented} 帧（{result.PresentFps:F1} fps）",
                $"- 丢弃：{result.Dropped} 帧（{dropRate * 100:F2}%）",
                $"- 回调年龄：avg {result.Age.Avg:F1} / p95 {result.Age.P95:F1} / max {result.Age.Max:F1} ms",
                $"- 端到端：avg {result.Present.Avg:F1} / p95 {result.Present.P95:F1} / max {result.Present.Max:F1} ms",
                $"- CPU：{result.CpuPercentSingleCore:F1}% 单核",
                "",
                "## 验收对照",
                "",
                $"- 丢弃率：{(dropRate < 0.001 ? "通过" : "未通过")}（目标 < 0.1%）",
                $"- 端到端 p95：{(result.Present.P95 <= 20 ? "通过" : "未通过")}（目标 ≤ 20 ms）",
                $"- 端到端 max：{(result.Present.Max <= 35 ? "通过" : "未通过")}（目标 ≤ 35 ms）",
                $"- CPU：{(result.CpuPercentSingleCore <= 20 ? "通过" : "未通过")}（目标 ≤ 20% 单核）",
                "",
            ]);
    }

    /// <summary>写入日志目录，返回实际文件路径。</summary>
    public static string Write(BenchmarkResult result, string directory)
    {
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, $"benchmark-{result.StartedAt:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(path, ToMarkdown(result));
        return path;
    }
}
