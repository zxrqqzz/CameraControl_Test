using System.Collections.ObjectModel;
using CameraControl.Application.Cua.Context;
using CameraControl.Infrastructure.Ai;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CameraControl.Presentation.ViewModels;

/// <summary>
/// AI 上下文块：上下文档位（Mods）、新建 / 清空上下文，以及设置与上下文窗口入口。
/// </summary>
public sealed partial class MainViewModel
{

    // ── 上下文（Mods/context 开关）───────────────────────────

    /// <summary>档位下拉的显示文本，顺序与 <see cref="ContextTier"/> 一致。</summary>
    public ObservableCollection<string> AiContextTierOptions { get; } =
    [
        "Off（关闭：不记录不注入）",
        "只存档（记录但不作为输入）",
        "完整注入（记录并作为固定前缀）"
    ];

    [ObservableProperty]
    private int _aiContextTierIndex = 2;

    [ObservableProperty]
    private string _aiContextSummary = "—";

    /// <summary>功能被开关关闭时隐藏整块，避免误导。</summary>
    [ObservableProperty]
    private bool _aiContextVisible;

    private bool _loadingContext;

    partial void OnAiContextTierIndexChanged(int value) => ApplyContextTier(value);

    private void ApplyContextTier(int index)
    {
        if (_loadingContext) return;

        LlmProfile? active = _llmSettings.ActiveProfile();

        if (active is null) return;

        try
        {
            _contextStore.SetTier(active.Id, (ContextTier)index);
            RefreshContextInfo();
            Status = $"上下文档位已切换为：{AiContextTierOptions[Math.Clamp(index, 0, AiContextTierOptions.Count - 1)]}";
        }
        catch (Exception ex)
        {
            LogFailure("切换上下文档位", ex, null);
            Status = $"切换档位失败：{ex.Message}";
        }
    }

    /// <summary>刷新编号、条数、占用与档位。</summary>
    private void RefreshContextInfo()
    {
        LlmProfile? active = _llmSettings.ActiveProfile();

        AiContextVisible = _contextStore.IsEnabled && active is not null;

        if (!AiContextVisible)
        {
            AiContextSummary = _contextStore.IsEnabled
                ? "还没有配置，无法建立上下文。"
                : "上下文功能已关闭：Mods/context/mod.json 缺失或 enabled=false。";
            return;
        }

        try
        {
            string? id = _contextStore.GetActiveContextId(active!.Id);
            ContextSnapshot? snapshot = id is null
                ? null
                : _contextStore.List(active.Id).FirstOrDefault(c => c.Id == id);

            AiContextSummary = snapshot is null
                ? "还没有上下文（本轮对话会自动创建）"
                : $"{snapshot.Id} · {snapshot.EntryCount} 条 · 约 {snapshot.EstimatedTokens:N0} tok";

            _loadingContext = true;
            AiContextTierIndex = (int)_contextStore.GetTier(active.Id);
            _loadingContext = false;
        }
        catch (Exception ex)
        {
            LogFailure("读取上下文信息", ex, null);
            AiContextSummary = $"读取失败：{ex.Message}";
        }
    }

    /// <summary>开启新对话：旧内容保留（归档），开一份新的。</summary>
    [RelayCommand]
    private void StartNewContext()
    {
        LlmProfile? active = _llmSettings.ActiveProfile();

        if (active is null || !_contextStore.IsEnabled)
        {
            Status = "没有可用的配置或上下文功能已关闭。";
            return;
        }

        try
        {
            string id = _contextStore.StartNewContext(active.Id);
            RefreshContextInfo();
            Status = $"已开启新对话（{id}）。上一份上下文已归档保留。";
                LoadAiHistory();
        }
        catch (Exception ex)
        {
            LogFailure("开启新对话", ex, null);
            Status = $"开启新对话失败：{ex.Message}";
        }
    }

    /// <summary>清空：删除当前上下文内容并新建。</summary>
    [RelayCommand]
    private void ClearContext()
    {
        LlmProfile? active = _llmSettings.ActiveProfile();

        if (active is null || !_contextStore.IsEnabled)
        {
            Status = "没有可用的配置或上下文功能已关闭。";
            return;
        }

        bool confirmed = Dialog.Confirm(
            "清空上下文",
            "这会删除当前上下文的全部内容（不可恢复），然后开启一份新的。\n\n" +
            "如果你只是想换个话题但保留旧内容，请用「开启新对话」。");

        if (!confirmed)
        {
            Status = "已取消清空。";
            return;
        }

        try
        {
            string id = _contextStore.ClearCurrent(active.Id);
            RefreshContextInfo();
            Status = $"已清空并开启新上下文（{id}）。";
                LoadAiHistory();
        }
        catch (Exception ex)
        {
            LogFailure("清空上下文", ex, null);
            Status = $"清空失败：{ex.Message}";
        }
    }

    /// <summary>管理：查看/删除全部上下文（含归档）。</summary>
    /// <summary>打开 AI 设置窗口（模型 / 档位 / 上下文）。</summary>
    [RelayCommand]
    private void OpenAiSettings()
    {
        try
        {
            var window = new Views.AiSettingsWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow,
                DataContext = this
            };

            window.ShowDialog();
            RefreshAiProfiles();
        }
        catch (Exception ex)
        {
            LogFailure("打开 AI 设置", ex, null);
            Status = $"无法打开设置窗口：{ex.Message}";
        }
    }


    [RelayCommand]
    private void ManageContexts()
    {
        LlmProfile? active = _llmSettings.ActiveProfile();

        if (active is null || !_contextStore.IsEnabled)
        {
            Status = "没有可用的配置或上下文功能已关闭。";
            return;
        }

        try
        {
            var window = new Views.ContextManagerWindow(_contextStore, active.Id, Dialog)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            window.ShowDialog();
            RefreshContextInfo();
        }
        catch (Exception ex)
        {
            LogFailure("打开上下文管理", ex, null);
            Status = $"无法打开管理窗口：{ex.Message}";
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
