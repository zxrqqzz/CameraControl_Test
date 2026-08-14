using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CameraControl.Models
{
    /// <summary>
    /// 标定点(从机器人读取的直角坐标点)
    /// </summary>
    public class CalibrationPoint : INotifyPropertyChanged
    {
        private string _name = "";
        private double _x;
        private double _y;
        private double _z;
        private bool _isRead;

        /// <summary>点名称(点1~点3)</summary>
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); } }
        }

        /// <summary>直角坐标 X(mm)</summary>
        public double X
        {
            get => _x;
            set { if (_x != value) { _x = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); } }
        }

        /// <summary>直角坐标 Y(mm)</summary>
        public double Y
        {
            get => _y;
            set { if (_y != value) { _y = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); } }
        }

        /// <summary>直角坐标 Z(mm)</summary>
        public double Z
        {
            get => _z;
            set { if (_z != value) { _z = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); } }
        }

        /// <summary>是否已从机器人读取</summary>
        public bool IsRead
        {
            get => _isRead;
            set { if (_isRead != value) { _isRead = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); } }
        }

        public string Display => IsRead
            ? $"{Name}: X={X:F3}  Y={Y:F3}  Z={Z:F3}"
            : Name;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 二维直角坐标系标定结果
    /// 由三个点确定一个平面：点1(原点)、点2(X轴上的点)、点3(Y轴上的点)。
    /// </summary>
    public class CalibrationFrame
    {
        /// <summary>原点(标定点1)</summary>
        public double OriginX, OriginY, OriginZ;

        /// <summary>X轴单位方向向量</summary>
        public double XDirX, XDirY, XDirZ;

        /// <summary>Y轴单位方向向量</summary>
        public double YDirX, YDirY, YDirZ;

        /// <summary>每个X单位对应的毫米数</summary>
        public double XScale;

        /// <summary>每个Y单位对应的毫米数</summary>
        public double YScale;

        public bool IsValid;

        /// <summary>
        /// 将三维直角坐标投影到标定的二维直角坐标系，得到二维坐标(X2, Y2)
        /// </summary>
        public (double X2, double Y2) Project(double x, double y, double z)
        {
            double tx = x - OriginX;
            double ty = y - OriginY;
            double tz = z - OriginZ;

            double x2 = (tx * XDirX + ty * XDirY + tz * XDirZ) / XScale;
            double y2 = (tx * YDirX + ty * YDirY + tz * YDirZ) / YScale;
            return (x2, y2);
        }

        /// <summary>
        /// 将二维坐标(X2, Y2)反投影到标定平面，得到三维直角坐标(X, Y, Z)
        /// </summary>
        public (double X, double Y, double Z) InverseProject(double x2, double y2)
        {
            double x = OriginX + x2 * XScale * XDirX + y2 * YScale * YDirX;
            double y = OriginY + x2 * XScale * XDirY + y2 * YScale * YDirY;
            double z = OriginZ + x2 * XScale * XDirZ + y2 * YScale * YDirZ;
            return (x, y, z);
        }

        /// <summary>转为可序列化的数据对象</summary>
        public CalibrationFrameData ToData()
        {
            return new CalibrationFrameData
            {
                Origin = new[] { OriginX, OriginY, OriginZ },
                XDirection = new[] { XDirX, XDirY, XDirZ },
                YDirection = new[] { YDirX, YDirY, YDirZ },
                XScale = XScale,
                YScale = YScale,
                IsValid = IsValid
            };
        }

        /// <summary>从序列化数据恢复坐标系</summary>
        public void FromData(CalibrationFrameData d)
        {
            if (d == null) return;
            if (d.Origin != null && d.Origin.Length >= 3)
            {
                OriginX = d.Origin[0];
                OriginY = d.Origin[1];
                OriginZ = d.Origin[2];
            }
            if (d.XDirection != null && d.XDirection.Length >= 3)
            {
                XDirX = d.XDirection[0];
                XDirY = d.XDirection[1];
                XDirZ = d.XDirection[2];
            }
            if (d.YDirection != null && d.YDirection.Length >= 3)
            {
                YDirX = d.YDirection[0];
                YDirY = d.YDirection[1];
                YDirZ = d.YDirection[2];
            }
            XScale = d.XScale;
            YScale = d.YScale;
            IsValid = d.IsValid;
        }
    }

    /// <summary>
    /// 标定坐标系取值范围/精度设置
    /// 物料板为 48cm(480mm)，坐标按 mm 级输出。
    /// </summary>
    public class CalibrationSettings
    {
        /// <summary>X 方向取值范围上限(mm)，默认 480(48cm 板)</summary>
        public double BoardWidthMm { get; set; } = 480;

        /// <summary>Y 方向取值范围上限(mm)，默认 480(48cm 板)</summary>
        public double BoardHeightMm { get; set; } = 480;

        /// <summary>坐标量化精度(mm)，默认 0.1(亚毫米)</summary>
        public double PrecisionMm { get; set; } = 0.1;
    }

    /// <summary>标定点序列化数据</summary>
    public class CalibrationPointData
    {
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    /// <summary>取值范围/精度序列化数据</summary>
    public class CalibrationSettingsData
    {
        public double BoardWidthMm { get; set; } = 480;
        public double BoardHeightMm { get; set; } = 480;
        public double PrecisionMm { get; set; } = 0.1;
    }

    /// <summary>二维坐标系序列化数据</summary>
    public class CalibrationFrameData
    {
        /// <summary>原点坐标 [X, Y, Z]</summary>
        public double[] Origin { get; set; } = new double[3];
        /// <summary>X 轴单位方向向量 [X, Y, Z]</summary>
        public double[] XDirection { get; set; } = new double[3];
        /// <summary>Y 轴单位方向向量 [X, Y, Z]</summary>
        public double[] YDirection { get; set; } = new double[3];
        /// <summary>每个 X 单位对应的毫米数</summary>
        public double XScale { get; set; }
        /// <summary>每个 Y 单位对应的毫米数</summary>
        public double YScale { get; set; }
        /// <summary>坐标系是否有效</summary>
        public bool IsValid { get; set; }
    }

    /// <summary>
    /// 标定坐标系导出/导入文件格式(JSON)
    /// 包含取值范围/精度设置、三个标定点与建立好的二维坐标系，导入后无需重复采集标定点。
    /// </summary>
    public class CalibrationExportData
    {
        /// <summary>格式版本号</summary>
        public int Version { get; set; } = 1;

        /// <summary>取值范围/精度设置</summary>
        public CalibrationSettingsData Settings { get; set; } = new CalibrationSettingsData();

        /// <summary>三个标定点</summary>
        public List<CalibrationPointData> Points { get; set; } = new List<CalibrationPointData>();

        /// <summary>建立好的二维坐标系</summary>
        public CalibrationFrameData Frame { get; set; } = new CalibrationFrameData();
    }
}