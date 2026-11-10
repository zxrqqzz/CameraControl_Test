# 10 · YMConnect 机器人 SDK 勘察

> 目的：在不加载、不执行目标库的前提下，摸清 YMConnect（Yaskawa Motoman 控制器通讯库）
> 的真实 API 面，为 `IRobot` 适配器提供依据。
> 方法：REA 静态元数据检查（managed 元数据 + PE 导出表），**全程未执行任何目标代码**。
> 状态：**API 面已摸清；适配器尚未实现**（见"还差什么"）。

---

## 一、构件身份

| 文件 | 类型 | 架构 | SHA-256 |
|---|---|---|---|
| `YMConnect_CS.dll` | 托管（.NET 8，IL-only） | AnyCPU（PE 头 x86，不影响） | `e4f529d4…1d4703` |
| `YMConnect.dll` | **原生 C++** | **amd64**（64 位，与我们的 x64 目标一致 ✓） | `88a05606…e83313` |

托管库统计：**194 个类型 / 982 个方法 / 478 个字段 / 1868 条调用边**；程序集版本 1.1.10.0。

> 原生库是 **amd64**，这一点很关键：说明本项目 `PlatformTarget=x64` 与之匹配，
> 不存在"托管库能引用但运行时加载不了原生库"的陷阱。

---

## 二、托管 API 结构

### 2.1 枚举（配置与选项）

`ControlGroupId`、`SystemInfoId`、`AlarmCategory`、`CoordinateType`、`ControlMode`、
`MoveInterpolationType`、`SpeedType`、`SignalStatus`、`KinematicType`、`IOType`、
`CycleMode`、`FileType`、`TimeType`、`FrontOrBack`、`UpperOrLower`、`FlipOrNoFlip`、`AxisAngle`

### 2.2 数据类（返回结构）

`PositionData`（位置）、`PositionErrorData`、`TorqueData`、`ControllerStateData`、
`AlarmData` / `ActiveAlarms` / `AlarmHistory`、`EulerMatrix`、`XyzVector`、`Figure`、
`JobData`、`IOData`（及 Byte/Word/Register 变体）、各 `*VariableData`、`StatusInfo`

### 2.3 接口分离（按功能域）

`ControlGroupInterface`、`MotionManagerInterface`、`ControlCommandsInterface`、
`FaultInterface`、`ControllerStateInterface`、`IOInterface`、`JobInterface`、`FileInterface`、
`KinematicsInterface`、各 `*VariableInterface`

### 2.4 主类（**这是适配器要用的入口**）

```
YMConnect.MotomanController                     （抽象基类，23 个方法）
  └─ YMConnect.Implementation.Controller        （具体实现，22 个方法）
```

已验证的成员（来自元数据签名）：

| 成员 | 签名 | 说明 |
|---|---|---|
| `OpenConnection` | `static (string, ref <info>, int, int, bool) → <controller>` | 静态工厂，**建连就靠它** |
| `CloseConnection` | `void ()` | 断开 |
| `.ctor` | `()` | 构造 |
| `Faults` | 属性 → 报警接口 | |
| `ControlGroup` | 属性 → 位置/轴配置/力矩读取 | |
| `Variables` | 属性 → 变量读写 | |
| `Status` | 属性 → 控制器状态 | |
| `Job` | 属性 → 作业管理 | |
| `Files` | 属性 → 文件读写 | |
| `IO` | 属性 → IO 读写 | |
| `ControlCommands` | 属性 → 伺服/保持/作业启动 | |
| `MotionManager` | 属性 → **轨迹与运动** | |
| `Kinematics` | 属性 → 笛卡尔/关节换算 | |

> 注意：`OpenConnection` 的**参数语义**（两个 `int` 是端口还是超时、`bool` 是什么开关）
> 只在签名层面已知，**含义未知**——见第四节。

---

## 三、原生导出表：API 语义地图（122 个导出）

这一层价值最高：C++ 导出名直接说明了每个功能干什么。

### 3.1 连接

```
open_connection / OpenConnection
close_connection / CloseConnection
```

### 3.2 读状态与位置

```
control_group_interface_read_position_data        ← 读当前位置（MoveTo 反馈/GetState 用）
control_group_interface_read_axis_configuration
control_group_interface_read_position_error
control_group_interface_read_torque_data
controller_state_interface_read_state             ← 控制器状态（报警/模式/伺服）
controller_state_interface_read_system_information
controller_state_interface_read_operating_times
controller_state_group_read_system_param
```

### 3.3 运动（**轨迹式，不是即点即动**）

```
motion_manager_ClearAllTrajectory                 ← 先清空
motion_manager_AddPointToTrajectory               ← 追加目标点
motion_manager_Add2PointsToTrajectory
motion_manager_Add3PointsToTrajectory
motion_manager_Add4PointsToTrajectory
motion_manager_MotionStart                        ← 启动运动
motion_manager_MotionStop                         ← 停止（对应 IRobot.StopAsync）
motion_manager_GetMotionTargetProgress            ← 到位进度（轮询用）
motion_manager_GetRemainingStepsInTrajectory      ← 剩余步数（判断是否到位）
```

**这直接决定适配器的实现方式**：

```
MoveToAsync(target):
  ClearAllTrajectory()
  AddPointToTrajectory(target)
  MotionStart()
  轮询 GetRemainingStepsInTrajectory / GetMotionTargetProgress 直到到位或超时

StopAsync():
  MotionStop()
```

### 3.4 控制命令

```
control_commands_interface_SetServos     ← 伺服上下电
control_commands_interface_SetHold       ← 保持
control_commands_interface_StartJob
control_commands_interface_SetCycleMode
control_commands_interface_DisplayStringToPendant
```

### 3.5 报警

```
fault_interface_get_active_alarms
fault_interface_get_alarm_history
fault_interface_clear_all_faults
```

### 3.6 坐标系与运动学（**直接印证手眼标定的约定选择**）

```
FrameToZYXeuler / ZYXeulerToFrame            ← 欧拉角约定是 ZYX！
frame_math_zyx_euler_to_frame
frame_math_frame_to_zyx_euler
MultiplyFrames / InvertFrame / RotateFrame
kinematics_interface_ConvertPosition
kinematics_interface_ConvertPositionFromCartesian
```

> **重要交叉验证**：本项目手眼标定 `Transform3D.EulerConvention` 默认选了 `Zyx`，
> 与原生库的 `FrameToZYXeuler` 一致。若当初选了 XYZ，手眼结果会整体偏转——
> 这类错误在现场表现为"残差很小但抓取偏"，极难排查。

### 3.7 IO 与变量

```
io_interface_ReadRegister / WriteRegister / address_* / type_* / uint*_*
*_variable_interface_read / write（byte/int/double_int/real/string/robot_position/...）
job_interface_get_executing_job_information / get_job_stack / set_active_job
file_interface_*（作业文件上下载）
```

---

## 四、还差什么才能写出适配器

| 缺口 | 说明 | 获取方式 |
|---|---|---|
| **`OpenConnection` 参数语义** | 两个 `int`、一个 `bool` 的含义（IP? 端口? 超时? 使能?） | 厂商手册，或读 `open_connection` 原生实现（需反汇编） |
| **接口方法的确切托管签名** | `ControlGroup`/`MotionManager`/`ControlCommands` 的成员名与参数 | 再查 5 个接口的方法表（元数据即可，无需执行） |
| 目标位置的数据结构 | `PositionData` 字段顺序与单位（mm? 关节?） | 元数据字段表 |
| 异常与错误码 | 连接失败的返回约定 | 手册 |

> 这些都不是"猜"能解决的。硬写只会得到一个**编译过但语义错**的适配器，
> 现场表现为"连上了但读不到位置"或"运动到错误位置"——比不写更危险。

---

## 五、下一步（建议顺序）

1. 补齐 5 个接口的方法表（元数据查询，约 5 次），确定成员名与参数类型；
2. 确定 `PositionData` 字段布局与单位；
3. 写 `Infrastructure/YmConnect/YmConnectRobot : IRobot`，只实现已验证部分；
4. 未确定语义的部分**返回 `ROBOT_MODE_NOT_IMPLEMENTED` 并说明原因**，不静默成功；
5. 有真实控制器时再做联调，并记录一份"实测参数语义"到本文档。

---

## 六、为什么值得先做这项工作

- 在没有机器人的情况下，**API 面已经可以完全摸清**（元数据 + 导出表）；
- 摸清之后，适配器的结构与 `IRobot` 的映射关系是确定的（见 3.3 的伪代码）；
- 避免"先写代码再对文档"导致的大面积返工。

---

## 七、适配器已实现（依据官方 C# 参考）

数据来源：[developer.motoman.com/en/YMConnect](https://developer.motoman.com/en/YMConnect) 的
C# 参考（Quick Start / Interfaces / MotionManager / FaultInterface / DataStructures），
以及旧项目实跑代码。**不是推测。**

### 7.1 确认下来的 API

| 用途 | 签名 |
|---|---|
| 连接（官方简版） | `static MotomanController OpenConnection(string ip, out StatusInfo status)` |
| 连接（带超时，旧项目用） | `OpenConnection(string ip, out StatusInfo status, int receiveTimeoutMs, int sendRetries, bool)` |
| 断开 | `CloseConnection()` |
| 读位置 | `ControlGroup.ReadPositionData(ControlGroupId, CoordinateType, index, specifier, out PositionData)` |
| 笛卡尔→关节 | `Kinematics.ConvertPositionFromCartesian(ControlGroupId, PositionData, KinematicConversions, KinematicType, double[] prevAngle, out PositionData)` |
| 报警 | `Faults.GetActiveAlarms(out ActiveAlarms)` / `GetAlarmHistory(AlarmCategory, uint, out AlarmHistory)` / `ClearAllFaults()` |
| 伺服/Hold | `ControlCommands.SetServos(SignalStatus.ON)` / `SetHold(SignalStatus.OFF)` |
| 清轨迹 | `MotionManager.ClearAllTrajectory()` |
| 加轨迹点 | `MotionManager.AddPointToTrajectory(Motion)`（另有 2/3/4 组同动重载） |
| 启动/停止 | `MotionManager.MotionStart()` / `MotionStop(bool clearTrajectory)` |
| 到位判断 | `MotionManager.GetRemainingStepsInTrajectory(out int remaining)` |
| 运动构造 | `new LinearMotion(ControlGroupId, PositionData, double speed, int accuracy)` **（1.1.10 实际为 4 参、第 4 为 int；文档示例写的是 MotionAccelDecel，与库不符）** |

### 7.2 官方明确的使用约束（必须遵守）

1. **MotionManager 仅 YRC1000 及更新控制器可用**（DX100/FS100 没有）
2. 使用运动前**控制器参数 `S2C425` 必须为 0**
3. 运动前需要：**伺服 ON**、**Hold OFF**、示教器处于**远程模式**
4. 轨迹语义：`AddPointToTrajectory` 不会立刻动；`MotionStart` 后才执行；
   若轨迹已空但未 `MotionStop`，再添加点会立即运动
5. 欧拉角为 **ZYX 内在（roll, pitch, yaw）** —— 与本项目 `Transform3D.EulerConvention.Zyx` 一致

### 7.3 实现位置

| 文件 | 内容 |
|---|---|
| `Infrastructure/YmConnect/RobotSettings.cs` | 配置模型 + 存储（IP / 速度 / 超时 / 重试） |
| `Infrastructure/YmConnect/YmConnectRobot.cs` | `IRobot` 实现：连接复用、读位姿、报警、轨迹式运动、停止 |
| `Composition/ServiceRegistration.cs` | `IRobot` 已切换为 `YmConnectRobot` |

`GetStateAsync` / `MoveToAsync` / `StopAsync` 的行为：

- **未配置 IP** → 返回明确的"未配置"，不联网、不抛异常
- 连接失败 → 错误信息里带 `StatusCode` 与四项排查清单
- 运动 → 清轨迹 → 加点 → 伺服 ON / Hold OFF → MotionStart → 轮询剩余步数 → 到位或超时（超时会下发停止）
- 停止 → `MotionStop(clearTrajectory: true)`，异常吞掉（急停路径不级联失败）

### 7.4 配置方式

配置文件：`%LocalAppData%\CameraControl\robot.json`

```json
{
  "Ip": "192.168.1.31",
  "SpeedMmPerSec": 25,
  "ReceiveTimeoutMs": 3000,
  "SendRetries": 3
}
```

> 端口由 YCP 协议固定，**只需 IP**。

### 7.5 已验证（无需真机）

| 项 | 结果 |
|---|---|
| 托管库可引用、签名匹配 | ✅ 编译通过 |
| **amd64 原生库可被加载** | ✅ 用 127.0.0.1 触发真实连接尝试，未出现 `DllNotFound` / `BadImageFormat` |
| 未配置 IP 的降级行为 | ✅ 返回"未配置"，不联网 |
| 配置读写 | ✅ 7 个单元测试 |

### 7.6 TODO-实测（现场必须核对）

代码里已用 `TODO-实测` 标注，逐条：

| # | 待核对 | 当前取法 | 错了会怎样 |
|---|---|---|---|
| 1 | 欧拉角方向（ZYX 的分量顺序） | 直接透传 Rx/Ry/Rz | 姿态整体偏转，位置对但角度错 |
| 2 | `LinearMotion` 第 4 个 int 的含义 | 取 `0`（按"精确到点"理解） | 可能变成混合运动或报参数错 |
| 3 | `SetServos` / `SetHold` 在远程模式下是否允许 | 无条件调用，失败只记日志 | 运动被 8015(254/253) 拒绝，但错误码会如实上报 |
| 4 | `ReadPositionData` 的 index/specifier 语义 | 非用户坐标系传 0（沿用旧项目） | 读到错误坐标系的位姿 |
| 5 | `ActiveAlarms` 的结构化成员 | 先用 `ToString()`（官方示例亦如此） | 报警信息只有文本，无法结构化判断 |
| 6 | 控制器是否 YRC1000+ 且 `S2C425=0` | 未做前置检查 | 运动接口直接不可用，报 8015 |

> 第 6 条建议现场第一步就确认：控制器型号 + `S2C425` 参数。

### 7.7 与旧项目的差异

| | 旧项目 | 现在 |
|---|---|---|
| 连接 / 读位置 / 坐标换算 | ✅ 已实现 | ✅ 已迁移 |
| **运动** | ❌ 只有 TODO | ✅ 已实现（轨迹式） |
| 报警读取 | ❌ 无 | ✅ 有（文本级） |
| 伺服 / Hold 控制 | ❌ 无 | ✅ 运动前自动处理 |

### 7.8 一个重要提醒

旧项目注释指出 `StatusInfo.IsOk()` 在 1.1.10 里**判断方向相反**。
官方文档未提这一点。适配器**一律用 `StatusCode == 0`** 判断，不复用 `IsOk()`。