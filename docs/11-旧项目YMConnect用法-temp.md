# 11 · 旧项目 YMConnect 真实用法（temp）

> 来源：分支 `旧项目备份` 的 `CameraControl/Services/RobotService.cs`（235 行）
> 及 `RobotControlViewModel.cs`、`CalibrationViewModel.cs`。
> **本文全部内容来自旧项目实际运行过的代码**，不是推测。
> 用途：为新架构的 `IRobot` 适配器提供已验证的调用方式。
> 状态：**勘察记录，尚未写代码**。

---

## 一、结论速览

| 项 | 旧项目实际情况 |
|---|---|
| 连接 | ✅ 已实现并现场使用过 |
| 读位置（脉冲/关节角/基座标/用户坐标系） | ✅ 已实现并使用 |
| **运动（MoveTo / 轨迹）** | ❌ **从未实现**，代码里只有 TODO |
| 停止 | ❌ 未实现 |

> 也就是说：**旧项目只能"看"机器人，不能"动"机器人。**
> 运动部分在本项目里是新增能力，没有旧代码可抄。

---

## 二、连接（已验证的完整签名）

```csharp
StatusInfo status;
var controller = MotomanController.OpenConnection(
    ip,                 // string：机器人 IP（端口由协议固定，不需要填）
    out status,         // out StatusInfo：连接结果
    3000,               // int：接收超时 ms（旧项目常量 ReceiveTimeoutMs = 3000）
    3,                  // int：发送重试次数（旧项目常量 SendRetries = 3）
    false);             // bool：旧项目固定传 false，语义未在代码中说明
```

旧项目使用的相关常量：

```csharp
private const int ReceiveTimeoutMs = 3000;
private const int SendRetries = 3;
private const int ConnectTimeoutMs = 5000;   // 未用于 OpenConnection
private const int ReadTimeoutMs = 10000;     // 未用于 OpenConnection
```

### 成功判据（**关键坑**）

```csharp
if (controller == null || status.StatusCode != 0)  // 失败
```

旧项目源码里明确注释：

> 注意: YMConnect_CS 1.1.10 的 `StatusInfo.IsOk()` 判断方向有误（实现为 `StatusCode != 0`），
> **必须以 `StatusCode == 0` 作为成功判据。**

⚠ **这是确定存在的缺陷**：调用 `IsOk()` 会得到相反结果。
新适配器**禁止使用 `IsOk()`**，一律用 `StatusCode`。

### 失败时的处置

```csharp
try { controller?.CloseConnection(); } catch { /* 忽略 */ }
```

旧项目的失败提示（现场排障清单，值得沿用）：

```
连接机器人失败: Code={StatusCode}, {Message}
请检查: ①机器人IP是否正确; ②机器人YCP通讯服务/端口是否开启;
③电脑与机器人网络是否连通; ④防火墙是否拦截(建议放行安川相关端口)。
```

---

## 三、读位置（已验证）

```csharp
StatusInfo status = _controller.ControlGroup.ReadPositionData(
    group,        // ControlGroupId，默认 ControlGroupId.R1
    coord,        // CoordinateType
    index,        // byte
    specifier,    // byte
    out pos);     // out PositionData
```

### 参数含义（来自旧代码注释）

| 参数 | 含义 |
|---|---|
| `group` | 控制组，默认 `ControlGroupId.R1` |
| `coord` | 坐标系类型 |
| `index` | 仅 `UserCoordinate` 时有意义：**用户坐标系编号 UF1~UF63**；其它情况传 0 |
| `specifier` | **工具号**；不用时传 0 |
| `pos` | 输出 `PositionData`，坐标在 `pos.AxisData`（`double[]`） |

### 用到的 CoordinateType

```csharp
CoordinateType.Pulse            // 脉冲
CoordinateType.Angle            // 关节角度
CoordinateType.BaseCoordinate   // 基座标（世界系）
CoordinateType.UserCoordinate   // 用户坐标系（需 index=UF 编号）
CoordinateType.RobotCoordinate  // 机器人坐标（运动学换算时必须用这个）
```

### 旧项目的读取组合

| 场景 | 调用 |
|---|---|
| 全量位姿 | 依次读 `Pulse`、`Angle`、`BaseCoordinate` |
| 用户坐标系位姿 | `Pulse`、`Angle`、`UserCoordinate(index=uf, specifier=0)` |
| 逆解参考姿态 | `Angle` 读一次当前关节角，传入运动学换算 |

---

### 两个已验证的坑

1. **输入必须 `CoordinateType.RobotCoordinate`**——旧代码注释："运动学逆解要求输入为 RobotCoordinate，BaseCoordinate 会报错"
2. **`prevAngle` 必须传当前真实关节角**——用于选择逆解分支；随便填会得到不可达或跳变的解

---

## 八、可直接沿用到新架构的现场经验

1. **不要用 `StatusInfo.IsOk()`**（1.1.10 实现相反）——这是踩过的坑。
2. **连接用 IP，不填端口**（端口由 YCP 协议固定）。
3. **读节点一律先判 `StatusCode == 0`** 再使用输出参数。
4. **逆解输入必须是 `RobotCoordinate`**，并且要传当前关节角作参考。
5. **关闭连接要吞异常**。
6. 排障顺序：IP → YCP 服务/端口 → 网络连通 → 防火墙。
