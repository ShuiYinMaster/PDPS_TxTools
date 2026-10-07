# ExportByColor — CATIA CGR and Mesh Export / 按颜色导出

Export Process Simulate resources to CATIA CGR or mesh formats, with color grouping and coordinate-origin options.

按当前 PS 场景中的可见资源导出 CATIA 可用的 CGR 或网格文件，并提供颜色、合并和坐标选项。

## 命令

- `.布局/资源导出至CATIA`

## 功能

- 将选定/可见资源按显示颜色生成 CGR 并导出到 CATIA。
- 支持 STL、OBJ、PLY、FBX 等网格格式导出。
- 可设置导出坐标原点、合并策略、边线显示和输出目录。
- CGR 导出包含面方向、颜色、模板及紧凑写出逻辑，适合大批量布局资源。
- 网格导出提供通用网格写出和 FBX 专用写出路径。

## 基本用法

1. 启动 `.布局/资源导出至CATIA`。
2. 在资源列表中确认可见对象和导出范围。
3. 选择 CGR 或网格格式，设置坐标、合并和边线选项。
4. 指定输出目录并执行导出，查看日志中的成功/失败统计。

## 注意事项

- 导出结果取决于资源是否提供可读的几何数据和颜色信息；空资源或不支持的几何类型可能被跳过。
- 大型布局建议按区域或资源组分批导出，并检查输出目录空间。
- 坐标原点和合并选项会影响 CATIA 中的装配结构与定位，正式交付前应抽检模型位置。

## 相关文档

- [ExportByColor 技术审查](TECHNICAL_REVIEW.md)

## 主要文件

- `ExportByColorCmd.cs`、`ExportByColorForm.cs`：命令与界面。
- `ExportByColorService.cs`：资源收集与导出编排。
- `CgrWriter.cs`、`CgrCompact95.cs`：CGR 写出。
- `MeshExport.cs`、`FbxExport.cs`：网格和 FBX 写出。

## 2026-10-07 更新与源码核对

导出窗口接入 `PickAwareTxForm`，导出原点对象框接入 `PickFocus`。资源列表和原点拾取可用 Esc 退出并保留已选值；CGR、网格与 JT 解码逻辑未在本次修改。

本模块随 `TxTools.csproj` 构建。环境、注册方式和本次完整更新日志见 [项目 README](../README.md)。

公共拾取实现位于 `SRC/PickFocus.cs` 和 `SRC/PickAwareTxForm.cs`；回归工程为 `Tests/PickFocus/PickFocus.csproj`。测试使用 SDK 控件替身和真实 WinForms 焦点/键盘消息，仍需在 PS 内确认实际拾取效果。

## JT 解码器构建依赖

`JtDirectCs/Worker.csproj` 编译已有 JTReader 源码，依赖 `JtDirectCs/Dependencies/SharpCompress.dll` 及配套的 `System.*.dll`、`Microsoft.Bcl.AsyncInterfaces.dll`。这些二进制按仓库规则不提交；首次构建需先准备对应依赖，版本声明可参考 `JtDirectCs/Vendor/JTReader.csproj`，许可证见解码器目录的说明文件。

主项目会联动构建解码器，并将它及依赖复制到输出目录的 `JtDirectCs/`。本次修正 `OutputPath` / `OutDir` 覆盖时对子项目的传递，保持解码器先生成在自身的 `bin/Release/`，再按既有规则复制。


## 撤销与恢复（2026-10-07）

PS 主要提供几何读取，导出文件及 CATIA 输出不在 PS 的工程撤销栈中；请使用文件备份或目标应用的恢复方式。

详见 [统一撤销与恢复说明](../docs/undo-safety.md)。
