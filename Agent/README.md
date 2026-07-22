# TxAgent — PDPS 进程内 AI 助手插件 (DeepSeek)

在 Process Simulate (PS 2402) 内嵌一个 AI 智能体，通过工具调用查询/操作场景。
**原生 C# 进程内方案**：直连 DeepSeek (OpenAI 兼容) 的 tool-calling 循环，所有工具执行都落在 PS 的 UI 主线程上，
不依赖 Node、不跨进程，最稳。

## 架构一览

```
TxAgent/
  TxAgentCommand.cs          插件入口 (TxButtonCommand)，注册工具、开窗
  Core/                      与 PS 解耦的 agent 核心 (可独立编译/单测)
    LlmModels.cs             DeepSeek/OpenAI 的请求/响应 DTO (messages, tools, tool_calls)
    DeepSeekClient.cs        直连 /chat/completions 的 HTTP 客户端 (Bearer + TLS1.2)
    KeyStore.cs              API key 的 DPAPI 加密落盘 (插件文件夹优先)
    PsContext.cs             把 PS 调用同步路由回主线程 (抽自 ExportService.OnPs)
    ITxAgentTool.cs          工具接口 + 可选基类
    ToolRegistry.cs          工具注册表
    AgentLoop.cs             编排循环 (tool_calls 语义 + 线程模型说明)
    Recipe.cs                配方数据模型 (步骤 + 参数模板)
    RecipeStore.cs           配方持久化 (recipes.json，插件文件夹优先)
    RecipeTool.cs            把配方包装成工具 (参数替换 + 只读继承 + 递归保护)
  Ps/
    PsBridge.cs              ★唯一需按现有 PsReader/各插件适配的文件
  Tools/
    SceneQueryTool.cs        只读示例 (免审批直跑)
    AlignDevicesZTool.cs     变更示例 (执行前需审批，可 Ctrl+Z 撤销)
    SaveRecipeTool.cs        save_recipe：把多步操作存成可复用配方
    ListRecipesTool.cs       list_recipes：列出已保存配方
  UI/
    TxAgentForm.cs           聊天窗口 (DPI/字体规范 + Name=FullName 修复)
    ApiKeyDialog.cs          API key 输入弹窗 (掩码/可显示)
```

## 依赖与引用

- 目标框架：.NET Framework 4.8，C# 7.3
- `Tecnomatix.Engineering`（PS SDK；主窗口继承 `TxForm`）
- `TxTools.Common`（你的 `FormUiKit`；UI 框架与导插枪保持一致）
- `MyPlugin.ExportGun`（`PsBridge` 引用 `PsReader`，仅此一处）
- `System.Net.Http`、`System.Windows.Forms`、`System.Drawing`
- `System.Security`（DPAPI / `ProtectedData`，用于加密 key）
- `Newtonsoft.Json` 13.x —— 照 `libs\CatiaInterop\` 那套放 `libs\` 并用 `<Reference><HintPath>` 引用

## API Key

- 首次打开窗口若无已保存的 key，会弹窗输入；点“确定”后用 **DPAPI 按当前 Windows 用户加密**，
  保存为插件文件夹下的 `deepseek.key`（Base64 密文，非明文）。
- 之后每次打开自动解密复用；点顶部“设置 API Key…”可随时重输覆盖。
- 若插件目录不可写（例如部署在 `Program Files`），自动回退到 `%LOCALAPPDATA%\TxAgent\deepseek.key`，
  状态栏会显示实际写入路径。
- 解密绑定当前用户：换 Windows 账户或换机器需重新输入。

## 运行前提

- **网络**：PS 工作站需放行到 `api.deepseek.com` 的出站 HTTPS。
- **TLS**：客户端已在静态构造里启用 TLS1.2。

## 模型

窗口顶部可切换：
- `deepseek-v4-pro`（默认）—— 复杂推理 / agent 工具循环
- `deepseek-v4-flash` —— 高并发、低成本
- `deepseek-chat` —— 旧名，现映射 v4-flash 非思考模式，**2026-07-24 停用**，仅作兼容

## 线程模型（关键，别踩坑）

`AgentLoop.SendAsync` 从 UI 线程的 `async void` 事件发起。`await` 的网络 I/O 在线程池完成，
但**没有用 `ConfigureAwait(false)`**，续延会回到 UI 同步上下文 —— 于是 `tool.Execute(...)`
天然在 UI 主线程上运行，可安全调用 PS SDK。**切勿在工具内另起线程去碰 PS 对象**（这正是当年
`_selTimer` 轮询导致 PS 崩溃的同一类问题）。

## DeepSeek 工具调用的回合结构（与 Anthropic 不同）

1. 请求带 `tools`（`{type:"function", function:{name, description, parameters(JSON Schema)}}`）。
2. 响应里 `choices[0].message.tool_calls[]`，每项含 `id` 与 `function.arguments`（**JSON 字符串**，需 `JObject.Parse`）。
3. 把该 assistant 消息**原样**追加回去（含 tool_calls），再为每次调用追加一条 `role:"tool"` 消息
   （带 `tool_call_id` + 结果文本）。
4. 再次请求，直到模型不再返回 tool_calls。

## UI 框架（与导插枪一致）

主窗口 `TxAgentForm` 现继承 `TxForm`，走 `FormUiKit`（`TxTools.Common`）同款约定：
- `FormUiKit.InitStandardForm(this, title, designSize, minSize, sizable:true)`——
  以类型全名作持久化键消除跨插件几何串扰，`AutoScaleMode=None`，关闭 Siemens flat 皮肤。
- `OnLoad` 里 `FormUiKit.ApplyDpiScaling(this, ref _dpiApplied, DesignSize)`——
  先把 Size 重置为 96-DPI 设计尺寸再 `Scale`，防 TxForm 持久化尺寸逐次叠加放大。
- 控件用 `FormUiKit.MkButton/MkLabel` 自绘，绕过 PS flat 皮肤吃配色。
- 构造签名 `(SynchronizationContext psCtx, ToolRegistry tools)` 与 `ExportGunForm` 一致。

`ApiKeyDialog` 仍是普通模态 `Form`（轻量弹窗），如需也统一可改继承 `TxForm`。

回答采用**流式输出**：`DeepSeekClient.SendStreamAsync` 解析 SSE，边收边把文本分片回调到界面(`AssistantDelta`)，
并增量累积 `tool_calls` 分片;窗口端开一行【助手】→追加分片→工具调用或回合结束时闭行。

## 线程模型与卡死规避

PS SDK 不是线程安全的，所有 SDK 调用必须在主线程。要点：

- **agent 循环跑在后台线程**(`SendCurrentInput` 用 `Task.Run`)，PS SDK 调用由 `PsContext` 统一回主线程。
  这样网络/编译/思考期间窗口与 PS 保持响应，“停止”按钮能在步骤之间生效。
- **审批弹窗跨线程回 UI**(`AskApproval` 的 `InvokeRequired`/`Invoke`)。
- **`run_csharp` 编译(后台) 与 执行(主线程) 分离**：CodeDom 编译是纯 CPU、不碰 PS，放后台线程，
  不再为编译冻结 UI；只有真正碰 SDK 的执行回主线程。
- **固有限制**：单个重操作(如移动所有机器人)在主线程执行时，PS 必然短暂无响应——这点原生命令也一样，
  无法消除；一旦执行进入主线程也无法安全中断。所以系统提示要求 `run_csharp` 写**有界代码**、
  大批量**分批并 log 进度**；审批框也提示"执行期 PS 会无响应"。真遇到生成代码里的死循环，只能结束 PS 进程，
  因此**批准前务必读一遍代码**是最后防线。

## 记忆（多对话 + 方法记忆）

**多对话保留。** `ConversationStore` 是一个多对话库：每条对话存成 `conversations/{id}.json`(含标题/时间/消息)，
路径策略同 KeyStore/RecipeStore(回退 `%LOCALAPPDATA%\TxAgent`)。旧版单文件 `conversation.json` 首次访问时自动迁移成一条。
- `AgentLoop` 每轮结束触发 `HistoryChanged`，窗口据此把**当前对话**写回其文件(空对话不落盘)。
- 开窗时加载**最近一条**对话并重绘进界面；agent 据此“记得”该对话聊过什么。
- 顶部“新对话” = 存好当前对话(保留) + 开一条新的；“历史对话”按钮弹出列表，可打开或删除任意过往对话。
- token 成本：`AgentOptions.MaxHistoryMessages`(默认 40)在每轮发送前把上下文裁到“系统提示 + 最近 N 条”，
  切点对齐到 user 边界以保 tool_call 配对完整(设 0 关闭)。注意这只裁“喂给模型的上下文”，不删磁盘上的对话记录。

**方法记忆(给 codegen 路径)。** `SnippetStore` 把摸索出的可用 `run_csharp` 代码持久化到 `snippets.json`，跨对话复用：
- `save_snippet(name, description, code)` 存；`list_snippets(keyword?)` 查；`get_snippet(name)` 取完整代码。
- 系统提示要求：遇到要写代码的新需求时**先查片段库**(`list_snippets`/`get_snippet`)；用 `run_csharp` 跑通一个有价值的做法后**主动 `save_snippet`**，必要时再 `save_recipe` 固化成可一键调用的工具。
- 于是它不再“每次现学、学完不留”——摸清一次 API、存下可用代码，下次直接命中复用。

## 动态能力：探查 API + 自写代码（兜底）

当现成工具覆盖不到时，agent 可以"从内部读懂 API、再据此写代码"：

- **API 探查(只读)**：`list_types(keyword)` 在已加载程序集(优先 Tecnomatix)搜类型；
  `inspect_type(type_name)` 反射列出某类型的公共属性/方法/事件签名；
  `inspect_object(name?)` 探查活动对象(按名或当前选中)的运行时类型与各属性取值(接 `DiagnoseApi` 思路)。
- **自写代码**：`run_csharp(code)` 用 .NET 自带的 CodeDom 在 **PS 进程内**编译执行用户代码——
  代码作为方法体注入(已 `using Tecnomatix.Engineering`，可用 `TxApplication.ActiveDocument`、`log("…")`、`return` 结果)，
  引用所有已加载的 `Tecnomatix.*` 程序集。

  **安全门控**：`run_csharp` 是变更工具 → 每次执行**强制审批**，审批框**展示完整代码**供审阅；
  执行包在 **Undo 块**里(可 Ctrl+Z 撤销)；审批/结果写入 `audit.log`；编译/运行异常都回传(便于 agent 自我修正)。
  **约束**：自带编译器是 **C# 5** 语法(无字符串插值/`?.`/表达式体)；编译出的程序集无法卸载，宜偶发使用。
  要现代 C# 语法可引用 NuGet 包 `Microsoft.CodeDom.Providers.DotNetCompilerPlatform`。

  典型用法：`list_types("Collision")` → `inspect_type("TxCollisionRoot")` → `run_csharp(...)` 完成一个没有现成工具的操作。

## 任务机制：计划/待办

`update_plan(items)` 让 agent 把复杂多步任务拆成带状态的清单(整表替换，每项 `text`+`done`)。
系统提示要求：开始多步任务前先列计划、每完成一步更新状态。计划以工具结果进入对话历史，随记忆持久化。

## 自写工具：配方机制 (路径 A)

让 agent "自己写工具"在这里不等于生成代码，而是把**验证过的多步操作存成数据**——
配方 = 一串对现有工具的调用 + `{{参数}}` 模板。能力完全被你的原子工具集合框死，
没有编译器、没有新依赖，也不会引入超出现有工具的破坏面。

工作流：
1. agent 用现有工具跑通一个多步任务。
2. 觉得值得复用时，调用 `save_recipe(name, description, parameters, steps)`，
   `steps[].tool` 必须是已存在的工具名，`steps[].input` 模板里用 `{{参数名}}` 引用 `parameters`。
3. 保存会校验步骤工具都存在 → 写入 `recipes.json` → **即时注册成新工具**(下一轮即可调用)。
4. 启动时 `RecipeStore.Load()` 把已存配方注册回来。`list_recipes` 供 agent 查已有配方避免重复造；
   `delete_recipe(name)` 删除配方(注册表 + 磁盘，内置原语不可删)。

安全：
- 配方 `IsReadOnly` 按步骤**继承**——所有步骤都只读才免审批；任一步会改场景，整条配方执行前需确认。
- 审批在配方层一次性完成，内部步骤不二次弹窗。
- `save_recipe` 本身不改场景(只读)，但不允许用配方名遮蔽内置原语；配方间递归有深度上限。

局限(有意为之)：配方只能是"参数化的固定序列"，不支持分支/循环/条件——那类逻辑由 agent 在对话里
直接编排工具，配方只固化稳定可复用的部分。要真正的代码生成(`run_csharp`)是另一条高风险路径，本套未启用。

预置内置配方：启动时 `SeedDefaultRecipes` 注册两条开箱即用的只读配方(仅入内存、不写盘)——
`preflight_check`(列操作+焊点数+参考系)、`scene_overview`(类型直方图+当前文档)；用户同名配方优先，不覆盖。
注意配方步骤之间**不传递数据**(各步独立调用)，所以"先汇总再用汇总结果导出"这类需求由 agent 在对话里完成，
或用一步式的 `export_object_list`(数据流在工具内部)。

**让配方有用的前提是原子工具够细。** 现已内置只读原语：`query_scene`、`count_objects`(全场景按类型统计/枚举，回答"有多少机器人")、`list_children`(展开组件数子对象，回答"CD_L 下有多少设备")、`list_operations`、`count_points`、`list_tcp_options`、`check_reachability`(选中操作的快速可达性摘要，用 `GetPoseAtLocation` 判定，只读)、`get_reference_frame`；导出原语 `export_table`(把汇总信息写成 xlsx)；变更原语 `align_devices_z`(对齐选中设备最低点到 Z=0，跳过枪/机器人/工具，**需审批、可 Ctrl+Z 撤销**，由无界面的 `DeviceZAlignService` 忠实复刻 DeviceZAligner 的多策略实现)。

> 防脑补：`count_objects` / `list_children` 走真实的物理树遍历(`PhysicalRoot/ComponentRoot/ResourceRoot` + `GetAllDescendants`)，机器人用 `is TxRobot` 识别。系统提示已要求模型用这些工具取真实数据，而非从 `list_operations` 推断对象类型/数量。

> 信息汇总→导出：`export_table(headers, rows, …)` 用手写 Open XML(无外部库、Office365 兼容、inlineStr 全字符串)把任意表格写成 `.xlsx`，默认存到 `桌面\TxAgentExport`。配合上面的信息工具：先汇总，再导出。

> 查到→选中→操作：`select_objects(names)` 按名把对象设为当前选中(精确优先、模糊兜底)，打通信息工具与基于选择的工具；`export_points_excel(point_type,…)` 是 ExportGun 同款的真实焊点坐标导出(复用 `ExcelExporter`，含参考系转换)；`export_object_list(type_keyword)` 一步把匹配对象清单导出 xlsx(真实数据流，如机器人/设备清单)；`align_devices_z` 把选中设备落地到 Z=0(需审批、可撤销)。典型链路：`count_objects("Robot")` → `select_objects([...])` → `align_devices_z` / `export_points_excel`。

继续按 `PsReader` 的方法补更多细粒度原语，配方能组合的解法随之变多。所有对 `MyPlugin.ExportGun.PsReader` 的依赖都集中在 `PsBridge` 一个文件。

## 加一个工具（把现有 TxTools 接进来）

1. 在 `Tools/` 下实现 `ITxAgentTool`（或继承 `TxAgentToolBase`）：`Name` / `Description` /
   `IsReadOnly`（true 免审批，false 执行前弹确认）/ `InputSchema`（JSON Schema）/ `Execute`。
   注意工具名须为 a-z A-Z 0-9 `_` `-`，最长 64。
2. 把 PS 实际调用写进 `PsBridge`（`dynamic` + `try/catch` 兜版本差异）。
3. 在 `TxAgentCommand.BuildToolRegistry()` 里 `reg.Register(new YourTool());`

建议接入顺序：`check_reachability`（包装 RobotReachabilityChecker，只读）→ `export_gun` →
`build_fence` / `line_to_solid`（变更，需审批）。

## 安全与审批

- 工具按 `IsReadOnly` 分级：只读直跑，变更经 `AskApproval` 弹窗确认。
- `PsBridge` 是 PS 调用的唯一出口，便于集中做白名单与审计。
- `AgentLoop.ApprovalRequest` 未设置时**默认拒绝所有变更**，失效方向安全。
- 优先包装带 Ctrl+Z 撤销的操作（如 DeviceZAligner）。
- 审计：每次变更类工具的审批结果与执行结果(APPLIED/DENIED/FAILED + 工具名 + 入参 + 结果首行)追加写入插件文件夹 `audit.log`(`AuditLog`，尽力而为、失败静默)。

## 待办

- `PsBridge.AlignSelectedDevicesToFloor()` 现为 `NotImplementedException`，接入 DeviceZAligner 即可。
- 如要并入 FormUiKit：`TxAgentForm` / `ApiKeyDialog` 基类改 `TxForm`，构造里调 `FormUiKit.InitStandardForm(this)`。
