using System.Windows;
using System.Windows.Media;
using CameraControl.Application.Diagnostics;
using CameraControl.Domain.Ports;

namespace CameraControl.Presentation.Services;

/// <summary>
/// 一个主色预设（调色卡上的一格）。
/// </summary>
public sealed class AccentPreset
{
    public AccentPreset(string key, string name, Color color)
    {
        Key = key;
        Name = name;
        Color = color;
        Halftone = ThemeService.BuildHalftone(color);
    }

    /// <summary>稳定标识（写进设置文件，不能改）。</summary>
    public string Key { get; }

    /// <summary>界面上显示的名字。</summary>
    public string Name { get; }

    public Color Color { get; }

    /// <summary>该主色的半调网点刷（色卡预览与控件填充共用）。</summary>
    public Brush Halftone { get; }


}

/// <summary>
/// 主色切换（调色卡的后端）。
///
/// 为什么是"换资源键"而不是"改刷子颜色"：
/// 控件样式统一用 <c>{DynamicResource Accent*}</c> 引用主色，替换
/// <see cref="Application.Resources"/> 里这几个键，WPF 会自己把用到的地方全部刷新；
/// 而改 <c>SolidColorBrush.Color</c> 会踩上"样式封存后刷子被冻结"的问题。
///
/// 记忆：写进统一的 JSON 设置存储（键 <c>appearance</c>），下次启动自动套用。
/// 任何失败都只记日志——外观偏好丢了不该影响用户干活。
/// </summary>
public sealed class ThemeService
{
    /// <summary>默认主色（波普黄）。</summary>
    public const string DefaultKey = "pop-yellow";

    private const string SettingsKey = "appearance";

    /// <summary>进程内单例。界面层自用，不进 DI 容器（Composition 不引用 Presentation）。</summary>
    public static ThemeService Current { get; } = new();

    /// <summary>可选的主色列表（顺序即调色卡上的顺序）。</summary>
    public static IReadOnlyList<AccentPreset> Presets { get; } =
    [
        new("pop-yellow", "波普黄", Color.FromRgb(0xFF, 0xD4, 0x00)),
        new("pop-orange", "橘", Color.FromRgb(0xFF, 0x7A, 0x1A)),
        new("pop-red", "朱红", Color.FromRgb(0xE4, 0x00, 0x2B)),
        new("pop-pink", "品红", Color.FromRgb(0xFF, 0x3D, 0x8B)),
        new("pop-violet", "紫", Color.FromRgb(0x7A, 0x4D, 0xFF)),
        new("pop-blue", "电光蓝", Color.FromRgb(0x00, 0x57, 0xFF)),
        new("pop-cyan", "青", Color.FromRgb(0x00, 0xB8, 0xD4)),
        new("pop-green", "绿", Color.FromRgb(0x00, 0xA6, 0x51))
    ];

    private ISettingsStore? _store;
    private IAppLogger? _logger;

    /// <summary>当前主色 key。</summary>
    public string CurrentKey { get; private set; } = DefaultKey;

    /// <summary>当前主色预设。</summary>
    public AccentPreset CurrentPreset =>
        Presets.FirstOrDefault(p => p.Key == CurrentKey) ?? Presets[0];

    /// <summary>
    /// 接上设置存储并套用上次选择。App 启动时调用一次。
    /// </summary>
    public void Initialize(ISettingsStore store, IAppLogger? logger)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _logger = logger;

        string key = DefaultKey;

        try
        {
            if (store.TryLoad<AppearanceSettings>(SettingsKey, out AppearanceSettings? saved) &&
                saved is not null &&
                Presets.Any(p => p.Key == saved.Accent))
            {
                key = saved.Accent;
            }
        }
        catch (Exception ex)
        {
            logger?.Warn("读取外观设置失败，回退默认主色", new Dictionary<string, object?>
            {
                ["error"] = ex.Message
            });
        }

        ApplyResources(key);
    }

    /// <summary>切换主色并记住（不落盘失败不影响切换本身）。</summary>
    public void Apply(string key)
    {
        AccentPreset preset = Presets.FirstOrDefault(p => p.Key == key) ?? Presets[0];

        ApplyResources(preset.Key);

        try
        {
            _store?.Save(SettingsKey, new AppearanceSettings(preset.Key));
        }
        catch (Exception ex)
        {
            _logger?.Warn("保存外观设置失败（本次切换仍然生效）", new Dictionary<string, object?>
            {
                ["accent"] = preset.Key,
                ["error"] = ex.Message
            });
        }
    }

    /// <summary>
    /// 只换显示、不写盘（色卡预览 / 目检脚本用）。
    /// </summary>
    public void Preview(string key) => ApplyResources(key);

    /// <summary>
    /// 把主色写进应用资源。五个键一起换：
    /// 主色 / 悬停 / 按下 / 淡底 / 网点。
    /// </summary>
    private void ApplyResources(string key)
    {
        AccentPreset preset = Presets.FirstOrDefault(p => p.Key == key) ?? Presets[0];

        CurrentKey = preset.Key;

        // 注意：必须全限定 System.Windows.Application —— 本项目有同名命名空间 CameraControl.Application
        System.Windows.Application? app = System.Windows.Application.Current;
        if (app is null) return; // 无 UI 宿主（测试/命令行）：不动资源

        ResourceDictionary resources = app.Resources;

        resources["AccentBrush"] = Frozen(new SolidColorBrush(preset.Color));
        resources["AccentHoverBrush"] = Frozen(new SolidColorBrush(Darken(preset.Color, 0.10)));
        resources["AccentDeepBrush"] = Frozen(new SolidColorBrush(Darken(preset.Color, 0.20)));
        resources["AccentSoftBrush"] = Frozen(new SolidColorBrush(Blend(preset.Color, Colors.White, 0.82)));
        resources["AccentHalftoneBrush"] = preset.Halftone;
        resources["TextOnAccentBrush"] = Frozen(new SolidColorBrush(TextColorFor(preset.Color)));
    }

    /// <summary>
    /// 造一支"主色底 + 12% 黑网点"的平铺刷子（6px 一格）。
    /// 调色卡的色卡预览与控件填充用的是同一支，颜色永远一致。
    /// </summary>
    public static DrawingBrush BuildHalftone(Color color)
    {
        var group = new DrawingGroup();

        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(color),
            null,
            new RectangleGeometry(new Rect(0, 0, 6, 6))));

        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromArgb(0x1F, 0x0B, 0x0B, 0x0B)),
            null,
            new EllipseGeometry(new Point(3, 3), 1.55, 1.55)));

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 6, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6),
            ViewboxUnits = BrushMappingMode.Absolute
        };

        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 主色上的文字颜色：亮底黑字、暗底白字（相对亮度 0.58 分界）。
    /// 波普黄要黑字、电光蓝/紫要白字——写死哪一个都会有一半预设读不清。
    /// </summary>
    private static Color TextColorFor(Color color)
    {
        double luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;

        return luminance > 0.58 ? Color.FromRgb(0x0B, 0x0B, 0x0B) : Colors.White;
    }

    private static Color Darken(Color color, double amount) => Color.FromArgb(
        color.A,
        (byte)Math.Round(color.R * (1 - amount)),
        (byte)Math.Round(color.G * (1 - amount)),
        (byte)Math.Round(color.B * (1 - amount)));

    private static Color Blend(Color color, Color onto, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a * (1 - amount) + b * amount);

        return Color.FromArgb(
            color.A,
            Mix(color.R, onto.R),
            Mix(color.G, onto.G),
            Mix(color.B, onto.B));
    }
}

/// <summary>外观偏好（目前只有主色）。</summary>
public sealed record AppearanceSettings(string Accent);
