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
- 提供直接 JT → CGR → CATIA 模式，以及试用的内外圆柱重建模式；重建参数写入 CGR 原生圆柱/圆边属性，不生成附属 CATPart 或参考轴线。
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

- [CFV2 容器字段生成与解码](../Tests/CgrContainer/README.md)
- [R2 身份字段与 CATIA COM 验收](../Tests/CgrContainer/METADATA-R2.md)
- [R3 精度字段、线框位与附加容器清单](../Tests/CgrContainer/FIELDS-R3.md)
- [R4 名称边界与变长身份编码](../Tests/CgrContainer/IDENTITY-R4.md)
- [R5 场景框架字段生成、矩阵与共享引用](../Tests/CgrContainer/SCENE-R5.md)
- [R6 图层字段、禁止拾取位与体积贡献位](../Tests/CgrContainer/SCENE-FLAGS-R6.md)
- [R7 变长公共头、显示属性继承与线样式打包](../Tests/CgrContainer/SCENE-STYLES-R7.md)
- [R57 PS 三角网格内外圆柱重建与压缩](../Tests/PsCgrPipeline/README.md)
- [R56 原型展开与批次进程隔离](../Tests/JtCgr/BATCH-SAFETY.md)
- [R55 显示细分密度与体积优化](../Tests/JtCgr/CGR-DENSITY.md)
- [ExportByColor 技术审查](TECHNICAL_REVIEW.md)
- [JT → CGR 内外圆柱重建与回归记录](../Tests/JtCgr/ROUND-HOLES.md)

## 2026-10-16：PS 三角网格圆柱重建 R57

当前生产版本为 `CGR-20261016-R57-ps-cylinder-pipeline`。插件已移除 PS/JT 两处“逐平面兼容”选项，PS 线面路线改为“PS 三角面 → CGR → CATIA（内外圆柱重建与压缩）”。该路线整理 PS 网格绕序后复用内外圆柱安全重建与连续面域压缩，并保留未通过重建检查的区域及颜色。旧方案和 JT 入口继续可用。

完整 Release 与 .NET Framework 回归通过，五类快照及两批 CATIA COM 回读通过，38 组默认输出保持一致。主 DLL/PDB 已备份并部署到指定生产 bin，下次启动 PS 加载；PS 实际项目采集仍需宿主验收。详见 [R57 记录](../Tests/PsCgrPipeline/README.md)。

## 2026-10-12：EquipmentPrototype 与批次隔离 R56（历史）

R56 版本为 `CGR-20261012-R56-prototype-isolated-batch`。EquipmentPrototype 按容器展开，
包括由普通 TxComponent 暴露的同名原型；无独立 JT 的组件也继续查找子项。
每个子资源单独解析 JT，并使用自身绝对放置矩阵；按对象身份去重，同一路径的不同实例保留。

资源元数据在 PS 主线程读取；每个资源的 JT 解码、圆柱重建与 CGR 写出在独立 C# 工作进程完成。
默认同时运行 2 个资源，`TXTOOLS_JT_WORKERS` 可设置 1–4；完整资源默认限时 900 秒，
`TXTOOLS_JT_TIMEOUT_SECONDS` 可设置 30–3600 秒。先完成的任务先处理，超时只结束该资源及其
解码子进程，失败项不插入 CATIA，其他资源继续。CATIA 插入仍在单一 STA 线程执行。
此直接模式不再因解码失败回退至 PS 几何采集；未增加 CGR 回读。

正式构建自动部署 `JtDirectCs/TxTools.JtCgrWorker.exe` 与已有解码器，均使用 .NET Framework 4.8，
无需 Python。真实 JT 9.5、故意损坏 JT、JT 10.6 的两路批次回归为 2 成功、1 失败；
最终版增加默认 Compact 模式，四项批次为 3 成功、1 失败；
FFM170 保留 1182 段圆柱、FFM130 为 880 段。EquipmentPrototype 场景展开需在 PS 实机验收；
模拟树已覆盖原型、嵌套、重复选择、循环和单个 SDK 枚举异常。详见 R56 记录。

## 2026-10-12：直接 JT 转换与 R54/R55 圆柱重建（历史）

R55 生产版本为 `CGR-20261012-R55-adaptive-cylinder-density`：普通圆柱默认至少 24 段、
0.02 mm 弦高自适应细分，复杂裁剪与相交边界保持原严格保护。相同 FFM170 JT 的 CGR
从 18.93 MB 降为 10.82 MB（减少 42.87%），1182 段圆柱的源测量参数全部精确保留，
最终输出已直接插入 CATIA。原法向编码保留；本轮未执行生成 CGR 的独立回读。
详细范围见上面的 R55 记录；以下是 R54 的历史结果。

资源的 JT 路径及放置矩阵在 PS 主线程取得；独立 C# 解码器、圆柱识别、网格重建和 CGR 写出在有界工作线程执行，CATIA 插入在 STA 线程完成。选择多个资源时可并行转码；几何转换无需 PS 网格采集，也不依赖 Python。

生产版本为 `CGR-20261012-R54-constrained-cylinder-refinement`。圆柱重建支持完整源开口环、跨 Shape 精确端面连接、倒角连接及非平面裁剪端口。孔壁圆弧显示弦高目标为 0.002 mm，仍受源拟合精度与 Float32 坐标限制；裁剪端口不伪装成平面圆边。

相交圆柱的穿孔壁面采用联合圆柱/平面约束细分，孔口和邻接端面共用顶点；原生圆柱属性保持原拟合参数，不用完整圆筒遮盖横向孔。曲面内部对角线须通过几何绕向检查。补充按原壁面拓扑处理混色圆柱，保留逐面 RGBA；不能证明约束、接缝正确、完全透明或超过工作量限制的候选整体保留原几何，不留下部分属性组。

正式源码已编译到 `E:\ProcessSimulatePlugin\Process Simulate\bin`，最终 DLL 的 9156 完整 JT 转换及 CATIA 插入通过：2562 段圆柱（1725 内孔、837 外圆柱），较 R53 增加 8 段，旧版 2554 段源参数均保留。原 15 段穿孔基础圆柱中 11 段完成细分，4 段因原始拓扑或连接保护保留源壁面。六组几何回归与三组实际片区回归通过；本轮未做 CGR 独立回读。CGR 显示网格及测量属性不等同于 CATPart BRep，捕捉中心轴线的效果由用户在 CATIA 中验收。

## 主要文件

- `ExportByColorCmd.cs`、`ExportByColorForm.cs`：命令与界面。
- `ExportByColorService.cs`：资源收集与导出编排。
- `CgrWriter.cs`、`CgrCompact95.cs`：CGR 写出。
- `RoundHoleCgrRefinement.cs`：相交圆柱的约束细分、材质保留和工作量保护。
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
