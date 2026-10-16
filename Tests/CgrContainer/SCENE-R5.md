# R5：场景记录字段生成，替换几何前后缀载荷

最新修正见 [R7](SCENE-STYLES-R7.md)：公共头首字段是紧凑继承掩码，头长并非始终 35 字节；原“category/encoding”部分槽实际组成跨字节图形样式字。本文固定偏移仅适用于原来的短继承掩码样本。

后续修正见 [R6](SCENE-FLAGS-R6.md)：本文“扩展控制字”实际为定长的 uint16 LE 图层、uint16 LE 待解选项、byte 矩阵标志。默认首字节 FF 掩盖了旧误读。R6 已修正解析及写出，并确认禁止拾取位和 95 体积贡献位；本文保留 R5 的历史证据。

日期：2026-10-16。场景版本 `CGRSCENE-20261016-R5-fields`；容器仍为 R4，主几何算法版本仍为 R56。未部署 PS DLL。

## 代码变化

删除 `ExportByColor/CgrTemplate.cs`，新增 `CgrSceneRecords.cs`。Legacy、Compact、LineFace 和 LineFacePlanar 的场景框架现在由字段写出，不再复制原先的 Base64/十六进制前后缀：

| 删除的载荷 | 字节数 |
| --- | ---: |
| ScenePrefix | 230 |
| SceneSuffix | 52 |
| PacketPrefix | 9 |
| FaceReserved / EdgeReserved | 2 + 2 |
| Root95 / Branch95 / Leaf95 | 41 + 41 + 41 |
| Pick95 | 61 |
| 合计 | **9 块、479 字节** |

这不是输出文件缩小 479 字节，而是这些固定载荷从源码中删除，替换为节点、长度、球、计数、链接和拾取字段序列化。输出保持兼容。

场景根球在写入节点时直接提供，删除了复制模板后再按多个固定偏移修改球/长度的路径。包裹记录长度根据实际起止位置计算；不再使用 `ScenePrefix` 切片或 `WrapFeature95` 的固定长度偏移。主工程及独立编码器工程已改为编译新文件，测试工程保留旧源码目录的条件回退。

## 新原生 COM 证据

`catia_scene_fixtures.py` 只创建本轮研究 Part/Product，导出 12 个状态：

- 盒子范围 `[11,22,0]..[41,62,50]`，重复导出、加高到 60、恢复到 50。
- 单实例装配、平移 `(101,203,307)`、增量 Z 旋转 90°、再添加同一 Part 的第二实例。
- 颜色、不透明度、线宽、线型变化。

源 Part 和双实例 Product 保存供复核。没有改全局设置，仅关闭本轮新建文档，恢复原活动文档及文档数量。

## 已确认的场景边界

独立 Python `scene_records.py` 从逻辑流起点递归解析，不搜索记录签名，不在出错后重同步。几何主体暂作为有严格长度边界的透明字节区保存。

| 记录 | 已验证布局 |
| --- | --- |
| `62` | tag + 35 字节公共头 + 扩展控制字 + 可选矩阵 + 子数量 + 子链接 |
| `A3 FF` | uint32LE 长度 + 公共头/控制 + 子链接 + 尾部 Float32 |
| `95 FF` | uint32LE 长度 + 35 字节公共头 + 有界几何主体；之后另有紧凑拾取偏移 |

**A3 长度 = 该记录总字节数 − 2**，不计 tag 和 FF。

**95 长度 = 该记录总字节数 − 1**，只不计 tag；其后的紧凑拾取偏移不在该长度内。两种计数不能共用同一个偏移修补规则。

公共头含三字节标志、三字节编码槽、一个大端 uint32 属性槽、单字节槽、5 个小端 Float32（球半径、未解释浮点槽、中心 XYZ）和一个小端 uint32 标识槽。标识槽并未被解释成完整的 CAD 对象 ID 分配规范，其余标志/属性的完整语义也未确认。

对于 `62`，球半径在记录 +12，中心在 +20；`A3/95` 多了长度封装，对应 +17/+25。本轮盒子的局部球中心为 `(26,42,25)`，加高后 Z 变为 30，球包含相应的 8 个盒角。没有强行将所有根球中心解释为 Header 包围盒中心，也没有推定原生球的完整拟合/容差算法。

## 矩阵和共享引用

平移后原生场景恰好多出 **64 字节**。扩展控制字的最高字节为矩阵标志，在 `62` 的 +40；为 1 时紧跟 16 个小端 Float32。平移样本得到：

```text
1 0 0 0
0 1 0 0
0 0 1 0
101 203 307 1
```

CATIA `Move.Apply` 是增量操作；再旋转 90° 后，平移项变为 `(-203,101,307)`，与矩阵组合相符。解析器已保留矩阵；当前 C# 生成路径仍沿用既有的几何输出策略，没有新增实例变换生成功能。

同一零件的双实例样本中，引用表索引 0 首次内联定义、第二次复用，几何 A3 只出现一次。这一引用索引与公共头的 32 位标识槽分别处理。

原生 FFM170 的完整场景解析并逐字节重建：**776 个节点、147 个矩阵、58 次引用复用**。没有把整个 A3 当成不可读黑盒后就宣称场景树解析完成；其子树、长度和拾取偏移均按本轮语法遍历，95 几何主体仍保留为有界载荷。

## 拾取记录字段

原来的 9 字节 PacketPrefix 已拆为 `Float32(π/4)`、`Float32(10^12)`、byte 0。数值与原字节一致，物理含义未确认，不将它们直接命名为角度容差或距离容差。

Compact 的 61 字节拾取记录由长度、上述控制字段、标识/保留槽、minXYZ、maxXYZ、球中心、半径和尾字节组成。六色盒子叶子的偏移为 `0,61,122,183,244,305`，最后一项触发扩展紧凑整数编码；已核对它们分别指向实际拾取记录起点。

## 验证

- **42 项 Python 测试通过**，覆盖 A3/95 长度差异、矩阵、共享引用、边界球、拾取偏移、长度损坏、缺失矩阵、未定义引用，以及载荷中的假记录签名。
- **73 份场景流**严格重建：本轮 12 个原生状态、38 个编码输出和 23 份历史原生样本。73 是文件/状态计数，不是 73 个独立模型。
- 7 个单独序列化配置在 .NET 8 与 .NET Framework 中与已有场景/拾取记录逐字节一致，包括非零和扩展拾取偏移。
- 最终完整编码器的 **38 组输出与 R4 二进制基线逐字节一致**；该基线此前也与 R56 基线一致。24 份旧容器重建、4 种分段布局继续通过。
- 盒子、六色盒子、圆柱 × Compact/LineFace/LineFacePlanar，共 **9 对、18 个独立 Product**，完成 COM 加载、显示更新、保存、体积对照。已查看六色盒子和圆柱的捕获图，模型完整、颜色及圆柱显示正常。
- 6 对线面模式均测得相同的非零体积。**3 对 Compact 模式均返回 0**，所以该部分仅作为加载、显示和字节兼容性证据，不能证明实体封闭性或 CAD 精确体积。
- 最终构建结果与 COM 验收使用的第一批文件逐字节一致，清单和哈希保存在汇总报告；根球写入路径的最终调整未改变已验收文件。

独立 .NET 8 编码器及场景探针构建成功，场景探针也通过 Windows .NET Framework 编译。完整 PS 工程未构建/部署；编码器构建有离线 NuGet 漏洞索引的 NU1900 警告，无编译错误。

## 仍保留的兼容配置

当前 CFV2 场景路径已删除这些前后缀/头部字节图片，但仍保留已接受的标志值、标识槽值、部分控制浮点值和树层次选择。例如线面包裹仍采用既有分支和占位节点，而本轮新原生 Part 的分支数量可以不同。

因此“字段生成”不等于全部字段语义已解开，也不等于任意 CATIA 场景都能生成。完整 ID 分配、部分公共头标志/浮点槽、Header/Describe 控制值、附加数据内部语义和 CFV3 仍是后续工作。

工作区目录：`analysis_cgr/scene_20261016_r5/`。

```text
before/                            修改前 4 个源码文件
native/native_scene_fixtures.json  12 个 COM 受控状态
native/scene_box.CATPart
native/scene_placements.CATProduct
verification_final/verification.json
scene_verification_final/scene_verification.json
com_acceptance/catia_scene_acceptance.json
com_acceptance/color_box_compact.png
com_acceptance/cylinder_features.png
R5-summary.json
```

重跑时在仓库目录使用新的输出目录：

```powershell
rtk python -X utf8 -m unittest discover -s Tests/CgrContainer -p test_*.py
rtk python -X utf8 Tests/CgrContainer/catia_scene_fixtures.py ../analysis_cgr/r5_native_new
rtk dotnet build Tests/CgrSceneRecords/CgrSceneRecords.csproj -p:MSBuildEnableWorkloadResolver=false -p:NuGetAudit=false --ignore-failed-sources -o ../analysis_cgr/r5_probe_new
```
