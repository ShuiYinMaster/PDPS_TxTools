# R3：可视化精度、线框位及附加容器清单

R4 更正：早期解析器将名称长度误读成 B/D 字符，实际存储名以 `.` 开始。本文的精度、线框及附加容器实验结论不受影响；身份边界和短名称现在已解出，详见 [R4](IDENTITY-R4.md)。

日期：2026-10-15。容器版本：`CFV2-20261015-R3-visualization-fields`。继续采用 CATIA COM 原生导出；没有使用鼠标键盘自动化，也没有部署 PS 插件。

## 新确认的 Header 字段

在新建点对象和圆柱 Part 上导出 34 个受控状态，逐项记录 COM 设置读回值。每个设置用例开始前恢复完整快照；结束后再次核对快照完全一致。这里只编辑本轮新建研究文档。

| Header 偏移 | 类型 | 已确认对应项 | 实验值 |
| --- | --- | --- | --- |
| +10 | byte | `Viz3DAccuracyMode`；1 固定，0 比例 | true / false |
| +11 | LE float32 | 当前模式选中的 3D 精度值 | 固定 0.37；比例 0.43 |
| +15 | LE float32 | `Viz3DCurveAccuracy` | 0.73 |
| +19 | byte | `Viz2DAccuracyMode`；1 固定，0 比例 | true / false |
| +20 | LE float32 | 当前模式选中的 2D 精度值 | 固定 0.047；比例 0.053 |

固定精度和比例精度是两份设置，但 Header 只写入当前模式选中的一份。修改 COM 的固定/比例精度值时，CATIA 同时选择对应模式；因此脚本恢复时必须先恢复精度值，最后恢复模式布尔值。第一轮原始报告保留在 `native_fields/`，用于结论和回归测试的是完整隔离、包含全部读回值的 `native_fields_isolated/`。

`HeaderRecord` 已将这 5 个字段改为语义名称，并保留偏移名只读别名。C# 增加 `CgrVisualizationAccuracy`，由字段生成这些字节，默认仍为固定 3D 0.2、曲线 1、固定 2D 0.02。研究探针可以传入不同值；默认生成结果保持逐字节兼容。

这是 **Header 元数据参数**。本轮对同一个已生成表示修改精度设置，原生导出中的几何流保持原值；不能据此宣称网格已经按新精度重新离散化。C# 参数同样不执行重新网格化，调用者需要提供相符的几何流。

## Describe 的线框位

原生 `LineicCgrMode=false` 时 capability 为 `0x16`，切换为 true 后为 `0x36`，仅增加 `0x20`。关闭缓存 `LODMode` 后组合复核，仍得到 `0x36`。

Python 已提供 `DescribeRecord.lineic_cgr_mode`。开启该选项时 `SceneGraph/MainDataStream` 和 `SurfacicReps` 也同时改变，因此 C# 没有提供仅翻转 Describe 位的开关。其余 capability 位，以及 `format_tag` 和末尾 7 字节的语义仍未确认。

## 附加数据存储和清单

通过本机安装的 `InfTypeLib.tlb`、`V5Automation.chm` 以及 `VisualizationBase.dic` 确认 COM 控制器名称和成员，然后逐项切换 `CATVisCGRAdhesionSettingCtrl`。在重新打开的本轮研究 Part 上，完成 14 个状态的最终矩阵：

| 原生选项 | 总节点数 | 主要新增内容 |
| --- | --- | --- |
| 基线 | 13 | 无附加存储 |
| `V5_SPA` | 21 | `Applicative Containers/V5_SPA/V5_SPA_Inertia` |
| `Voxels` | 25 | `Applicative Containers/Voxels/Level0`、`Level1` |
| 单独打开任一 V4 附加选项 | 18 | 对应 V4 存储，存在空载荷 |
| `V5_SPA` + `Voxels` | 30 | 两组附加数据 |
| 5 项全部打开 | 36 | 全部目录节点；清单仍只登记两项 |

这些附加选项没有改变 Describe 的 `0x16`。没有将附加存储存在与否误判为 capability 位。

`Applicative Containers/MainDataStream` 的已观测短 ASCII 布局为：

```text
FF | uint32_LE 名称数量 | (单字节短长度 | ASCII 名称) × 数量
```

空 V4 情况为 `ff00000000`；SPA 为 `ff010000000656355f535041`；SPA 与体素同时启用为 `ff020000000656355f53504106566f78656c73`。全部选项打开时，目录中有 5 个附加存储，但该记录数量是 2，内容只有 `V5_SPA` 和 `Voxels`。它不是目录子节点总数。

新增 `ApplicativeContainerList` 和 `applicative_records()`，支持本轮观测的短 ASCII 名称并逐字节重建。长名称、非 ASCII 编码及 FF 标志的通用含义未推定；SPA/体素内部数据尚未解码。

## 原生空存储对解析器的修正

原生 V4 附加选项可以产生 `size=0`、`extent_count=0` 的 class=1 存储；即使没有索引表载荷，目录中仍可有子节点。先前把“每个存储都必须有索引表”当作通用规则会拒绝这种合法文件。

现在空存储的 `storage_index()` 返回 `None`，表示索引缺席；它与有索引表、表内子项数为 0 的情况不同。有索引表时仍核对目录树；描述符/分配记录一致性、连续性、物理完整覆盖和全部长度验证继续执行。空流/空存储在重新分段时保持零分段，不制造空 extent。

初次附加选项报告保留在 `native_options/`，当时严格解析器未支持这些形式。修正后的完整无错误 COM 报告在 `native_options_final/`；没有覆盖初次失败证据。

## 计数器和未被外观实验解释的槽

两个新点对象的独立重复实验得到相同 word3 增量：重复导出、无修改 Update 后导出、仅重命名后导出各 +1；单次颜色、不透明度、符号和宽度修改后导出各 +3；X 改变和恢复后导出各 +6。这是当前实验的分配行为，不是已还原的完整计数器算法。

点/实体的颜色、不透明度、宽度等修改会改变场景载荷，但没有改变 Header +51 的 4 字节槽和 +55/+59 浮点值。因此仍保留未知名称，不将其解释为模型颜色或透明度。

首次选项实验被旧解析器展示为 `B.*`，其实是 66 字节名称长度的低半字误读；并非重新打开 CATPart 产生了另一种身份命名空间。后续最终 14 份选项样本本来都通过了身份时间校验，R3 汇总脚本却错误地将它们统称为 14 份未解释 B 身份。R4 已从原始字节重新核对并修正汇总，48 个字段/选项状态均可校验；旧汇总保留为 `R3-summary.original.json`。首次短名称样本现在也可校验。具体编码规则和分界见 R4。

## 验证结果与产物

工作区根目录：`analysis_cgr/container_20261015_r3/`。

- 34 个隔离字段状态 + 14 个选项状态，共 48 份原生导出完成严格容器重建。这是同一组研究 Part 的受控状态，不是 48 个独立模型。
- 8 组 C# 参数输出与对应原生 Header 的 63 字节完全一致，几何/拾取流保持原生字节。拒绝 NaN、Infinity、负数和零精度输入。
- 1 个原生对照 + 8 个 C# 候选，分别放入独立 Product，通过 COM 加载、显示更新、体积对照及 SaveAs；体积均为 `14627.516981078854 mm³`。这是 CGR 表示的测量值，不作为圆柱解析体积精度证明。
- Python **28 项测试通过**；包含原生读回映射、线框几何联动、清单计数、空存储、损坏输入和已有身份测试。
- **38 组**既有完整编码器输出与 R56 基线逐字节一致；24 份旧样本重建及 4 种分段布局继续通过。
- .NET 8 两个探针构建成功；.NET Framework 独立容器探针构建成功，默认输出与 .NET 8 相同。完整编码器构建仍有离线 NuGet 漏洞索引的 NU1900 警告，无编译错误。

关键报告：

```text
native_fields_isolated/native_field_fixtures.json
native_options_final/native_option_fixtures.json
accuracy_verification/visualization_verification.json
com_accuracy/catia_accuracy_acceptance.json
verification/verification.json
R3-summary.json
```

仍待解码：身份 word0、完整 word3 分配规则；Header +9/+24/+25/+26、+51/+55/+59；Describe 其余位和尾部；目录部分控制值；几何场景前后缀与附加数据内部载荷。R4 已解决本文原列为未知的 B/D 名称问题。容器已经字段生成，但仍不能宣称整个 CGR 脱离全部固定配置。

## 重跑

在仓库目录使用新输出目录：

```powershell
rtk python -X utf8 -m unittest discover -s Tests/CgrContainer -p test_*.py
rtk python -X utf8 Tests/CgrContainer/catia_field_fixtures.py ../analysis_cgr/r3_fields_new
rtk python -X utf8 Tests/CgrContainer/catia_option_fixtures.py ../analysis_cgr/container_20261015_r3/native_fields_isolated/field_cylinder.CATPart ../analysis_cgr/r3_options_new
rtk python -X utf8 Tests/CgrContainer/verify_visualization.py ../analysis_cgr/container_20261015_r3/native_fields_isolated ../analysis_cgr/r3_accuracy_new --probe ../analysis_cgr/container_20261015_r3/probe/CgrContainer.dll
```

COM 脚本需要连接运行中的桌面 CATIA；当前环境通过正常审批在沙箱外运行。设置快照在首次修改前写盘，不执行 Commit/SaveRepository；正常结束和异常分支都恢复设置，仅关闭本脚本创建或打开的研究文档。
