# 13 · CUA 外部调用验证（已通过）

> 结论：**外部进程可以真正驱动这个软件**——开相机、取图（含可解码 PNG）、连续预览、
> 读统计、关相机，全部走与 AI 完全相同的命令总线。
> 同时验证了风险策略：`robot.moveTo` 被拒绝，AI 无法绕过。
> 验证日期：2026-11-10　验证机：本机（Win11 / .NET 10.0.13 / x64）

---

## 一、怎么验证的

给 `CameraControl.Tools.Agent` 加了 **`--replay <json>`** 模式：

```
能力清单(catalog) → DefaultCommandBinder → ICommandBus → 实际动作
```

**唯一区别**：正常情况下"调哪个能力"由 LLM 决定，回放模式下由 JSON 脚本决定。
其余代码路径完全相同——所以这验证的是真实的 CUA 链路，不是另写的一条捷径。

### 用法

```powershell
# 1. 先查设备 id
dotnet tools\...\Agent.dll --replay scripts\probe.json

# 2. 按 id 驱动（scripts/drive.json 里替换 cameraId）
dotnet tools\...\Agent.dll --replay scripts\drive.json
```

脚本格式：

```json
[
  { "capability": "camera.open", "arguments": { "cameraId": "USB\\VID_xxxx..." } },
  { "capability": "capture.single",
    "arguments": { "cameraId": "USB\\VID_xxxx...", "includeImage": true },
    "savePng": "D:\\out\\frame.png" },
  { "capability": "robot.moveTo",
    "arguments": { "x": 100, "y": 200, "z": 300, "rx": 0, "ry": 90, "rz": 0 },
    "expectError": "CONFIRMATION_REQUIRED" }
]
```

- `savePng`：可选。把返回的 base64 PNG 落盘（默认不落盘）
- `expectError`：声明"这一步应该被拒绝"，用于验证风险策略而不算失败

---

## 二、实测结果（11 步全部通过）

| # | 能力 | 结果 |
|---|---|---|
| 1 | `system.getEnvironment` | OK（Win11 / x64 / .NET 10.0.13） |
| 2 | `device.list` | OK，发现 2 台（1 台可采集 UVC + 1 台红外只列出） |
| 3 | `camera.open` | OK，332 ms |
| 4 | `capture.single` + `includeImage` | OK，1280×720 Bgra32，**返回有效 PNG** |
| 5 | `capture.getStats` | OK，帧计数正确累加 |
| 6 | `capture.startContinuous` | OK，15 fps |
| 7 | `capture.getFrame` | OK，659 ms 拿到首帧 |
| 8 | `capture.stopContinuous` | OK |
| 9 | `robot.moveTo` | ✅ **按预期被拒绝** `CONFIRMATION_REQUIRED` |
| 10 | `cua.getAuthorizationMode` | OK，`mode=Confirm` |
| 11 | `camera.close` | OK |

### PNG 校验（关键证据）

```
文件大小：1,076,181 字节
签名：89 50 4E 47 0D 0A 1A 0A（合法 PNG）
IHDR：1280×720，bitDepth=8，colorType=2（RGB），IEND 完整
```

打开确认：**画面内容正确、方向正确、无翻转无错位**。
说明 `FrameImage(Bgra32) → PngEncoder → RGB` 全链路正确，AI 通过这条路能真正"看到"。

---

## 三、两个发现的问题（已修）

### 1. `capture.getFrame` 刚启动预览时返回 null

`startContinuous` 是异步的，第一帧还没到就查询 → 返回 `frame=null`，
调用方（尤其 AI）会误判成"没在预览"。

**修复**：`capture.getFrame` 增加 `timeoutMs`（默认 1000），有界等待首帧。
实测首次查询等待 659 ms 后拿到帧。

### 2. 刻意验证的拒绝被算作失败

回放脚本把所有失败都记为 error，导致"验证风险策略"这一步让整体退出码为 1。
**修复**：支持 `expectError`，按预期拒绝时计为通过，且若本该拒绝却成功了则明确报失败。

---

## 四、隐私核对

- 验证过程中产生的 PNG **已删除**
- `%LocalAppData%\CameraControl` 下**图像文件数 = 0**
- 只有显式 `savePng` 才会落盘；默认路径只返回 base64，不写文件
- 红外相机仍为"只列出、不取流"

---

## 五、局限（要说清楚）

| 项 | 说明 |
|---|---|
| 未测真实 LLM | 回放模式证明"链路通"，不代表模型能正确选择能力；模型侧需真实 API Key |
| 机器人未接真机 | `moveTo` 只验证了**被拦截**，未验证真实运动 |
| 单相机 | 多相机同步未验证 |
| 标定链路未跑 | `calibration.*` 需要棋盘图，本机未拍 |
