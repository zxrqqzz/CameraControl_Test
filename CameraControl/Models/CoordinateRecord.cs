using System;
using System.Text;

namespace CameraControl.Models
{
    /// <summary>
    /// 坐标记录(实际读取坐标 / 转换坐标)
    /// 用于将两类坐标分别保存为 txt 文件，便于数据整理与比对。
    /// 保存顺序: 可直接被机器人与示教器读取运行的直角坐标、六轴关节角优先，
    /// 其后为二维(相机)坐标，脉冲仅作监视/参考。
    /// </summary>
    public class CoordinateRecord
    {
        /// <summary>记录时间</summary>
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>直角坐标 X(mm)</summary>
        public double X { get; set; }

        /// <summary>直角坐标 Y(mm)</summary>
        public double Y { get; set; }

        /// <summary>直角坐标 Z(mm)</summary>
        public double Z { get; set; }

        /// <summary>直角坐标 Rx(°)</summary>
        public double Rx { get; set; }

        /// <summary>直角坐标 Ry(°)</summary>
        public double Ry { get; set; }

        /// <summary>直角坐标 Rz(°)</summary>
        public double Rz { get; set; }

        /// <summary>六轴关节角 S L U R B T(单位: 度)，示教器可直接运行</summary>
        public double[] Angle { get; set; } = new double[8];

        /// <summary>二维坐标 X2(mm)</summary>
        public double X2 { get; set; }

        /// <summary>二维坐标 Y2(mm)</summary>
        public double Y2 { get; set; }

        /// <summary>脉冲 S L U R B T(仅监视/参考，不用于直接运行)</summary>
        public double[] Pulse { get; set; } = new double[8];

        /// <summary>列表显示文本(优先显示可运行坐标)</summary>
        public string Display => $"{Time:HH:mm:ss}  直角: {X:F2},{Y:F2},{Z:F2}  " +
                                 $"6轴: {Angle[0]:F2}/{Angle[1]:F2}/{Angle[2]:F2}/{Angle[3]:F2}/{Angle[4]:F2}/{Angle[5]:F2}";

        /// <summary>保存到 txt 的一行数据(Tab分隔)</summary>
        public string ToFileLine()
        {
            var sb = new StringBuilder();
            sb.Append($"{Time:yyyy-MM-dd HH:mm:ss.fff}\t");
            sb.Append($"{X:F3}\t{Y:F3}\t{Z:F3}\t");
            sb.Append($"{Rx:F3}\t{Ry:F3}\t{Rz:F3}\t");
            sb.Append($"{Angle[0]:F3}\t{Angle[1]:F3}\t{Angle[2]:F3}\t{Angle[3]:F3}\t{Angle[4]:F3}\t{Angle[5]:F3}\t");
            sb.Append($"{X2:F3}\t{Y2:F3}\t");
            sb.Append($"{Pulse[0]:F0}\t{Pulse[1]:F0}\t{Pulse[2]:F0}\t{Pulse[3]:F0}\t{Pulse[4]:F0}\t{Pulse[5]:F0}");
            return sb.ToString();
        }

        /// <summary>txt 表头</summary>
        public static string FileHeader =>
            "时间\tX(mm)\tY(mm)\tZ(mm)\tRx(°)\tRy(°)\tRz(°)\t" +
            "S(°)\tL(°)\tU(°)\tR(°)\tB(°)\tT(°)\t" +
            "X2(mm)\tY2(mm)\t" +
            "PulseS\tPulseL\tPulseU\tPulseR\tPulseB\tPulseT";
    }

    /// <summary>
    /// 二维坐标记录(仅 X2/Y2)
    /// 输出格式: X：数据，Y：数据
    /// </summary>
    public class TwoDRecord
    {
        /// <summary>二维坐标 X(mm)</summary>
        public double X { get; set; }

        /// <summary>二维坐标 Y(mm)</summary>
        public double Y { get; set; }

        /// <summary>输出文本(格式: X：数据，Y：数据)</summary>
        public string Display => $"X：{X:F3}，Y：{Y:F3}";
    }

    /// <summary>
    /// 用户坐标系读取记录(含绕用户Z轴的旋转角)
    /// 输出格式: X：数据，Y：数据，Rz：数据°
    /// Rz 为用户坐标系 Z 轴(垂直于用户坐标平面的法线轴)的旋转角度，用于视觉标定旋转轴。
    /// </summary>
    public class UserCoordinateRecord
    {
        /// <summary>用户坐标 X(mm)</summary>
        public double X { get; set; }

        /// <summary>用户坐标 Y(mm)</summary>
        public double Y { get; set; }

        /// <summary>绕用户Z轴的旋转角(°)</summary>
        public double Rz { get; set; }

        /// <summary>输出文本(格式: X：数据，Y：数据，Rz：数据°)</summary>
        public string Display => $"X：{X:F3}，Y：{Y:F3}，Rz：{Rz:F3}°";
    }
}