# TxTools 更新日志

本文件记录 TxTools 主分支的主要功能、兼容性和文档变化。日期以中国标准时间为准。

## 2.1.3 — 2026-09-15

### 新增

- **TxAgent 进程内 AI 助手**：在 Process Simulate 内通过工具调用查询和操作场景，工具执行统一回到 PS UI 主线程。
- **多模型支持与路由**：内置 DeepSeek、Kimi、千问、OpenAI、Ollama，并支持自定义 OpenAI 兼容端点；根据对话、视觉、轻量任务和长上下文场景选择模型。
- **TxAgent 工具体系**：增加场景查询、对象搜索/选中、操作与焊点分析、机器人/TCP 信息、可达性、碰撞组、对象位置、仿真控制、截图与图像分析等能力。
- **源码工作区能力**：支持在限定工作区内读取、搜索、创建、精确修改、回滚和编译 C# 源码；变更操作经过审批并保留备份。
- **记忆、知识和配方**：支持多对话历史、Facts/Gotchas、Markdown 知识库、代码片段、语义检索、任务计划和可复用配方。
- **多 PDPS 实例协作**：支持实例发现、跨环境只读执行和结果对比，并提供无界面 RPC 执行器。
- **CrossEnvIO**：新增跨环境结构、组件、焊点和数学数据的导入、导出与重建辅助工具。
- **McpBridge**：新增通过 stdio 向外部 Agent 暴露 PS 工具的桥接子项目。
- **ExportByColor 通用导出**：支持按资源导出 CGR、STL、OBJ、PLY、FBX，并支持设备合并、大网格分批处理、原点变换和 CATIA Product 导入。

### 改进

- 完善 `ExportByColor` 的资源树展开、可见性筛选、设备去重、Detailed 表示读取、颜色分组、CGR 拓扑处理和失败恢复日志。
- CGR 编码增加 compact95 路径、表面方向处理、退化面过滤、开放网格背面策略、边线兼容路径和大网格分块。
- STL 增加 RGB 旁车清单；OBJ 输出 MTL 材质；PLY 输出面颜色；FBX 支持二进制和 ASCII 两种格式。
- 完善 AutoPath 的碰撞世界、碰撞组、枪轴和焊接路径规划逻辑。
- 更新 AutoRecorder、CatiaPartTree、ExportGun、LineToSolid、ThemeTuner、WeldGunDefiner、WeldSpotAllocator 和 WeldSpotGrouper 的实现与界面。
- 统一部分窗口的 DPI、主题、日志和控件布局处理，提升 PS 宿主内的显示一致性。
- TxAgent API Key 使用 Windows DPAPI 加密保存；插件目录不可写时回退到用户本地数据目录。
- 记忆和知识类数据由 JSON 逐步迁移到按条目保存的 Markdown 文件，改善可读性、并发性和 Git diff 质量。
