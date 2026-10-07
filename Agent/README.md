# TxAgent — Process Simulate AI Agent / 进程内 AI 助手

An in-process AI assistant for Tecnomatix Process Simulate, with scene tools, reusable C# / Python recipes and multi-instance collaboration.

源码版本：2026-10-07。TxAgent 在 Process Simulate 2402 中提供聊天、工具调用、代码片段、配方和多实例协作。入口是 `TxAgentCommand.cs`，详细项目环境见 [总 README](../README.md)。

## 环境与启动

- 主插件为 .NET Framework 4.8 / x64；`TxTools.csproj` 当前设置 `LangVersion=8.0`，PS 脚本与配方中的 C# 保持 C# 7.3 兼容。
- 聊天和配方界面使用 WebView2；模型请求由 C# 客户端发出。桌面宠物使用独立 Electron 进程，不在 PS 内解码动画。
- 注册 `TxAgent` 命令并打开窗口，在界面配置提供商、模型和 API Key。源码提供 DeepSeek、Kimi、千问、OpenAI、Ollama 及自定义 OpenAI 兼容提供商；具体可用模型取决于端点配置。
- Key 按提供商分别存入 `{providerId}.key`，由 Windows DPAPI 绑定当前用户加密。优先使用插件目录，不可写时回退到 `%LOCALAPPDATA%\TxTools.Agent`；更换用户或机器后需重新配置。

## 使用流程

1. 先通过场景查询、搜索和操作列表读取真实工程数据。
2. 工程变更前检查工具参数或代码审批；支持撤销的操作完成后可在 PS 中回退。
3. 复杂任务可建立计划，将验证过的 C# 或 Python 代码保存为带参数的配方。
4. 在侧栏或快捷配方窗口选取当前研究的对象、填写参数并执行，不需要再次调用模型。

## 主要能力

| 能力 | 当前实现 |
|---|---|
| 场景与机器人 | 对象搜索/选中、操作与焊点统计、机器人/TCP、参考系、可达性摘要、碰撞组与仿真控制 |
| 导出与外部系统 | Excel 表格及点位导出、CATIA 产品树与焊枪导出、视口/窗口截图、CEE/PLC 相关工具 |
| 代码与工作区 | `run_csharp`、`run_python`，源码大纲、读取、搜索、精确编辑、创建、回滚和编译 |
| 配方与片段 | Markdown 配方、参数与执行按钮、导入导出、来源记录、执行统计、共享执行锁 |
| 历史与知识 | 多对话历史、Facts/Gotchas、片段库、Markdown 知识库和按需检索 |
| 模型路由 | 主对话尊重界面所选模型；视觉、轻量和长上下文任务按能力与已配置的端点选择候选 |
| 多 PS 实例 | 实例发现、命名管道 RPC、跨实例只读执行与对比；外部 MCP 接入见 [McpBridge](../McpBridge/README.md) |

## 配方格式与控件

配方以 Markdown 保存完整代码、参数及可选执行按钮，读取和写入由 `Core/RecipeStore.cs` 负责。旧 `recipes.json` 不等同于当前可执行 Markdown 配方。

- 参数变量名为英文标识，界面标题和说明可用中文。类型支持 `object`、`objects`、`number`、`text`、`bool`、`color`。
- 文本和数字可通过 `choices` 提供下拉选项；颜色使用色盘和常用色块，值为 `#RRGGBB`。
- 对象参数启用 `objectFilter` 后可按类型和名称关键词取当前选择或查找当前研究。更改条件或研究会清除旧绑定；对象 ID 不写入默认值、按钮参数或标量偏好。
- `actions` 为同一份代码定义多个执行按钮，`args` 覆盖标量参数。模型调用时通过 `__recipe_action` 选择按钮；执行端重新校验参数及按钮覆盖值。
- 侧栏与快捷窗口均支持参数记忆和恢复默认值。所有按钮都已覆盖的参数可隐藏输入框；没有按钮声明的旧配方继续显示单个执行按钮。
- `save_recipe` 从片段固化时传 `source_snippet`，更新时传 `id`，保留配方身份、来源和运行记录；导入导出同时保留参数选项和按钮定义。

按钮示例（用于配方的执行按钮 JSON 数组）：

```json
[
  {"id":"show","label":"显示","args":{"show_objects":"true"}},
  {"id":"hide","label":"隐藏","args":{"show_objects":"false"}}
]
```

默认配方覆盖显示/隐藏、原点/几何中心坐标、常用色与自定义色、对象编号，以及从标记几何生成焊点。升级仅替换与旧内置定义完全匹配的文件，先备份并保留运行记录；用户修改或删除的默认配方不会强行恢复。

## 启动与对象类型

命令注册阶段设置 5 秒延后定时器及退出事件。主窗口出现且消息循环可处理定时消息后，捕获 PS 主线程上下文，在后台读取配方、注册工具和启动实例服务。桌面宠物的文件检查和进程启动也放在后台；所有 SDK 调用及界面回调仍回到 PS 主线程。

对象类别在对象参数展开时从当前场景读取，按参数类型过滤；“刷新类型”同步新增和删除的类别。收起的侧栏与快捷窗口列表页不扫描场景。中文类型映射维护于 `RecipeUiActions`，标签重名时显示原始类型名，未知类别保留 SDK 名称。

`audit.log` 的 `[Startup]` 记录是后台初始化耗时，不是 PS 完整启动时间。

## 桌面宠物组件

源码包含适配脚本；Electron 与动画素材按需恢复，不提交下载的运行组件。安装、固定版本和校验方法见 [桌宠组件说明](maintenance/DesktopPetComponents.md)。

```powershell
pwsh -File Agent/maintenance/Install-DshPetComponents.ps1
node Agent/UI/pet/dsh-pet-tools/validate.cjs
```

## 线程、审批与执行

`PsContext` / `PsAgentHost` 负责将 SDK 调用封送回 PS 主线程，工具内不得另起线程直接访问工程对象。模型调用按工具只读/变更属性进入审批和审计；界面直接运行配方使用既有撤销块与共享执行锁。可撤销性取决于具体工具，不应假定 Python、文件或外部系统写入都能由 PS 撤销。

## 开发入口

| 目录/文件 | 职责 |
|---|---|
| `TxAgentCommand.cs` | 命令、工具注册、延后服务启动、桌面宠物与快捷窗口 |
| `Core/Harness/` | 通用 Agent 循环、宿主适配与工具适配，见 [接入说明](Core/Harness/README_Harness接入.md) |
| `Core/RecipeStore.cs` / `RecipeRunner.cs` | 配方解析、校验、迁移和参数化代码生成 |
| `Core/RecipeUiActions.cs` | 对象查询、动态类别与界面执行桥接 |
| `Core/Multi/` | 实例发现与命名管道服务 |
| `Tools/` / `Ps/` | 工具定义与 PS 访问辅助 |
| `UI/` | WinForms、WebView2 聊天、配方侧栏及快捷窗口 |
| `recipes/defaults/` | 当前内置配方 |
| `maintenance/recipe-migrations/` | 默认配方旧版精确匹配基准 |

添加工具时实现 `ITxAgentTool`，声明名称、说明、JSON Schema 和只读属性，再在 `BuildToolRegistry()` 中注册。工具名需符合客户端 API 的英文标识限制；业务 SDK 调用通过主线程封送路径执行。

## 验证与 2026-10-07 更新

本次更新配方控件、动态对象筛选、默认配方迁移、后台启动与桌宠组件恢复说明，并将新增迁移文件明确纳入嵌入资源。资源校验使用 64 位 Windows PowerShell，兼容 32 位 MSBuild 检查 x64 插件。

[配方及启动回归说明](maintenance/recipe-controls-tests/README.md) 包含 C# 回归、JavaScript 界面验证、预览和编译后资源检查命令。历史记忆维护记录见 [maintenance/README.md](maintenance/README.md)，其中的部署日期与路径属于历史记录。

回归测试与源码编译不能替代 PS、CATIA 和真实模型服务的实机验证；本次验证范围见 [项目更新日志](../README.md#十三更新日志)。


## 撤销与恢复（2026-10-07）

C#、Python 及相关工程写工具使用场景撤销分组；开启失败阻止写入，关闭失败报告错误。执行失败不代表自动回滚。独立模式恢复点为已导出并校验的工程快照，外部文件和应用写入需另行恢复。

详见 [统一撤销与恢复说明](../docs/undo-safety.md)。
