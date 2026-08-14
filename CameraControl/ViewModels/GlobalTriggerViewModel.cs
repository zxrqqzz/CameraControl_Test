using CameraControl.Models;
using CameraControl.Services;
using GalaSoft.MvvmLight.Command;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    /// <summary>
    /// 全局触发 ViewModel(添加/删除/测试字符串触发)
    /// 触发配置: 执行两个保存记录坐标按钮 → 读取机器人位置 → 转换为6轴坐标 → 回传数据。
    /// </summary>
    public class GlobalTriggerViewModel : INotifyPropertyChanged
    {
        private readonly GlobalTriggerService _service;
        private string _newTriggerText = "trigger";
        private TriggerMatchMode _newMatchMode = TriggerMatchMode.Contains;

        /// <summary>触发列表</summary>
        public ObservableCollection<StringTrigger> Triggers => _service.Triggers;

        public string NewTriggerText
        {
            get => _newTriggerText;
            set { _newTriggerText = value; OnPropertyChanged(); }
        }

        public TriggerMatchMode NewMatchMode
        {
            get => _newMatchMode;
            set { _newMatchMode = value; OnPropertyChanged(); }
        }

        /// <summary>匹配模式可选列表</summary>
        public IReadOnlyList<MatchModeItem> MatchModes { get; } = new[]
        {
            new MatchModeItem { Value = TriggerMatchMode.Contains, Display = "包含" },
            new MatchModeItem { Value = TriggerMatchMode.Equals, Display = "等于" },
            new MatchModeItem { Value = TriggerMatchMode.StartsWith, Display = "以...开头" },
            new MatchModeItem { Value = TriggerMatchMode.EndsWith, Display = "以...结尾" },
            new MatchModeItem { Value = TriggerMatchMode.Regex, Display = "正则" }
        };

        public ICommand AddTriggerCommand { get; }
        public ICommand RemoveTriggerCommand { get; }
        public ICommand TestTriggerCommand { get; }

        public GlobalTriggerViewModel(GlobalTriggerService service)
        {
            _service = service;
            AddTriggerCommand = new RelayCommand(AddTrigger);
            RemoveTriggerCommand = new RelayCommand<StringTrigger>(RemoveTrigger);
            TestTriggerCommand = new RelayCommand<StringTrigger>(TestTrigger);
        }

        private void AddTrigger()
        {
            string text = NewTriggerText;
            if (string.IsNullOrWhiteSpace(text))
            {
                App.LogVM.AddLog("触发字符不能为空。");
                return;
            }
            _service.AddTrigger(text, NewMatchMode);
        }

        private void RemoveTrigger(StringTrigger trigger) => _service.RemoveTrigger(trigger);

        private async void TestTrigger(StringTrigger trigger)
        {
            await _service.TestAsync(trigger);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>匹配模式下拉选项</summary>
    public class MatchModeItem
    {
        public TriggerMatchMode Value { get; set; }
        public string Display { get; set; } = "";
    }
}