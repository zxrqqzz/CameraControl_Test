using CameraControl.Infrastructure.Ai;

namespace CameraControl.Presentation.ViewModels;

/// <summary>
/// 配置列表里的一行。
///
/// 只读投影：界面只用来展示与选中，不直接改配置——
/// 修改一律走 <see cref="LlmSettingsStore"/> 的方法，避免绕过"key 不可改"的约束。
/// </summary>
public sealed record LlmProfileRow(
    string Id,
    string Name,
    string BaseUrl,
    string Model,
    LlmStrength Strength,
    string KeyHint,
    bool IsActive)
{
    /// <summary>下拉列表显示文本。</summary>
    public string Display => $"{(IsActive ? "● " : string.Empty)}{Name} · {Model} · {StrengthText}";

    /// <summary>强度中文。</summary>
    public string StrengthText => Strength switch
    {
        LlmStrength.Low => "低",
        LlmStrength.High => "高",
        _ => "中"
    };
}
