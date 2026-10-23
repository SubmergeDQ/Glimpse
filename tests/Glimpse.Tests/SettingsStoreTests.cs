using Glimpse.Abstractions.Configuration;
using Glimpse.Configuration;
using Xunit;

namespace Glimpse.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "glimpse-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _filePath;

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "settings.json");
    }

    [Fact]
    public void LoadReturnsDefaultsWhenFileMissing()
    {
        var store = new SettingsStore(_filePath);

        AppSettings settings = store.Load(out string? warning);

        Assert.Null(warning);
        Assert.Null(settings.SourceDeviceName);
        Assert.Null(settings.Placement);
        Assert.False(settings.ClickThrough);
    }

    [Fact]
    public void LoadFallsBackWhenJsonIsBroken()
    {
        File.WriteAllText(_filePath, "{ this is not json");
        var store = new SettingsStore(_filePath);

        AppSettings settings = store.Load(out string? warning);

        Assert.NotNull(warning);
        Assert.Null(settings.SourceDeviceName);
    }

    [Fact]
    public void SaveAndLoadRoundTrips()
    {
        var store = new SettingsStore(_filePath);
        var expected = new AppSettings
        {
            SourceDeviceName = @"\\.\DISPLAY2",
            PreviewDeviceName = @"\\.\DISPLAY1",
            SourceMonitorIndex = 1,
            PreviewMonitorIndex = 0,
            Placement = new PreviewPlacement(100, 200, 446, 792),
            ClickThrough = true,
        };

        store.Save(expected);
        AppSettings actual = store.Load(out string? warning);

        Assert.Null(warning);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LegacyJsonWithoutNewFieldsStillLoads()
    {
        // 旧版本配置只有序号与穿透状态，新字段缺失时必须能直接读出
        File.WriteAllText(_filePath, "{\"SourceMonitorIndex\":1,\"ClickThrough\":true}");
        var store = new SettingsStore(_filePath);

        AppSettings settings = store.Load(out string? warning);

        Assert.Null(warning);
        Assert.Equal(1, settings.SourceMonitorIndex);
        Assert.True(settings.ClickThrough);
        Assert.Null(settings.SourceDeviceName);
        Assert.Equal(50, settings.PreviewScalePercent);
        Assert.Equal(PreviewAnchor.BottomRight, settings.PreviewAnchor);
        Assert.Equal(60, settings.TestDurationSeconds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
