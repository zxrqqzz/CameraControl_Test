using System;
using System.Text;

namespace CameraControl.Models
{
    /// <summary>
    /// 坐标记录(实际读取坐标 / 转换坐标)
    /// 用于将两类坐标分别保存为 txt 文件，便于数据整理与比对。
    /// </summary>
    public class CoordinateRecord
    {
        /// <summary>记录时间</summary>
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>二维坐标 X2(mm)</summary>
        public double X2 { get; set; }

        /// <summary>二维坐标 Y2(mm)</summary>
        public double Y2 { get; set; }

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

        /// <summary>六轴关节角 S L U R B T(单位: 度)</summary>
        public double[] Angle { get; set; } = new double[8];

        /// <summary>列表显示文本</summary>
        public string Display => $"{Time:HH:mm:ss}  X2={X2:F3}  Y2={Y2:F3}  直角: {X:F2},{Y:F2},{Z:F2}";

        /// <summary>保存到 txt 的一行数据(Tab分隔)</summary>
        public string ToFileLine()
        {
            var sb = new StringBuilder();
            sb.Append($"{Time:yyyy-MM-dd HH:mm:ss.fff}\t");
            sb.Append($"{X2:F3}\t{Y2:F3}\t");
            sb.Append($"{X:F3}\t{Y:F3}\t{Z:F3}\t");
            sb.Append($"{Rx:F3}\t{Ry:F3}\t{Rz:F3}\t");
            sb.Append($"{Angle[0]:F3}\t{Angle[1]:F3}\t{Angle[2]:F3}\t{Angle[3]:F3}\t{Angle[4]:F3}\t{Angle[5]:F3}");
            return sb.ToString();
        }

        /// <summary>txt 表头</summary>
        public static string FileHeader =>
            "时间\tX2(mm)\tY2(mm)\tX(mm)\tY(mm)\tZ(mm)\tRx(°)\tRy(°)\tRz(°)\tS(°)\tL(°)\tU(°)\tR(°)\tB(°)\tT(°)";
    }
}
