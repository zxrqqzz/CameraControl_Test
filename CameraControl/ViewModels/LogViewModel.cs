using GalaSoft.MvvmLight.Command;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    public class LogViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<string> _logs = new ObservableCollection<string>();
        private ICommand _clearCommand;
        public ICommand ClearLogCommand => _clearCommand ??= new RelayCommand(ClearLogs);

        public ObservableCollection<string> Logs
        {
            get => _logs;
            set
            {
                _logs = value;
                OnPropertyChanged();
            }
        }

        // 添加日志（可加时间戳）
        public void AddLog(string message)
        {
            string timestamped = $"{DateTime.Now:HH:mm:ss.fff} - {message}";
            Logs.Add(timestamped);
            // 可选：自动滚动到最新（在视图中处理）
        }

        // 清空日志
        public void ClearLogs()
        {
            Logs.Clear();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}