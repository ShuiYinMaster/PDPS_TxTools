# R48 单个 CGR 内的内外圆柱重建

生产试用模式：**读取 JT → 内外圆柱重建 CGR → CATIA（试用）**。
版本标识：`CGR-20261010-R48-inner-outer-cylinders`。
正式源码位于 TxTools 项目，Release 输出到 `E:\ProcessSimulatePlugin\Process Simulate\bin`。
JT 路径和资源放置矩阵在 PS 线程获取；解码、识别、重建、CGR 编码在有界工作线程执行。
CATIA 装配插入在 STA 线程完成。纯 .NET Framework 4.8 C#，无需 Python、CAA SDK 或运行时 NuGet 包。

## 修复原因与实现

R47 只接受向内的圆柱壁。低分段 JT 圆环还会受到最小 10 点和 45° 相邻面分组阈值限制。
另一类漏检来自端面的细长三角片：float32 坐标误差放大了几何法向误差，同一个平面被拆成多个
不闭合片区；旧边界算法也将多个轮廓在同一顶点相接的情形整体拒绝。

R48 将最小完整圆环调整为 8 点，允许粗分段圆柱壁连续分组，继续检查单次 360° 环绕、圆拟合残差、
两端同轴同半径和完整墙面组件。端面分组使用拟合圆环平面及坐标尺度容差，同时检查原始面朝向；
容差限制在 0.001～0.01 mm。带分支的端面按有向边走闭合轮廓，分别关联外轮廓和其内部孔环。
EarCut 在每个带孔区域内三角化，恢复被省略的共线边界顶点，检查面积及边的邻接次数。

向外的法向证据识别外圆柱，生成壁面使用相反绕向。内外壁共用端面时一次重建整个端面区域，
避免旧端面挡住新圆孔或与新增圆柱叠加。默认 `Find` 仍仅分析内孔；生产重建入口明确开启
`Options.IncludeExternalCylinders`。其他 JT→CGR 模式不会自动启用重建。

## CGR 内容和适用范围

每个资源输出一个 CGR，重建壁面替换原壁面三角形，圆形端边与相邻端面共用细分顶点。
Shape、材质和不相连的片区分别处理，保留原始颜色与透明度。
圆弧理论弦高不超过 0.002 mm，至少 96、最多 2048 段；实际精度也受源网格拟合和 float32 坐标影响。
拟合容差为 `max(0.01, radius * 0.001)`，沿用生产毫米坐标。

CGR type-13 面属性保存 CATIA 原生圆柱中心、单位轴方向和半径，圆边属性保存圆心与半径。
参数格式对照 CATIA 自行导出的五个不同轴向圆柱验证。显示网格和测量属性同时生成，不附加 CATPart 或参考轴线。
参照 [Dassault CAA CATSurfacicRep 示例](https://www.maruf.ca/files/caadoc/CAAVisUseCases/CAAVisSampleCATSurfacicRep.htm)
中显示几何与 `CATVisMeasurableGP.SetCylinder` 的组合。

CGR 显示仍使用三角网格，不具有 CATPart 的建模历史或 BRep。不同 CATIA 捕捉工具是否使用
这些圆柱属性，需由用户实际操作验收；属性记录和成功打开文档不能证明所有工具都已识别轴线。
本算法从离散网格拟合，不能证明原 CAD 的设计意图；规则多边形也可能与低分段圆采样无法区分。
当前覆盖完整圆周、两端有可连接平面区域的圆柱壁；锥面、局部圆弧壁、开放边界、颜色混合壁面等保留原几何。
壁面及相连端面原子接受或回退，不用叠加新表面掩盖失败。

## 2026-10-10 正式 DLL 验证

| 样本 | JT 版本 | R47 内孔 | R48 内孔 | R48 外圆柱 | R48 输出三角形 | 原生圆柱/圆边属性 |
|---|---|---:|---:|---:|---:|---:|
| T1E24MY-9156 | 10.6 | 642 | 788 | 636 | 1,314,769 | 1424 / 2848 |
| T13J-5156 | 10.0 | 548 | 932 | 462 | 1,553,796 | 1394 / 2788 |
| T13J-5153 | 8.0 | 377 | 649 | 295 | 976,983 | 944 / 1888 |

三份完整 CGR 经独立解码回读，有向 XYZ + RGBA 多重集合差异均为零。
T1E24MY 的正式 JT 解码入口输出与独立验证 CGR SHA256 相同，34,284,721 字节；
已作为单 CGR 插入 CATIA 新建验证文档。整模、恢复内孔的 Shape 1749 和外圆柱 Shape 1679
局部均已打开；Shape 1749 在 R47 没有成功替换，R48 替换了 8 个内孔。

T1E24MY 共 1870 个拟合候选，其中 1504 个具备双圆环；1477 个满足颜色、深度等替换预条件。
实际替换 1424 个，剩余 53 个的原因：端面不符合拟合平面 25、端面轮廓拓扑 5、边界恢复 15、
非流形 4、平面漂移 2、开放边界 1、内环包含关系 1。回退原因及示例 Shape 会写入日志。
`*.cgr.holes.csv` 保存所有拟合候选，`kind` 区分内孔与外圆柱；返回值和替换日志只统计实际输出的圆柱。
这些数量不是人工标注的真实孔数，也不代表所有可见圆孔已覆盖。

## 回归和复现

`RoundCylinderRegression.cs` 覆盖 8/16 分段实心外圆柱、共用端面的空心圆柱、任意轴向和大坐标；
检查封闭模型每条边恰好两次邻接、内外绕向、半径/长度、RGBA 与输入不变，并拒绝外椭圆、锥面和破口。
已有回归覆盖十点阶梯孔两级壁面与共用环形端面、圆孔旋转/平移、Shape 隔离、单孔口不赋深度，
以及椭圆、六角孔、非平面环、双环绕星形、重叠网格等拒绝案例。
线面压缩的 105 个有向数据包、6 个边链、3 个面索引置换及 alpha 0/128/255 拓扑检查通过。
五个 CATIA 原生轴向探针的 10 个圆柱属性逐字节往返通过。

`RoundHoleCgrMeshRegression.cs`、`RoundCylinderRegression.cs` 与 `DirectHoleCgrRegression.cs`
引用生产 TxTools.dll 编译；最后一个还编译独立的 `FeatureTestDecoder.cs`。
`RoundHoleDirectRegression.cs` 链接 INFITF 与 ProductStructureTypeLib interop，在 STA 中运行正式转换并插入 CATIA。

```powershell
rtk proxy .\RoundHoleRegression.exe production-bin
rtk proxy .\RoundHoleCgrMeshRegression.exe production-bin
rtk proxy .\RoundHoleCounterboreRegression.exe production-bin
rtk proxy .\RoundCylinderRegression.exe production-bin
rtk proxy .\DirectHoleCgrRegression.exe source.jt matching.jtmesh new-output-directory all production-bin
rtk proxy .\RoundHoleDirectRegression.exe source.jt new-output-directory production-bin verified.cgr
rtk proxy dotnet run --project Tests/CgrLineFace/CgrLineFace.csproj -c Release -- --self-test
```

旧 CATPart 参考工具仅保留为独立实验，不参与 R48 生产模式。客户 JT、输出 CGR/CATProduct 和截图不进入 Git。
