# TxAgent.Core Harness 接入说明

本目录（`Agent/Core/Harness/`）存放从 `files (83).zip` 引入的**通用 agent 编排骨架**（`TxAgent.Core`）以及把它接入现有 TxAgent 项目所需的**适配/桥接层**。

骨架本身不引用任何 Tecnomatix / CATIA / Process Simulate 类型，是模型无关、宿主无关的纯 agent 循环。接入采用**非侵入式**设计：新旧两套循环引擎通过 `IAgentLoop` 接口共存，UI 只依赖接口，默认走旧引擎，改一个开关即可切换。

---

## 一、目录结构

```
Agent/Core/Harness/
├── 骨架（原样引入，namespace = TxAgent.Core，勿改）
│   ├── Messages.cs        ChatMessage / ToolCall / MessageRole
│   ├── ITool.cs           ITool / ToolResult / ToolSchema
│   ├── IAgentHost.cs      IAgentHost / HostMode / RestorePoint
│   ├── ILlmClient.cs      ILlmClient / LlmRequest / LlmResponse
│   ├── ToolRegistry.cs    工具注册表
│   ├── AgentSession.cs    单次会话状态（消息、pinned、token 预算）
│   └── AgentLoop.cs       核心循环：RunAsync / AgentLoopOptions / AgentRunResult
│
└── 适配 & 桥接（自研，namespace = TxTools.Agent.Harness）
    ├── PsAgentHost.cs         实现 IAgentHost —— 把 PS 宿主接进来
    ├── DeepSeekLlmClient.cs   实现 ILlmClient —— 桥接现有 DeepSeekClient
    ├── TxAgentToolAdapter.cs  实现 ITool —— 包装现有 ITxAgentTool
    ├── IAgentLoop.cs          新旧循环共享的 UI 契约接口（namespace TxTools.Agent.Core）
    └── HarnessAgentLoop.cs    实现 IAgentLoop —— 用旧 UI 表面驱动新 harness
```

---

## 二、接入架构

```
        TxAgentForm (UI)
              │  只依赖 IAgentLoop
      ┌───────┴────────┐
      ▼                ▼
  AgentLoop      HarnessAgentLoop         ← 二选一，由 UseNewHarness 开关决定
 (旧·自研)              │
                        │ 组装并驱动
                        ▼
                 TxAgent.Core.AgentLoop   ← zip 引入的骨架
                        │
        ┌───────────────┼───────────────────┐
        ▼               ▼                    ▼
   PsAgentHost   DeepSeekLlmClient   TxAgentToolAdapter × N
   (IAgentHost)   (ILlmClient)        (ITool，包住每个 ITxAgentTool)
        │               │                    │
        ▼               ▼                    ▼
   PsContext/       DeepSeekClient      现有 26 个 PS 工具（零改动）
   AuditLog/        (复用)
   TxApplication
```

**核心思路**：现有 26 个工具、`DeepSeekClient`、`PsContext`、`AuditLog` 全部**不动**，只在外面套三层适配器，再用 `HarnessAgentLoop` 把新引擎伪装成旧 `AgentLoop` 的样子交给 UI。

---

## 三、三层适配器职责

### 1. `PsAgentHost` : `TxAgent.Core.IAgentHost`
- **主线程封送**：构造接收 `SynchronizationContext`，`Invoke` / `Invoke<T>` 用 `_ctx.Send` 把工具执行封送回 PS 主线程（PS API 非线程安全）。
- **模式探测**：`Mode` 通过反射读 `TxApplication.IsTeamcenterConnected` 决定 `Connected` / `Standalone`。
- **确认弹窗**：`Confirm` 解析 harness 固定格式 `"工具：<name>\n参数：<json>"`，优先调 `ConfirmRequest` 委托，无委托时 fallback 到 `MessageBox`。
- **回滚点**：`CreateRestorePoint` 反射调 `TxApplication.ActiveDocument.Save()`。
- **日志**：`Log` 转 `AuditLog.Write`。
- 公开属性：`ConfirmRequest`、`AutoApproveTools`。

### 2. `DeepSeekLlmClient` : `TxAgent.Core.ILlmClient`
- `CompleteAsync` 把 harness 的 `LlmRequest` 翻译成旧 `ChatRequest`（消息 + 工具 schema 双向翻译），复用现有 `DeepSeekClient.SendAsync`，再把结果翻译回 `LlmResponse`。
- 用 `using` 别名消歧义：`ChatMessage = TxTools.Agent.Core.ChatMessage`（旧）、`ToolCall = TxAgent.Core.ToolCall`（新）。

### 3. `TxAgentToolAdapter` : `TxAgent.Core.ITool`
- 包装 `ITxAgentTool`，`Execute` 用 `host.Invoke(() => _tool.Execute(input))` 封送主线程，失败返回 `ToolResult.Fail(...)`**不抛异常**（交给 harness 的错误回灌自修机制）。
- 写/破坏性判定保守：`IsWrite = IsDestructive = !_tool.IsReadOnly`（所有写操作都会触发确认弹窗）。
- 静态工厂 `BuildHarnessRegistry(existing, host)` 一次性把整张旧工具表包成新 `ToolRegistry`。

---

## 四、如何开启 / 关闭新 harness

开关在 `Agent/UI/TxAgentForm.cs`：

```csharp
// 默认关闭，保证现有行为完全不变
private const bool UseNewHarness = false;
```

- `false`（默认）→ `BuildLoop` 走旧 `AgentLoop`，行为与接入前**逐字节一致**。
- `true` → `BuildLoop` 走 `HarnessAgentLoop`，改用 zip 引入的新循环引擎。

切换只需改这一个常量，重新编译即可。UI 其余代码只认 `IAgentLoop` 接口，无需改动。

---

## 五、已知取舍（切到新 harness 后的行为差异）

| 能力 | 旧 AgentLoop | 新 Harness | 说明 |
|------|:---:|:---:|------|
| 逐字流式输出 | ✅ | ✅(伪) | [P7] 最终消息切成片段逐字发出,模拟打字机效果;非真流式(边收边发) |
| 错误回灌自修 | ✅ | ✅ | harness 内建,连续失败 3 次熔断 |
| 主线程封送 | ✅ | ✅ | 由 PsAgentHost.Invoke 保证 |
| 写操作确认弹窗 | ✅ | ✅ | 由 PsAgentHost.Confirm + Adapter 的 IsWrite 保证 |
| 回滚点 | ✅ | ✅ | CreateRestorePoint(保存文档) |
| 记忆注入(Facts+Gotchas) | ✅ | ✅ | [P1] Reset/LoadHistory 时调用 BuildSystemPromptWithMemory 注入 |
| 历史压缩 | ✅ | ✅ | [P5] SendAsync 前按 MaxTurnsToKeep 压缩旧消息为摘要 |
| AutoSnippet | ✅ | ✅ | [P4] 每轮 SendAsync 开始时按需注入 Top-3 相关 Snippet,finally 移除 |
| AutoGotcha | ✅ | ✅ | [P3] TxAgentToolAdapter 里 run_csharp 输出含错误特征时自动落库 |
| 经验萃取 LessonExtractor | ✅ | ✅ | [P6] 复用旧 LessonExtractor,ExtractLessonsAsync 已接入 |
| `ask_user` 工具 | ✅ | ✅ | [P2] HarnessAgentLoop.Current 静态入口 + AskUserTool fallback 检查 |
| 记忆工具取 convId | ✅ | ✅ | [P2] TxAgentCommand 里 convIdGetter 同时检查 AgentLoop.Current / HarnessAgentLoop.Current |

**结论**：新 harness 的核心增值层已全部桥接接入(P1-P7)。旧引擎的逐字流式是真流式(边收边发),harness 当前是伪流式(收到完整消息后切成片段逐字发出);若需要真流式,需扩展 ILlmClient + AgentLoop 支持流式回调,改动骨架文件。

---

## 六、实际验证步骤（重要）

> ⚠️ 本次接入的代码已按接口契约**静态核对**（命名空间歧义、接口成员匹配、类型别名均已处理），但**未经编译**——当前环境无可用的 C# 编译器。请务必在 Visual Studio / PS 插件工程内实际构建验证。

建议按 README_接入说明.md 的渐进策略验证：

1. 在 Visual Studio 打开 `TxTools.csproj`，确认 `Agent\Core\Harness\*.cs` 全部 12 个文件已在编译列表（已注册）。
2. 先保持 `UseNewHarness = false` 编译一遍，确认引入骨架**不破坏现有构建**。
3. 把 `UseNewHarness` 改为 `true`，编译。若出现命名空间 `ChatMessage`/`ToolCall` 歧义报错，检查对应文件顶部的 `using` 别名。
4. 启动 PS 插件，先用 **2~3 个只读工具**（如查询类）跑通"提问 → 工具调用 → 返回"闭环。
5. 再测 **1 个写工具**，确认确认弹窗、主线程封送、回滚点正常。
6. 闭环无误后再全量放开 26 个工具（`BuildHarnessRegistry` 已自动包全表，无需逐个加）。

---

## 七、命名冲突备忘

`TxAgent.Core`（骨架）与 `TxTools.Agent.Core`（现有）都定义了 `ChatMessage` 和 `ToolCall`。规约：

- **旧类型**用 `using` 别名指向裸名：`using ChatMessage = TxTools.Agent.Core.ChatMessage;`
- **harness 类型**一律**全限定**：`new TxAgent.Core.AgentLoop(...)`、`new TxAgent.Core.AgentSession(...)`。

改动这些文件时请保持该约定，避免歧义编译错误。
