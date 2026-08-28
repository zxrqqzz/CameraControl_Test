using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CameraControl.Models
{
    public enum CameraParamType
    {
        Int,
        Float,
        Enum,
        Bool,
        String,
        Command
    }

    /// <summary>
    /// 单一相机参数节点。包含类型、键名、显示名称、单位、范围、枚举项及当前值，
    /// 通过 CameraParameterService 从 SDK 载入/写入。
    /// </summary>
    public class CameraParameter : INotifyPropertyChanged
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Category { get; set; } = "其他";
        public CameraParamType ParamType { get; set; }
        public string Unit { get; set; } = string.Empty;

        // Int / Float 范围
        public double Min { get; set; }
        public double Max { get; set; }
        public double Step { get; set; }

        // 枚举项 (显示名 -> SDK symbolic)
        public System.Collections.Generic.List<(string Display, string Symbolic)> EnumItems { get; } = new();

        private double _numericValue;
        public double NumericValue
        {
            get => _numericValue;
            set { _numericValue = value; OnPropertyChanged(); }
        }

        private string _stringValue = string.Empty;
        public string StringValue
        {
            get => _stringValue;
            set { _stringValue = value; OnPropertyChanged(); }
        }

        private bool _boolValue;
        public bool BoolValue
        {
            get => _boolValue;
            set { _boolValue = value; OnPropertyChanged(); }
        }

        private string _selectedEnum = string.Empty;
        public string SelectedEnum
        {
            get => _selectedEnum;
            set { _selectedEnum = value; OnPropertyChanged(); }
        }

        /// <summary>是否只读(节点当前不可写)</summary>
        private bool _isReadOnly;
        public bool IsReadOnly
        {
            get => _isReadOnly;
            set { _isReadOnly = value; OnPropertyChanged(); OnPropertyChanged(nameof(NumericVisibility)); OnPropertyChanged(nameof(EnumVisibility)); OnPropertyChanged(nameof(BoolVisibility)); OnPropertyChanged(nameof(StringVisibility)); OnPropertyChanged(nameof(CommandVisibility)); }
        }

        // 根据参数类型决定显示哪个值控件
        public Visibility NumericVisibility =>
            (ParamType == CameraParamType.Int || ParamType == CameraParamType.Float) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EnumVisibility =>
            ParamType == CameraParamType.Enum ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BoolVisibility =>
            ParamType == CameraParamType.Bool ? Visibility.Visible : Visibility.Collapsed;
        public Visibility StringVisibility =>
            ParamType == CameraParamType.String ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CommandVisibility =>
            ParamType == CameraParamType.Command ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>通知指定属性变化(供服务层调用)</summary>
        public void Notify(string? name = null) => OnPropertyChanged(name);
    }
}
