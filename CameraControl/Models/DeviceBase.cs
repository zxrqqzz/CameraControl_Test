using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CameraControl.Models
{
    public enum DeviceType
    {
        Camera,
        LightSource
    }

    public abstract class DeviceBase : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        public abstract DeviceType DeviceType { get; }

        public abstract string Display { get; }

        /// <summary>列表里第二行的小字说明(默认无)</summary>
        public virtual string SubtitleText => string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
