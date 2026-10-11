# R54 单个 CGR 内的内外圆柱重建

生产试用模式：**读取 JT → 内外圆柱重建 CGR → CATIA（试用）**。
版本标识：`CGR-20261012-R54-constrained-cylinder-refinement`。
正式源码位于 TxTools 项目，Release 输出到 `E:\ProcessSimulatePlugin\Process Simulate\bin`。
JT 路径和资源放置矩阵在 PS 线程获取；解码、识别、重建、CGR 编码在有界工作线程执行。
CATIA 装配插入在 STA 线程完成。纯 .NET Framework 4.8 C#，无需 Python、CAA SDK 或运行时 NuGet 包。

## R54 相交壁面细分与遗漏候选回退（2026-10-12）

R53 保留带横向孔的基础圆柱粗网格，以避免完整圆筒盖住开口。R54 对这些源壁面进行
约束细分：内外圆柱与相交孔的共同顶点同时满足各自半径，孔口处保留原端面分片平面。
共享边中点同步拆分两侧三角片；仅在同一个源 Shape 内合并坐标完全相同的顶点，
不同零件的接触面不能因坐标重合而焊接。源中心、轴线、半径、深度和逐面 RGBA 保持不变。

细分过程中，曲率可能使参数域中近共线的小三角片在三维空间翻转。先尝试调整同材质、
同圆柱片区的内部对角线，两片的新法向都正确时才接受；必要时在原片区内重新三角化并
保留内部顶点，不能直接反转面绕向。最终检查半径残差、原端面平面、正向三角片和边邻接；
原本封闭的 Shape 重建后仍须封闭。显示边中点弦高目标 0.002 mm，径向及平面残差上限
0.001 mm，均受源拟合及 Float32 坐标限制。

已具有完整源圆柱拟合证据、但旧端面重建未通过的候选，补充直接约束细分其原壁面。
全部原壁面片都须仍在结果中且未被其他圆柱占用，不能以部分壁面宣称重建成功。
支持壁面分片颜色/透明度不同；成功后保留各片的原 RGBA，完全透明壁面不新增重建。
失败时恢复原分组，不留下部分圆柱属性。每 Shape 采用最多 24 次细分迭代、150000 面片、
100000 顶点的主要预算，并限制内部搜索工作量为 4000000；超限记录
`refinement-work-budget` 并保留原几何，避免复杂网格长时间占住并行转码线程。

最终正式 DLL 重新解码完整 9156 JT、输出一个 CGR 并插入 CATIA：
**2562 段原生圆柱组（1725 内孔、837 外圆柱），67 段非平面裁剪端口**。
较 R53 多 8 段；源参数 CSV 比较确认原 2554 段全部保留。
原 15 段穿孔基础壁面中 11 段完成细分；另 8 段旧失败候选通过原壁面回退路径接受。
输出 1,912,688 个三角形、42,642,763 字节，完整模型位于
`r54_final_production_catia/validation.CATProduct`。

六组源几何回归通过：裁剪端口、开口圆柱、倒角连接、内外圆柱、圆孔拒绝样例与新增
`RoundCylinderMaterialRegression.cs`。混色测试检查两种颜色及透明度边界、封闭拓扑、
输入不变和新增三角形记账。三个实际片区回归通过，其中
`PuncturedCylinderFixtureRegression.cs` 的 43 段圆柱中，目标基础壁面的 **9 段全部完成细分**，
同时检查横向孔未被任何壁面三角片内部覆盖、圆柱半径、弦高、封闭邻接、材质及源输入不变。
另两个实际片区回归分别保持 19 和 16 段。

最终 DLL 对匹配源 JT 缓存的兼容检查：5153（JT 8.0）接受 **2310 段（1817 内孔、493 外圆柱）**；
5156（JT 10.0）接受 **2899 段（2138 内孔、761 外圆柱）**。均较 R53 增加 10 段；
这两项检查没有重新导出 CGR 或进行 CATIA 显示，不等同于完整输出或轴线捕捉验收。
局部直接 CGR 审查文档为 `r54_joined_details/shape_461.CATProduct`、
`shape_1720.CATProduct` 和 `shape_831.CATProduct`，均已插入 CATIA。
本轮未执行 CGR 独立回读，源码及 README 同步到 GitHub，不提交客户 JT 或输出模型。

剩余 4 段原生圆柱仍保留源壁面：Shapes 525、864、2426 的原 JT Shape 分别有
3、40、40 条非流形边；Shape 831 在单独片区中可细分，但完整装配重建第 7 次迭代产生
非流形连接，已整体回退。该段不能统计为完成显示细分，也不能将其归咎于原始非流形网格。
其他未通过拟合、端面、约束或预算的候选仍保留原几何；接受数量不代表所有孔轴已覆盖。

## R53 漏检原因与修复（历史）

R53 检查整个源圆柱壁的边邻接。Shape 461、477 的内外筒壁，以及 1720、1843
的轴套/轴壁，除两端圆环外还存在横向孔边界。旧算法仅在两端圆环之间生成完整圆筒，
会遮住横向孔，并使后续孔口失去原端面接缝。现在保留这些带穿孔壁面的原分片，
对其经过源网格验证的中心、轴向及半径写入原生圆柱属性，再沿原片区裁剪并重建横向孔。
生成面保存源面来源，端面裁剪后仍可将保留的筒壁分配到正确的圆柱属性组。
这类基础壁面的显示细分密度尚未提高，不能将其统计为已重新细分的完整圆筒。

Shape 1724 的半径约 1.54994 mm 孔，一端虽接近同一平面，却连接多个原分片。
原算法将该端统一按台阶环面重建，产生自交边界。R53 根据每条原孔环边的相邻三角面
判断端面填充圆环的内侧还是外侧：外侧孔口逐片裁剪并按原平面计算高度，
内侧沉孔台阶继续采用整体环面三角化。填充侧不一致、裁剪台阶不能证明或拓扑检查失败时回退。
0.001 mm 原片平面检查及完整接缝检查保留；不放宽几何容差来接受自交轮廓。

正式 R53 DLL 重新解码并转换完整 9156 JT、插入 CATIA：**2554 段原生圆柱组，
其中内孔 1723、外圆柱 831，非平面裁剪端口 67 段，保留穿孔壁面分片 15 段**。
较 R52 增加 14 段内孔；源参数 CSV 对比确认原 2540 段全部保留。
输出 1,801,446 个三角形、41,387,129 字节，完整审查文档为
`r53_production_catia/validation.CATProduct`。

五组纯源几何回归与三个实际片区回归通过。新增
`PuncturedCylinderFixtureRegression.cs` 检查 Shapes 461、477、1720、1724、1843，
共 43 段圆柱，其中 9 段基础壁面保留原分片；检查横向开口未被封住、孔壁半径、
原材质及输入坐标/索引不变。已有 Shape 302 和近相切端面回归分别保持 19、16 段。
另以正式 DLL 对匹配的源 JT 缓存进行识别及重建检查：5153（JT 8.0）接受
2300 段（1812 内孔、488 外圆柱），5156（JT 10.0）接受 2889 段
（2132 内孔、757 外圆柱）。这两个样本本轮未重新导出 CGR 或进行 CATIA 显示。
Shape 461 横向孔与 Shape 1724 台阶孔局部 CGR 已插入 CATIA 并对准孔口，
审查文档位于 `r53_joined_details/cross_bores.CATProduct` 和 `stepped_bore.CATProduct`。
本轮按用户要求上传源码并更新 README；未执行 CGR 独立回读。
仍有未通过完整边界、材质或接缝检查的候选保留原几何，接受数量不代表人工验收的孔轴总数。

以下 R52 及更早内容是对应版本的历史记录。

R52 修复曲面端面分片接近相切时的交点关联。解析圆与显示短弦的交点在 Float32
坐标下可能沿端面边偏移，按固定 0.001 mm 的最近圆点限制会漏掉有效圆柱。
仍优先使用原限制；额外关联仅允许在同一源端面边上、垂距不超过两倍实际坐标 ULP、
沿边位移不超过 0.01 mm 的圆周顶点。新端面三角形还须通过 0.001 mm 原分片平面
偏移检查及整个片区的接缝检查；壁面仍在原拟合圆柱上，不能任意合并邻近孔。
`TrimIntersectionFixtureRegression.cs` 使用 9156 的 Shapes 643、1037、1191：
三处先前遗漏的目标孔全部接受，片区 13 → 16 段；检查半径、材质及输入不变。
正式 R52 DLL 完整转换 9156 并插入 CATIA：2540 段圆柱，其中内孔 1709、外圆柱 831，
非平面裁剪端口 55 段。输出 1,804,497 个三角形、41,441,067 字节。
源参数 CSV 对比确认 R51 的 2537 段全部保留，新增目标为 Shapes 643、1037、1191 各一处内孔。
带相邻端面的 Shape 1191 局部模型包含 Shapes 977、1163、1182、1191，共 27 段圆柱，
已直接插入 CATIA 并对准目标孔口；完整模型位于 `r52_production_catia/validation.CATProduct`，
局部模型位于 `r52_joined_detail/trimmed_holes.CATProduct`。
源网格回归、真实目标片区回归均通过。另以正式 DLL 检查已有缓存样本的识别及网格重建：
5153（JT 8.0）接受 2291 段（内孔 1803、外圆柱 488）；5156（JT 10.0）接受 2876 段
（内孔 2119、外圆柱 757）。此处是源网格兼容检查，未对这两个样本重新导出并显示 CGR。
仍有端环连接不完整、相交圆柱边界及台阶端面拓扑等失败候选，继续保留原网格，未宣称全部重建。
未执行 CGR 独立回读，未上传源码。以下 R51 数量为上一版历史结果。

R51 处理完整圆柱被非平面端面裁剪的情况。端口不再必须位于垂直于轴线的同一平面，
但在轴线法平面中的投影仍须是完整单次圆周，全部壁面顶点半径和法向仍须通过原容差。
生产入口显式开启 `Options.IncludeTrimmedCylinderEnds`；默认关闭，旧版严格平面环分析继续可用。
优先采用经过整段壁面验证的平面圆环轴线；没有平面端环时，由壁面法向初估，
再通过两个投影圆心校正，避免两端采样点数不同导致三角面法向偏斜。

无连接端面的源开口保留逐段轴向高度；有端面时，新圆弧与原端面片区求交。
简单连接按相连端面片区整体三角化。圆弧越过曲面片区接缝时，
沿圆柱轮廓裁剪原端面三角片，将片区接缝与圆柱的交点加入孔壁边界，
保留端面的原分片平面、材质及外边界；另一端台阶环面仍按完整片区重建，不能错误裁除。
最终检查整个受影响端面片区及孔壁的边邻接，不能留下新裂缝或非流形连接。
原始三角片法向重算，输入坐标与索引不修改。不能证明单值端面高度、完整匹配或接缝正确时保留原几何。
非平面端口仅写圆柱属性，不伪装成平面圆边；平面端口保留原生圆边属性。

R51 正式 DLL 直接转换 T1E24MY-9156 并插入 CATIA：2537 段圆柱，
内孔 1706、外圆柱 831，较 R50 增加 50 段内孔和 2 段外圆柱。
52 段具有非平面裁剪端口；输出 1,803,085 个三角形、41,420,849 字节。
源参数 CSV 对比确认 R50 的 2485 个已接受候选全部仍存在。
Shape 302 的两个半径约 7 mm、端口轴向起伏约 0.09 mm 的目标孔连同端面恢复，
该 Shape 的圆柱由 10 段增加到 12 段，带周围端面的局部模型由 17 段增加到 19 段。
`RoundCylinderTrimRegression.cs` 覆盖内外圆柱、任意轴、双端裁剪、16/18 个不同采样点及接缝，
`TrimmedFixtureRegression.cs` 对已定位的实际 9156 片区检查两个目标孔、材质、起伏、半径和输入不变。
新增与原有源网格回归通过。本轮仍不上传源码、不执行 CGR 独立回读；
只运行源网格/参数检查、正式编译以及 CATIA 直接显示。失败候选仍可能造成遗漏，未宣称覆盖所有孔和轴。
最终接受数量以 `[CGR final cylinders]`、`[CGR final source trim]` 和实际输出的 `*.cylinders.csv` 为准；
此前的 guard 日志是初次连接尝试，后续裁剪成功会输出 `[CGR trim clip accepted]`。

以下为 R50 及更早版本的历史记录。

R50 针对壁面与端面分片的 JT：原算法只从有两侧相邻面的锐边提取圆环，
完整圆柱壁的开口端环只有一侧面，因而没有进入配对候选。生产入口开启
`Options.IncludeOpenCylinderEnds`，从壁面组件自身的开口边界提取完整圆环，
仍以两个源圆环、全部壁面顶点和法向共同证明圆柱及其实际长度。
不从单个孔口推测深度，也不补造端盖；已存在的端面及过渡连接仍原子重建。
若端面位于另一个 Shape，仅在整个源孔环每条边都存在唯一、坐标精确相同的平面端面邻接时连接，
同时更新该端面的圆孔边界并保留其颜色。邻近但不重合、多个重合端面或不完整匹配不会被任选合并。
最低 8 点、完整单次环绕、同轴同半径及拟合容差保持不变，
纵向缺片、非平面环、锥面、椭圆、六角形和重叠网格继续拒绝。
完全透明壁面与混色壁面分别记录原因。默认分析不启用开口端环，生产重建入口显式开启。

本轮按用户要求不上传源码、不执行 CGR 独立回读；执行源网格算法回归、正式编译及 CATIA 直接显示验证。
以下 R49 回读结果为此前历史记录，不代表 R50 执行过同样检查。

R50 正式入口直接转换 T1E24MY-9156 并插入 CATIA：2485 段圆柱，
其中内孔 1656、外圆柱 829；相对 R49 新增 779 段内孔和 24 段外圆柱。
803 段使用源开口端环证据，1235 个完整跨 Shape 端环精确匹配到平面端面。
输出 1,780,348 个三角形、41,023,680 字节。40 个连接检查失败的候选继续保留原网格。
新增源网格回归及原有圆孔、内外圆柱、倒角连接回归通过；本次不记录 CGR 回读结论。
局部仍有非平面裁剪边界：例如 Shape 302 的两段半径约 7 mm 孔壁，另一端环的
轴向坐标在 630～630.092 mm 之间变化，超出平面容差。它们不是缺失端盖的标准圆环，
不能按恒定端平面截断后宣称恢复原孔，当前保留原网格；与上述 40 个已配对但连接失败的候选分别计算。
本轮沿用 T1E24MY-9156 样本；用户截图对应的资源名称尚未确认。

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

每个资源输出一个 CGR，完整且连接验证通过的重建壁面替换原壁面三角形，圆形端边与相邻端面共用细分顶点。
Shape、材质和不相连片区分别处理，保留原始颜色与透明度。
常规重建圆弧理论弦高不超过 0.002 mm，至少 96、最多 2048 段；带横向开口的基础壁面
采用约束自适应细分，检查失败时保留原细分密度并记录原因。
实际精度也受源网格拟合和 float32 坐标影响。
拟合容差为 `max(0.01, radius * 0.001)`，沿用生产毫米坐标。

CGR type-13 面属性保存 CATIA 原生圆柱中心、单位轴方向和半径，圆边属性保存圆心与半径。
参数格式已对照 CATIA 自行导出的五个不同轴向圆柱验证。显示网格和测量属性同时生成，不附加 CATPart 或参考轴线。
参照 [Dassault CAA CATSurfacicRep 示例](https://www.maruf.ca/files/caadoc/CAAVisUseCases/CAAVisSampleCATSurfacicRep.htm)
中显示几何与 `CATVisMeasurableGP.SetCylinder` 的组合。

CGR 显示仍使用三角网格，不具有 CATPart 的建模历史或 BRep。不同 CATIA 捕捉工具是否使用
这些圆柱属性，需由用户实际操作验收；属性记录和成功打开文档不能证明所有工具都已识别轴线。
本算法从离散网格拟合，不能证明原 CAD 的设计意图；规则多边形也可能与低分段圆采样无法区分。
当前覆盖完整圆周、两端投影为完整源圆环的圆柱壁，包含通过连接检查的非平面裁剪端口；纯锥面、局部圆弧壁、纵向开放边界、
完全透明壁面或不满足约束、连接及预算检查的候选保留原几何；通过原壁面约束回退的混色圆柱保留逐面 RGBA。

## R49 历史验证（2026-10-10）

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

R54 源网格回归使用正式 DLL；三个真实片区测试的参数依次为 9156 源 JT、
与该 JT 匹配的 jtmesh 缓存、生产 bin 目录。以下测试不读取输出 CGR。

```powershell
rtk proxy csc /nologo /platform:x64 /out:PuncturedCylinderFixtureRegression.exe /r:production-bin/TxTools.dll Tests/JtCgr/PuncturedCylinderFixtureRegression.cs
rtk proxy .\PuncturedCylinderFixtureRegression.exe source-9156.jt matching.jtmesh production-bin
rtk proxy .\TrimmedFixtureRegression.exe source-9156.jt matching.jtmesh production-bin
rtk proxy .\TrimIntersectionFixtureRegression.exe source-9156.jt matching.jtmesh production-bin
rtk proxy .\RoundCylinderOpenRimRegression.exe production-bin
rtk proxy .\RoundCylinderTrimRegression.exe production-bin
rtk proxy .\RoundCylinderMaterialRegression.exe production-bin
rtk proxy .\RoundHoleDirectRegression.exe source.jt new-output-directory production-bin
```

最后一个命令直接调用正式 JT 解码器、写 CGR 并插入 CATIA；仅传三个参数，
不传第四个历史比较 CGR 参数，不运行下面的历史回读命令。源缓存指纹验证仍启用。
新增测试源文件可分别引用正式 `TxTools.dll` 编译。CATIA 集成工具还需链接本机
INFITF 与 ProductStructureTypeLib interop 类型库。

`RoundCylinderOpenRimRegression.cs` 覆盖完整内外圆柱壁的双开口、任意轴向，
以及一端开口、一端连接环形端面、跨 Shape 端面连接和重复端面歧义的情况；
检查尺寸、RGBA、原边界和输入不变，拒绝不完整/非圆柱网格。
R50–R54 未运行下面列出的 CGR 回读工具；其源码保留用于以后显式启用的验证。

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

旧 CATPart 参考工具仅保留为独立实验，不参与 R51 生产模式。客户 JT、输出 CGR/CATProduct 和截图不进入 Git。
