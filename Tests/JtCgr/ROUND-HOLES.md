# R49 单个 CGR 内的内外圆柱重建

生产试用模式：**读取 JT → 内外圆柱重建 CGR → CATIA（试用）**。
版本标识：`CGR-20261010-R49-cylinders-chamfer-boundaries`。
正式源码位于 TxTools 项目，Release 输出到 `E:\ProcessSimulatePlugin\Process Simulate\bin`。
JT 路径和资源放置矩阵在 PS 线程获取；解码、识别、重建、CGR 编码在有界工作线程执行。
CATIA 装配插入在 STA 线程完成。纯 .NET Framework 4.8 C#，无需 Python、CAA SDK 或运行时 NuGet 包。

## 漏检原因与修复

R48 已支持低至 8 点的完整圆环及外圆柱，但仍有以下遗漏：

- 圆柱壁与倒角在同一个平滑组件中，壁面径向检查将整个组件拒绝。
- 孔口与端面接缝共用顶点，全局锐边图产生分支，实际闭合的圆柱边界没有被提取。
- 小孔环的 float32 坐标误差使拟合轴倾斜，误差传播到大端面后超过平面容差。
- 共线孔桥或细长三角片使端面三角化、边界恢复失败。

R49 在原有 60° 组件分组之外补充 30°、15° 分组，分别提取每个壁面组件自身的闭合边界。
补充候选不得占用已接受候选的壁面三角形；继续验证完整单次 360° 环绕、圆拟合残差、
两端同轴同半径、全部壁面顶点半径及法向证据。生产入口显式开启
`Options.IncludeExternalCylinders` 和 `Options.IncludeTransitionRims`；默认分析行为保持关闭。

轴向由相邻平面面积加权法向及两个圆环中心共同校验。配对后的轴修正仅在原误差超过
0.001 mm、且新轴将误差降低至少 20% 时接受，并重新拟合两个完整圆环。
该门槛防止微小轴向更新导致 float32 细片退化。

带倒角的圆柱边缘单独连接：在原过渡三角片内插入圆弧边界，保留另一侧顶点及原材质，
重新三角化连接片。这里重建的是圆柱与原过渡面的连接，未将倒角整体转换为原生锥面。
平面端面的初步点面检查容差为 0.01 mm，最终仍执行平面漂移和拓扑检查。
端面三角化失败时，最多尝试四个刚性旋转的二维投影，不改变三维尺寸或顶点；
只清除投影面积恰好为零的内部孔桥三角形，随后检查所有边界、内部边邻接、绕向及面积。
圆柱壁、端面和过渡连接共同接受或整体回退。

## CGR 内容和适用范围

每个资源输出一个 CGR，重建壁面替换原壁面三角形，圆形端边与相邻端面共用细分顶点。
Shape、材质和不相连片区分别处理，保留原始颜色与透明度。
圆弧理论弦高不超过 0.002 mm，至少 96、最多 2048 段；实际精度也受源网格拟合和 float32 坐标影响。
拟合容差为 `max(0.01, radius * 0.001)`，沿用生产毫米坐标。

CGR type-13 面属性保存 CATIA 原生圆柱中心、单位轴方向和半径，圆边属性保存圆心与半径。
参数格式已对照 CATIA 自行导出的五个不同轴向圆柱验证。显示网格和测量属性同时生成，不附加 CATPart 或参考轴线。
参照 [Dassault CAA CATSurfacicRep 示例](https://www.maruf.ca/files/caadoc/CAAVisUseCases/CAAVisSampleCATSurfacicRep.htm)
中显示几何与 `CATVisMeasurableGP.SetCylinder` 的组合。

CGR 显示仍使用三角网格，不具有 CATPart 的建模历史或 BRep。不同 CATIA 捕捉工具是否使用
这些圆柱属性，需由用户实际操作验收；属性记录和成功打开文档不能证明所有工具都已识别轴线。
本算法从离散网格拟合，不能证明原 CAD 的设计意图；规则多边形也可能与低分段圆采样无法区分。
当前覆盖完整圆周、两端可连接平面或过渡区域的圆柱壁；纯锥面、局部圆弧壁、开放边界、
颜色混合壁面或不满足连接检查的候选保留原几何。

## 2026-10-10 正式 DLL 验证

| 样本 | JT 版本 | R48 内孔 | R49 内孔 | R48 外圆柱 | R49 外圆柱 | R49 输出三角形 | 原生圆柱/圆边属性 |
|---|---|---:|---:|---:|---:|---:|---:|
| T1E24MY-9156 | 10.6 | 788 | 877 | 636 | 805 | 1,465,097 | 1682 / 3364 |
| T13J-5156 | 10.0 | 932 | 1005 | 462 | 726 | 1,744,934 | 1731 / 3462 |
| T13J-5153 | 8.0 | 649 | 717 | 295 | 460 | 1,114,541 | 1177 / 2354 |

三份完整 CGR 经独立解码回读，有向 XYZ + RGBA 多重集合及原生圆柱参数差异均为零。
T1E24MY 正式 JT 解码入口输出与独立验证 CGR SHA256 相同，37,014,058 字节；
已作为单 CGR 插入 CATIA 新建验证文档。整模和 Shape 464 多孔局部、Shape 525 外圆柱局部均已打开检查。
Shape 464 实际替换 16 段内圆柱；Shape 525 新恢复一段半径约 5 mm、长度约 157 mm 的外圆柱。

T1E24MY 共 2061 个拟合候选，其中 1750 个具备双圆环，1722 个满足材质、轴向和深度等替换预条件。
实际替换 1682 个，另外 40 个保留原网格：端面轮廓拓扑 13、边界恢复 14、
过渡面三角化 4、非流形 4、平面漂移 3、开放边界 1、内环包含关系 1。
28 个双圆环候选未满足替换预条件。其他两份样本分别保留 83、74 个符合初步条件但连接检查失败的候选。
这些数量不是人工标注的真实孔数，也不代表所有可见圆孔已覆盖。

`*.cgr.holes.csv` 保存所有拟合候选，`kind` 区分内孔与外圆柱；
`*.cgr.cylinders.csv` 仅保存实际输出的圆柱；`*.cgr.cylinders.log` 保存保留原几何的详细原因。
返回值和重建日志只统计实际输出的圆柱。

## 回归和复现

新增 `RoundCylinderTransitionRegression.cs` 覆盖带倒角的内孔、外圆柱、任意轴向、
1 mm 小孔邻接 200 mm 大端面时的微小环平面误差，并在过渡模式下拒绝椭圆、纯锥面和破口。
`RoundCylinderRegression.cs` 覆盖 8/16 分段实心外圆柱及共用端面的空心圆柱、大坐标；
检查封闭模型每条边恰好两次邻接、内外绕向、半径/长度、RGBA 与输入不变。
已有圆孔、阶梯孔、Shape 隔离、单孔口不赋深度及非平面、星形、重叠网格等回归均通过。
线面压缩的 105 个有向数据包、6 个边链、3 个面索引置换及 alpha 0/128/255 拓扑检查通过。
原生编码未改；已有五个 CATIA 轴向探针的 10 个圆柱属性逐字节往返验证继续适用。

生产回读工具引用正式 TxTools.dll，并编译独立的 `FeatureTestDecoder.cs`。
`RoundHoleDirectRegression.cs` 链接 INFITF 与 ProductStructureTypeLib interop，在 STA 中运行正式转换并插入 CATIA。
匹配的 jtmesh 缓存必须通过源 JT 指纹检查；`verify:` 模式重新计算预期重建并独立读取已有 CGR，不重写输出。

```powershell
rtk proxy .\RoundHoleRegression.exe production-bin
rtk proxy .\RoundHoleCgrMeshRegression.exe production-bin
rtk proxy .\RoundHoleCounterboreRegression.exe production-bin
rtk proxy .\RoundCylinderRegression.exe production-bin
rtk proxy .\RoundCylinderTransitionRegression.exe production-bin
rtk proxy .\DirectHoleCgrProductionReadback.exe source.jt matching.jtmesh new-output-directory all production-bin
rtk proxy .\DirectHoleCgrProductionReadback.exe source.jt matching.jtmesh new-check-directory verify:existing.cgr production-bin
rtk proxy .\RoundHoleDirectRegression.exe source.jt new-output-directory production-bin verified.cgr
rtk proxy dotnet run --project Tests/CgrLineFace/CgrLineFace.csproj -c Release -- --self-test
```

旧 CATPart 参考工具仅保留为独立实验，不参与 R49 生产模式。客户 JT、输出 CGR/CATProduct 和截图不进入 Git。
