# CameraControl — 当前状态

> 分支：`CUA_重建`
> 最后更新：2026-11-06
> **旧单工程（`CameraControl/` WPF 应用）已从本分支删除**，备份在 `master` 分支。

恢复旧代码：

```powershell
git checkout master -- CameraControl/
```

---

## 现在这个仓库是什么

一个**分层重写中的**机器视觉工位控制软件。当前处于 **P0 已完成**（骨架 + 单相机垂直切片 + 离线连拍分析工具）。

```
CameraControl.slnx
├─ src/
│  ├─ CameraControl.Domain          net10.0        领域模型 + 端口（零框架依赖）
│  ├─ CameraControl.Application     net10.0        命令总线 / 能力 / 采集编排 / 安全
│  ├─ CameraControl.Infrastructure  win            海康适配器 / 触发 / 就绪门 / 存储
│  └─ CameraControl.Composition     win            唯一组合根（DI 装配）
├─ tests/                           Domain 25 + Application 13
├─ tools/CameraControl.Tools.BurstAnalyzer          离线连拍振动分析
├─ third_party/                     海康 MVS SDK / YMConnect（含 SHA256 清单）
└─ docs/                            需求 / 架构 / 能力清单 / P0 进度
```

## 文档索引（按阅读顺序）

| 文档 | 内容 |
|---|---|
| `ARCHITECTURE-REVIEW.md` | 旧架构量化体检、资产盘点、目标分层、迁移路线 |
| `docs/01-需求规格.md` | 正式 SRS：功能域 FR（含验收准则）+ 非功能 NFR |
| `docs/02-架构总览.md` | 分层与依赖方向、端口契约、同步采集编排、DI、P0 范围 |
| `docs/03-能力清单.md` | CUA 能力目录：id / schema / 风险等级 / 确认要求 |
| `docs/04-P0进度.md` | **P0/P1 交付物、测试证据、如何运行、已知取舍** |
| `docs/05-待实测数据清单.md` | **所有 `TODO-REALDATA` 占位数据的权威清单**（阈值、来源、风险、验证方法） |
| `docs/06-CUA接入.md` | **AI 操控：架构、配置、安全边界、已知限制** |
| `docs/07-P4界面.md` | **WPF 新壳：启动方式、冒烟测试、确认弹窗、已知缺口** |
| `docs/08-P5机器人标定.md` | **标定数学内核（已验证）+ 机器人端口与能力 + 当前缺口** |
| `docs/09-真实数据验证.md` | **真实数据集（Caltech 20 张）、已完成/未完成、失败根因与下一步（RANSAC）** |

## 快速命令

```powershell
cd D:\Xjk\C#\Test\CameraControl
dotnet build CameraControl.slnx
dotnet test  CameraControl.slnx

# 离线连拍振动分析（需真机）
cd tools\CameraControl.Tools.BurstAnalyzer
dotnet run -- --help
dotnet run -- --mode baseline --frames 60 --out D:\temp\baseline.json
```

## UI 验证约定（改界面只看这一节）

改样式 / 布局 / XAML，**只做两件事：构建 + 出图**。
**不要顺手跑单元测试**——测试留给逻辑改动，界面改动跑全量测试纯属浪费时间。

```powershell
# 构建表现层 → 15 秒内出 10 张图 → 没出图直接判失败
powershell -ExecutionPolicy Bypass -File tools\ui-verify.ps1

# 迭代聊天界面时更快：只出 AI 页一张
powershell -ExecutionPolicy Bypass -File tools\ui-verify.ps1 -AiOnly
```

**判定规则（硬性）**

- **超时固定 15 秒**。到点没出图 = 失败，**不要加大超时重试**，先看 `artifacts\shot-verify\error.txt`
  （有内容通常是 XAML 解析或绑定异常），然后直接报告失败。
- 只看"有没有 PNG"，不看张数（`-AiOnly` 只出 1 张）。
- 脚本会先检查有没有正在运行的 `CameraControl` 实例；开着界面会出旧图或直接失败，先关掉。
- 截图写到 `artifacts\shot-verify\`，核对完可整个删掉，不要提交。
- Agent 出图后要**逐张读图**核对，别只看"文件是否存在"。

## 不可放宽的约束（历史决策，勿回退）

| 约束 | 来源 |
|---|---|
| **程序必须以普通用户运行** | 管理员(高完整性)进程会被 Windows 拒绝访问摄像头（`0x80070005`） |
| **UVC / 内置摄像头画面只进内存** | 用户隐私要求：不落盘，关闭即消失；任何保存必须过 `IImagePersistencePolicy` |
| **局域网设备仅检测、不可连接** | 用户要求：不干扰他人摄像头 |
| **AI 不能直接执行物理/破坏性操作** | 机器人运动属物理动作，必须人工确认（`DefaultRiskPolicy`） |
| **同步采集一律返回组语义** | 现在单相机，未来多相机；调用方不得依赖"单帧" |
| **静止采集必须先过振动就绪门** | 不许"发完停止指令立即拍"，残余微振污染融合 |
| **Domain/Application 保持平台中立** | 用 `net10.0` 而非 `-windows`，误引 WPF 会编译失败 |
| **经验值必须标 `TODO-REALDATA`** | 未实测的阈值不得伪装成已验证；清单见 `docs/05-待实测数据清单.md` |

## 占位数据处理约定

代码里凡"还没用真实工位数据验证过"的经验值，都用统一标记：

```csharp
// TODO-REALDATA: <为什么是占位、真实值取决于什么>。验证后移除本标记。
```

查找全部占位：

```powershell
Get-ChildItem src,tools -Include *.cs -Recurse | Select-String 'TODO-REALDATA'
```

**拿到实测数据后必须**：① 删除代码里的标记 ② 更新 `docs/05-待实测数据清单.md` ③ 在 ADR 记录阈值来源。

## 关键历史踩坑（已固化进代码/文档）

- `IGigEDeviceInfo.CurrentIp` 是 `UInt32`，需按网络字节序转点分十进制；`MacAddrHigh/Low` 同样。
- 海康 SDK 是 **AMD64 原生库**，项目必须 `x64`（`MSB3270` 曾经是个隐患）。
- `{65e8773d-...}` 是 KSCATEGORY_CAPTURE（**含音频**），`{6bdd1fc6-...}` 是 Image 类（**含打印机**）——
  早期 UVC 扫描因此误报过麦克风/打印机。
- `ListView + GridView` 的列头会往 `%TEMP%\WPF\` 写光标临时文件，临时目录不可写时窗口直接崩；
  已改用 `ListBox + Grid` 行模板。
- 相机隐私开关（ConsentStore）全 `Allow` 时仍可能失败，先看**进程是否提权**。
- PowerShell 5.1 的 `Set-Content`/`Get-Content` 默认按 **ANSI**（本机 GBK）处理，会把 UTF-8 中文源码**双向搞坏**
  （读成乱码→写回即双重编码）。改文件用写文件工具；校验编码也用读文件工具，不要用 `Get-Content` 判断。

## 当前可运行的东西

```powershell
# ① 图形界面（P4 新壳）
dotnet build CameraControl.slnx
.\src\CameraControl.Presentation\bin\Debug\net10.0-windows10.0.19041.0\CameraControl.exe

# ② 不弹窗冒烟测试（CI/排障）
dotnet .\src\CameraControl.Presentation\bin\Debug\net10.0-windows10.0.19041.0\CameraControl.dll --smoke-test D:\temp\smoke.txt

# ③ 离线连拍振动分析（需真机；启动先跑自检）
cd tools\CameraControl.Tools.BurstAnalyzer
dotnet run -- --mode baseline --frames 60 --out D:\temp\baseline.json

# ④ 能力清单导出（CUA 白名单真相源）
cd tools\CameraControl.Tools.Capabilities
dotnet run -- --format markdown

# ⑤ AI 操控（先配置 API，再下发自然语言任务）
cd tools\CameraControl.Tools.Agent
dotnet run -- --configure --base-url https://api.deepseek.com/v1 --model deepseek-chat --api-key-stdin
dotnet run -- --goal "列出当前所有相机" --show-steps
```

## ✅ 已完成：UVC / 内置摄像头（原最大功能缺口）

已迁移到新架构，**真机验证通过**：

| 环节 | 实测 |
|---|---|
| 枚举 | `Integrated Camera`（可采集）+ `Integrated IR Camera`（红外，只列出不取流） |
| 打开 | 成功（共享只读，不独占） |
| 采集 | **1280×720 Bgra32，3,686,400 字节** |
| 隐私 | 采集全程走内存流，数据目录**图片文件数 = 0** |

实现位置：`src/CameraControl.Infrastructure/Uvc/`
- `UvcDeviceEnumerator` — 注册表 `Service=usbvideo` 枚举（避免误判音频/打印机）
- `UvcCameraSession` — WinRT `MediaCapture` + `CapturePhotoToStreamAsync` + `GetPixelDataAsync`
- `UvcCameraProvider` — 红外拒绝打开
- `CompositeCameraProvider` — 海康 + UVC 合并，**单路失败不影响另一路**

### 踩坑记录（都已写进代码注释）

1. **手拼设备符号链接得到 0x80070490** → 改用 `DeviceInformation.FindAllAsync` 权威枚举，按实例末段匹配。
2. **.NET 5+ CsWinRT 不支持 `[ComImport]` 强转** 取 `IMemoryBufferByteAccess` → 改用官方 `GetPixelDataAsync()`。
3. **WinRT 的 Access Denied 异常 Message 为空**，只能靠 `HResult` 判断 → 必须显式翻译，否则用户只看到一句空话。
4. 平台目标提升到 `net10.0-windows10.0.19041.0`（WinRT 投影需要），Domain/Application 仍是平台中立的 `net10.0`。

## 下一步

1. **标定产品化**：把 `calibration.*` 能力接进命令总线 + 标定结果存储（`ICalibrationStore`）+ 标定界面
2. **畸变系数 + 非线性精化**（当前与公开参考值差约 3%，见 `docs/09`）
3. **手眼标定 AX=XB**（EyeInHand 必需，否则图像坐标换不到机器人坐标）
4. **YMConnect 机器人适配器**（需先摸清 API：194 类型 / 982 方法 / .NET 8）
5. **实地跑 BurstAnalyzer baseline** → P2 视觉振动门（需真机数据）
6. 实时预览流（`ICameraSession.StartStreaming`）

## 真实数据备查

Caltech 标定数据集已下载到 `testdata/caltech/ex/`（20 张 640×480 真实 TIFF，**已 gitignore**）。
来源、SHA-256、复现命令见 `docs/09-真实数据验证.md`。
验证工具：`tools/CameraControl.Tools.CalibCheck`。
