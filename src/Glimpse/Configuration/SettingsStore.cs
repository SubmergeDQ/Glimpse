using System.Text.Json;
using Glimpse.Abstractions.Configuration;

namespace Glimpse.Configuration;

/// <summary>
/// 设置的读写。写入走同目录临时文件再覆盖移动，避免进程崩溃时留下半截 JSON；
/// 读取失败一律回退默认值，不向调用方抛异常。
/// </summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;

    public SettingsStore(string filePath) => _filePath = filePath;

    public static SettingsStore CreateDefault()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Glimpse");

        return new SettingsStore(Path.Combine(directory, "settings.json"));
    }

    public string FilePath => _filePath;

    /// <summary>
    /// 读取设置。文件不存在、无法读取或 JSON 损坏时返回默认值，
    /// 并通过 <paramref name="warning"/> 返回原因供日志记录。
    /// </summary>
    public AppSettings Load(out string? warning)
    {
        warning = null;

        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            warning = $"设置读取失败，已回退默认值：{ex.Message}";
            return new AppSettings();
        }
    }

    /// <summary>
    /// 保存设置。临时文件与目标同目录，保证覆盖移动发生在同一卷上。
    /// 磁盘或权限异常向上抛出，由调用方决定是否中断。
    /// </summary>
    public void Save(AppSettings settings)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _filePath + ".tmp";
        string json = JsonSerializer.Serialize(settings, SerializerOptions);

        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
