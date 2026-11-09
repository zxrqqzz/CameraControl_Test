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
