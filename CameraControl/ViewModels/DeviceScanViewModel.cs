using CameraControl.Models;
using CameraControl.Services;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    /// <summary>
    /// 设备扫描界面 ViewModel。
    ///
    /// 流程: 勾选扫描范围 → 开始扫描 → 结果作为候选列出(已存在设备自动禁用勾选)
    /// → 用户挑好 → "添加所选" 去重后写入设备列表。
    /// </summary>
    public class DeviceScanViewModel : INotifyPropertyChanged
    {
        private readonly DeviceScanService _scanService = new DeviceScanService();
        private CancellationTokenSource? _cts;

        private bool _isScanning;
        private string _progressText = "选择扫描范围后点击「开始扫描」。";
        private string _summaryText = string.Empty;
        private int _selectedCount;
        private bool _hasScanned;

        /// <summary>扫描候选结果</summary>
        public ObservableCollection<ScanCandidate> Candidates { get; } = new();

        /// <summary>扫描选项(与设备管理共用同一个对象，勾选状态会随设置保存)</summary>
        public ScanOptions Options => App.DeviceManagement.Scanning;

        /// <summary>正在扫描</summary>
        public bool IsScanning
        {
            get => _isScanning;
            private set
            {
                if (_isScanning == value) return;
                _isScanning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanStartScan));
                OnPropertyChanged(nameof(CanAddSelected));
            }
        }

        /// <summary>进度/状态文字</summary>
        public string ProgressText
        {
            get => _progressText;
            private set { _progressText = value; OnPropertyChanged(); }
        }

        /// <summary>结果统计文字</summary>
        public string SummaryText
        {
            get => _summaryText;
            private set { _summaryText = value; OnPropertyChanged(); }
        }

        /// <summary>已勾选数量</summary>
        public int SelectedCount
        {
            get => _selectedCount;
            private set
            {
                if (_selectedCount == value) return;
                _selectedCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAddSelected));
            }
        }

        /// <summary>是否已经扫描过(用于显示"没有找到设备"提示)</summary>
        public bool HasScanned
        {
            get => _hasScanned;
            private set { _hasScanned = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowEmptyHint)); }
        }

        /// <summary>扫完了但一个结果都没有</summary>
        public bool ShowEmptyHint => HasScanned && !IsScanning && Candidates.Count == 0;

        public bool CanStartScan => !IsScanning;

        public bool CanAddSelected => !IsScanning && SelectedCount > 0;

        // ────────────────────────────────────────────────────────────────
        // 命令
        // ────────────────────────────────────────────────────────────────

        public ICommand StartScanCommand { get; }
        public ICommand CancelScanCommand { get; }
        public ICommand SelectAllCommand { get; }
        public ICommand SelectNoneCommand { get; }
        public ICommand SelectNewOnlyCommand { get; }
        public ICommand AddSelectedCommand { get; }

        /// <summary>设备已加入列表(窗口可以据此关闭)</summary>
        public event Action<int>? DevicesAdded;

        public DeviceScanViewModel()
        {
            StartScanCommand = new RelayCommand(async () => await StartScanAsync().ConfigureAwait(true));
            CancelScanCommand = new RelayCommand(CancelScan);
            SelectAllCommand = new RelayCommand(() => SetSelection(onlyNew: false, select: true));
            SelectNoneCommand = new RelayCommand(() => SetSelection(onlyNew: false, select: false));
            SelectNewOnlyCommand = new RelayCommand(() => SetSelection(onlyNew: true, select: true));
            AddSelectedCommand = new RelayCommand(AddSelected);
        }

        // ────────────────────────────────────────────────────────────────
        // 扫描
        // ────────────────────────────────────────────────────────────────

        private async Task StartScanAsync()
        {
            if (IsScanning) return;

            if (!Options.EnableHikSdk && !Options.EnableUvc && !Options.EnableLanPassive && !Options.EnableLanOnvif)
            {
                ProgressText = "请至少勾选一个扫描范围。";
                return;
            }

            _cts = new CancellationTokenSource();
            IsScanning = true;
            HasScanned = false;
            Candidates.Clear();
            SelectedCount = 0;
            SummaryText = string.Empty;
            ProgressText = "开始扫描…";

            var progress = new Progress<string>(message => ProgressText = message);

            try
            {
                ScanReport report = await _scanService
                    .ScanAsync(Options, progress, _cts.Token)
                    .ConfigureAwait(true);

                App.DeviceManagement.MarkAlreadyAdded(report.Candidates);

                foreach (ScanCandidate candidate in report.Candidates)
                {
                    candidate.PropertyChanged += OnCandidatePropertyChanged;
                    Candidates.Add(candidate);
                }

                UpdateSelectedCount();
                HasScanned = true;

                string subnets = report.LocalSubnets.Count > 0
                    ? $"，本机网段 {string.Join("、", report.LocalSubnets)}"
                    : string.Empty;

                SummaryText = Candidates.Count == 0
                    ? $"没有发现设备{subnets}。"
                    : $"共 {Candidates.Count} 个候选设备，其中 " +
                      $"{Candidates.Count(c => c.IsAlreadyAdded)} 个已在列表中{subnets}。";

                ProgressText = $"扫描完成，用时 {report.Elapsed.TotalSeconds:F1} 秒。";

                foreach (string message in report.Messages)
                    App.LogVM.AddLog("[扫描] " + message);

                // 记住这次勾选的扫描范围
                App.Settings?.SaveAll();
            }
            catch (OperationCanceledException)
            {
                ProgressText = "扫描已取消。";
            }
            catch (Exception e)
            {
                ProgressText = "扫描失败: " + e.Message;
                App.LogVM.AddLog("[扫描] 失败: " + e.Message);
            }
            finally
            {
                IsScanning = false;
                _cts?.Dispose();
                _cts = null;
                OnPropertyChanged(nameof(ShowEmptyHint));
            }
        }

        private void CancelScan()
        {
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // ────────────────────────────────────────────────────────────────
        // 勾选
        // ────────────────────────────────────────────────────────────────

        private void SetSelection(bool onlyNew, bool select)
        {
            foreach (ScanCandidate candidate in Candidates)
            {
                if (candidate.IsAlreadyAdded) continue;
                if (onlyNew && !candidate.CanSelect) continue;
                candidate.IsSelected = select;
            }
            UpdateSelectedCount();
        }

        private void OnCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ScanCandidate.IsSelected))
                UpdateSelectedCount();
        }

        private void UpdateSelectedCount()
        {
            SelectedCount = 0;
            foreach (ScanCandidate candidate in Candidates)
                if (candidate.IsSelected) SelectedCount++;
        }

        // ────────────────────────────────────────────────────────────────
        // 添加
        // ────────────────────────────────────────────────────────────────

        private void AddSelected()
        {
            List<ScanCandidate> selected = Candidates.Where(c => c.IsSelected && c.CanSelect).ToList();
            if (selected.Count == 0)
            {
                ProgressText = "没有勾选任何设备。";
                return;
            }

            bool onlyDetected = selected.All(c => c.IsDetectedOnly);
            int added = App.DeviceManagement.AddScannedDevices(selected);

            // 加进去以后重新标记一遍，防止重复点击
            App.DeviceManagement.MarkAlreadyAdded(Candidates);
            UpdateSelectedCount();

            ProgressText = added > 0 ? $"已添加 {added} 个设备。" : "所选设备都已在列表中。";
            if (onlyDetected && added > 0)
                ProgressText += " 注意: 这些是局域网检测结果，只做登记，本程序不会连接它们。";

            DevicesAdded?.Invoke(added);
        }

        // ────────────────────────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
