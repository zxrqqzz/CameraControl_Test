using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CameraControl.Application.Cua.Context;
using CameraControl.Presentation.ViewModels;

namespace CameraControl.Presentation.Views;

/// <summary>
/// 上下文管理窗口：列出该配置下所有上下文（含归档），可以逐条删除、打开目录手工清理。
/// 不做编辑——上下文是只追加的。
///
/// 视觉层在 ContextManagerWindow.xaml；这里只留数据装配与动作。
/// </summary>
public partial class ContextManagerWindow : Window
{
    private readonly IContextStore _store;
    private readonly string _profileId;
    private readonly IDialogService _dialog;

    private IReadOnlyList<ContextSnapshot> _snapshots = [];

    /// <summary>列表行：把快照摊平成界面要显示的样子（当前/归档标记在这里算好）。</summary>
    public sealed class ContextRow
    {
        public ContextRow(ContextSnapshot snapshot, bool isCurrent)
        {
            Id = snapshot.Id;
            IsCurrent = isCurrent;
            IsArchived = snapshot.IsArchived;
            Meta =
                $"{snapshot.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  " +
                $"{snapshot.EntryCount} 条  ·  {snapshot.EstimatedTokens:N0} tok";
        }

        public string Id { get; }

        public bool IsCurrent { get; }

        public bool IsArchived { get; }

        public string Meta { get; }
    }

    public ObservableCollection<ContextRow> Rows { get; } = [];

    public ContextManagerWindow(IContextStore store, string profileId, IDialogService dialog)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _profileId = profileId;
        _dialog = dialog;

        InitializeComponent();

        DataContext = this;
        Reload();
    }

    private void Reload()
    {
        _snapshots = _store.List(_profileId);
        string? activeId = _store.GetActiveContextId(_profileId);

        Rows.Clear();

        foreach (ContextSnapshot snapshot in _snapshots)
            Rows.Add(new ContextRow(snapshot, snapshot.Id == activeId));

        EmptyText.Visibility = _snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        HintText.Text = _snapshots.Count == 0
            ? "先在「AI」页跑一次，或换一个配置。"
            : $"共 {_snapshots.Count} 份。上下文只会在末尾追加，不会改写已有内容；删除会连带删除磁盘上的 jsonl 文件。";
    }

    private void DeleteSelected()
    {
        int index = List.SelectedIndex;

        if (index < 0 || index >= _snapshots.Count)
        {
            HintText.Text = "请先在列表里选中一份上下文。";
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
            HintText.Text = "已取消删除。";
            return;
        }

        try
        {
            bool removed = _store.Delete(_profileId, target.Id);

            Reload();
            HintText.Text = removed ? $"已删除 {target.Id}。" : $"没有找到 {target.Id}。";
        }
        catch (Exception ex)
        {
            HintText.Text = $"删除失败：{ex.GetType().Name} / {ex.Message}";
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
                HintText.Text = "目录还不存在（还没有写入过上下文）。";
                return;
            }

            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            HintText.Text = $"打开目录失败：{ex.Message}";
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteSelected();

    private void OnReloadClick(object sender, RoutedEventArgs e) => Reload();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => OpenFolder();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
