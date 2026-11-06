namespace CameraControl.Domain.Cameras;

/// <summary>
/// 相机的稳定标识。
/// 由适配器保证同一物理设备每次枚举得到相同的值（序列号 / 设备实例路径 / MAC）。
/// </summary>
public readonly record struct CameraId(string Value)
{
    public static CameraId Empty => new(string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value ?? string.Empty;
}
