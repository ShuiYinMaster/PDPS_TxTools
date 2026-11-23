# ExportByColor — CATIA CGR and Mesh Export / 按颜色导出

Export Process Simulate resources to CATIA CGR or mesh formats, with color grouping and coordinate-origin options.

按当前 PS 场景中的可见资源导出 CATIA 可用的 CGR 或网格文件，并提供颜色、合并和坐标选项。

## 命令

- `.布局/资源导出至CATIA`

## 功能

- 将选定/可见资源按显示颜色生成 CGR 并导出到 CATIA。
- 支持 STL、OBJ、PLY、FBX 等网格格式导出。
- 可设置导出坐标原点、合并策略、边线显示和输出目录。
- CGR 导出包含面方向、颜色、字段生成的 CFV2 容器及紧凑写出逻辑，适合大批量布局资源。
- 提供 JT → CGR → CATIA 路线。内外圆柱重建可与 PS、JT 的 CGR 路线组合，启用后使用支持圆柱面和圆边测量属性的线面编码。
- 网格导出提供通用网格写出和 FBX 专用写出路径。
- 零件/滑橇外观及其设备形式通过关联零件的实体表示读取三角面：按需临时显示并加载实体级（Detailed），应用完整外观位置变换后导出，再恢复原加载级别及显示状态；JT 模式也自动使用该策略。接口依据、限制与回归方法见 [外观三角面获取与导出](APPEARANCE-EXPORT.md)。

## 基本用法

1. 启动 `.布局/资源导出至CATIA`。
2. 在资源列表中确认可见对象和导出范围。
3. 勾选导出路线或网格格式，按需拾取参考坐标。导出页、路线、格式、圆柱重建和合并选项按 Windows 用户保存。
4. 指定输出目录并执行导出，查看日志中的成功/失败统计。

## 注意事项

- 导出结果取决于资源是否提供可读的几何数据和颜色信息；空资源或不支持的几何类型可能被跳过。
- 大型布局建议按区域或资源组分批导出，并检查输出目录空间。
- 坐标原点和合并选项会影响 CATIA 中的装配结构与定位，正式交付前应抽检模型位置。

## 相关文档

- [PS 网格编码与线程边界](../docs/build.md)

## 网格处理

PS 网格压缩和圆柱识别可独立选择；SDK 采集在宿主线程完成，纯网格处理在后台执行。

## 编码边界

连续线面每块最多 65,535 个顶点，Compact 使用 40,960 顶点策略；编码前检查索引范围，Legacy 多块拾取偏移按块累积。更大的模型通过内部拆块处理。

## 块大小保护

编码器对块大小设置边界保护；加载和拾取兼容性仍需在目标 CATIA 版本验收。

## 进度与线程

PS 大网格处理按阶段报告采集、加载级别恢复与后台编码进度；SDK 和 CATIA 调用保持各自线程约束。

## PS 圆柱重建

PS 三角网格可执行圆柱拟合和连续面域压缩。不同源几何身份保持隔离，不能安全重建的区域保留源网格。拟合结果不是完整 CAD 特征历史。

## JT 批次隔离

EquipmentPrototype 和没有独立 JT 的组件继续展开到实际资源，按对象身份去重并保留实例放置。JT 直读使用独立工作进程、有界并发与超时，保留已解码的 RGB、透明度和源几何。镜像资源放置会同步修正三角面绕序。

找不到 JT、COJT 主文件存在歧义或直接转码失败时，按资源回退到 Process Simulate 的原生三角网格，并使用同一参考坐标和 CGR 设置。回退读取 PS 可访问的几何和颜色，结果可能与 JT 文件内的细节层级或透明度不同。两条路线都失败时记录各自原因，批次继续处理其他资源。部署时同时保留工作进程、解码器及其依赖。

## JT 圆柱重建

JT 圆柱重建保留源面颜色、透明度及裁剪边界。普通圆柱按弦高自适应细分；复杂相交区域限制搜索和网格预算，失败时保留源网格。

## 主要文件

- `ExportByColorCmd.cs`、`ExportByColorForm.cs`：命令与界面。
- `ExportByColorService.cs`：资源收集与导出编排。
- `CgrWriter.cs`、`CgrCompact95.cs`：CGR 写出。
- `RoundHoleCgrRefinement.cs`：相交圆柱的约束细分、材质保留和工作量保护。
- `MeshExport.cs`、`FbxExport.cs`：网格和 FBX 写出。

## 界面与构建

导出窗口接入 `PickAwareTxForm`，导出原点对象框接入 `PickFocus`。资源列表和原点拾取可用 Esc 退出并保留已选值。

本模块随 `TxTools.csproj` 构建。环境与注册方式见 [项目 README](../README.md)。

公共拾取实现位于 `SRC/PickFocus.cs` 和 `SRC/PickAwareTxForm.cs`；焦点回归位于独立测试目录。测试使用 SDK 控件替身和真实 WinForms 焦点/键盘消息，仍需在 PS 内确认实际拾取效果。

## JT 解码器构建依赖

直读覆盖小端 JT 8.0、8.1、8.2、9.0、9.1、9.4、9.5、10.0、10.1、10.2 和 10.6 中已支持的三角网格布局。Range LOD 按文件声明顺序选取首个包含受支持三角网格段的表示。资源内部的正交镜像实例会同步修正三角面绕序。仅含点、线或精确 B-rep 的文件没有可供此路线写入 CGR 的三角网格。JT 10 的量化坐标、逐顶点颜色和非正交变换仍可能需要 PS 原生网格回退。

JT 解码与网格读取按实际文件长度校验数据。转换规模受运行时集合容量和可用内存约束。单个 CGR 内按编码器的顶点容量拆块。

`JtDirectCs/Worker.csproj` 编译已有 JTReader 源码，依赖 `$(TxToolsJtDependenciesDir)SharpCompress.dll` 及配套的 `System.*.dll`、`Microsoft.Bcl.AsyncInterfaces.dll`。这些二进制按仓库规则不提交；首次构建需先准备对应依赖，版本声明可参考 `JtDirectCs/Vendor/JTReader.csproj`，许可证见解码器目录的说明文件。

主项目会联动构建解码器，并将它及依赖复制到输出目录的 `JtDirectCs/`。`OutputPath` / `OutDir` 的覆盖不改变解码器独立输出目录；解码器先生成在外部 `build/bin/JtDecoder/Release/`，再复制到主项目输出。

## 撤销与恢复（2026-10-07）

PS 主要提供几何读取，导出文件及 CATIA 输出不在 PS 的工程撤销栈中；请使用文件备份或目标应用的恢复方式。

详见 [统一撤销与恢复说明](../docs/undo-safety.md)。
