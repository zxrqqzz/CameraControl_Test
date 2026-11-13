using System.Collections.ObjectModel;
using System.Windows.Media;
using CameraControl.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CameraControl.Presentation.ViewModels;

/// <summary>调色卡上的一格。</summary>
public sealed partial class AccentSwatch : ObservableObject
{
    public AccentSwatch(AccentPreset preset)
    {
        Key = preset.Key;
        Name = preset.Name;
        Halftone = preset.Halftone;
    }

    public string Key { get; }

    public string Name { get; }

    /// <summary>色卡预览：与控件填充完全同一支网点刷。</summary>
    public Brush Halftone { get; }

    /// <summary>当前是否选中（画粗边框）。</summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 调色卡（外观偏好）视图模型：列出预设主色，点一下立即生效并记住。
///
/// 只碰"主色"这一件事——背景/结构仍然是固定的极简黑白，
/// 所以换色不会把版面换乱，只是把"可交互的地方"换个色。
/// </summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly ThemeService _theme;

    public AppearanceViewModel(ThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        _theme = theme;

        foreach (AccentPreset preset in ThemeService.Presets)
            Swatches.Add(new AccentSwatch(preset));

        SyncSelection();
    }

    public ObservableCollection<AccentSwatch> Swatches { get; } = [];

    /// <summary>当前主色名字（显示在提示行里）。</summary>
    public string CurrentName => _theme.CurrentPreset.Name;

    [RelayCommand]
    private void Apply(AccentSwatch? swatch)
    {
        if (swatch is null) return;

        _theme.Apply(swatch.Key);
        SyncSelection();
    }

    [RelayCommand]
    private void Reset()
    {
        _theme.Apply(ThemeService.DefaultKey);
        SyncSelection();
    }

    private void SyncSelection()
    {
        foreach (AccentSwatch swatch in Swatches)
            swatch.IsSelected = swatch.Key == _theme.CurrentKey;

        OnPropertyChanged(nameof(CurrentName));
    }
}
