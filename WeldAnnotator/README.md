# WeldAnnotator

把 PS 视口截图和焊点标注信息写入活动 Excel，用于焊点布局、评审或交付资料制作。

## 命令

- `.焊点标注截图`

## 功能

- 从当前 OP 或视口选择中识别焊点，并统计焊点数量和视口内数量。
- 保存 PS 视口快照，支持恢复快照和临时隐藏/显示对象。
- 将视口截图、焊点标注和可选焊点数据表写入活动 Excel。
- 支持新建 Sheet、附加焊点数据表和自动检测板厚。
- 可配置标注前缀/后缀、字体、颜色、线宽等样式。
- 通过日志和状态栏显示识别、截图、标注及 Excel 写入结果。

## 基本用法

1. 启动 `.焊点标注截图`，在 PS 中选择 OP 或在视口中选择焊点。
2. 设置输出 Sheet、是否写入新 Sheet、是否附加数据表及板厚检测。
3. 设置标注命名的前缀/后缀；需要时打开样式设置调整颜色和字体。
4. 执行截图/写入 Excel，完成后检查 Excel 中的图片、标注和数据。

## 显示控制

- **拍摄快照**：记录物理树和操作树中可显示对象的 `None / Partial / All` 状态。仅拍快照不触发关闭恢复提示；快照可重复恢复。
- **仅显示外观**：保留操作绑定外观、机器人及工具的子树，保护其祖先；隐藏其他对象，包括焊接操作节点、焊点和过渡点。子级隐藏后还会显式隐藏复合操作节点，避免其残留 `Partial`。
- **恢复快照 / 恢复显示**：优先恢复最近一次手动快照；没有手动快照时，恢复本窗口第一次显示变更前的自动基线。连续切换外观不会覆盖自动基线，原本隐藏的白名单对象也会恢复为隐藏。
- 每次显示变更使用一次 `StartTransaction / EndTransaction` 撤销事务；按运行时签名优先调用 `StartTransaction()`，兼容 `StartTransaction(string)`，并在开启前确认 `EndTransaction()` 存在。恢复按父子关系重建可见状态，再逐项比较原始枚举；无法重建的 `Partial`、对象删除或 SDK 调用失败会记录到日志并保留恢复依据，便于重试。
- 日志汇总 `None / Partial / All` 数量和未达目标的对象数。按真实场景 ID 去重和匹配白名单，名字中的括号数字不参与匹配。
- 关闭窗口只在存在未恢复变更时询问；选择恢复且恢复失败时保留窗口。切换活动文档后拒绝使用原文档快照修改场景。

## 注意事项

- 插件依赖活动 Excel 和 COM 写入能力；Excel 未启动、工作簿不可写或 COM 异常时应根据日志处理。
- 自动板厚检测属于辅助识别，交付前应人工复核板厚和焊点编号。
- 恢复快照用于回退本次隐藏/显示操作，不替代 PS 文档保存和版本备份。

## 主要文件

- `WeldPointAnnotatorCmd.cs`：命令入口。
- `WeldAnnotatorForm.cs`、`WeldAnnotatorForm.UI.cs`：识别、截图、标注和界面逻辑。
- `DisplaySession.cs`：窗口独立的显示快照、白名单隔离、撤销事务及状态校验。
- `AnnotationStyle.cs`、`AnnotationStyleForm.cs`：标注样式模型和设置窗口。

## 回归验证

`dotnet run --project Tests/WeldAnnotatorDisplay/WeldAnnotatorDisplay.csproj --configuration Release`

测试链接实际 `DisplaySession.cs`，使用 SDK 模型覆盖复合操作残留 `Partial`、父级级联、连续隔离、白名单 ID 匹配、失败重试、文档切换和窗口独立记录。模型测试不替代 Process Simulate 2402 中的真实场景验证。

Windows 离线验证可运行 `powershell -NoProfile -File Tests/WeldAnnotatorDisplay/Run-Regression.ps1`，用本机 Roslyn 和 .NET Framework 引用编译为 C# 7.3，无需还原 NuGet 包。

## 2026-10-07 更新与源码核对

显示状态管理从公共 `PsReader` 迁入窗口独立的 `DisplaySession`，精确保留 `None / Partial / All`。新增恢复基线、白名单 ID 校验、事务能力检查与失败重试保护，详见上文“显示控制”。

本模块随 `TxTools.csproj` 构建。环境、注册方式和本次完整更新日志见 [项目 README](../README.md)。
