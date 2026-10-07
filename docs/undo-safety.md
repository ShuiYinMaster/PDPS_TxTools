# Ctrl+Z 撤销与恢复

本说明对应 2026-10-07 开始的撤销逻辑审查与修复，覆盖总 README 中列出的 23 个模块。修复基于本机 Process Simulate 2402 SDK（`Tecnomatix.Engineering` 2402.9.0.1092）和实际源码，真实工程中的 Undo/Redo 行为仍需按下文验收。

## 场景事务的实际能力

SDK 的 `TxUndoTransactionManager` 提供无参 `StartTransaction()`、`EndTransaction()` 和 `ClearAllTransactions()`。其中 `ClearAllTransactions()` 会清除历史，不能用作失败回滚。本项目没有调用它，也不使用不存在的 `AbortTransaction`、`CommitTransaction` 或 `TxDocument.Save()`。

公共 [SceneUndoScope](../SRC/SceneUndoScope.cs) 将同一工具批次支持撤销的场景变更分组。嵌套工具共用外层事务；开始失败阻止写入，结束时使用原工程的管理器。结束失败明确报告错误，并阻止该文档继续执行相关写操作，直到重新打开工程。长任务放行 UI 消息后检查活动工程，避免继续写入已切走的工程。

结束事务不会自动恢复模型。工具失败时先查看日志和工程状态；**如已产生部分场景变更，在目标 PS 工程的图形窗口按 Ctrl+Z**。输入框可能先撤销文本，远程 MCP 工具的历史属于目标 PDPS 实例。不要在没有变更的情况下盲目撤销，避免撤销前一项用户操作。

事务保证调用配对和错误报告，不能保证所有 SDK 成员都受宿主历史管理，也不覆盖任意 C#/Python 脚本的文件、网络或外部应用副作用。

## 逐项修复清单

| 审查项 | 修复及恢复方式 | 主要源码 |
|---|---|---|
| F01：围栏撤回可能删除既有建模资源 | 按对象 ID 清理生成物；复用容器不登记所有权；新容器和分组仅在空时删除，后加用户内容会保留 | [SceneCreationBatch](../SRC/SceneCreationBatch.cs)、[FenceBuilderForm](../AutoFance/FenceBuilderForm.cs) |
| F02：五个写入层调用无效的事务接口 | 焊点分配、分组、CATIA 建树、结构和焊点重建统一使用真实 Start/End；部分失败结束分组并报告，需手动撤销 | [SpotWriter](../WeldSpotAllocator/Ps/SpotWriter.cs)、[GroupWriter](../WeldSpotGrouper/Ps/GroupWriter.cs)、[CatiaPartTreeService](../CatiaPartTree/Ps/CatiaPartTreeService.cs)、[StructureIO](../CrossEnvIO/Core/StructureIO.cs)、[WeldPointIO](../CrossEnvIO/Core/WeldPointIO.cs) |
| F03：Agent 写工具使用无效文档级事务 | C#、批处理和设备对齐改为公共场景事务，不再无事务继续执行或把空调用当成功 | [PsBridge](../Agent/Ps/PsBridge.cs)、[BatchTools](../Agent/Tools/BatchTools.cs)、[DeviceZAlignService](../Agent/Ps/DeviceZAlignService.cs) |
| F04：焊钳重建先按名称删除已有机构 | 创建的关节/Link 带 `TxTools.RprrOwner=RPRR-v1`；重建仅清理已标记关节；同名未标记机构、用户关节引用工具 Link 时停止。生成、TCPF 和定义处于同一外层批次；部分失败需 Ctrl+Z | [RprrBuilder](../WeldGunDefiner/Core/Rprrbuilder.cs)、[WeldGunService](../WeldGunDefiner/Core/WeldGunService.cs)、[WeldGunWizardForm](../WeldGunDefiner/UI/WeldGunWizardForm.cs) |
| F05：没有实际保存也报告恢复点成功 | 独立模式用 `SaveDataToFile(..., AllAttributes)` 导出唯一 `.psz`，存在且非空才报告成功；连接模式指向平台版本管理 | [StudySnapshot](../SRC/StudySnapshot.cs)、[PsAgentHost](../Agent/Core/Harness/PsAgentHost.cs) |
| F06：独立设备对齐假装事务成功 | 删除空条件反射的成功路径；直接调用公共事务，无法开启就停止 | [DeviceZAligner](../DeviceZAligner/DeviceZAligner.cs) |
| F07：围栏多基线、失败和清理记录丢失 | 一次生成收集全部基线；部分创建失败仍登记生成物；无新增物的失败尝试保留原批次；清理失败保留 ID 可重试。按钮更名“清理上次生成”，清理本身也是场景变更 | [FenceBuilderForm](../AutoFance/FenceBuilderForm.cs)、[FenceGeometryBuilder](../AutoFance/FenceGeometryBuilder.cs) |
| F08：几何、路径、BASE0、矩阵等缺少分组 | 曲线实体、路径规划、批量干涉集、BASE0、焊钳、矩阵与组件插入补齐事务；矩阵移动失败尝试恢复源操作和位置，补偿失败报告。几何建模失败尝试清理新空容器 | [GeometryBuilder](../LineToSolid/GeometryBuilder.cs)、[WeldPathPlanner](../AutoPath/WeldPathPlanner.cs)、[AutoPathPlannerForm](../AutoPath/AutoPathPlannerForm.cs)、[RobotBaseCheckerForm](../RobotBaseChecker/RobotBaseCheckerForm.cs)、[PointService](../WeldPointMatrix/PointService.cs)、[ComponentIO](../CrossEnvIO/Core/ComponentIO.cs) |
| F09：cojt 半复制未登记，清理误删或丢记录 | 复制到唯一暂存目录再发布；不覆盖已有目标；登记失败半复制。清理要求目录内容清单和 SHA256 一致、当前工程引用检查通过；已用于重建的批次保留，失败可重试。Agent 成功插入后保留唯一中转目录供重做 | [CojtTransfer](../CrossEnvIO/Core/CojtTransfer.cs)、[CrossEnvIOForm](../CrossEnvIO/CrossEnvIOForm.cs)、[ImportComponentTool](../Agent/Tools/ImportComponentTool.cs) |
| F10：撤销后设备偏移和矩阵缓存过期 | 对齐写入前及窗口重新激活时重读几何，工程切换要求重扫；矩阵窗口重新激活刷新数据 | [DeviceZAligner](../DeviceZAligner/DeviceZAligner.cs)、[WeldPointMatrixForm](../WeldPointMatrix/WeldPointMatrixForm.cs) |
| F11：保存重载和磁盘修改混称场景撤销 | CATIA 保存重载默认关闭；跨环境重建默认保留当前工程历史，MCP `save_reload=true` 才显式保存/修补/重载；覆盖前备份原文件；外层事务未结束时禁止该操作 | [CatiaPartTreeForm](../CatiaPartTree/CatiaPartTreeForm.cs)、[CatiaPartTreeService](../CatiaPartTree/Ps/CatiaPartTreeService.cs)、[CrossEnvRebuildTool](../CrossEnvIO/Core/CrossEnvRebuildTool.cs)、[ComponentIO](../CrossEnvIO/Core/ComponentIO.cs) |
| F12：文档和远程工具夸大撤销保障 | 23 个模块 README、总 README、Agent 和 MCP 描述明确场景/外部状态恢复边界，移除自动回滚承诺 | [模块入口](../README.md#四主要插件与功能简介)、[Agent](../Agent/README.md)、[McpBridge](../McpBridge/README.md) |
| F13：Python 事务关闭失败仍报告成功 | 开始失败不运行脚本；关闭失败设置执行失败和专门错误类型；只有正常关闭才报告完成的撤销分组 | [PythonHost](../Agent/Scripting/PythonHost.cs) |

旧版焊钳没有所有权标记，工具不会自动接管。请先在 PS 原生命令中核对旧机构并处理名称冲突，再生成新机构；不要通过批量加标记绕过检查。

## 场景历史之外的恢复

| 操作 | 恢复边界 |
|---|---|
| 保存/重载工程 | 日志记录唯一 `.before-tree-*.bak` 或 `.before-rebuild-*.bak` 原文件备份。需自行核对备份并重新打开；Ctrl+Z 不恢复磁盘覆盖，也不能承诺重载保留历史 |
| Agent 恢复快照 | 日志提供导出的 `.psz` 路径。需实际打开并核验；文件非空校验不等同于完整工程恢复测试，不打包外部库、CATIA、Excel 或注册表 |
| cojt 复制与导入 | 文件清理是独立功能，不是 PS Ctrl+Z。成功导入的中转目录可能被 Ctrl+Y 引用，应保留至确认工程和历史不再需要它。引用检查仅覆盖当前工程，其他实例或工程的依赖须另行确认 |
| CATIA、Excel、视频与导出文件 | 在目标应用内撤销或从文件备份恢复，PS 历史不负责这些输出 |
| 库路径、主题和配置 | 使用对应设置恢复原值或恢复配置备份 |
| IK 检查、游戏、录制与视图 | 属于临时运行/显示状态，按模块停止、退出、还原方式处理，不能把一局游戏或一段录制当成一个可撤销模型批次 |

## 已完成验证

- 新增 [UndoSafety 回归](../Tests/UndoSafety/Program.cs)：17 项通过，覆盖嵌套配对、开始/结束失败、切换工程后返回失败文档、线程及关闭顺序、围栏多层容器和用户内容保护、失败重试、快照结果、半复制、内容变化、引用保护和目标不覆盖。
- 既有 [WeldAnnotatorDisplay 回归](../Tests/WeldAnnotatorDisplay/Program.cs)：17 项通过，核对显示状态和事务恢复边界。
- Release 构建通过，主插件、MCP 桥和 JT 解码器产物生成；14 份嵌入配方资源校验通过。现有编译警告保留。
- 这些测试编译实际公共实现并使用 SDK 替身，不能替代真实 Process Simulate 的撤销栈验收。

在仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File Tests\UndoSafety\Run-Regression.ps1
powershell -ExecutionPolicy Bypass -File Tests\WeldAnnotatorDisplay\Run-Regression.ps1
```

## 真实宿主验收清单

使用可丢弃的工程副本，每项先记录对象 ID、层级、位置、顺序及相关文件；在 PS 图形窗口执行 Ctrl+Z/Ctrl+Y，并核对只影响对应批次。

1. 围栏：复用含原几何的建模资源、多条基线、新建分组后增加用户内容、生成途中失败、清理失败重试、原生 Undo 后清理。
2. 焊点分配/分组、树/焊点重建、曲线实体、BASE0：成功及部分失败各一次，核对新增、删除、迁移和顺序均随批次恢复。
3. 路径及干涉集：多机器人、多操作、中途停止、长任务放行 UI 时尝试切换工程；核对保留的 Via、碰撞对和机器人临时姿态。
4. 焊钳：未标记旧机构应停止；标记机构重建、TCPF 和定义写入失败后撤销，核对关节、Link、几何归属、公式与姿态；用户关节引用工具 Link 时应停止。
5. 矩阵和 Z 对齐：修改后 Undo/Redo，重新激活窗口，再次移动/对齐，核对数据和偏移来自当前场景。
6. Agent C#/Python：正常脚本、写入后抛异常、事务开始/结束失败、嵌套工具；核对错误输出与实际场景状态。快照须实际重新打开验收。
7. cojt：锁定源文件制造半复制、目标已存在、复制后编辑文件、插入/重建后清理、导入 Undo/Redo；核对源和既有目标不变，依赖文件仍可用。
8. 保存重载：默认路径应保留当前工程历史；显式启用时核对原文件备份可恢复。CATIA、Excel、配置、录制和游戏按各自恢复方式单独验收。

本轮尚未完成上述真实 PS/CATIA/Excel 宿主操作验收。
