using CameraControl.Models;
using CameraControl.Services;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    public class CameraParameterViewModel : INotifyPropertyChanged
    {
        private readonly CameraParameterService _service;

        // 分类侧边栏
        public ObservableCollection<CategoryItem> Categories { get; } = new();

        // 全部参数按分类存储
        private readonly System.Collections.Generic.Dictionary<string, ObservableCollection<CameraParameter>> _byCategory = new();

        private string _selectedCategory = string.Empty;
        public string SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                _selectedCategory = value;
                foreach (var c in Categories) c.SetSelected(string.Equals(c.Name, value));
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentParameters));
            }
        }

        public ObservableCollection<CameraParameter> CurrentParameters
        {
            get
            {
                if (_byCategory.TryGetValue(SelectedCategory, out var list))
                    return list;
                return new ObservableCollection<CameraParameter>();
            }
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public ICommand RefreshCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand SelectCategoryCommand { get; }

        public CameraParameterViewModel(CameraParameterService service)
        {
            _service = service;
            RefreshCommand = new RelayCommand(LoadParameters);
            ApplyCommand = new RelayCommand<CameraParameter>(ApplyParameter);
            SelectCategoryCommand = new RelayCommand<string>(SelectCategory);

            var catalog = _service.BuildCatalog();
            foreach (var (category, params_) in catalog)
            {
                Categories.Add(new CategoryItem(category, () => SelectedCategory = category));
                _byCategory[category] = new ObservableCollection<CameraParameter>(params_);
            }
            if (Categories.Count > 0)
                SelectedCategory = Categories[0].Name;
        }

        /// <summary>从 SDK 刷新读取全部参数值</summary>
        public void LoadParameters()
        {
            if (!_service.IsAvailable)
            {
                StatusText = "相机未连接，无法读取参数。";
                foreach (var coll in _byCategory.Values)
                {
                    foreach (var p in coll) p.IsReadOnly = true;
                }
                return;
            }

            StatusText = "正在读取参数...";
            int ok = 0, fail = 0;
            foreach (var (category, coll) in _byCategory)
            {
                foreach (var p in coll)
                {
                    // 命令节点视为可访问
                    p.IsReadOnly = p.ParamType == CameraParamType.Command;
                    if (_service.ReadParameter(p))
                        ok++;
                    else
                    {
                        // 该节点可能不存在或不支持，标记只读
                        p.IsReadOnly = true;
                        fail++;
                    }
                }
            }
            OnPropertyChanged(nameof(CurrentParameters));
            StatusText = fail == 0
                ? $"参数已加载 ({ok} 项)。"
                : $"参数已加载 ({ok} 项可读, {fail} 项不可用)。";
        }

        private void ApplyParameter(CameraParameter p)
        {
            if (p.IsReadOnly) return;

            var err = _service.WriteParameter(p);
            if (err == null)
            {
                App.LogVM.AddLog($"参数已设置: {p.DisplayName} ({p.Key})");
                // 重新读取以确认
                _service.ReadParameter(p);
                StatusText = $"已设置 {p.DisplayName}";
            }
            else
            {
                App.LogVM.AddLog($"设置参数失败 {p.DisplayName}: {err}");
                StatusText = $"设置 {p.DisplayName} 失败: {err}";
            }
        }

        /// <summary>点击分类侧边栏选择分类</summary>
        public void SelectCategory(string? category)
        {
            if (!string.IsNullOrEmpty(category))
                SelectedCategory = category;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>分类侧边栏项(绑定选中状态)</summary>
    public class CategoryItem : INotifyPropertyChanged
    {
        private readonly Action _select;
        private bool _isSelected;

        public string Name { get; }
        public System.Windows.Input.ICommand SelectCommand { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }

        public CategoryItem(string name, Action select)
        {
            Name = name;
            _select = select;
            SelectCommand = new GalaSoft.MvvmLight.Command.RelayCommand(_select);
        }

        public void SetSelected(bool selected) => IsSelected = selected;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
