using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CameraControl.Application.Cua.Context;
using CameraControl.Presentation.ViewModels;

namespace CameraControl.Presentation.Views;

/// <summary>
/// 上下文管理窗口（纯代码构建，无 XAML）。
///
/// 这是「保留查询入口」的落点：列出该配置下所有上下文（含归档），
/// 可以逐条删除、打开目录手工清理。不做编辑——上下文是只追加的。
/// </summary>
public sealed class ContextManagerWindow : Window
{
    private readonly IContextStore _store;
    private readonly string _profileId;
    private readonly IDialogService _dialog;

    private readonly ListBox _list = new();
    private readonly TextBlock _hint = new();
    private IReadOnlyList<ContextSnapshot> _snapshots = [];

    public ContextManagerWindow(IContextStore store, string profileId, IDialogService dialog)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _profileId = profileId;
        _dialog = dialog;

        Title = "上下文管理";
        Width = 760;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = BuildContent();
        Reload();
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };

        buttons.Children.Add(MakeButton("删除选中", DeleteSelected));
        buttons.Children.Add(MakeButton("刷新", Reload));
        buttons.Children.Add(MakeButton("打开文件夹", OpenFolder));
        buttons.Children.Add(MakeButton("关闭", Close));

        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _hint.FontSize = 11;
        _hint.Foreground = System.Windows.Media.Brushes.Gray;
        _hint.Margin = new Thickness(0, 6, 0, 0);
        _hint.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(_hint, Dock.Bottom);
        root.Children.Add(_hint);

        _list.FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI");
        _list.FontSize = 12;
        _list.Margin = new Thickness(0, 0, 0, 0);

        root.Children.Add(_list);

        return root;
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Width = 96,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0)
        };

        button.Click += (_, _) => onClick();

        return button;
    }

    private void Reload()
    {
        _snapshots = _store.List(_profileId);

        _list.Items.Clear();

        if (_snapshots.Count == 0)
        {
            _hint.Text = "该配置下还没有上下文。";
            return;
        }

        string? activeId = _store.GetActiveContextId(_profileId);

        foreach (ContextSnapshot snapshot in _snapshots)
        {
            string mark = snapshot.Id == activeId ? "● 当前" : snapshot.IsArchived ? "  归档" : "      ";

            _list.Items.Add(
                $"{mark}  {snapshot.Id}  {snapshot.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}  " +
                $"{snapshot.EntryCount,4} 条  {snapshot.EstimatedTokens,10:N0} tok");
        }

        _hint.Text =
            $"共 {_snapshots.Count} 份。上下文只会在末尾追加，不会改写已有内容；" +
            "删除会连带删除磁盘上的 jsonl 文件。";
    }

    private void DeleteSelected()
    {
        int index = _list.SelectedIndex;

        if (index < 0 || index >= _snapshots.Count)
        {
            _hint.Text = "请先在列表里选中一份上下文。";
            return;
        }

        ContextSnapshot target = _snapshots[index];

        bool confirmed = _dialog.Confirm(
            "删除上下文",
            $"确定删除 {target.Id} 吗？\n\n" +
            $"包含 {target.EntryCount} 条记录，约 {target.EstimatedTokens:N0} tok。\n" +
            "文件会从磁盘删除，不可恢复。");

        if (!confirmed)
        {
            _hint.Text = "已取消删除。";
            return;
        }

        try
        {
            bool removed = _store.Delete(_profileId, target.Id);

            Reload();
            _hint.Text = removed ? $"已删除 {target.Id}。" : $"没有找到 {target.Id}。";
        }
        catch (Exception ex)
        {
            _hint.Text = $"删除失败：{ex.GetType().Name} / {ex.Message}";
        }
    }

    private void OpenFolder()
    {
        try
        {
            string root = Path.Combine(
                CameraControl.Composition.ServiceRegistration.ResolveDataDirectory(),
                "context",
                _profileId);

            if (!Directory.Exists(root))
            {
                _hint.Text = "目录还不存在（还没有写入过上下文）。";
                return;
            }

            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _hint.Text = $"打开目录失败：{ex.Message}";
        }
    }
}
