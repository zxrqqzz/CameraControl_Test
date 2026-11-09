# 06 · CUA 接入（AI 操控软件）

> 目标：配置大模型 API 后，用自然语言让 AI 操作本软件。
> 状态：**P6 主体已实现并可运行**（命令行向导）；WPF 集成与确认 UI 待 P4。

---

## 一、架构

```
用户目标（自然语言）
      │
      ▼
┌─────────────────┐
│    CuaAgent     │  循环：模型 ⇄ 工具
│  （Application） │
└────────┬────────┘
         │ ① 系统提示 = 规则 + 能力清单（Markdown）
         │ ② 模型返回 tool_calls
         ▼
┌─────────────────┐
│  ICommandBinder │  JSON 参数 → 强类型命令（AI 输入边界）
└────────┬────────┘
         ▼
┌─────────────────┐
│  ICommandBus    │  白名单 → 风险策略 → 执行 → 指标 → 审计
└────────┬────────┘
         ▼
┌─────────────────┐
│  能力实现        │  capture.group / device.list / system.* / cua.getCapabilities
└─────────────────┘
         │
         ▼
   结构化结果回填模型（ok / data / error{code,message,hint}）
```

**核心约束：AI 与人走同一条路径。** AI 没有任何绕过 `ICommandBus` 的通道，
因此风险策略、审计、指标对 AI 全部生效。

---

## 二、关键设计

### 2.1 工具名映射

OpenAI 兼容接口要求函数名匹配 `^[a-zA-Z0-9_-]{1,64}$`，而能力 id 用点分层：

```
capture.group  →  capture_group
cua.getCapabilities → cua_getCapabilities
```

映射与还原都走 `ToolName`，还原时**以能力清单为准**，避免歧义。

### 2.2 失败必须结构化回填

```json
{ "ok": false,
  "error": { "code": "AI_BAD_ARGUMENTS",
             "message": "cameraIds 不能为空。可先调用 device_list 查询可用相机 id。",
             "hint": null } }
```

AI 依赖 `code` 自我纠正。如果只回 "出错了"，模型会反复重试同一个错误。

### 2.3 结果截断

单次工具结果超过 **4000 字符**会被截断并标注 `…(已截断)`，
防止一次返回大对象（如完整能力清单）把上下文撑爆。

### 2.4 轮数上限

`MaxSteps`（默认 8，上限 32）。到达上限返回 `AiStopReason.MaxSteps`，
不会无限循环烧 token。

### 2.5 安全：物理操作无法被 AI 绕过

```
AI 请求 robot.moveTo
  → CommandBus 白名单校验
  → DefaultRiskPolicy：Physical 且 actor=Ai → 拒绝
  → 返回 CONFIRMATION_REQUIRED
  → 处理器**根本不会被调用**
```

这条有单元测试锁死（`CuaAgentTests.AI调用物理能力应被拒且处理器不执行`）。

### 2.6 API Key 存储

- 用 **Windows DPAPI**（`CryptProtectData`，当前用户作用域）加密后存 `llm.json`
- **永不落明文**；日志与审计只记录是否已配置，不记录密钥
- 换用户或换机器后无法解密，需重新输入（这是预期行为）
- 推荐 `--api-key-stdin`，避免密钥留在命令历史

---

## 三、配置与使用

### 3.1 配置模型

```powershell
cd tools\CameraControl.Tools.Agent

# 以 DeepSeek 为例（任何 OpenAI 兼容服务都可以）
dotnet run -- --configure --base-url https://api.deepseek.com/v1 --model deepseek-chat --api-key-stdin
```

会提示输入 Key（不回显），确认后打印：

```
  BaseUrl : https://api.deepseek.com/v1
  Model   : deepseek-chat
  Timeout : 60s
  API Key : sk-a…z9f1（35 字符）
  状态    : 已就绪
```

配置存放位置：`%LocalAppData%\CameraControl\llm.json`（Key 为 DPAPI 密文）。

### 3.2 下达任务

```powershell
dotnet run -- --goal "列出当前所有相机，并告诉我哪一台可以采集"
dotnet run -- --goal "用 Integrated Camera 拍一张，报告分辨率" --show-steps
```

`--show-steps` 会打印每一步的模型输出与工具调用。

### 3.3 常见服务配置参考

| 服务 | base-url 示例 |
|---|---|
| OpenAI | `https://api.openai.com/v1` |
| DeepSeek | `https://api.deepseek.com/v1` |
| 通义千问（兼容模式） | `https://dashscope.aliyuncs.com/compatible-mode/v1` |
| 本地 Ollama | `http://localhost:11434/v1` |

> 只要求服务支持 `chat/completions` + `tools`（function calling）。
> 不支持 function calling 的模型无法用于本功能。

---

## 四、已知限制（诚实清单）

| 限制 | 说明 | 计划 |
|---|---|---|
| 无流式输出 | 一次请求等完整回复，长任务感知慢 | P4 可加 |
| 无对话多轮记忆 | 每次 `--goal` 是独立任务，不保留上下文 | 可按需加会话存储 |
| **无人工确认 UI** | 物理操作对 AI 一律拒绝；人类走 UI 时视为已确认 | **P4 必须补确认弹窗** |
| 无工具执行预览 | AI 执行前不展示"将要做什么" | P4 补 dry-run 提示 |
| `cua.runTask` 未暴露为能力 | 避免 AI 递归调用自己 | 保持（有意为之） |
| 未做 token 预算控制 | 只有轮数上限 | 可加预算守卫 |

---

## 五、验证

```powershell
dotnet test CameraControl.slnx
```

覆盖 AI 循环的关键用例：

| 用例 | 锁住的行为 |
|---|---|
| 模型直接答复 | 正常收尾 |
| 工具调用 | 经总线执行、结果回填、审计记录 actor=Ai |
| 工具名 | 暴露为下划线形式，不含点 |
| **AI 调物理能力** | **被拒且处理器未执行** |
| 未知工具 | 回填 `AI_UNKNOWN_TOOL`，不崩溃 |
| 参数非法 JSON | 回填 `AI_BAD_ARGUMENTS` |
| 无限调工具 | 达上限停止，`MaxSteps` |
| 模型报错 | `LlmError` + 原始错误码 |
| 系统提示 | 含能力清单与硬性规则 |

> 测试全部使用脚本化假模型，**不联网、不消耗 token、结果确定**。

---

## 六、下一步

1. **P4**：WPF 里集成 AI 面板（配置、对话、步骤可视化、确认弹窗）
2. 为剩余能力补 `ICommandBinder`（`calibration.*` / `robot.*` 接入 AI 时）
3. 加 token 预算与成本统计（`system.getMetrics` 已有埋点基础）
4. 会话记忆与任务重放（审计日志已具备回放所需信息）
