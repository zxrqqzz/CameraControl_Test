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
| `docs/04-P0进度.md` | **P0 交付物、测试证据、如何运行、已知取舍** |

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

## 关键历史踩坑（已固化进代码/文档）

- `IGigEDeviceInfo.CurrentIp` 是 `UInt32`，需按网络字节序转点分十进制；`MacAddrHigh/Low` 同样。
- 海康 SDK 是 **AMD64 原生库**，项目必须 `x64`（`MSB3270` 曾经是个隐患）。
- `{65e8773d-...}` 是 KSCATEGORY_CAPTURE（**含音频**），`{6bdd1fc6-...}` 是 Image 类（**含打印机**）——
  早期 UVC 扫描因此误报过麦克风/打印机。
- `ListView + GridView` 的列头会往 `%TEMP%\WPF\` 写光标临时文件，临时目录不可写时窗口直接崩；
  已改用 `ListBox + Grid` 行模板。
- 相机隐私开关（ConsentStore）全 `Allow` 时仍可能失败，先看**进程是否提权**。
- PowerShell 的 `Set-Content` 默认按 ANSI 写，会把中文源码写坏；改文件用写文件工具。

## 下一步

1. **实地跑 BurstAnalyzer baseline** → 拿到工位噪声底，决定 P2 视觉振动门是否可行
2. P1：Serilog + 启动自检 + 指标埋点
3. P2：硬件触发（PLC/光源）+ `VisionStabilityGate` + 频闪/运动冻结
4. P4：WPF 新壳（CommunityToolkit.Mvvm）接入命令总线
5. P6：CUA（LLM + 能力清单导出）
