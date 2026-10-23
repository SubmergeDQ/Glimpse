using Glimpse.Abstractions.Configuration;
using Glimpse.Abstractions.Display;
using Glimpse.App;
using Xunit;

namespace Glimpse.Tests;

public sealed class StartupOptionsTests
{
    private static readonly MonitorDescriptor Primary =
        new(IntPtr.Zero, 0, true, 0, 0, 2560, 1440, @"\\.\DISPLAY1");

    private static readonly MonitorDescriptor Secondary =
        new(new IntPtr(1), 1, false, 2560, 0, 1080, 1920, @"\\.\DISPLAY2");

    private static readonly MonitorDescriptor[] Monitors = [Primary, Secondary];

    [Fact]
    public void DefaultsToSecondarySourceAndPrimaryPreview()
    {
        MonitorSelection selection = new StartupOptions(null, null).Resolve(Monitors, out string? warning);

        Assert.Null(warning);
        Assert.Equal(1, selection.Source.Index);
        Assert.Equal(0, selection.Preview.Index);
    }

    [Fact]
    public void SourceOnlyPutsPreviewOnTheOtherMonitor()
    {
        MonitorSelection selection = new StartupOptions(1, null).Resolve(Monitors, out _);

        Assert.Equal(1, selection.Source.Index);
        Assert.Equal(0, selection.Preview.Index);
    }

    [Fact]
    public void PreviewOnlyPutsSourceOnThePrimaryMonitor()
    {
        MonitorSelection selection = new StartupOptions(null, 1).Resolve(Monitors, out _);

        Assert.Equal(0, selection.Source.Index);
        Assert.Equal(1, selection.Preview.Index);
    }

    [Fact]
    public void ReverseModeCapturesPrimaryAndPreviewsSecondary()
    {
        MonitorSelection selection = new StartupOptions(0, 1).Resolve(Monitors, out _);

        Assert.Equal(0, selection.Source.Index);
        Assert.Equal(1, selection.Preview.Index);
    }

    [Fact]
    public void OutOfRangeIndexFallsBackWithWarning()
    {
        MonitorSelection selection = new StartupOptions(9, null).Resolve(Monitors, out string? warning);

        Assert.NotNull(warning);
        Assert.Contains("--source 9", warning);
        Assert.Equal(1, selection.Source.Index);
        Assert.Equal(0, selection.Preview.Index);
    }

    [Fact]
    public void ParseReadsBothArguments()
    {
        StartupOptions options = StartupOptions.Parse(["--source", "0", "--preview", "1"]);

        Assert.Equal(0, options.SourceMonitorIndex);
        Assert.Equal(1, options.PreviewMonitorIndex);
    }

    [Fact]
    public void ParseIgnoresIncompleteArguments()
    {
        StartupOptions options = StartupOptions.Parse(["--source"]);

        Assert.Null(options.SourceMonitorIndex);
        Assert.Null(options.PreviewMonitorIndex);
    }

    [Fact]
    public void ParseReadsBenchmarkSeconds()
    {
        StartupOptions options = StartupOptions.Parse(["--benchmark", "60"]);

        Assert.Equal(60, options.BenchmarkSeconds);
        Assert.Null(options.SourceMonitorIndex);
    }

    [Fact]
    public void SettingsDeviceNameWinsOverSavedIndex()
    {
        // 设备名指向副屏，序号指向主屏，配置损坏时以设备名为准
        var settings = new AppSettings
        {
            SourceMonitorIndex = 0,
            SourceDeviceName = @"\\.\DISPLAY2",
        };

        MonitorSelection selection = new StartupOptions(null, null).Resolve(Monitors, settings, out string? warning);

        Assert.Null(warning);
        Assert.Equal(1, selection.Source.Index);
    }

    [Fact]
    public void CommandLineOverridesSettings()
    {
        var settings = new AppSettings { SourceDeviceName = @"\\.\DISPLAY2" };

        MonitorSelection selection = new StartupOptions(0, null).Resolve(Monitors, settings, out _);

        Assert.Equal(0, selection.Source.Index);
    }

    [Fact]
    public void MissingDeviceNameFallsBackToSavedIndex()
    {
        var settings = new AppSettings
        {
            SourceDeviceName = @"\\.\DISPLAY9",
            SourceMonitorIndex = 0,
        };

        MonitorSelection selection = new StartupOptions(null, null).Resolve(Monitors, settings, out _);

        Assert.Equal(0, selection.Source.Index);
    }

    [Fact]
    public void MissingDeviceNameAndIndexFallsBackWithWarning()
    {
        var settings = new AppSettings
        {
            SourceDeviceName = @"\\.\DISPLAY9",
            SourceMonitorIndex = 8,
        };

        MonitorSelection selection = new StartupOptions(null, null).Resolve(Monitors, settings, out string? warning);

        Assert.NotNull(warning);
        Assert.Contains(@"\\.\DISPLAY9", warning);
        Assert.Equal(1, selection.Source.Index);
    }
}
