# TxTools 使用与开发手册（中文）

版本：2026-10-07
说明：本文件基于当前仓库代码整理，面向插件使用者与二次开发者。`Agent/` 为当前版本已纳入主项目的 TxAgent 实现，详细设计说明见 [`Agent/README.md`](Agent/README.md)。

---

## 一、项目概述
TxTools 是面向机器人焊接工艺的 Process Simulate（Tecnomatix）二次开发插件集合。基于 `Tecnomatix.Engineering` SDK，提供一组常用功能以辅助焊接仿真与后处理，包括：焊枪/焊点导出、焊点可视化（点球）、机器人可达性检查、由曲线生成实体、沿线生成围栏，以及可在 PS 进程内直接查询和操作场景的 TxAgent 智能助手。

设计要点：
- 在不同 PS 版本下采用防御式 API 探测（dynamic + 多路径 try/catch）。
- 统一的 GUI 风格：基于 TxForm + TxFlexGrid 的卡片式面板与日志。
- 批量操作包裹在 UndoScope 中，支持 Ctrl+Z 回滚。
- 对外部系统（如 CATIA）使用桥接层以集中管理 COM 互操作。
- TxAgent 的 PS 调用统一经过 `PsContext` / `PsAgentHost` 回到 UI 主线程，变更工具默认需要审批并记录审计日志。
- 记忆、配方和知识库使用可读的 Markdown 文件保存，方便查看、版本控制和迁移。

---

## 二、环境与依赖
- Process Simulate：推荐 2402（其它版本可能可用但未全面测试）。
- .NET Framework：4.8（与 PS 宿主匹配）。
- C# 语言：主项目 `LangVersion=8.0`；PS 脚本及运行时代码片段保持 C# 7.3 兼容。
- Visual Studio：使用支持 .NET Framework 4.8 的 VS/MSBuild；仓库提供 `TxTools.slnx`，旧版 VS 可直接打开 `TxTools.csproj`。
- 可选：CATIA V5（用于 CATIA 相关功能的桥接）。
- TxAgent UI：WebView2（聊天面板）；`run_python` 需要目标机器提供可用的 Python 环境。
- TxAgent 网络模型：DeepSeek、Kimi、千问、OpenAI，或本机 Ollama；也支持添加 OpenAI 兼容的自定义提供商。

编译时常用引用：
- Tecnomatix.Engineering.dll
- Tecnomatix.Engineering.Ui.dll
- System.Windows.Forms、System.Drawing 等

构建配置：Release / AnyCPU；主插件输出为 x64 DLL，连同资源部署到 PS 插件目录。JT 解码器的 `Dependencies/` 还需准备未纳入 Git 的第三方 DLL，详见 [ExportByColor 构建依赖](ExportByColor/README.md#jt-解码器构建依赖)。

---

## 三、快速安装与注册
1. 克隆仓库并在支持 `.slnx` 的 Visual Studio 中打开 `TxTools.slnx`，或直接打开 `TxTools.csproj`；先还原 `packages.config` 中的 NuGet 依赖。
2. 配置 `ProcessSimulateDllDir` 为实际 PS SDK 目录；当前默认是 `G:\Program Files\Tecnomatix_2402\eMPower`。CATIA COM 引用还需要本机对应类型库。
3. 选择 Release / AnyCPU 配置生成；主项目实际 `PlatformTarget=x64`。默认直接输出至 G 盘 PS 插件目录，检查或独立构建时建议覆盖 `OutputPath` / `OutDir`。
4. 将生成的插件文件夹（DLL + Resources）复制到：  
   `<Tecnomatix 安装目录>\eMPower\DotNetCommands\<PluginName>\`  
5. 进入 `<Tecnomatix 安装目录>\eMPower\`，运行 `CommandReg.exe`：  
   - Assembly：选择插件 DLL。  
   - Class(es)：勾选要注册的 `TxButtonCommand` 类（如 ExportGunCmd、LineToSolidCommand 等）。  
   - Product(s)：通常勾选 Process Simulate。  
   - File：选择或新建 XML（例如 TxTools.xml），点击 Register。  
6. 启动 Process Simulate → Customize → 将新命令拖到工具栏。  
卸载：再次运行 CommandReg.exe，选择相同 XML，点击 Unregister。

---

## 四、主要插件与功能简介

以下为全部 23 个模块的使用说明入口；“本次变化”以本轮 F 盘源码同步为准。

| 模块 | 用途 | 本次变化 |
|---|---|---|
| [TxAgent](Agent/README.md) | 进程内 AI、工具与配方 | 配方控件、迁移、动态类型、后台启动、桌宠组件 |
| [ExportGun](ExportGun/README.md) | 导插枪、坐标与 CATIA 导出 | 对象/TCP/参考系拾取退出 |
| [ExportByColor](ExportByColor/README.md) | 按颜色导出 CGR 和网格 | 资源与原点拾取退出 |
| [WeldAnnotator](WeldAnnotator/README.md) | 焊点截图及 Excel 标注 | 独立显示会话、三态恢复与事务保护 |
| [AutoPath](AutoPath/README.md) | 自动过渡点与路径规划 | 操作拾取退出 |
| [AutoFance](AutoFance/README.md) | 沿基线生成围栏 | 基线拾取退出 |
| [LineToSolid](LineToSolid/README.md) | 曲线生成实体 | 特征拾取退出 |
| [AutoRecorder](AutoRecorder/README.md) | 仿真自动录屏 | 操作拾取退出 |
| [CrossEnvIO](CrossEnvIO/README.md) | 跨 PS 环境传输和重建 | 资源/操作/原点拾取退出 |
| [RobotReachabilityChecker](RobotReachabilityChecker/README.md) | 机器人可达性检查 | 操作节点拾取退出 |
| [WeldGunDefiner](WeldGunDefiner/README.md) | X 枪运动学向导 | 几何与 TCP 拾取退出 |
| [WeldSpotAllocator](WeldSpotAllocator/README.md) | 焊点更新、分配与镜像 | 参考集/目标集拾取退出 |
| [WeldSpotGrouper](WeldSpotGrouper/README.md) | 按绑定零件分组焊点 | 范围节点拾取退出 |
| [McpBridge](McpBridge/README.md) | 外部 Agent 的 MCP stdio 桥 | 构建输出路径与说明 |
| [WeldPointMatrix](WeldPointMatrix/README.md) | 多操作点位矩阵与插入 | 为已有模块补齐 README |
| [CatiaPartTree](CatiaPartTree/README.md) | CATIA 产品树 | 核对文档，业务源码未变 |
| [RobotBaseChecker](RobotBaseChecker/README.md) | BASE0 检查与校正 | 核对文档，业务源码未变 |
| [DeviceZAligner](DeviceZAligner/README.md) | 设备 Z 向对齐 | 补充说明，保留原有不可正常运行状态 |
| [LibPathSync](LibPathSync/README.md) | PS 库目录同步 | 核对文档，业务源码未变 |
| [SelectButton](SelectButton/README.md) | 快捷选点与资源目录 | 核对文档，业务源码未变 |
| [ThemeTuner](ThemeTuner/README.md) | 统一主题配色 | 核对文档，业务源码未变 |
| [SnakeGame](SnakeGame/README.md) | 场景贪吃蛇 | 核对文档，业务源码未变 |
| [MechArena](MechArena/README.md) | 机械对战演示 | 核对文档，业务源码未变 |

- ExportGun（导插枪）  
  将焊枪与焊点数据从 PS 导出到 CATIA（CGR 放置）或 Excel，便于下游工艺校验与可视化。包含 PsReader 风格的数据提取与 CatiaBridge（CATIA COM 互操作）。

- DotBall（点球）  
  在焊点处生成球形几何，便于在 CATIA 等系统中直观显示焊点位置。

- RobotReachabilityChecker（可达性验证）  
  基于机器人模型、焊接操作点与关节限位，分析可达性、是否超限、是否满足 TCP 余量等。

- LineToSolid（曲线转实体）  
  将场景中的 Polyline / Line / Arc 拆分成段，并按用户指定截面（矩形/圆形）为每段生成独立 Solid（每段为一个零件）。支持圆弧自适应细分以满足最大弦高设置。

- FenceBuilder / AutoFance（围栏生成）  
  根据直线或折线基线按参数生成网片 + 立柱 + 可选底板。优先采用“单薄板 + 纹理”策略以显著降低 Solid 数量并保证可视效果，提供降级路径以适配不同 PS 版本的纹理 API。

- TxAgent（`Agent/`）  
  在 Process Simulate 进程内提供带工具调用的 AI 助手。支持场景查询、对象搜索/选中、操作与焊点分析、机器人与 TCP 信息、可达性摘要、碰撞组查询、对象位置和仿真控制；支持焊点/对象清单导出 Excel、CATIA 产品树读取与焊枪导出、视口/窗口截图与图像分析，以及 CEE/PLC 资源操作。

  TxAgent 还提供以下工程化能力：
  - 多模型与路由：DeepSeek、Kimi、千问、OpenAI、Ollama 和自定义 OpenAI 兼容端点；按视觉、长上下文和轻量任务选择合适模型。
  - 记忆与知识：多对话历史、Facts/Gotchas、可复用代码片段、Markdown 知识库和语义检索；数据优先保存在插件目录，不可写时回退到 `%LOCALAPPDATA%`。
  - 配方与计划：把经过验证的多步操作保存为带参数的可复用配方，并用计划工具管理复杂任务。
  - 源码工作区：在限定的工作区内读取、搜索、创建、精确修改、回滚和编译 C# 源码；修改类操作需要审批并自动保留备份。
  - 安全与并发：只读/变更工具分级、变更审批、Undo 回滚、审计日志；支持多个 PDPS 实例之间发现、只读执行和结果对比。

- CrossEnvIO（`CrossEnvIO/`）  
  提供跨环境结构、组件、焊点和数学数据的导入导出及重建辅助。
- McpBridge（`McpBridge/`）  
  将 PS 工具通过 stdio 暴露给外部 Agent；主项目构建时会联动编译该子项目（存在其项目文件时）。

仓库还包含：AutoRecorder、DeviceZAligner、WeldAnnotator、WeldSpotAllocator、SelectButton 等模块（详见各子目录）。

---

## 五、典型使用流程（示例）
LineToSolid：
1. 在 PS 中选中一个或多个曲线特征。  
2. 菜单 → TxTools → LineToSolid，点【从选择添加】加入特征列表。  
3. 选择截面类型（矩形/圆形）、输入尺寸（mm），如有圆弧调整“最大弦高”。  
4. 点击【生成几何体】，操作包裹在 UndoScope 中，支持 Ctrl+Z 撤销。

FenceBuilder（围栏）：
1. 在场景准备直线或多段线基线（首端 Z 作为地面）。  
2. 菜单 → TxTools → 围栏生成器，选择基线并调整参数（网片宽/高、立柱尺寸、间隙、底板、纹理）。  
3. 点击【生成围栏】；若不满意使用 Ctrl+Z 或【撤销上次】。

ExportGun：
1. 在插件窗体加载或选择焊点集合/焊枪对象。  
2. 选择导出目标（Excel / CATIA），配置输出选项并执行导出。

TxAgent：
1. 在 PS 中注册并打开 `TxAgent` 命令，首次使用时按所选模型提供 API Key；API Key 使用 Windows DPAPI 加密保存。  
2. 先用只读工具查询真实场景数据，例如 `query_scene`、`find_objects`、`list_operations`、`check_reachability` 或 `api_lookup`。  
3. 需要修改场景时，检查审批对话框中的工具参数或生成代码；通过审批后执行，支持撤销的操作可使用 Ctrl+Z 回滚。  
4. 复杂任务可先使用 `update_plan`，将验证过的步骤保存为配方；需要导出时使用 `export_table`、`export_points_excel`、`export_object_list` 或文档导出工具。

---

## 六、TxAgent 重要约定

- `Agent/TxAgentCommand.cs` 是 TxAgent 的 PS 命令入口；工具注册、窗口生命周期和多 PDPS 无界面执行器都从这里接入。
- `Agent/Core/Harness/` 提供与宿主解耦的 Agent 循环，`PsAgentHost` 负责 PS 主线程封送，`TxAgentToolAdapter` 负责把 TxTools 工具接入通用 harness。
- `Agent/Core/MdStore.cs` 是 Markdown 存储底座；知识库按 `##` / `###` 小节解析，目录摘要常驻提示词，正文按需检索，避免一次性占满上下文。
- `Agent/Core/ModelRouter.cs` 按 Chat、Vision、Cheap、LongContext 等任务场景选择模型；主对话模型仍以用户在界面中的选择为准。
- `run_csharp`、`run_python` 和源码变更工具属于高风险路径：执行前应先查 API/片段库并审阅代码，不能在后台线程直接访问 PS SDK。

## 七、实现细节与重要约定
- 圆弧离散（LineToSolid）：给定半径 r 与最大弦高 s，分段角度 θ = 2·arccos(1 - s/r)，总扫掠角 / θ 向上取整得到段数 n，然后等分生成点与直线段。  
- 姿态对齐：段方向 dir = normalize(End - Start) 作为局部 Z；选取不共线的辅助轴以生成局部 X、Y，组装 4×4 变换赋给几何的 AbsoluteLocation。  
- 围栏几何约定（FenceBuilder）：强制水平投影、立柱两端放置与段内定间距生成、角点合并规则、网片宽度按中心距减去立柱与间隙计算等。  
- 纹理/颜色设置采用分级回退：若纹理 API 不可用则退为半透明纯色；颜色构造尝试多种构造形式以兼容不同 PS 版本接口。

---

## 八、常见问题与排查建议
1. 找不到或无法访问 PS API 成员：检查当前引用的 PS SDK 版本，使用 IntelliSense 确认实际类型/成员名；首次运行观察日志以确定实际生效的探测路径。  
2. 插件未在 PS 中出现：确认 CommandReg 注册成功、选择的 XML 与产品是否正确、PS 是否重启。  
3. TxTransformation 或几何姿态异常：尝试不同的矩阵构造路径（ctor、SetMatrix、Matrix 属性等），并观察日志中回退路径。  
4. 纹理无法加载或颜色异常：检查资源是否随 DLL 部署或嵌入资源是否正确解包，纹理 API 有多级降级逻辑。  
5. 圆弧顶点/属性读取失败：模块尝试多种候选属性名（Center、Radius、Start/End、Normal/Axis、SweepAngle 等），请以日志为准固化正确字段。  
调试建议：在小场景下快速验证几何与姿态，再扩展到大场景；启用并审阅内置日志以判断探测路径与 API 调用结果。

---

## 九、打包与发布建议
- 资源（如 mesh_pattern.png）可随 DLL 同目录下的 Resources 文件夹一起发布，或嵌入并在运行时正确解包。  
- 插件发布前在对应 PS 版本上做回归测试（测量 Solid 数量与生成耗时），以避免在生产场景中出现性能问题。  
- 对于 CATIA 交互功能，确保目标机器上已安装所需的 CATIA COM 组件并做好权限/COM 注册验证。

---

## 十、开发与贡献要点
- 主项目当前编译配置为 C# 8.0；PS 脚本与运行时代码片段遵守 C# 7.3 兼容约定。贡献指南中的 7.3 约定应与该运行时限制一起理解。
- 所有不确定的外部 API 都应优先用 dynamic + try/catch 多路径探测，稳定后可替换为强类型以提升性能。  
- 每个 `TxButtonCommand` 的 GUID 顶部注解请确保为合法 GUID（使用 VS Create GUID 工具生成）。  
- 贡献前请阅读并遵守仓库根目录的 CONTRIBUTING.md。欢迎 Issues 与 PR。

---

## 十一、许可与联系方式
- 授权：MIT（详见 LICENSE 文件）。  
- 欢迎在 [PDPS_TxTools 仓库](https://github.com/ShuiYinMaster/PDPS_TxTools) 中提交 Issue 与 PR。

---

## 十二、附录：仓库中应关注的模块与文件（供开发者快速定位）
- ExportGun/：导出相关实现（PsReader、ExportGunForm、CatiaBridge 等）。  
- LineToSolid/：曲线离散、几何构建、姿态对齐的实现。  
- AutoFance/（FenceBuilder）：围栏布局、几何构建、纹理处理。  
- RobotReachabilityChecker/：可达性检查逻辑。  
- Agent/：TxAgent 入口、harness、模型路由、工具、记忆/知识库、配方和 UI。  
- CrossEnvIO/：跨环境对象、组件、焊点和结构数据处理。  
- McpBridge/：面向外部 Agent 的 stdio 桥接程序。  
- 共享工具与资源：Common/、Image/、SRC/、Resources 等。

---

（文档由当前仓库代码、`Agent/README.md` 与子模块说明整理而成）


## 十三、更新日志

### 2026-10-07 — F 盘源码同步与文档更新

本次以 `F:\Process插件\TxTools` 的源码为来源，同步相对 GitHub 基线 `e50adbb` 的未提交改动。以下日期为同步与文档整理日期，不代表每项功能的最初开发日期。

- **TxAgent 配方**：新增多执行按钮、文本/数字选项、颜色控件、参数记忆和恢复默认值；对象按当前研究的动态类型和名称筛选，条件或研究变化时清除旧绑定。模型调用与界面执行均校验按钮覆盖参数。
- **默认配方升级**：更新显示/隐藏、坐标、颜色、编号及标记几何焊点配方；对未修改的旧内置定义做精确匹配、备份和迁移，保护用户编辑、删除及运行记录。当前 5 份默认定义与 9 份迁移基准明确嵌入 DLL，并校验内容。
- **启动响应**：命令注册只安排延后启动；后台初始化工具、实例服务和桌宠，SDK 调用仍封送到 PS 主线程，隐藏/收起的配方界面不扫描场景类型。
- **公共拾取**：新增 `PickFocus` / `PickAwareTxForm`，覆盖对象网格、对象框、Frame 编辑框和下拉框。11 个插件窗口接入 Esc 退出拾取、保留已选对象及再次激活拾取；支持 PS 宿主键盘路径和视口消息。
- **焊点标注显示恢复**：新增窗口独立的 `DisplaySession`，替代 `PsReader` 中旧的布尔快照逻辑；保留 `None / Partial / All`，恢复父子可见关系，校验白名单 ID、文档身份及撤销事务，失败保留恢复依据。
- **构建与维护**：主项目/MCP 桥默认 SDK 与输出路径调整到 G 盘；修正 32 位 MSBuild 调用资源检查时误用 32 位 PowerShell，以及覆盖输出目录时 JT 解码器未按预期复制的问题。新增桌宠组件下载/校验说明及回归工具，下载组件、本地工作树和编译产物不进入提交。
- **文档**：更新全部模块 README，补齐已有 `WeldPointMatrix` 的说明；修正 TxAgent 旧配方说明、主项目语言配置、解决方案入口及 MCP 产物路径；保留 DeviceZAligner 的既有运行问题标记。
- **仓库地址**：GitHub 仓库已更名为 `ShuiYinMaster/PDPS_TxTools`，项目链接使用新地址。

验证结果：配方控件 108 项、配方分享 39 项、拾取焦点 369 项、显示会话 17 个场景、启动响应 8 项，以及配方侧栏/快捷窗口/Markdown JavaScript 回归全部通过。完整 Release 构建通过，包含 TxTools、MCP 桥和 JT 解码器，14 份嵌入配方资源与源码一致；仍有 COM 封送、重复编译项及未使用成员等既有警告。PS、CATIA、Excel 的实际工程操作及在线模型服务未在本次运行验证。

独立构建时可在 VS Developer PowerShell 运行（按本机路径调整 SDK）：

```powershell
$buildOut = Join-Path (Get-Location) 'artifacts\release'
MSBuild.exe TxTools.csproj /p:Configuration=Release /p:ProcessSimulateDllDir="G:\Program Files\Tecnomatix_2402\eMPower" "/p:OutputPath=$buildOut\" "/p:OutDir=$buildOut\"
```

资源校验在构建后自动执行；详细回归命令见 [配方与启动测试](Agent/maintenance/recipe-controls-tests/README.md) 和 [显示会话测试](WeldAnnotator/README.md)。
