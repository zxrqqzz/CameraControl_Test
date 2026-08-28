using CameraControl.Models;
using MvCameraControl;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CameraControl.Services
{
    /// <summary>
    /// 相机参数目录与 SDK 读写封装。
    /// 基于海康 MVS 标准 GenICam 参数节点按分类组织，覆盖曝光、增益、图像格式、
    /// 白平衡、触发、采集、模拟控制、数字IO、传输等尽可能全的常用参数。
    /// 通过 IParameters 动态读写，支持 Float/Int/Enum/Bool/String/Command 各类节点。
    /// </summary>
    public class CameraParameterService
    {
        private readonly Func<IParameters?> _getParameters;

        public CameraParameterService(Func<IParameters?> getParameters)
        {
            _getParameters = getParameters;
        }

        public bool IsAvailable => _getParameters() != null;

        /// <summary>
        /// 按分类组织的参数目录。Key 为 SDK 节点名。
        /// </summary>
        public List<(string Category, List<CameraParameter> Params)> BuildCatalog()
        {
            var list = new List<(string, List<CameraParameter>)>();

            // 各类参数的目录定义
            AddCategory(list, "曝光", new[]
            {
                Param("ExposureAuto", "自动曝光", CameraParamType.Enum),
                Param("ExposureMode", "曝光模式", CameraParamType.Enum),
                Param("ExposureTime", "曝光时间", CameraParamType.Float, "μs"),
                Param("ExposureTimeAbs", "曝光时间(Abs)", CameraParamType.Float, "μs"),
                Param("ExposureTimeRaw", "曝光时间(原始)", CameraParamType.Int),
                Param("AutoExposureTimeLowerLimit", "自动曝光下限", CameraParamType.Float, "μs"),
                Param("AutoExposureTimeUpperLimit", "自动曝光上限", CameraParamType.Float, "μs"),
                Param("ExposureDelay", "曝光延时", CameraParamType.Float, "μs"),
            });

            AddCategory(list, "增益", new[]
            {
                Param("GainAuto", "自动增益", CameraParamType.Enum),
                Param("Gain", "增益", CameraParamType.Float, "dB"),
                Param("GainRaw", "增益(原始)", CameraParamType.Int),
                Param("AutoGainLowerLimit", "自动增益下限", CameraParamType.Float, "dB"),
                Param("AutoGainUpperLimit", "自动增益上限", CameraParamType.Float, "dB"),
                Param("BlackLevel", "黑电平", CameraParamType.Int),
                Param("BlackLevelRaw", "黑电平(原始)", CameraParamType.Int),
                Param("DigitalShift", "数字增益位移", CameraParamType.Int),
            });

            AddCategory(list, "白平衡", new[]
            {
                Param("BalanceWhiteAuto", "自动白平衡", CameraParamType.Enum),
                Param("BalanceRatio", "白平衡比例", CameraParamType.Float),
                Param("BalanceRatioSelector", "白平衡通道选择", CameraParamType.Enum),
                Param("BalanceRatioRed", "红通道比例", CameraParamType.Float),
                Param("BalanceRatioGreen", "绿通道比例", CameraParamType.Float),
                Param("BalanceRatioBlue", "蓝通道比例", CameraParamType.Float),
            });

            AddCategory(list, "图像格式", new[]
            {
                Param("Width", "宽度", CameraParamType.Int, "px"),
                Param("Height", "高度", CameraParamType.Int, "px"),
                Param("OffsetX", "水平偏移X", CameraParamType.Int, "px"),
                Param("OffsetY", "垂直偏移Y", CameraParamType.Int, "px"),
                Param("PixelFormat", "像素格式", CameraParamType.Enum),
                Param("PixelColorFilter", "像素颜色滤波", CameraParamType.Enum),
                Param("ReverseX", "水平镜像", CameraParamType.Bool),
                Param("ReverseY", "垂直镜像", CameraParamType.Bool),
                Param("SensorWidth", "传感器宽度", CameraParamType.Int, "px"),
                Param("SensorHeight", "传感器高度", CameraParamType.Int, "px"),
                Param("WidthMax", "最大宽度", CameraParamType.Int, "px"),
                Param("HeightMax", "最大高度", CameraParamType.Int, "px"),
                Param("ImageSize", "图像大小", CameraParamType.Int, "Bytes"),
                Param("PayloadSize", "数据负载大小", CameraParamType.Int, "Bytes"),
            });

            AddCategory(list, "触发", new[]
            {
                Param("TriggerMode", "触发模式", CameraParamType.Enum),
                Param("TriggerSource", "触发源", CameraParamType.Enum),
                Param("TriggerActivation", "触发激活方式", CameraParamType.Enum),
                Param("TriggerSelector", "触发选择器", CameraParamType.Enum),
                Param("TriggerDelay", "触发延时", CameraParamType.Float, "μs"),
                Param("TriggerSoftware", "软件触发", CameraParamType.Command),
                Param("TriggerOverlap", "触发重叠", CameraParamType.Enum),
                Param("TriggerOffDelay", "触发关闭延时", CameraParamType.Float, "μs"),
                Param("AcquisitionMode", "采集模式", CameraParamType.Enum),
                Param("AcquisitionFrameRate", "采集帧率", CameraParamType.Float, "fps"),
                Param("AcquisitionFrameRateEnable", "帧率使能", CameraParamType.Bool),
                Param("AcquisitionFrameRateAuto", "帧率自动", CameraParamType.Enum),
                Param("FrameStartTriggerMode", "帧开始触发模式", CameraParamType.Enum),
            });

            AddCategory(list, "采集控制", new[]
            {
                Param("AcquisitionStart", "开始采集", CameraParamType.Command),
                Param("AcquisitionStop", "停止采集", CameraParamType.Command),
                Param("DeviceLinkThroughputLimit", "设备链路吞吐量限制", CameraParamType.Int, "Bps"),
                Param("DeviceLinkThroughputLimitMode", "吞吐量限制模式", CameraParamType.Enum),
                Param("DeviceLinkHeartbeatTimeout", "链路心跳超时", CameraParamType.Int, "ms"),
                Param("TransferSize", "传输尺寸", CameraParamType.Int),
                Param("TransferBlockCount", "传输块数", CameraParamType.Int),
                Param("GevSCPSPacketSize", "网络包大小", CameraParamType.Int, "Bytes"),
                Param("GevSCPD", "网络包发送延时", CameraParamType.Int, "ns"),
                Param("GevCurrentIPConfigurationFailSafe", "IP配置失败安全", CameraParamType.Bool),
            });

            AddCategory(list, "模拟控制", new[]
            {
                Param("AnalogGain", "模拟增益", CameraParamType.Float, "dB"),
                Param("AnalogBlackLevel", "模拟黑电平", CameraParamType.Int),
                Param("Gamma", "伽马", CameraParamType.Float),
                Param("GammaEnable", "伽马使能", CameraParamType.Bool),
                Param("GammaSelector", "伽马选择器", CameraParamType.Enum),
                Param("Sharpness", "锐度", CameraParamType.Int),
                Param("SharpnessEnable", "锐度使能", CameraParamType.Bool),
                Param("Saturation", "饱和度", CameraParamType.Int),
                Param("Hue", "色相", CameraParamType.Int),
                Param("Contrast", "对比度", CameraParamType.Int),
                Param("NoiseReduction", "降噪", CameraParamType.Int),
            });

            AddCategory(list, "数字IO", new[]
            {
                Param("LineSelector", "IO线选择", CameraParamType.Enum),
                Param("LineMode", "IO线模式", CameraParamType.Enum),
                Param("LineSource", "IO线信号源", CameraParamType.Enum),
                Param("LineInverter", "IO线反相", CameraParamType.Bool),
                Param("LineStatus", "IO线状态", CameraParamType.Bool),
                Param("LineStatusAll", "IO线状态(全部)", CameraParamType.Enum),
                Param("StrobeEnable", "闪光灯使能", CameraParamType.Bool),
                Param("UserOutputSelector", "用户输出选择", CameraParamType.Enum),
                Param("UserOutputValue", "用户输出值", CameraParamType.Bool),
            });

            AddCategory(list, "设备信息", new[]
            {
                Param("DeviceVendorName", "设备厂商", CameraParamType.String),
                Param("DeviceModelName", "设备型号", CameraParamType.String),
                Param("DeviceManufacturerInfo", "制造商信息", CameraParamType.String),
                Param("DeviceVersion", "固件版本", CameraParamType.String),
                Param("DeviceSerialNumber", "设备序列号", CameraParamType.String),
                Param("DeviceUserID", "设备用户ID", CameraParamType.String),
                Param("DeviceScanType", "扫描类型", CameraParamType.Enum),
                Param("DeviceLinkSelector", "链路选择", CameraParamType.Int),
                Param("DeviceReset", "设备复位", CameraParamType.Command),
                Param("DeviceTemperatureSelector", "温度传感器", CameraParamType.Enum),
                Param("DeviceTemperature", "设备温度", CameraParamType.Float, "℃"),
                Param("DeviceTLType", "传输层类型", CameraParamType.Enum),
                Param("DeviceIPAddress", "设备IP地址", CameraParamType.String),
            });

            AddCategory(list, "其他", new[]
            {
                Param("UserSetSelector", "用户参数组", CameraParamType.Enum),
                Param("UserSetLoad", "载入用户参数", CameraParamType.Command),
                Param("UserSetSave", "保存用户参数", CameraParamType.Command),
                Param("UserSetDefault", "默认用户参数组", CameraParamType.Enum),
                Param("TimestampControl", "时间戳使能", CameraParamType.Bool),
                Param("TimestampLatch", "时间戳锁定", CameraParamType.Command),
                Param("TimerSelector", "定时器选择", CameraParamType.Enum),
                Param("TimerDuration", "定时器时长", CameraParamType.Float, "μs"),
                Param("TimerDelay", "定时器延时", CameraParamType.Float, "μs"),
                Param("PwmSelector", "PWM选择", CameraParamType.Enum),
                Param("PwmFrequency", "PWM频率", CameraParamType.Float, "Hz"),
                Param("PwmDutyRatio", "PWM占空比", CameraParamType.Float, "%"),
            });

            return list;
        }

        private static void AddCategory(List<(string, List<CameraParameter>)> list, string category, CameraParameter[] params_)
        {
            foreach (var p in params_)
            {
                if (string.IsNullOrEmpty(p.Category)) p.Category = category;
            }
            list.Add((category, params_.ToList()));
        }

        private static CameraParameter Param(string key, string name, CameraParamType type, string unit = "") => new()
        {
            Key = key,
            DisplayName = name,
            ParamType = type,
            Unit = unit
        };

        /// <summary>
        /// 从 SDK 读取单个参数节点的当前值、范围与枚举项。
        /// 返回 false 表示该节点当前不可访问。
        /// </summary>
        public bool ReadParameter(CameraParameter p)
        {
            var par = _getParameters();
            if (par == null) return false;

            try
            {
                switch (p.ParamType)
                {
                    case CameraParamType.Float:
                        if (par.GetFloatValue(p.Key, out var f) != MvError.MV_OK) return false;
                        p.Min = f.Min;
                        p.Max = f.Max;
                        p.NumericValue = f.CurValue;
                        p.Step = 1;
                        return true;
                    case CameraParamType.Int:
                        if (par.GetIntValue(p.Key, out var iv) != MvError.MV_OK) return false;
                        p.Min = iv.Min;
                        p.Max = iv.Max;
                        p.Step = iv.Inc > 0 ? iv.Inc : 1;
                        p.NumericValue = iv.CurValue;
                        return true;
                    case CameraParamType.Enum:
                        if (par.GetEnumValue(p.Key, out var ev) != MvError.MV_OK) return false;
                        p.EnumItems.Clear();
                        foreach (var entry in ev.SupportEnumEntries ?? Enumerable.Empty<IEnumEntry>())
                        {
                            p.EnumItems.Add((entry.Symbolic, entry.Symbolic));
                        }
                        p.SelectedEnum = ev.CurEnumEntry?.Symbolic ?? string.Empty;
                        p.Notify(nameof(CameraParameter.EnumItems));
                        return true;
                    case CameraParamType.Bool:
                        if (par.GetBoolValue(p.Key, out var bv) != MvError.MV_OK) return false;
                        p.BoolValue = bv;
                        return true;
                    case CameraParamType.String:
                        if (par.GetStringValue(p.Key, out var sv) != MvError.MV_OK) return false;
                        p.StringValue = sv.CurValue ?? string.Empty;
                        return true;
                    case CameraParamType.Command:
                        // 命令节点无当前值，视为可访问
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>写入单个参数节点值。返回错误描述，null 表示成功。</summary>
        public string? WriteParameter(CameraParameter p)
        {
            var par = _getParameters();
            if (par == null) return "相机未连接";

            try
            {
                int ret;
                switch (p.ParamType)
                {
                    case CameraParamType.Float:
                        ret = par.SetFloatValue(p.Key, (float)p.NumericValue);
                        return ret == MvError.MV_OK ? null : $"0x{ret:X}";
                    case CameraParamType.Int:
                        ret = par.SetIntValue(p.Key, (long)Math.Round(p.NumericValue));
                        return ret == MvError.MV_OK ? null : $"0x{ret:X}";
                    case CameraParamType.Enum:
                        ret = par.SetEnumValueByString(p.Key, p.SelectedEnum);
                        return ret == MvError.MV_OK ? null : $"0x{ret:X}";
                    case CameraParamType.Bool:
                        ret = par.SetBoolValue(p.Key, p.BoolValue);
                        return ret == MvError.MV_OK ? null : $"0x{ret:X}";
                    case CameraParamType.String:
                        ret = par.SetStringValue(p.Key, p.StringValue);
                        return ret == MvError.MV_OK ? null : $"0x{ret:X}";
                    case CameraParamType.Command:
                        ret = par.SetCommandValue(p.Key);
                        return ret == MvError.MV_OK ? null : $"0x{ret:X}";
                    default:
                        return "不支持的参数类型";
                }
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
    }
}
