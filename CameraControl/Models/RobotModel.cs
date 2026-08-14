using System;

namespace CameraControl.Models
{
    /// <summary>
    /// 机器人位置数据模型(存储从机器人读取的六个轴的位置/姿态信息)
    /// </summary>
    public class RobotModel
    {
        /// <summary>读取时间</summary>
        public DateTime ReadTime { get; set; } = DateTime.Now;

        /// <summary>脉冲坐标 S L U R B T(下标0~7，通常使用前6个)</summary>
        public double[] Pulse { get; set; } = new double[8];

        /// <summary>关节角度坐标 S L U R B T(单位: 度)</summary>
        public double[] Angle { get; set; } = new double[8];

        /// <summary>基座标系/直角坐标 X Y Z Rx Ry Rz(XYZ单位: mm, RxRyRz单位: 度)</summary>
        public double[] Cartesian { get; set; } = new double[8];

        /// <summary>第1轴(关节角)</summary>
        public double Ax1 => GetAngle(0);
        /// <summary>第2轴(关节角)</summary>
        public double Ax2 => GetAngle(1);
        /// <summary>第3轴(关节角)</summary>
        public double Ax3 => GetAngle(2);
        /// <summary>第4轴(关节角)</summary>
        public double Ax4 => GetAngle(3);
        /// <summary>第5轴(关节角)</summary>
        public double Ax5 => GetAngle(4);
        /// <summary>第6轴(关节角)</summary>
        public double Ax6 => GetAngle(5);

        /// <summary>直角坐标 X(mm)</summary>
        public double X => GetCartesian(0);
        /// <summary>直角坐标 Y(mm)</summary>
        public double Y => GetCartesian(1);
        /// <summary>直角坐标 Z(mm)</summary>
        public double Z => GetCartesian(2);

        private double GetAngle(int index) => Angle != null && Angle.Length > index ? Angle[index] : 0;
        private double GetCartesian(int index) => Cartesian != null && Cartesian.Length > index ? Cartesian[index] : 0;
    }
}