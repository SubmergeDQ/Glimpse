namespace Glimpse.Ui;

/// <summary>
/// 主控面板的色板与字体，取值来自 docs/design/DESIGN.md 第 6 节。
/// 字号按磅书写，由 GDI+ 按屏幕 DPI 换算成物理像素。
/// </summary>
internal static class PanelTheme
{
    public static readonly Color Background = Color.FromArgb(8, 9, 11);
    public static readonly Color Surface = Color.FromArgb(16, 18, 21);
    public static readonly Color Surface2 = Color.FromArgb(21, 24, 28);
    public static readonly Color Line = Color.FromArgb(33, 37, 43);
    public static readonly Color Line2 = Color.FromArgb(44, 49, 56);
    public static readonly Color Line3 = Color.FromArgb(58, 64, 72);
    public static readonly Color Foreground = Color.FromArgb(232, 235, 240);
    public static readonly Color Foreground2 = Color.FromArgb(168, 175, 185);
    public static readonly Color Foreground3 = Color.FromArgb(138, 145, 156);
    public static readonly Color Amber = Color.FromArgb(242, 169, 59);
    public static readonly Color AmberInk = Color.FromArgb(25, 17, 4);
    public static readonly Color Ok = Color.FromArgb(69, 214, 160);
    public static readonly Color Warn = Color.FromArgb(232, 178, 60);
    public static readonly Color Error = Color.FromArgb(239, 91, 91);

    private static readonly HashSet<string> InstalledFamilies = LoadFamilies();

    public static readonly Font Body = CreateSans(9.75f, FontStyle.Regular);
    public static readonly Font BodyStrong = CreateSans(9.75f, FontStyle.Bold);
    public static readonly Font Small = CreateSans(8.25f, FontStyle.Regular);
    public static readonly Font Brand = CreateSans(10.5f, FontStyle.Bold);
    public static readonly Font MonoSmall = CreateMono(8.25f);
    public static readonly Font MonoValue = CreateMono(12f);

    private static HashSet<string> LoadFamilies()
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamily family in FontFamily.Families)
        {
            families.Add(family.Name);
        }

        return families;
    }

    private static Font CreateSans(float size, FontStyle style) =>
        new(PickFamily("Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI"), size, style, GraphicsUnit.Point);

    private static Font CreateMono(float size) =>
        new(PickFamily("Cascadia Mono", "Consolas"), size, FontStyle.Regular, GraphicsUnit.Point);

    private static string PickFamily(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (InstalledFamilies.Contains(candidate))
            {
                return candidate;
            }
        }

        return FontFamily.GenericSansSerif.Name;
    }
}
