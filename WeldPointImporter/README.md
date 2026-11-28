# WeldPointImporter · 焊点提取与创建

命令：`焊点提取与创建`（`TxTools` 类别，实现类 `WeldPointImporterCommand`）。读取 CATIA V5 或文件中的点坐标，在 PS 操作树根下创建一个复合操作，把焊点位操作和对应制造焊点写入同一批次。

## 点位来源

| 来源 | 坐标提取规则 |
|---|---|
| CATIA 几何点 | 遍历 CATPart 图形集，用 SPA 测量点坐标。活动文档为 CATProduct 时，按实例逐级累乘位姿，转换到根装配坐标。 |
| CATIA 图形集球体 | 遍历 `HybridShapeSphere` 特征，通过 SPA 读取球面重心。 |
| CATIA 几何体重心 | 对可测量的 Part Body 读取重心。该来源也可能返回普通物料实体，请在预览中移除无关点。 |
| 3DXML 焊点符号 | 读取文件或 CATIA 当前 3DXML 的装配树和实例变换。自动、名称和几何模式识别 `V5_CFV3` 小型网格符号，默认名称线索为 `XWS`，包络半径上限为 20 mm。兼容模式按旧版二进制节点或 XML `VertexBuffer` 计算包络中心。 |
| BiW Joint Element | 读取已保存的 `V5_CFV2` CATProduct，按 `Joint Element` 名称数与坐标记录组配对。活动 CATIA 装配模式会遍历子装配并转换到根装配坐标。文件模式也支持 ABF Neutral 的制表格式和 `LOCX/LOCY/LOCZ` 键值行。 |
| CATIA 螺柱实例原点 | 对 CATProduct 中的实例名和零件号做忽略大小写的关键词匹配，读取匹配实例在根装配中的原点。多个关键词可用逗号分隔。 |
| CSV、TSV、XLSX | 使用 `X/Y/Z`、`X_mm/Y_mm/Z_mm` 或 `center_x_mm/center_y_mm/center_z_mm`。`Name` 或 `名称` 可选。`Unit` 支持 `mm`、`cm`、`m`、`in`，默认 `mm`。 |

单机版和 Process Designer 的焊点导入均提供 **排除隐藏对象**，默认关闭。勾选后，本次读取跳过 CATIA 中隐藏的装配分支、图形集和点位来源对象。BiW 当前装配模式按装配分支过滤，3DXML 按文件记录的默认视图过滤。无法确认可见性的 CATIA 对象保留，并在读取详情中提示。列表已有点位按 **追加到列表** 的设置处理。

## 核对与创建

1. 选择来源并读取。启用“追加到列表”可合并不同来源，并对全表筛查重复点。
2. 用 XZ、XY、YZ 或可旋转的 3D 预览核对点位。滚轮缩放，拖动平移，3D 视图中左键拖动旋转。单击预览点会选中该点并定位到对应列表行。信息栏统计全表的板件层级，预览和点名称前的圆点均用蓝色表示双层、橙色表示三层、紫色表示四层及以上。
3. 默认按 5 mm 的三维欧氏距离标记重复点，边界计入阈值。优先保留置信度较高的候选。可筛选重复点、查看与主点的距离、移除所选行，或者在创建时跳过重复点。筛选只影响查看范围，创建数量由“创建时跳过重复点”和实际留在列表中的点共同决定。
4. 表格只显示名称和 XYZ。双击名称或按 F2 可单点编辑。自动重命名使用 `前缀_零件号_焊点号` 规则。自定义命名可设置前缀、主名称、分隔符、后缀、起始编号和补零位数。
5. 在目标 PS 工程核对新操作名称并创建。根装配或文件坐标以毫米直接写入 PS 世界坐标，姿态为世界坐标系零旋转。整个批次可以在 PS 中按 `Ctrl+Z` 撤销。

CATIA 几何点按图形集名称中 `&` 分隔的板件数量推断层级。已识别的球体、立方体、圆锥分别对应双层、三层、四层，几何类型优先于点表的 `WeldLayers`、`LayerCount`、`Type` 等层级标记。层级缺失或有冲突时按双层显示。

## 文件

| 文件 | 职责 |
| --- | --- |
| `WeldPointImporterCommand.cs` | 命令注册与窗体启动 |
| `WeldPointImporterForm.cs` | 来源选择、列表、预览与创建流程 |
| `CatiaPointReader.cs` | CATIA 几何点、球体、重心与螺柱实例读取 |
| `ThreeDXmlPoints.cs` | 3DXML 焊点符号与 BiW Joint Element 解析 |
| `PointFiles.cs` | CSV、TSV、XLSX 点表读取与单位换算 |
| `PointData.cs` | 点位模型、重复点筛查与命名规则 |
| `PointPreview.cs` | 2D/3D 预览、层级配色 |
| `PointInformationBars.cs` | 板件层级统计信息栏 |
| `PsPointWriter.cs` | 复合操作、位操作与 `PmWeldPoint` 写入 |
