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

## 2026-10-11：直接 JT 转换与 R53 圆柱重建

资源的 JT 路径及放置矩阵在 PS 主线程取得；独立 C# 解码器、圆柱识别、网格重建和 CGR 写出在有界工作线程执行，CATIA 插入在 STA 线程完成。选择多个资源时可并行转码；几何转换无需 PS 网格采集，也不依赖 Python。

生产版本为 `CGR-20261011-R53-punctured-cylinder-boundaries`。圆柱重建支持完整源开口环、跨 Shape 精确端面连接、倒角连接及非平面裁剪端口。孔壁圆弧显示弦高目标为 0.002 mm，仍受源拟合精度与 Float32 坐标限制；裁剪端口不伪装成平面圆边。

相交圆柱的壁面若带横向孔，保留其原壁面分片并附加原生圆柱属性，避免完整圆筒封住横向孔，再按原端面分片重建孔壁。保留壁面的细分密度尚未提高。近似平面的多分片孔口按逐片裁剪处理，台阶环面按实际填充侧选择整体重建；不能证明连接正确、混色或透明的候选继续保留原几何。

## 主要文件

- `ExportByColorCmd.cs`、`ExportByColorForm.cs`：命令与界面。
- `ExportByColorService.cs`：资源收集与导出编排。
- `CgrWriter.cs`、`CgrCompact95.cs`：CGR 写出。
- `MeshExport.cs`、`FbxExport.cs`：网格和 FBX 写出。

## 界面与构建

导出窗口接入 `PickAwareTxForm`，导出原点对象框接入 `PickFocus`。资源列表和原点拾取可用 Esc 退出并保留已选值。

本模块随 `TxTools.csproj` 构建。环境与注册方式见 [项目 README](../README.md)。

公共拾取实现位于 `SRC/PickFocus.cs` 和 `SRC/PickAwareTxForm.cs`；回归工程为 `Tests/PickFocus/PickFocus.csproj`。测试使用 SDK 控件替身和真实 WinForms 焦点/键盘消息，仍需在 PS 内确认实际拾取效果。

## JT 解码器构建依赖

`JtDirectCs/Worker.csproj` 编译已有 JTReader 源码，依赖 `JtDirectCs/Dependencies/SharpCompress.dll` 及配套的 `System.*.dll`、`Microsoft.Bcl.AsyncInterfaces.dll`。这些二进制按仓库规则不提交；首次构建需先准备对应依赖，版本声明可参考 `JtDirectCs/Vendor/JTReader.csproj`，许可证见解码器目录的说明文件。

主项目会联动构建解码器，并将它及依赖复制到输出目录的 `JtDirectCs/`。`OutputPath` / `OutDir` 的覆盖不改变解码器独立输出目录；解码器先生成在自身 `bin/Release/`，再复制到主项目输出。

## 撤销与恢复（2026-10-07）

PS 主要提供几何读取，导出文件及 CATIA 输出不在 PS 的工程撤销栈中；请使用文件备份或目标应用的恢复方式。

详见 [统一撤销与恢复说明](../docs/undo-safety.md)。
