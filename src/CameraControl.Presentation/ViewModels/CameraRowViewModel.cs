using CameraControl.Domain.Cameras;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CameraControl.Presentation.ViewModels;

/// <summary>相机列表的一行。</summary>
public sealed partial class CameraRowViewModel : ObservableObject
{
    public CameraRowViewModel(CameraDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public CameraDescriptor Descriptor { get; }

    public string Id => Descriptor.Id.Value;

    public string DisplayName => Descriptor.DisplayName;

    public string KindText => Descriptor.Kind switch
    {
        CameraKind.HikGigE => "海康 GigE",
        CameraKind.HikUsb3 => "海康 USB3",
        CameraKind.Uvc => "UVC / 内置",
        _ => Descriptor.Kind.ToString()
    };

    public bool IsInfrared => Descriptor.IsInfraredOnly;

    public bool CanCapture => Descriptor.CanCapture;

    /// <summary>是否可用（红外设备只列出，不允许取流）。</summary>
    public string Hint => IsInfrared ? "仅列出，不取流（隐私）" : string.Empty;

    [ObservableProperty]
    private bool _isOpen;
}
