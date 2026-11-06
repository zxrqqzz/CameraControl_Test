# CameraControl 架构审查与重构方案

> 审查日期：2026-11-06
> 审查范围：当前仓库全部源码（65 个文件 / 9,821 行 / 单项目）
> 审查目的：为"引入 CUA（AI 操控软件）"，判断现有骨架是否可作为大项目底座

---

## 一、结论

| 问题 | 结论 |
|---|---|
| 现有骨架是否工业标准？ | **否**。是"能跑的原型"，不是可扩展工程 |
| 是否应该全部推倒重写？ | **不建议**。会白白丢掉踩过坑的硬件/标定业务逻辑 |
| 应该怎么做？ | **"内核重写 + 外壳重写 + 业务逻辑迁移"**（分层重写并逐步搬家） |
| 为什么不能只改不改结构？ | **CUA 需求与当前架构根本冲突**（详见第四章） |

一句话：**现在的代码是"一个 950 行的 ViewModel 直接操作硬件和文件"的形态，而 AI 操控要求"每一个动作都是一个有 schema、有权限、可审计的原子命令"。这两者不是重构能桥接的，是骨架层面的差异。**

---

## 二、现状量化体检

### 2.1 规模与分布

| 指标 | 数值 |
|---|---|
| 项目数 | **1**（所有代码一个工程） |
| 文件数 | 65（.cs + .xaml） |
| 总行数 | 9,821 |
| 最大文件 | `CalibrationViewModel.cs` **950 行 / 23 个方法** |
| 超 400 行的文件 | 4 个（Calibration/DeviceManagement/HikCamera/CommunicationDevice） |

### 2.2 耦合与抽象（核心问题）

| 问题 | 实测数据 |
|---|---|
| **全局静态服务定位器** | `App.LogVM/Robot/Communication/Calibration/...` 被 **97 处**直接引用，横跨 **19 个文件** |
| **服务层几乎无抽象** | 12 个 Service 中**只有 1 个**（`ICameraService`）有接口 |
| **View 直接 new 窗口** | **7 处**（`MainWindow`、`DeviceManagementWindow` 内直接构造对话框） |
| **ViewModel 承担 IO** | `CalibrationViewModel` 里 8 处 `SaveFileDialog` / `File.WriteAllText` |
| **"模型"混入服务定位** | `CommunicationDevice.cs`（领域实体）引用 `App.LogVM` **14 次** |

> 影响：**没有任何一个业务用例能在无 UI、无硬件的情况下被测试**。

### 2.3 质量与健壮性

| 指标 | 数值 | 说明 |
|---|---|---|
| 编译警告 | **70** | 其中可空性 CS86xx **52 处** |
| 空 `catch {}` | **27 处** | 异常被静默吞掉，故障不可诊断 |
| `catch (Exception)` | **52 处** | 缺乏异常分类策略 |
| `async void` | **15 处** | 异常无法被捕获，可能直接崩进程 |
| `.Result` / `.Wait()` | 4 处 | UI 线程死锁风险 |
| 事件订阅 / 反订阅 | **27 / 12** | 反订阅缺口 = 内存泄漏与幽灵回调 |

### 2.4 工程化基础设施

| 项 | 状态 |
|---|---|
| `.editorconfig` | ❌ 缺失 |
| `Directory.Build.props`（统一 TFM/警告/可空性） | ❌ 缺失 |
| `Directory.Packages.props`（中央包管理） | ❌ 缺失 |
| `global.json`（锁定 SDK 版本） | ❌ 缺失 |
| 单元测试项目 | ❌ **完全没有** |
| CI（编译/测试门禁） | ❌ 缺失 |
| README / ADR / 需求文档 | ❌ 缺失 |

### 2.5 依赖健康度

| 依赖 | 问题 |
|---|---|
| **MvvmLightLibs 5.4.1.1** | **项目已于 2018 年停止维护**；当前被以 `.NETFramework 4.8` 兼容模式还原（`NU1701`），随时可能失效 |
| **12 条 `NU1903`** | 传递依赖存在已知高危漏洞 |
| **`MSB3270`** | 项目为 **AnyCPU**，却引用 **AMD64** 原生库 `MvCameraControl.Net` → **在 x86 或 ARM64 上运行时直接崩** |
| `SDK/` 二进制进仓库 | 1.2 MB 二进制（含 858 KB XML 文档）提交在 git 里，无校验、无版本说明 |
| 硬编码业务常量 | 机器人 IP `192.168.1.11`、通讯设备 `192.168.1.50:6000` 写死在 ViewModel |

### 2.6 结构性问题（一句话概述）

```
当前形态：  View ──(直接 new)──> View ──> ViewModel ──(App.静态)──> Service ──> 硬件
              ↑                                    ↑
         代码后置逻辑                      文件IO / 对话框 / 硬件调用混在一起
```

- **没有领域层**：`Models/` 里同时装着领域实体（`CameraDevice`）、持久化 DTO（`AppSettings`）、UI 辅助（`ScanCandidate`）
- **没有应用层**：用例逻辑散落在 ViewModel 里
- **没有组合根**：依赖关系硬编码在 `App` 静态构造器里
- **单向依赖无法保证**：任何文件都能 `using` 任何文件

---

## 三、存量资产盘点：什么能留、什么必须换

### 3.1 值得保留（真正的资产）

| 资产 | 位置 | 价值 |
|---|---|---|
| 海康 MVS SDK 封装知识 | `HikCameraService` / `HikDeviceEnumerator` / `HikSdkRuntime` | IP 是 `UInt32`、MAC 高低位、引用计数初始化、`CreateDevice(info)` 这些坑都已趟过 |
| UVC 注册表枚举 + IR 识别 | `UvcDeviceScanner` | 误报过滤规则（KSCATEGORY 含音频、Image 含打印机）是实测结论 |
| UVC WinRT 取流 | `UvcCameraService` | 共享只读模式、BGR→BitmapSource、IR 拒绝取流 |
| 标定算法与交互 | `CalibrationViewModel`（部分） | 九点标定、坐标系换算、机器人联动 |
| 机器人通讯协议 | `RobotService` / `YMConnect_CS` | 私有 DLL 调用细节 |
| 隐私闸门 | `ImagePersistenceGuard` | 设计可用 |
| UI 设计令牌 | `App.xaml` | 配色/控件样式可直接迁移 |

### 3.2 必须替换

| 项 | 替换为 |
|---|---|
| `App.XXX` 静态单例 | **DI 容器 + 组合根** |
| 无接口 Service | 每个外部依赖一个**端口接口**（Ports & Adapters） |
| MvvmLight（停更） | **CommunityToolkit.Mvvm** |
| 手写 `RelayCommand` / `INotifyPropertyChanged` 样板 | CommunityToolkit 源生成器 |
| View 内 `new` 窗口 | **对话服务（`IDialogService`）** |
| ViewModel 直写文件 | **`IStorage` 端口 + 应用层用例** |
| 空 `catch` | **异常分类 + 结构化日志 + 用户可读提示** |
| 手写日志 `LogViewModel` | **Serilog / ILogger 抽象**（UI 只做展示订阅） |
| 无测试 | **xUnit + FluentAssertions**（Domain/Application 必须可测） |

### 3.3 建议删除

- `MvvmLightLibs`
- `App` 上的 8 个静态属性
- `Models/` 里的持久化 DTO（迁到 Infrastructure）

---

## 四、CUA 需求带来的硬约束（这是重构的决定性理由）

你要做的"配置 API → AI 操控软件"不是加一个功能，而是**要求整个软件的每个动作都能被程序化调用**。

### 4.1 AI 不能靠"点按钮"

如果用 UI 自动化（模拟鼠标键盘）去驱动当前程序：
- 分辨率/DPI/窗口位置一变就失效
- 无法获知操作结果（成功/失败/为什么）
- 无法在后台/无界面运行
- 无法做权限与审计

### 4.2 必须引入"能力清单 + 命令总线"

```
             ┌──────────────┐
   人(UI) ──>│              │
             │  CommandBus  │──> 用例处理 ──> 领域/硬件
   AI(CUA) ─>│              │
             └──────────────┘
                    │
                    └──> 审计日志 / 权限 / 确认门
```

**人与 AI 必须走同一条路径**，这样：
- 同一个功能只实现一次
- 人类点按钮等价于 AI 发命令 → 可回放、可测试、可审计
- 无 UI 也能跑完整流程（这是自动化的前提）

### 4.3 每个能力必须有元数据

```csharp
// 目标形态（示意）
public sealed record CapabilityDescriptor(
    string Id,                       // camera.capture.single
    string Title,                    // 单次采集
    string Description,              // 供 LLM 阅读的自然语言说明
    JsonSchema Parameters,           // 参数 schema
    RiskLevel Risk,                  // ReadOnly / Reversible / Physical / Destructive
    bool RequiresConfirmation,       // 是否需要人工确认
    IReadOnlyList<string> Preconditions
);
```

### 4.4 🚨 安全：机器人运动是物理动作

这是本项目**最容易被忽视的高风险点**。AI 一旦能操控机器人：

| 风险等级 | 例子 | 策略 |
|---|---|---|
| ReadOnly | 读相机参数、查设备列表、读机器人位姿 | AI 可直接执行 |
| Reversible | 改相机曝光、改光源亮度、切换坐标系 | AI 可执行 + 留痕 |
| **Physical** | **机器人运动、设当前位姿、切工具坐标** | **默认必须人工确认**；支持"授权模式"放行 |
| Destructive | 删除设备、清空标定数据、改 IP | 必须确认 + 二次校验 |

必须有的安全设施：
- **急停/中断**：AI 执行中随时可打断
- **速率限制**：防止 AI 高频下发运动指令
- **软限位**：机器人工作空间边界校验（在应用层，不信任 AI）
- **干跑（dry-run）**：只算不动的预演
- **全量审计**：谁（AI/人）、何时、什么命令、什么参数、结果

### 4.5 隐私约束要延续

之前定下的规则必须在新区里继承：
- UVC/内置摄像头画面**只进内存**，不落盘
- 所有"保存图像"必须过 `ImagePersistenceGuard`
- AI 也不能绕过（AI 调用的保存命令同样要过闸门）

---

## 五、目标架构

### 5.1 分层与依赖方向

```
        ┌─────────────────────────────┐
        │  Presentation.Wpf (UI 外壳)  │
        └──────────────┬──────────────┘
                       │ 只能依赖 ↓
        ┌──────────────▼──────────────┐
        │  Application (用例 + 命令)   │◄──── Automation (CUA/LLM)
        └──────────────┬──────────────┘
                       │ 只能依赖 ↓
        ┌──────────────▼──────────────┐
        │  Domain (领域模型, 零依赖)    │
        └──────────────▲──────────────┘
                       │ 实现端口
        ┌──────────────┴──────────────┐
        │  Infrastructure (适配器)     │
        └─────────────────────────────┘
```

**铁律**：
- `Domain` 不引用任何框架（无 WPF、无 Newtonsoft、无 SDK）
- `Application` 只依赖 `Domain` 的抽象
- `Infrastructure` 实现 `Domain`/`Application` 定义的端口
- `Presentation` 只依赖 `Application`
- **用项目引用强制约束**，编译器帮你守规矩

### 5.2 目录结构（目标）

```
CameraControl.sln
├─ Directory.Build.props              # 统一 TFM/可空性/警告等级
├─ Directory.Packages.props           # 中央包版本
├─ .editorconfig                      # 代码风格
├─ global.json                         # 锁定 .NET SDK
├─ docs/
│  ├─ 01-需求规格.md
│  ├─ 02-架构总览.md
│  ├─ 03-能力清单.md                   # CUA 的能力目录(人可读)
│  └─ adr/                            # 架构决策记录
│     ├─ ADR-0001-分层与依赖方向.md
│     ├─ ADR-0002-命令总线设计.md
│     ├─ ADR-0003-AI安全策略.md
│     └─ ADR-0004-相机抽象.md
├─ src/
│  ├─ CameraControl.Domain/            # 纯领域
│  │  ├─ Cameras/                      # Camera, CameraKind, PrivacyPolicy
│  │  ├─ Calibration/                  # Board, Point, Frame, Affine
│  │  ├─ Robotics/                     # Pose, UserFrame, MotionLimit
│  │  ├─ Communication/                # Endpoint, TriggerRule
│  │  └─ Abstractions/                 # 端口接口
│  ├─ CameraControl.Application/        # 用例/命令
│  │  ├─ Commands/                     # ICommand, ICommandHandler, CommandBus
│  │  ├─ Capabilities/                 # 能力实现 + 描述符
│  │  ├─ Queries/
│  │  ├─ Safety/                       # 确认门/速率限制/软限位
│  │  └─ Auditing/
│  ├─ CameraControl.Infrastructure/    # 外部世界
│  │  ├─ Cameras/Hik/                  # 海康适配
│  │  ├─ Cameras/Uvc/                  # UVC 适配
│  │  ├─ Cameras/Discovery/            # 设备发现(注册表/ARP/ONVIF)
│  │  ├─ Robotics/YmConnect/
│  │  ├─ Persistence/                  # settings/profiles
│  │  ├─ Logging/
│  │  └─ Ai/                           # LLM 提供方 + Agent 宿主
│  ├─ CameraControl.Automation/         # 对外自动化面(可选进程外)
│  │  ├─ Mcp/                          # MCP 工具暴露
│  │  └─ Schema/
│  └─ CameraControl.Presentation.Wpf/  # 仅 UI
│     ├─ Views/  ViewModels/  Controls/  Resources/
│     ├─ Composition/                  # DI 装配(组合根)
│     └─ App.xaml.cs                   # 极薄，只做引导
├─ tests/
│  ├─ CameraControl.Domain.Tests/
│  ├─ CameraControl.Application.Tests/  # 命令/安全策略/权限(纯内存假实现)
│  └─ CameraControl.Infrastructure.Tests/
└─ third_party/                        # 原 SDK/ 迁到这里
   ├─ hik-mvs/  (README + SHA256 + 版本说明)
   └─ ymconnect/
```

### 5.3 关键接口（骨架契约）

```csharp
// ── Domain 端口 ──────────────────────────────
public interface ICameraSession : IAsyncDisposable
{
    CameraDescriptor Descriptor { get; }
    Task OpenAsync(CancellationToken ct);
    Task CloseAsync();
    Task<Frame> AcquireAsync(CancellationToken ct);
    event EventHandler<Frame> FrameArrived;
}

public interface ICameraProvider
{
    Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken ct);
    Task<ICameraSession> CreateSessionAsync(CameraId id, CancellationToken ct);
}

public interface IRobotClient
{
    Task<RobotPose> GetPoseAsync(CancellationToken ct);
    Task MoveAsync(RobotPose target, CancellationToken ct);   // 高风险
    Task<bool> IsInSoftLimitAsync(RobotPose pose, CancellationToken ct);
}

public interface IImagePersistencePolicy
{
    bool IsAllowed(CameraDescriptor camera, ImagePurpose purpose);
}

// ── Application 命令总线 ──────────────────────
public interface ICommand
{
    string CapabilityId { get; }
}

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct);
}

public interface ICommandBus
{
    Task<CommandResult> ExecuteAsync(ICommand command, Actor actor, CancellationToken ct);
}

public interface ICapabilityCatalog
{
    IReadOnlyList<CapabilityDescriptor> All { get; }
    CapabilityDescriptor? Find(string id);
}

public enum ActorKind { Human, Ai }
public sealed record Actor(ActorKind Kind, string? Name, string? SessionId);
```

### 5.4 CUA 执行流

```
用户在设置里配置: Provider=OpenAI/DeepSeek/..., ApiKey, BaseUrl, Model
        ↓
AI 收到系统提示: 能力清单(从 ICapabilityCatalog 导出 JSON Schema)
        ↓
AI 决策 → 调用 camera.capture.single
        ↓
ICommandBus.ExecuteAsync(cmd, Actor(Ai))
        ↓
SafetyGate: 风险等级 → Physical? → 要求人工确认
        ↓
ICommandHandler → 领域/硬件
        ↓
AuditLog 记录 + 结果返回给 AI(结构化 JSON)
```

---

## 六、需求分析（重做版）

### 6.1 系统边界

本系统是**机器视觉工位控制软件**，负责：
设备接入、图像采集、标定、机器人协同、通讯联动、流程触发、记录追溯，
并新增**AI 编排（CUA）**能力。

```
        ┌──────────────── 本系统 ────────────────┐
 相机 ◄─┤ 采集│标定│机器人协同│触发编排│记录追溯 │─► 机器人
 光源 ◄─┤             │                          │
        │        AI 编排(CUA) / 人机双通道        │─► PLC/MES
        └────────────────────────────────────────┘
```

### 6.2 功能域（用例分组）

| 编号 | 功能域 | 核心用例 | 风险 |
|---|---|---|---|
| F1 | **设备接入** | 发现/登记/连接/断开/参数读写/身份管理 | 低 |
| F2 | **图像采集** | 单帧/连续/软触发/预览/帧统计 | 低 |
| F3 | **标定** | 九点标定/手眼标定/坐标系换算/精度校验/导入导出 | 中 |
| F4 | **机器人** | 读位姿/运动/坐标系切换/软限位/急停 | **高** |
| F5 | **通讯** | TCP 客户端/服务端/收发/断线重连 | 中 |
| F6 | **触发编排** | 规则定义/匹配/动作序列/连续执行 | 中 |
| F7 | **记录与追溯** | 采集记录/标定记录/操作审计/导出 | 低 |
| F8 | **AI 编排（新）** | 配置模型/自然语言指令/任务编排/执行确认 | **高** |
| F9 | **配置与诊断** | 全局设置/设备清单/自检/日志查看 | 低 |

### 6.3 非功能需求（工业化的关键）

| 类别 | 要求 |
|---|---|
| **可测试性** | 领域与应用层 **≥70% 覆盖率**；用例可在无 UI、无硬件下用假实现跑通 |
| **可观测性** | 结构化日志（Serilog）+ 全量操作审计；关键路径有耗时埋点 |
| **安全性** | 物理动作默认需确认；软限位；速率限制；急停；AI 权限可收紧 |
| **隐私** | 内置/ UVC 摄像头画面只进内存；保存必须过策略闸门（AI 同样受限） |
| **可靠性** | 断线重连、设备热插拔、异常不静默吞掉（禁止空 catch） |
| **可部署性** | 单机离线运行；普通用户权限（**不得要求管理员**，否则相机不可用）；x64 明确 |
| **性能** | 预览 ≥30 fps；命令响应 <100 ms；UI 不阻塞（无 `.Result`/`Wait`） |
| **可维护性** | 依赖方向由项目引用强制；新代码零警告；统一代码风格 |
| **可扩展** | 新增相机厂商/机器人品牌只需新增适配器，不改应用层 |

### 6.4 关键架构决策（**已确认**）

| 编号 | 决策点 | 结论 |
|---|---|---|
| D1 | 重构策略 | **分层重写 + 逐步迁移**（绞杀者模式） |
| D2 | CUA 形态 | **进程内命令总线**（人与 AI 共用 `ICommandBus`） |
| D3 | AI 权限 | **分级授权**：只读/可逆直接执行；物理/破坏性需确认 |
| D4 | 技术选型 | CommunityToolkit.Mvvm + MS.Extensions.DI/Hosting + Serilog + xUnit + FluentAssertions |
| D5 | 扩展规模 | **单机多相机**（≥4 台并发，含协同采集） |

### 6.5 多相机同步采集模型（因 D5 而必须明确）

> **业务真相**（2026-11-06 确认）：当前单相机 + 单机械臂。未来是**单机械臂 + 多相机从不同角度对准同一工位，
> 事件触发的同步单帧采集**，用于多视角融合 / 3D 定位。
> **不是多路视频流，也不是轮流拍。**

```
   机械臂到位（事件）
          │
          ▼
   ┌──────────────┐   trigger = hardware(P2) / software(P0)
   │ CaptureGroup │──────────────────────┐
   └──────────────┘                      │
          │  同时触发曝光                │
   ┌──────▼───────┬──────────┬───────────▼──┐
   │  Camera #1   │ Camera #2│  Camera #N    │  各自独立会话
   └──────┬───────┴────┬─────┴───────┬──────┘
          │            │             │
          ▼            ▼             ▼
   ┌─────────────────────────────────────────┐
   │ CaptureGroupResult                       │
   │  timestampUtc（组级统一时间戳）           │
   │  timestampQuality: HardwareStamped/Approx │
   │  skewUs（实测帧间最大偏斜）               │
   │  frames[] + missingCameraIds[]            │
   └─────────────────────────────────────────┘
          │
          ▼
      融合 / 3D 定位（未来，端口先留）
```

关键规则：

| 规则 | 原因 |
|---|---|
| **接口一律组语义** | P0 单相机返回长度 1 的 `frames`；加相机不改调用方 |
| **组级时间戳 + 实测偏斜** | 软同步毫秒级偏差会导致视角错位；必须如实上报 |
| **`timestampQuality` 是类型级区分** | 融合层不得把 `SoftwareApprox` 当硬同步用 |
| **触发方式抽象为参数** | `TriggerSpec{Kind}`；P0 实现 software，P2 扩展 hardware |
| **Arm/Disarm 生命周期** | 硬触发时相机必须提前进入等待态，不能等到事件才打开 |
| **静态采集必须先过"振动衰减门"** | 不许"发完停止指令立即拍"，残余微振会污染高精度融合 |
| **动态抓拍需四件套** | 位置触发 + 频闪 + 短曝光 + 同一脉冲；缺一不可，且要先过曝光/模糊预算校验 |
| **标定形态可切换** | 当前眼在手上（位姿随动），未来眼在手外（固定外参），同一契约两种来源 |
| **禁止静默部分成功** | 某台相机没取到帧必须出现在 `missingCameraIds` |
| **每会话独立 CancellationToken** | 单台卡死不拖垮整组 |
| **预览单路独占** | UI 只显示一路；但**采集是事件驱动的单帧组，不占用预览带宽** |
| **每台相机独立隐私策略** | UVC/内置默认禁落盘，工业相机不受限 |

### 6.6 相关文档

- `docs/01-需求规格.md` — 正式 SRS（FR/NFR/AC）
- `docs/02-架构总览.md` — 分层、端口接口、DI 装配、P0 范围
- `docs/03-能力清单.md` — CUA 能力目录（约 50 个能力）


---

## 七、迁移路线（不等同于"推倒重来"）

采用**绞杀者模式（Strangler）**：新骨架先立起来，旧逻辑逐个搬家，旧项目始终可运行。

| 阶段 | 内容 | 产出 | 风险 |
|---|---|---|---|
| **P0 立骨架** | 建 solution 分层、Directory.Build.props、DI、日志、测试工程、CI | 能编译能跑空壳 | 低 |
| **P1 领域下沉** | 把 `Models/` 纯逻辑（标定算法、坐标换算、设备身份）搬进 `Domain` + 单元测试 | 有测试保护的领域层 | 低 |
| **P2 相机迁移** | `HikCameraService`/`UvcCameraService`/扫描器 → Infrastructure 适配器 + 端口接口 | 相机能力可在无 UI 下测试 | 中 |
| **P3 命令总线** | 定义 `ICommandBus` + 能力清单；把 P2 能力包装成命令 | **人与 AI 共用入口就绪** | 中 |
| **P4 UI 重建** | 新 WPF 外壳（CommunityToolkit.Mvvm），所有操作走命令总线 | 新界面可用 | 中 |
| **P5 机器人与标定** | 迁移机器人/标定/触发，加重试、软限位、确认门 | 功能对齐 | **高** |
| **P6 CUA 接入** | `CameraControl.Automation` + LLM 提供方 + 设置界面 | AI 可操控 | 中 |
| **P7 切换** | 灰度替换旧程序，保留回退 | 旧项目归档 | 低 |

> 每个阶段结束都应有可运行的产物 + 测试，**不存在"大爆炸式一次性切换"**。

---

## 八、风险清单

| 风险 | 影响 | 缓解 |
|---|---|---|
| 重写期间旧程序还要用 | 双线维护 | 旧项目冻结功能，仅修 bug |
| 机器人/标定逻辑迁移引入回归 | **生产事故** | 先写特征测试（characterization test）锁住当前行为，再迁移 |
| 私有 SDK（YMConnect）无文档 | 适配器难写 | 保留现有调用点，只做薄包装 |
| MvvmLight 迁移工作量 | 进度 | 源生成器可批量替换，实际体量不大 |
| AI 误操作机器人 | **人身/设备安全** | 默认人工确认 + 软限位 + 审计 + 急停 |
| 相机在管理员权限下不可用 | 功能异常 | 启动时检测并拒绝/提示（已实现过） |

---

## 九、下一步

1. ~~确认 5 个决策~~ ✅
2. ~~输出需求规格~~ ✅ `docs/01-需求规格.md`
3. ~~输出能力清单~~ ✅ `docs/03-能力清单.md`
4. ~~输出架构总览~~ ✅ `docs/02-架构总览.md`
5. ~~补充工艺约束（振动门 / 频闪 / 安装形态）~~ ✅ 2026-11-06
6. ~~补充 P0 离线连拍分析（定参 + 可行性验证）~~ ✅ 2026-11-06
7. **待办**：拆解 P0 任务清单（建 solution、Directory.Build.props、DI、日志、测试、CI、BurstAnalyzer）
8. 你确认文档后，开始 P0（**不动旧代码**）
