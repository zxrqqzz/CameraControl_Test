using GalaSoft.MvvmLight.Command;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
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

        // 添加日志（可加时间戳，支持后台线程调用）
        public void AddLog(string message)
        {
            string timestamped = $"{DateTime.Now:HH:mm:ss.fff} - {message}";
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => Logs.Add(timestamped)));
            }
            else
            {
                Logs.Add(timestamped);
            }
        }

        // 清空日志
        public void ClearLogs()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(Logs.Clear));
            }
            else
            {
                Logs.Clear();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}