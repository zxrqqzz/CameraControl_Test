using System.Windows;
using CameraControl.Application.Commands;

namespace CameraControl.Presentation.Views;

/// <summary>
/// 人工确认弹窗。
///
/// 刻意做成"默认不执行"：关闭窗口、按 ESC、点拒绝都返回 false。
/// 只有明确点"批准执行"才返回 true。
/// </summary>
public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog(CapabilityDescriptor descriptor, Actor actor, string? summary)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(actor);

        InitializeComponent();

        TitleText.Text = summary ?? descriptor.Title;
        CapabilityText.Text = descriptor.Id;
        RiskText.Text = descriptor.Risk switch
        {
            RiskLevel.Physical => "Physical（会驱动物理设备）",
            RiskLevel.Destructive => "Destructive（不可逆）",
            RiskLevel.Reversible => "Reversible（可撤销）",
            _ => descriptor.Risk.ToString()
        };

        ActorText.Text = actor.Kind == ActorKind.Ai
            ? $"AI（{actor.Name}）"
            : $"人工（{actor.Name}）";

        // 只有物理/破坏性操作才显示红色警告
        WarningBanner.Visibility = descriptor.Risk is RiskLevel.Physical or RiskLevel.Destructive
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnApproveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnRejectClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
