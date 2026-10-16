# R6：图层字段、禁止拾取位与叶节点体积贡献位

后续修正见 [R7](SCENE-STYLES-R7.md)：公共头首字段是变长紧凑继承掩码，头长可为 35/39 字节；本文记录的 +36/+40 等偏移仅适用于短掩码。图层和矩阵标志仍在实际公共头之后，R6 位标志结论继续有效。

日期：2026-10-16。场景版本 `CGRSCENE-20261016-R6-layer-flags`；容器版本仍为 R4，几何算法版本仍为 R56。本轮全部通过 CATIA COM 操作研究文档，没有使用界面自动化，没有部署 PS DLL。

## 修正：原“扩展控制字”实际是定长字段

R5 将 62/A3 公共头之后的 `FF 03 01 00 00` 解释为扩展紧凑整数 259。这只在首字节恰好为 FF 的默认样本上成立。COM `SetLayer(catVisLayerBasic, 37)` 导出变为 `25 00 01 00 00`，旧解析器因此拒绝该合法记录。

现已改为从当前位置顺序读取固定 5 字节：

| 相对公共头结束的位置 | 类型 | 本轮证据 |
| --- | --- | --- |
| +0 | uint16 LE `layer_code` | COM 图层 0、1、37、255、256、999 逐项对应 |
| +2 | uint16 LE `node_options` | 本轮 Part 为 1；完整含义未解 |
| +4 | byte 矩阵标志 | 0 无矩阵，1 后接 16 个 Float32 LE |

这些字段用于 62/A3，95 的公共头后直接进入几何主体。62 的图层字段在记录 +36，矩阵标志仍在 +40；A3 因长度封装多 5 字节。

原生 Part 无图层时为 1023；`SetLayer(catVisLayerNone, 0)` 恢复 1023。另一些装配/既有兼容分支采用 `layer_code=65535,node_options=0`，其完整继承语义尚未证明，不能把 65535 和 1023 无条件互换。

`scene_records.py` 与 C# `WriteSceneNodeAttributes` 均已修正。C# 原来的 `CompactExtended(259/255)` 改为实际图层、选项和矩阵标志写出，默认字节保持一致。子数量、引用号及拾取偏移仍使用真正的紧凑整数。

## 禁止拾取位：公共头第二标志字节的 0x01

对同一研究 Part 分别选择 MainBody 和 Pad：

- `SetPick(catVisPropertyNoPickAttr=1)` 仅将对应节点的标志从 `0x30` 改为 `0x31`。
- `SetPick(catVisPropertyPickAttr=0)` 恢复 `0x30`。
- MainBody 和 Pad 对应不同节点，变化跟随被选对象；叶节点的 0x10 位保持存在。

因此解析器提供 `CommonHeader.no_pick`。它描述该节点的局部属性，不能代替整条父子链的最终可拾取性判断。

隐藏 MainBody 的原生导出只剩根节点，重新显示后恢复四个节点。本轮没有发现可据此命名的“隐藏标志位”，不将节点省略误报成某一位的变化。

## 95 叶节点 0x10：控制本轮 Analyze.Volume 的体积贡献

R5 中 Compact 箱体和圆柱的 `Analyze.Volume` 均为 0。首先用 22 个场景干预文件保持几何和其他逻辑流不变，分别更换根头、A3 包装、叶头属性；然后对三个叶头属性做完整双向组合，共 32 个文件。

三个因素为第二标志字节 `0x20/0x30`、编码槽 `04 FF FF/04 FD FF`、大端属性槽 `FFFFFF01/00000001`。每个模型每个方向穷举 8 个组合。

| 对照 | 箱体体积 mm³ | 圆柱体积 mm³ |
| --- | ---: | ---: |
| Compact 原始叶头，0x10 清除 | 0 | 0 |
| Compact 仅将叶标志 0x20 改为 0x30 | 1,920,000 | 501,220.57596742234 |
| Feature 原始叶头，0x10 存在 | 1,920,000 | 501,220.57596742234 |
| Feature 仅将叶标志 0x30 改为 0x20 | 0 | 0 |

另外两个因素不改变这两份样本的体积结果。根头各因素、添加 Feature 树而保留 Compact 叶头，都不能恢复体积；移除 Feature 的 A3 包装仍保留原体积。因此此前“可能是 A3 包装导致”的假设已被本轮对照排除。

`CommonHeader.volume_contribution_bit` 和 C# `SceneVolumeContributionBit` 记录这项观察。每个变体生成时均验证 95 几何主体哈希不变、拾取及全部其他逻辑流逐字节不变；CFV2 物理分段和目录重新生成。

**这不是“闭合实体认证位”。** 本轮只证明两个已有网格在当前 CATIA 环境中的体积贡献门控；圆柱数值与相同网格的 Feature 输出一致，不等于解析曲面的精确体积。尚未证明开放面、多叶颜色分组、非流形和重复面的测量语义。Compact 默认输出没有自动开启该位。

## 验证和保存的证据

- 14 个新的原生 COM 状态，全部可用修正后解析器严格重建。首次运行保留了图层导致旧解析失败的记录；汇总重新读取这些原始 CGR，证明现已解决，未篡改原始日志。
- 54 个独立 COM Product（22 结构干预 + 32 双向组合）全部加载、测量、保存，错误列表为空。各批恢复原活动文档、告警设置及文档数量。
- 48 项 Python 测试通过，新增原生图层边界值、节点级禁止拾取、隐藏导出、双向体积门控和容器逻辑流保持检查。
- 7 个图层配置的 5 字节属性写出在 .NET 8、.NET Framework 与原生字段完全一致。
- 原有 7 个场景/拾取跨运行时配置继续逐字节一致，73 份既有场景流重建通过。
- 完整编码器 38 组输出与 R5 基线逐字节一致；24 份容器重建、4 种分段布局通过。
- 独立编码器、容器探针、场景探针构建成功；场景代码通过 Windows .NET Framework 编译。编码器有已知的离线 NU1900 警告，无编译错误。完整 PS 工程本轮未构建/部署。

上述计数是文件、状态和配置数量，不是独立 CAD 模型数量。仍未解释的内容包括 0x20、公共头其余标志/标量、node_options 完整位定义、65535 的继承含义、完整标识分配规则，以及附加数据和 CFV3。

工作区证据目录 `analysis_cgr/scene_20261016_r6/`：

```text
variants/variants.json                 22 个结构干预清单
com/com_results.json                   对应 COM 测量结果及 Product
leaf_variants/variants.json            32 个双向组合清单
leaf_com/com_results.json              对应 COM 测量结果及 Product
native_flags_full/native_flags.json    14 个原生状态和原始解析日志
attributes_verification/               原生字段与 net8/net48 写出对照
scene_verification/scene_verification.json
verification/verification.json
R6-summary.json                        全部证据汇总及修正后原生字段
```

复现脚本：`probe_scene_representation.py`、`catia_representation_acceptance.py`、`catia_scene_flag_fixtures.py`、`summarize_scene_r6.py`。创建输出目录时须使用新路径，避免覆盖原始证据。COM 脚本只操作研究副本和自身创建的 Product。
