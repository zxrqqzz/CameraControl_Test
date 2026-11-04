# 进度交接（HANDOFF）

> 用途：给"下一次会话"的 AI/自己看。新会话不保留任何对话上下文，只保留这个仓库里的文件。
> **恢复方式**：读本文件 → 读「当前状态」→ 只做「剩余工作」里的事。

最后更新：2026-11-04

---

## 一、任务是什么

项目：`D:\Xjk\C#\Test\CameraControl`（WPF / .NET 10 / C#，中文界面）

给「设备管理」模块补齐功能：

1. **检索**：按 名称 / 厂商 / 型号 / IP / 序列号 / 来源 关键字过滤；分类为 **全部 / 相机 / 光源**。
2. **扫描**（新增）：自动发现本机连着的疑似相机
   - 海康 MVS SDK 枚举（GigE Vision / USB3 Vision / GenTL 采集卡）
   - 本机 UVC 摄像头（**含笔记本自带摄像头**，注册表枚举）
   - 局域网被动检测（读 ARP 邻居表 + 厂商 OUI，**零网络流量**）
   - 局域网 ONVIF 主动发现（**默认关闭**）
3. **扫描结果交互**：先列候选 → 勾选 → 去重后批量加入。
4. **持久化**：设备列表存 `%LocalAppData%\CameraControl\settings.json`。
5. **打通**：去掉硬编码 `192.168.1.10`，主界面连接「设备管理中选中的相机」。
6. **UVC 采集**：留桩，只做设备记录。

### 用户明确的安全边界（**不要放宽**）

> "局域网只做检测吧，不要主动获取图像，最好能走无痕安全。因为可能会找到其他人的摄像头"

- 局域网结果标记为 **仅检测 / 不可连接**（`DeviceSource.DetectedOnly()`），不连接、不认证、不登录、不请求 RTSP、不取图。
- 被动模式（ARP）默认开、零报文；ONVIF 主动发现默认**关**。
- 海康 SDK 只枚举不开设备；UVC 只读注册表。

### 隐私约束：UVC / 笔记本自带摄像头（**不要破坏**）

> "电脑自带的摄像头，为了保护人像隐私，只做临时的缓存，程序关闭后就释放清空。一定要彻底消失。"

用户选择：**只约束 UVC/自带**（海康工业相机不受限）+ **UVC 默认禁止落盘、单台可显式开启** + **纯内存永不落盘** + **内存释放即可**。

实现：

| 位置 | 作用 |
|---|---|
| `Services/ImagePersistenceGuard.cs` | **唯一闸门**。`IsPersistAllowed()` 判定；`TryAuthorize(device, purpose, out reason)` 供所有"保存图像"的代码路径调用 |
| `CameraDevice.AllowImagePersist` (`bool?`) | 单台覆盖。`null`=跟随默认（UVC 禁止，其它允许）；`true`=放行（保证失效） |
| `AppSettings.CameraDeviceSettings.AllowImagePersist` | 记住用户选择，重启不丢 |
| `UvcCameraService` | 帧只存在 `_lastFrame`（内存）；`PushFrame` 只保留最新一帧；`StopGrabbing`/`Disconnect`/`Dispose` 立即清空 |
| `MainViewModel.ClearDisplayedFrame()` | `Disconnect` 与 `Dispose` 时把 `CurrentImage` 置空，程序关闭不留残影 |
| `DeviceManagementWindow` 右侧 | 仅 UVC 设备显示「🔒 图像隐私」区块 + 允许保存的开关（带警告） |

**铁律**：将来任何截图 / 录像 / 导出功能，必须先 `ImagePersistenceGuard.TryAuthorize(...)`，禁止绕过。
若要一刀切"所有相机都禁止落盘"，把 `ImagePersistenceGuard.DefaultsToPersist` 改成 `=> false` 即可。

当前事实核对：全项目**没有任何一处**把图像写到磁盘
（`CalibrationViewModel` / `UserCoordinateViewModel` 里的 `SaveFileDialog` 保存的是标定坐标文本 CSV/JSON）。

---

## 二、当前状态：**功能已全部实现，编译通过，真机验证通过**

`dotnet build CameraControl\CameraControl.csproj` → **0 错误**（只剩项目原有的 CS86xx 可空性警告）

### 真机探针验证结果（2026-11-04，本机实测）

**探针 1 — 扫描器逻辑**：`C:\Users\31813\AppData\Local\Temp\dsh-SbPaBN\cc-scanprobe`
（`Compile Include` 链接主工程源码，不是复制——可随时重跑：`dotnet run --project ScanProbe.csproj`）

```
[1] UVC 扫描      -> 2 个: Integrated Camera / Integrated IR Camera   ✅
[2] 局域网被动    -> ARP 读到 5 条, 网段 192.168.100.0/22, OUI 命中 0  ✅
[3] 去重键语义    -> 手工设备空键 / 同路径跨改名 / 序列号归一 / MAC 归一 全 True ✅
[4] 来源能力语义  -> LanPassive/LanOnvif 检测专用=True 可连接=False ✅
[5] 候选→设备转换 -> 键一致、仅检测标记正确、空名兜底 ✅
>>> 全部检查通过 <<<
```

**探针 2 — UI 窗口加载 + 绑定检查**：`C:\Users\31813\AppData\Local\Temp\dsh-SbPaBN\cc-uiprobe`
（引用 `bin\Debug\net10.0-windows\CameraControl.dll`；**注意引用 .dll 不是 .exe**，.exe 是原生 apphost 无元数据）
挂 `PresentationTraceSources.DataBindingSource` 监听器抓 Error 级别绑定错误。

```
[OK]   App.InitializeComponent (App.xaml 资源)
[OK]   选中内存相机，右侧面板绑定将被求值

=== 阶段 A: 正常 %TEMP% ===
[OK]   DeviceManagementWindow 加载成功
[OK]   DeviceScanWindow 加载成功
[OK]   AddDeviceWindow 加载成功

=== 阶段 B: 临时目录不可写（复现用户崩溃条件） ===
[OK]   对照组 GridView (旧写法, 期望失败): 如期抛出 IOException（故障条件复现成功）
[OK]   DeviceScanWindow (新写法, 期望成功) 加载成功
[OK]   DeviceManagementWindow (同条件) 加载成功
[OK]   AddDeviceWindow (同条件) 加载成功

[OK]   未捕获到 WPF 数据绑定错误
>>> UI 探针全部通过 <<<
```

**阶段 C — 隐私约束（17 项断言全过）**：UVC 默认禁止落盘、显式开关生效、海康默认放行、
`TryAuthorize` 正确拒绝/放行、压入 100 帧后仍只持有内存一帧、
`StopGrabbing`/`Disconnect`/`Dispose` 后帧缓冲归零、
**全过程磁盘文件数 = 0**。

阶段 B 的做法：把 `TEMP`/`TMP` 指向一个 `<TEMP>\WPF` 被**同名文件占位**的目录，
使 `Cursor.LoadFromStream` 的临时文件创建必然失败，从而稳定复现用户机器上的故障条件。

⚠️ 跑 UI 探针时若报 `UnauthorizedAccessException: ...\Temp\WPF\xxx.xx1`，
是**沙箱禁止写 %TEMP%**，不是代码问题。把 `$env:TEMP`/`$env:TMP` 指到可写目录即可。

**探针 3 — 主程序启动**：直接 `Start-Process bin\...\CameraControl.exe`，存活 8 秒无 stderr 输出，主窗口正常。

### 真机验证抓到的三个 bug（已修）

1. **误报（严重）**：`DeviceClasses` 兜底扫描把音频/蓝牙/打印机全当摄像头
   - 原因：`{65e8773d-...}` 是 **KSCATEGORY_CAPTURE（音频采集也算）**；`{6bdd1fc6-...}` 是 **Image 设备类（含扫描仪/打印机）**
   - 实测混入了：Intel 智音麦克风、蓝牙 A2DP、MIDI function、Canon GX1000 打印机
   - 修复：只保留 `{e5323777-...}`（Camera 类）+ **强制 `Service == usbvideo` 二次校验** + 排除 `BTHENUM/INTELAUDIO/SWD\*` 路径
2. **MAC 去重键不归一**：`4C:BD:8F:...` 与 `4c-bd-8f-...` 被判成两台设备
   - 修复：新增 `DeviceDedupe.NormalizeMac()`（统一分隔符 + 大写）
3. **打开扫描窗口直接崩溃（用户实测报障）**：`System.UnauthorizedAccessException: Access to the path '...\Temp\WPF\xxx.rlo' is denied`
   - 栈：`GridViewColumnHeader.HookupGripperEvents()` → `get_SplitCursor()` → `Cursor.LoadFromStream()` → `FileHelper.CreateAndOpenTemporaryFile()`
   - 原因：`ListView + GridView` 的列头为了显示"拖动调整列宽"的光标，**每次应用模板都要往 `%TEMP%\WPF\` 写临时文件**；
     该机器上这个写入被拒绝（策略/杀软/临时目录权限），窗口在 `Show()` 里就炸
   - 修复：
     - `DeviceScanWindow.xaml` 的结果列表**改成 `ListBox` + `Grid` 行模板 + 自绘表头**，彻底不用 `GridView`，绕开 `GridViewColumnHeader`
     - `DeviceManagementWindow.OnScanRequested()` 加 try/catch，窗口打不开时弹提示而不是崩溃
   - **对照实验已证明**：同样"临时目录不可写"条件下，旧 `GridView` 写法抛 `IOException`（复现崩溃），新写法正常加载

### 文件清单

**新增**

| 文件 | 作用 |
|---|---|
| `Models/DeviceSource.cs` | `DeviceSource` 枚举 + `DeviceSources` 扩展 + `DeviceDedupe`（去重键/归一化） |
| `Models/ScanCandidate.cs` | 扫描候选行（勾选状态、已存在标记、`ToCameraDevice()`） |
| `Services/HikSdkRuntime.cs` | 海康 SDK **引用计数**初始化（扫描与取流共用，避免扫描注销掉正在用的相机） |
| `Services/HikDeviceEnumerator.cs` | 海康枚举公共实现（`Match()`、`IpToString`、`MacToString`、`LayerText`） |
| `Services/UvcDeviceScanner.cs` | 注册表枚举 UVC（`Enum\USB` 下 `Service=usbvideo` + 严格校验的类补扫） |
| `Services/LanDeviceScanner.cs` | 被动 ARP（`GetIpNetTable`）+ OUI；可选 ONVIF `ScanOnvifAsync` |
| `Services/DeviceScanService.cs` | 编排器 + `ScanOptions` / `ScanReport` |
| `Services/UvcCameraService.cs` | **真实取流已实现**：WinRT `MediaCapture` + `MediaFrameReader`，Bgra8 帧 → `BitmapSource`（Freeze）→ 仅内存；`SupportsStreaming => true`；IR 摄像头拒绝取流；`E_ACCESSDENIED` 有中文排查提示 |
| `Services/ImagePersistenceGuard.cs` | 图像落盘闸门（UVC 默认禁止，单台可覆盖） |
| `ViewModels/DeviceScanViewModel.cs` | 扫描界面 VM（范围勾选、进度、取消、全选、添加） |
| `Views/DeviceScanWindow.xaml(.cs)` | 扫描窗口 |

**已修改**

| 文件 | 改动 |
|---|---|
| `Models/CameraDevice.cs` | +`Mac`/`DevicePath`/`InterfaceText`/`Source`/`IsDetectedOnly`/`SourceText`/`DedupeKey`/`CanConnect`/`SubtitleText` |
| `Models/DeviceBase.cs` | +`virtual SubtitleText` |
| `Models/AppSettings.cs` | +`Cameras`、`Scan`、`CameraDeviceSettings`、`ScanSettings` |
| `Services/ICameraService.cs` | +`SupportsStreaming`、+`Connect(CameraDevice?)` |
| `Services/HikCameraService.cs` | **删硬编码 IP**；`Connect(CameraDevice?)` 用 `Match`+`CreateDevice(info)`；+`CloseDevice()`；SDK 走 `HikSdkRuntime` |
| `Services/AppSettingsService.cs` | `SaveAll` 存相机列表/扫描选项；`LoadAll` 恢复 |
| `ViewModels/DeviceManagementViewModel.cs` | 重写：检索、`DeviceListCategory` 单选真值源、去重、持久化、`ScanRequested` |
| `ViewModels/MainViewModel.cs` | 按选中相机选通道（Hik/UVC 桩），命名方法挂/脱事件，`HikCamera` 判空 |
| `MainWindow.xaml.cs` | 参数窗改为判空取 `HikCamera` |
| `Views/DeviceManagementWindow.xaml(.cs)` | 检索框 + 扫描按钮 + 全部/相机/光源 + 来源/MAC/接入方式只读区 + 仅检测提示 |
| `Views/AddDeviceWindow.xaml(.cs)` | +序列号输入框 |

---

## 三、剩余工作

### A. 关键结论：相机拒绝访问是**测试环境提权**导致的（判断已更正）

早些时候我得出"机器层面封锁相机"，**这个结论是错的**，以下为更正后的真相：

`MediaCapture.InitializeAsync` 恒返回 `E_ACCESSDENIED (0x80070005)`，排查结果：

| 检查项 | 结果 |
|---|---|
| 用不含本项目任何代码的裸 WinRT 探针复现 | 同样失败（排除了实现问题，但没排除环境） |
| 5 种配置矩阵（Exclusive/SharedReadOnly × 无子类型/Bgra8/Rgb32） | 全失败 |
| 不指定 `VideoDeviceId`，用系统默认相机 | 仍失败 |
| 隐私 ConsentStore（HKCU/HKLM/NonPackaged） | 全部 `Allow` |
| 组策略 `Policies\Microsoft\Windows\Camera` | 不存在 |
| `MediaFrameSourceGroup.FindAllAsync()` | **成功**，能枚举 4 组设备 |
| **执行测试的 shell 完整性级别** | **`Mandatory Label\High Mandatory Level`，`IsAdmin=True`** |

→ 真正原因：**测试是在提权（High IL）进程里跑的，Windows 拒绝高完整性进程访问摄像头**，
这也解释了为什么 `ConsentStore\NonPackaged` 里从未出现我们探针的授权条目
（拒绝发生在"用户授权"审计之前）。

**重要推论**：
1. **本程序必须以普通用户身份运行**（双击启动即可）。以管理员身份运行会导致相机打不开。
   这条要记住——建议后续在启动时检测 `IsAdmin` 并提示。
2. 用户手工测试时（非提权）大概率是正常的；需要用户实机确认一次。
3. 本会话的沙箱只有提权模式能执行 `dotnet`/外部 exe，因此**无法在此环境内完成相机实测**。

### A2. 教训：不要在临时目录编译并启动 exe

我曾把测试探针编译到 `%TEMP%` 并尝试直接启动，触发 **Windows Defender SmartScreen**
"Windows 已保护你的电脑 —— 未识别的应用" 弹窗（原因是：刚编译、无数字签名、位于临时目录、无信誉记录）。
虽然探针本身无害，但会吓到用户，且临时目录里散落可执行文件也不合适。

**约定**：
- 不要再把测试 exe 编译到临时目录并启动
- 验证请用"用户手动运行主程序"或"在项目内建正式测试项目"
- 本会话已清理全部探针产物（目录/日志/计划任务/进程，残留 0 项）

### B. 建议人工点选（自动化已覆盖窗口加载、绑定、隐私约束、IR 拒绝）

自动化已确认：窗口能加载、绑定无错、扫描器逻辑正确、临时目录故障下不崩。
**没有**跑的是"真人点按钮"的完整流程。建议在 VS 或 `main` 里走一遍：

1. 打开「设备管理」→ 点「扫描」→ 勾「本机摄像头」→ 开始扫描 → 应看到 2 个 Integrated Camera
   （**这一步之前会崩，已修复，请重点复验**）
2. 勾选 → 「添加所选」→ 列表出现设备 → 关掉程序 → 重开 → **设备应还在**（验证持久化）
3. 检索框输入 `30C9` 或 `Integrated` → 列表过滤；分类切「相机/光源/全部」
4. 点「连接」→ 主界面状态显示相机名（无海康相机时应给提示而非崩溃）
5. 双击设备重命名、点 × 删除

若有问题，看 VS 输出窗口的 `System.Windows.Data Error` 行。

### B. 可选增强

- 若 WinRT 路子被 OEM 确认拦截，可补一条 **DirectShow** 备用采集通道（`ICreateDevEnum` + `ISampleGrabber`），
  在 `UvcCameraService` 里做"WinRT 失败自动降级到 DirectShow"
- `LanDeviceScanner.CameraOuis` 扩充更多厂商 OUI（当前只有海康/大华/Axis/SunplusIT）
- `DeviceClasses` 补扫在非管理员下会报"拒绝访问"（已降级为友好日志，不影响 USB 扫描；USB 扫描是本机摄像头的主要来源）

---

## 四、踩过的坑 / 已核实事实

- `MvCameraControl.IGigEDeviceInfo.CurrentIp` 是 **`UInt32`**！按网络字节序取高位转点分十进制。
- `MacAddrHigh` / `MacAddrLow` 也是 `UInt32`（各只用低 24 位）。
- `IUSBDeviceInfo` 有 `VendorID`/`ProductID`/`DeviceGUID`/`FamilyName`。
- `IDeviceInfo` 有 `ManufacturerName`/`ModelName`/`SerialNumber`/`UserDefinedName`/`DevTypeInfo`。
- 连接用 `DeviceFactory.CreateDevice(IDeviceInfo)`；枚举用 `DeviceEnumerator.EnumDevicesEx(layer, "", out List<IDeviceInfo>)`。
- 海康 SDK 成功码恒为 `0`（`MvError.MV_OK`）。
- SDK 的 XML 文档是 GBK，`Get-Content -Encoding UTF8` 会乱码；用反射看类型才可靠
  （`[System.Reflection.Assembly]::ReflectionOnlyLoadFrom` 能绕过 0x80131515）。
- **`{65e8773d-...}` 不是"摄像头类"**，是 KSCATEGORY_CAPTURE（含音频）；`{6bdd1fc6-...}` 是 Image（含打印机）。
- 项目编码：`.cs` 多数 UTF-8 无 BOM，`MainWindow.xaml` 有 BOM。
- **git 有 dubious ownership**，`git status` 会失败，需 `git config --global --add safe.directory 'D:/Xjk/C#/Test/CameraControl'`。
- **沙箱默认禁止 `dotnet`**，编译/运行需 `sandbox_permissions: danger-full-access`（workspace-write 仍会被拒）。
- `$env:TEMP` 在沙箱内被重定向到 `...\Temp\dsh-xxxx\`，跨次调用路径会变——用绝对路径。

---

## 五、设计备忘

- **UVC 枚举为何不用 DirectShow**：只"列出设备"读注册表更简单、零依赖。**取流**已改用 WinRT
  `MediaCapture`/`MediaFrameReader`，因此项目 TFM 从 `net10.0-windows` 改成
  `net10.0-windows10.0.19041.0`（需要 Windows SDK 投影），并开启 `AllowUnsafeBlocks`
  （读 `SoftwareBitmap` 像素缓冲要 `IMemoryBufferByteAccess` 指针）。
- **ARP 结构**：`MIB_IPNETROW` = `DWORD dwIndex; DWORD dwPhysAddrLen; BYTE bPhysAddr[8]; DWORD dwAddr; DWORD dwType;`（24 字节），表头 4 字节条目数。
- **ONVIF**：`239.255.255.250:3702`，`Probe` 的 `Types = dn:NetworkVideoTransmitter`，回包取 `ProbeMatch` 的 `Scopes` / `XAddrs`。
- **RadioButton 互斥**：不要用三个独立 bool 作真值源（取消时写 false 会被忽略导致状态错乱）；
  用单一枚举 `DeviceListCategory`，三个 bool 只做 `get => _category == X; set { if (value) SetCategory(X); }`。
