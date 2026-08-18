using CameraControl.Models;
using System;
using System.Threading.Tasks;
using YMConnect;

namespace CameraControl.Services
{
    /// <summary>
    /// 安川机器人通信服务(封装 YMConnect_CS.dll)
    /// 提供连接/断开、读取六个轴位置信息等功能。
    /// </summary>
    public class RobotService : IDisposable
    {
        private const int ReceiveTimeoutMs = 3000;
        private const int SendRetries = 3;
        private const int ConnectTimeoutMs = 5000;
        private const int ReadTimeoutMs = 10000;

        private readonly object _sync = new object();
        private MotomanController? _controller;
        private string _ip = "";

        /// <summary>是否已连接机器人</summary>
        public bool IsConnected => _controller != null;

        /// <summary>已连接的机器人IP</summary>
        public string Ip => _ip;

        /// <summary>
        /// 连接机器人
        /// </summary>
        public bool Connect(string ip)
        {
            lock (_sync)
            {
                Dispose();
                App.LogVM.AddLog($"正在连接机器人 {ip} ...");

                StatusInfo status;
                var controller = MotomanController.OpenConnection(ip, out status,
                    ReceiveTimeoutMs, SendRetries, false);

// 注意: YMConnect_CS 1.1.10 的 StatusInfo.IsOk() 判断方向有误(实现为 StatusCode != 0)，
                // 必须以 StatusCode == 0 作为成功判据。
                if (controller == null || status.StatusCode != 0)
                {
                    try { controller?.CloseConnection(); } catch { /* 忽略关闭异常 */ }
                    string msg = $"连接机器人失败: Code={status.StatusCode}, {status.Message}。\r\n" +
                                 "请检查: ①机器人IP是否正确; ②机器人YCP通讯服务/端口是否开启; ③电脑与机器人网络是否连通; ④防火墙是否拦截(建议放行安川相关端口)。";
                    App.LogVM.AddLog(msg);
                    throw new InvalidOperationException(msg);
                }

                _controller = controller;
                _ip = ip;
                App.LogVM.AddLog($"机器人 {ip} 连接成功");
                return true;
            }
        }
        //阅读项目，修改一下标定二维坐标中的取值范围，由于

        /// <summary>
        /// 读取机器人位置信息(脉冲/六轴关节角/直角坐标)
        /// </summary>
        public RobotModel ReadPosition(ControlGroupId group = ControlGroupId.R1)
        {
            lock (_sync)
            {
                EnsureConnected();
                var model = new RobotModel { ReadTime = DateTime.Now };
                ReadAxis(group, CoordinateType.Pulse, model.Pulse, "脉冲");
                ReadAxis(group, CoordinateType.Angle, model.Angle, "关节角度");
                ReadAxis(group, CoordinateType.BaseCoordinate, model.Cartesian, "基座标");
                return model;
            }
        }

        /// <summary>后台线程连接机器人，带超时保护，不阻塞UI</summary>
        public Task<bool> ConnectAsync(string ip) =>
            RunWithTimeoutAsync(() => Connect(ip), "连接机器人", ConnectTimeoutMs);

        /// <summary>后台线程读取机器人位置，带超时保护，不阻塞UI</summary>
        public Task<RobotModel> ReadPositionAsync(ControlGroupId group = ControlGroupId.R1) =>
            RunWithTimeoutAsync(() => ReadPosition(group), "读取坐标", ReadTimeoutMs);

        /// <summary>
        /// 读取机器人位置信息(脉冲/六轴关节角/用户直角坐标系坐标)。
        /// 直接使用示教器中已建立好的用户坐标系(UF)，读取到的 X/Y 即为二维坐标，
        /// 无需再通过三点手动标定，减少中间环节与累积误差。
        /// </summary>
        /// <param name="userFrameNumber">用户坐标系编号(1~63)</param>
        /// <param name="group">控制组</param>
        public RobotModel ReadPositionInUserFrame(byte userFrameNumber, ControlGroupId group = ControlGroupId.R1)
        {
            lock (_sync)
            {
                EnsureConnected();
                if (userFrameNumber < 1 || userFrameNumber > 63)
                    throw new ArgumentOutOfRangeException(nameof(userFrameNumber), "用户坐标系编号应为1~63");

                var model = new RobotModel { ReadTime = DateTime.Now };
                ReadAxis(group, CoordinateType.Pulse, model.Pulse, "脉冲");
                ReadAxis(group, CoordinateType.Angle, model.Angle, "关节角度");
                ReadAxis(group, CoordinateType.UserCoordinate, userFrameNumber, 0, model.Cartesian, "用户坐标系");
                return model;
            }
        }

        /// <summary>后台线程读取用户坐标系位置，带超时保护，不阻塞UI</summary>
        public Task<RobotModel> ReadPositionInUserFrameAsync(byte userFrameNumber, ControlGroupId group = ControlGroupId.R1) =>
            RunWithTimeoutAsync(() => ReadPositionInUserFrame(userFrameNumber, group), "读取用户坐标", ReadTimeoutMs);

        /// <summary>
        /// 将直角坐标(X,Y,Z,Rx,Ry,Rz)转换为六轴关节角(度)。
        /// 用于二维坐标反算6轴坐标，带超时保护，不阻塞UI。
        /// </summary>
        public Task<RobotModel> ConvertCartesianToJointAsync(double x, double y, double z, double rx, double ry, double rz) =>
            RunWithTimeoutAsync(() => ConvertCartesianToJoint(x, y, z, rx, ry, rz), "坐标转换", ReadTimeoutMs);

        /// <summary>
        /// 将直角坐标转换为六轴关节角(度)。
        /// 使用当前机器人关节角作为逆解参考姿态，避免多解/奇异点问题。
        /// </summary>
        private RobotModel ConvertCartesianToJoint(double x, double y, double z, double rx, double ry, double rz)
        {
            lock (_sync)
            {
                EnsureConnected();

                // 读取当前关节角作为逆解参考姿态
                var prevAngle = new double[8];
                ReadAxis(ControlGroupId.R1, CoordinateType.Angle, prevAngle, "参考关节角");

                var toConvert = new PositionData
                {
                    // 注意: 运动学逆解要求输入为 RobotCoordinate，BaseCoordinate 会报错
                    CoordinateType = CoordinateType.RobotCoordinate,
                    AxisData = new double[] { x, y, z, rx, ry, rz, 0, 0 }
                };

                PositionData converted;
                StatusInfo status = _controller!.Kinematics.ConvertPositionFromCartesian(
                    ControlGroupId.R1, toConvert, KinematicConversions.CartesianPosToJointAngle,
                    KinematicType.Default, prevAngle, out converted);

                if (status.StatusCode != 0)
                    throw new InvalidOperationException($"坐标转换失败: Code={status.StatusCode}, {status.Message}");

                var model = new RobotModel { ReadTime = DateTime.Now };
                if (converted.AxisData != null)
                    Array.Copy(converted.AxisData, model.Angle, Math.Min(converted.AxisData.Length, model.Angle.Length));

                model.Cartesian[0] = x;
                model.Cartesian[1] = y;
                model.Cartesian[2] = z;
                model.Cartesian[3] = rx;
                model.Cartesian[4] = ry;
                model.Cartesian[5] = rz;
                return model;
            }
        }

        /// <summary>
        /// 在后台线程执行机器人操作并带超时保护。
        /// 超时后抛出 TimeoutException，不再等待被放弃的任务。
        /// </summary>
        private static async Task<T> RunWithTimeoutAsync<T>(Func<T> action, string opName, int timeoutMs)
        {
            var task = Task.Run(action);
            var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (done != task)
            {
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException($"{opName}超时({timeoutMs / 1000}秒)：设备无响应，请检查机器人连接/服务/端口及防火墙。");
            }
            return await task;
        }

        /// <summary>
        /// 按指定坐标系读取轴数据到数组(默认索引0)
        /// </summary>
        private void ReadAxis(ControlGroupId group, CoordinateType coord, double[] target, string label) =>
            ReadAxis(group, coord, 0, 0, target, label);

        /// <summary>
        /// 按指定坐标系读取轴数据到数组
        /// 用户坐标系(UserCoordinate)时 index=用户坐标系编号(UF1~UF63)，specifier=工具号。
        /// </summary>
        private void ReadAxis(ControlGroupId group, CoordinateType coord, byte index, byte specifier, double[] target, string label)
        {
            try
            {
                StatusInfo status;
                PositionData pos;
                status = _controller!.ControlGroup.ReadPositionData(group, coord, index, specifier, out pos);
                if (status.StatusCode != 0)
                {
                    App.LogVM.AddLog($"读取{label}坐标失败: Code={status.StatusCode}, {status.Message}");
                    return;
                }
                if (pos.AxisData != null)
                {
                    for (int i = 0; i < pos.AxisData.Length && i < target.Length; i++)
                        target[i] = pos.AxisData[i];
                }
            }
            catch (Exception e)
            {
                App.LogVM.AddLog($"读取{label}坐标异常: {e.Message}");
            }
        }

        /// <summary>
        /// 断开连接，释放资源
        /// </summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_controller != null)
                {
                    try { _controller.CloseConnection(); }
                    catch { /* 忽略关闭异常 */ }
                    _controller = null;
                }
            }
        }

        private void EnsureConnected()
        {
            if (_controller == null)
                throw new InvalidOperationException("未连接到机器人，请先点击“连接机器人”");
        }
    }
}