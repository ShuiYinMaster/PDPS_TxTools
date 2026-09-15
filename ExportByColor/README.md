# ExportByColor

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
