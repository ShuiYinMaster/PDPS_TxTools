# Process Designer on eMServer 导入插件

适用宿主为 Tecnomatix 2402 Process Designer，Ribbon 配置为 `newviewer / eMServer`。

| 命令 | 来源与处理 | Process Designer 目标 |
|---|---|---|
| 零件导入 | 读取 CATIA 当前装配，或选择、拖入一个 CATProduct、3DXML 或 STEP 文件。编辑名称、勾选和调整层级。可按需转换 JT。 | 原型进入 `PartLibrary`，实例进入工程根节点下新建的复合零件 |
| 资源导入 | 批量选择或拖入 JT、STEP、3DXML、CGR、CATPart 或 CATProduct 文件。CATProduct 文件转换需要同名 `.jtcoords.json` 离线装配索引。 | 原型进入 `ResourceLibrary`，实例进入工程根节点下新建的复合资源 |
| 焊点导入 | 从 CATIA、3DXML、BiW 文件或点表读取点位，预览、改名和筛除重复点。按项目世界坐标（mm）生成 `PmWeldPoint` 制造特征。 | 当前工程的 `MfgLibrary` |

## 工程导入

1. 在左侧核对来源，资源可在原型列逐件选择，也可批量设置原型。将零件、装配分支或多选的资源拖到右侧系统库目录。目录预览会列出计划存放的 COJT，底部的 **清除分配** 可让所选模型恢复使用默认目录。右击目录可新建待用文件夹，待用文件夹可重命名，准备 COJT 时才创建有几何写入的文件夹。同一来源几何的多个零件实例共享一个 COJT 及其保存目录。未分配的模型使用 **默认 COJT 保存至** 输入框中的目录，可直接修改或通过 **浏览…** 选择。零件默认使用系统库中匹配 `product` 的目录，并可在该默认目录下按文档名建子文件夹。资源默认使用 `TxToolsImports`。eBOP XML 通过 `#\` 路径引用对应 COJT。
2. 在 Process Designer 打开 eMServer 工程。零件和资源使用 **导入当前工程**，插件先提交库原型，确认后再提交项目结构。焊点使用 **导入到 MfgLibrary**。无法生成 JT 的实例保留在结构中，可在 **转换提示** 查看原因。
3. 提交后自动核对目标库、复合对象位置和实例的原型关联。使用 **核对入库结果** 可重新查询。签出、导入权限和签入按项目流程处理。

## SDK 接入

插件使用 `IEmpProjectManager.GetCurrentProject` 取得当前工程，在其规划树中解析目标库。原型和结构各自生成 EmpXML，通过 `IEmpImportCollection` 分别提交到目标库和工程根节点。零件和资源分别使用 `PmCompoundPart` / `PmPartInstance`、`PmCompoundResource` / `PmToolInstance`。实例按外部 ID 引用库原型，已生成 JT 的原型关联几何表示。零件的刚性位姿沿用来源装配坐标。焊点用 `PmWeldPoint` 表达名称和坐标，零件关联和焊接操作可在 Process Designer 的工艺流程中配置。

## 命令与文件

三条命令均注册在功能区类别 `TxTools`：

| 命令 | 实现类 | 目标 |
| --- | --- | --- |
| 零件导入 | `DesignerPartTreeCommand` | 原型入 `PartLibrary`，实例入工程根下新建的复合零件 |
| 资源导入 | `DesignerResourceImporterCommand` | 原型入 `ResourceLibrary`，实例入工程根下新建的复合资源 |
| 焊点导入 | `DesignerWeldMfgImporterCommand` | `MfgLibrary` 中的 `PmWeldPoint` 制造特征 |

| 文件 | 职责 |
| --- | --- |
| `DesignerCommands.cs` | 三个命令的注册与窗体启动 |
| `DesignerModelForm.cs`、`DesignerModelForm.Preview.cs` | 模型导入窗体、来源列表与目标树预览 |
| `DesignerImportDialog.cs` | 库目录、保存位置与转换选项对话框 |
| `DesignerImportPackage.cs` | 导入包模型、EmpXML 生成、临时 CAD 包释放 |
| `DesignerLibraryImport.cs` | 工程与目标库解析、`IEmpImportCollection` 提交、错误映射 |
| `TxTools.ProcessDesigner.xml` | Process Designer 外部应用描述（程序集与命令映射） |

## 注册

1. 找到 Release 构建输出。将 `TxTools.dll` 及运行依赖按原相对目录部署到 `eMPower/DotNetCommands/TxTools/`。
2. 将构建输出中的 `ProcessDesigner/TxTools.ProcessDesigner.xml` 放入 `eMPower/DotNetExternalApplications/`。对应程序集路径为 `DotNetCommands/TxTools/TxTools.dll`。
3. 重启 Process Designer，在功能区 **Customize** 的 **TxTools** 类别中添加三个命令。使用旧版安装器为 Process Simulate 注册时，保留该产品对应的命令即可。
