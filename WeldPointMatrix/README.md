# WeldPointMatrix — Weld Point Matrix / 焊点矩阵

Compare locations across multiple operations, position associated robots and insert weld or via locations in Process Simulate.

## 命令

`焊点矩阵`；入口类：`WeldPointMatrixCommand`。

## 功能与使用

1. 打开命令，在左侧选取多个焊接或连续点操作。
2. 右侧按操作列并列显示焊点和过渡点，方便对照点位顺序；外部修改后点击“刷新列表”。
3. 在表格中选择点位，可定位关联机器人；拖动点位用于调整顺序。
   表格获得焦点后，方向键、Tab/Shift+Tab、Home/End、PageUp/PageDown 切换选中点位时也会定位机器人；Enter 或空格可重新定位当前点位。选中空单元格不驱动机器人。播放会话期间鼠标和键盘选取均不会驱动机器人。
4. 使用“收起选取区/展开选取区”折叠或恢复左侧操作拾取框。
5. 选中已有点位，点击“前插过渡点”或“后插过渡点”。插件先将机器人定位到该点，再通过 PS 原生命令在其前面或后面添加过渡点，使用当前机器人姿态。
6. 新增焊点可先插入空行，再选中目标操作列填写点位信息。

## 播放

每列表头的“播放/暂停”通过 `TxSimulationPlayer` 播放该操作的真实机器人轨迹。暂停后只能继续当前操作；播放完成后再次点击会从头开始。切换另一列前，先点击顶部“停止播放”，等待播放器完成停止和清理。

顶部“序列播放”使用 `TxApplication.ViewersManager.SequenceEditor`。插件将选中操作已有的共同复合操作设为 `ActiveDocument.CurrentOperation`，调用序列编辑器的 `Play/Stop`，由原生序列调度器播放。暂停使用原生 `Stop`，继续使用 `Play`；不调用 `Reset`，不强制重置操作开始时间。操作是否同时运动取决于现有序列时序。

多个操作必须完整覆盖某个现有复合操作的操作分支。插件优先选择最小的匹配范围；共同范围包含未选中的操作、或没有共同复合范围时，会提示原因并拒绝播放。2402 公开 SDK 未提供为序列编辑器设置独立操作列表的接口，因此此入口不支持将任意操作子集拼为新序列。插件不复制或移动操作、不建立临时复合组，以保留原有父级关系、继承的外观绑定和时序。

播放结束或停止后恢复播放前的当前操作；若外部已经切换当前操作，则保留外部选择。序列播放器替换文档的仿真播放器时重新订阅其完成事件。检测到原生仿真已在运行时拒绝启动，不停止已有仿真。

播放开始、运行、暂停、停止和清理期间，点位定位、拖放、插点、操作拾取、清空和刷新均被互锁。暂停仍保留机器人占用，不会开放点位操作。“停止播放”先请求 SDK 停止；只有播放调用返回并完成清理后才解锁。停止或清理失败会保持锁定，允许重试停止。播放期间关闭窗口会先停止播放，待清理完成后关闭。

播放或点位操作开始前，通过公共拾取绑定调用 `LoseFocus`、关闭 `ListenToPick` 并移出拾取控件，再禁用选取区。结束后仅恢复可用性，重新点击选取框才开启拾取。折叠选取区及关闭窗口同样释放拾取；挂起期间的 Enter、点击和延迟激活不会重新注册拾取提供者。

`Ended` 事件去重后切回 PS 主线程收尾。SDK 调用重入深度用于阻止播放尚未返回时释放播放器或写入机器人姿态。点位编辑/原生命令执行期间也不能启动播放。

## 原生插点命令

命令 ID 取决于 PS 版本和安装的命令组件。插件读取安装目录、SDK 程序集目录及用户配置目录的 `RibbonConfiguration*.xml`，按 `Name: Add Location Before/After` 注释匹配紧随其后的 `RibbonItem Id`，也支持名称属性。ID 可为 GUID 或托管命令名称，不按 ID 中的文字猜测功能。通过 `ActiveSelection.Clear/AddItems → CommandsManager.ExecuteCommand` 执行；命令返回后清空临时选区并刷新显示。环境变量 `TXTOOLS_ADD_LOCATION_BEFORE_COMMAND` 与 `TXTOOLS_ADD_LOCATION_AFTER_COMMAND` 可显式覆盖本机命令 ID。命令不存在或不可用时显示错误。

新增和调整会修改操作点位。使用前保存场景，完成后检查点位位置、姿态、顺序和机器人关联；实际行为需在 PS 宿主验收。

## 主要文件

- `WeldPointMatrixCommand.cs`：命令入口。
- `WeldPointMatrixForm.cs`：单例窗口、操作拾取、矩阵和点位交互。
- `PointService.cs`：点位读取、机器人定位与点位创建辅助。
- `WeldPointMatrixForm.Playback.cs`：选取区折叠、轨迹/序列播放会话和原生插点入口。
- `WeldPointMatrixForm.Interlock.cs`：播放/点位操作互锁、界面禁用状态和关闭窗口等待。
- `SequencePlaybackScope.cs`：解析完整覆盖所选操作的现有复合范围，不修改操作层级。
- `SequenceEditorSession.cs`：原生序列编辑器播放、完成事件转接及当前操作恢复。
- `NativeLocationCommands.cs`：解析 PS Ribbon 中的原生前插/后插位置命令 ID。

本模块随 `TxTools.csproj` 构建，环境与注册方式见 [项目 README](../README.md)。

## 撤销与恢复（2026-10-07）

新增和移动点位使用场景撤销分组，移动验证失败会尝试恢复源操作和顺序；补偿失败明确提示 Ctrl+Z。窗口重新激活时刷新矩阵；机器人驱动是独立状态变化。

详见 [统一撤销与恢复说明](../docs/undo-safety.md)。
