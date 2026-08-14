using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CameraControl.Models
{
    /// <summary>字符串触发匹配模式</summary>
    public enum TriggerMatchMode
    {
        Contains,
        Equals,
        StartsWith,
        EndsWith,
        Regex
    }

    /// <summary>
    /// 字符串触发条目(全局触发)
    /// 有序号、触发字符、匹配模式与触发配置(记录读取/转换坐标、读取机器人位置、转换为6轴坐标)。
    /// 触发命中后按配置依次执行，并通过接收设备回传数据。
    /// </summary>
    public class StringTrigger : INotifyPropertyChanged
    {
        private int _index;
        private string _triggerText = "";
        private TriggerMatchMode _matchMode = TriggerMatchMode.Contains;
        private bool _enabled = true;

        // 触发配置
        private bool _recordRead = true;
        private bool _recordConvert = true;
        private bool _readRobot = true;
        private bool _convert6Axis = true;

        /// <summary>序号</summary>
        public int Index
        {
            get => _index;
            set { _index = value; OnPropertyChanged(); }
        }

        /// <summary>触发字符</summary>
        public string TriggerText
        {
            get => _triggerText;
            set { _triggerText = value; OnPropertyChanged(); }
        }

        /// <summary>匹配模式</summary>
        public TriggerMatchMode MatchMode
        {
            get => _matchMode;
            set { _matchMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(MatchModeText)); }
        }

        /// <summary>是否启用</summary>
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); }
        }

        /// <summary>触发配置: 记录读取坐标</summary>
        public bool RecordRead
        {
            get => _recordRead;
            set { _recordRead = value; OnPropertyChanged(); }
        }

        /// <summary>触发配置: 记录转换坐标</summary>
        public bool RecordConvert
        {
            get => _recordConvert;
            set { _recordConvert = value; OnPropertyChanged(); }
        }

        /// <summary>触发配置: 读取机器人位置</summary>
        public bool ReadRobot
        {
            get => _readRobot;
            set { _readRobot = value; OnPropertyChanged(); }
        }

        /// <summary>触发配置: 转换为6轴坐标</summary>
        public bool Convert6Axis
        {
            get => _convert6Axis;
            set { _convert6Axis = value; OnPropertyChanged(); }
        }

        /// <summary>匹配模式显示文本</summary>
        public string MatchModeText => MatchMode switch
        {
            TriggerMatchMode.Contains => "包含",
            TriggerMatchMode.Equals => "等于",
            TriggerMatchMode.StartsWith => "以...开头",
            TriggerMatchMode.EndsWith => "以...结尾",
            TriggerMatchMode.Regex => "正则",
            _ => MatchMode.ToString()
        };

        /// <summary>列表显示文本</summary>
        public string Display => $"[{Index}] {TriggerText} ({MatchModeText})";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}