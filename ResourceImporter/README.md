# 资源导入

在 Process Simulate 离线工程中打开 **TxTools → 资源导入**。通过“添加文件”批量选择来源，也可把文件从资源管理器拖入左侧列表。

1. 用 Ctrl 选取不连续的文件，用 Shift 选取连续的文件，或从列表空白处拖动框选。点击原型列的下拉框可修改单行，点击“批量设原型”可修改已选文件。下拉选项和文件名前都显示原型图标。
2. 把已选中的文件拖到右侧系统库目录，或使用“分配到所选目录”。可以为不同文件指定不同目录。
3. 点击“导入根树”。每个文件生成一个 COJT 和一个根树实例。3DXML、STEP 和 CATProduct 的装配位姿保留在模型几何中。同名 COJT 自动编号。悬停文件行可查看存放路径和失败原因。

## 原型客制化

原型 Family 固定为 `ToolPrototype`。插件优先读取当前工程已加载的 Planning 类型，并用 SDK 校验原型 Family。加载基于客户 `.pszt` 模板的工程后，导入窗口会显示该工程生效的类型。

当 SDK 无法提供类型时，插件尝试读取当前工程包中的 `metadata.xml`。没有可读取的工程元数据时，才使用当前 PS 版本的 `InitData\DefaultCust\defaultCust.ppc`。默认文件优先通过当前宿主安装目录定位；需要查询注册表时，使用 32 位和 64 位视图并核对 SDK 文件版本。切换工程后重新打开导入窗口。

## 文件转换

转换中间文件写入 TxTools 临时工作区。JT 完成后才暂存 COJT，写入并校验原型数据后将完整包移到所选目录。

| 格式 | 转换方式 |
| --- | --- |
| JT | 原样封装独立 JT 文件 |
| STEP / STP | 使用随插件部署的 OCCT STEP 读取器生成单个 JT。部分面细分失败时保留可用几何，文件行提示列出缺面数量。零件没有可用网格时跳过其对应实例 |
| 3DXML | 将包内 CGR 或 XML 细分面合成为一个 JT |
| CGR / CATPart | 使用 CatiaPartTree 转换器读取 CGR 或 CATPart 内嵌几何缓存 |
| CATProduct | 读取同目录的 `<文件名>.CATProduct.jtcoords.json`，校验源装配、引用文件及变换后合成为一个 JT |

CATProduct 使用 `cad-drop.offline-assembly.v1` 索引。所引用的 CATProduct 和 CATPart 文件须与索引摘要一致，并位于源装配目录内。直接从原生 CATProduct 解码装配引用和实例变换尚未支持。转换在本机离线运行。
