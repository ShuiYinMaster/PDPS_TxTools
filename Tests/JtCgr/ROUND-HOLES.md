# R46 圆孔参数重建试用

新增模式：**JT → CGR + 圆孔参考 → CATIA（试用）**。JT 路径与放置矩阵仍在 PS 上快照，JT 解码、线面 CGR 编码和圆孔拟合在有界工作线程上执行。CATIA 解析几何构造及装配插入由原 STA 协调线程串行执行。

算法实现位于 `ExportByColor/RoundHoleReconstruction.cs`，纯 C#、无需 Python 或外部几何库：

1. 按 JT Shape 分组，仅在分析数据中合并完全相同的位置。原 CGR 顶点身份、颜色、透明度、三角形和绕序不改变。
2. 建立三角形边邻接，排除非流形连接与开放边。在法向夹角至少 45° 的连接上提取闭合边界，要求至少 12 个采样点、单次环绕和充分角度覆盖。
3. 用环法向确定孔口平面，将坐标居中和归一化，再解三元最小二乘圆方程。校验每点的平面和径向残差。
4. 要求孔口两侧具有平面与朝内的柱壁法向，排除外圆柱/凸台。对同一连续内壁的全部顶点校验到孔轴的径向距离，排除椭圆及锥孔。
5. 仅将同一内壁分量中的同轴、同半径孔口配对，避免跨空气把两个独立孔配成一个孔。重新校验公共孔轴、平均半径和两端平面。

默认容差 `max(0.01, radius × 0.001)`，单位与输入网格一致；当前生产 JT→CATIA 路线使用毫米。误差字段是**源网格顶点的拟合残差**，不代表原 CAD 尺寸误差，也不计算多边形面内部到解析曲面的弦高。

导出会保存 `*.cgr.holes.csv`，包含 Shape、圆心、轴向、半径/直径、孔口间距、残差和采样数量。仅一个孔口的候选 `depth=0` 表示**缺少孔深证据**，不是零深孔。配对也不直接证明通孔；盲孔底面可能有同样的圆形边界。倒角孔、粗网格、断开的面片或没有完整孔口的内壁可能漏检。规则多边形与其对应圆的采样点可能无法仅凭网格区分，因此结果仍是候选参考特征。

`RoundHoleCatiaWriter.cs` 在独立 CATPart 中构造完整解析圆和轴线；有双孔口证据时还可通过 API 生成解析圆柱参考面。生产试用模式默认只附加圆与轴线，隐藏辅助点，不附加圆柱面以免覆盖原模型。它不会产生实体切孔或恢复原建模历史，也不会向现有 CGR 伪写解析曲面记录。

CATIA SPA 会校验圆半径、圆心、整圆周长、轴线长度；启用圆柱面时还校验侧面积。SPA 面积返回 m²，需要换算为 mm²。参考 CATPart 与原 CGR 在同一个资源子装配下分别插入，便于单独隐藏参考或选择解析圆测径。无候选时保留原 CGR 并明确记录日志。该试用模式要求直接 JT 解码成功，不切换至 PS 原生几何采集。

2026-10-10 正式 DLL 样本结果：

| 样本 | 候选 | 同内壁双孔口 | 最大顶点拟合残差/mm |
|---|---:|---:|---:|
| T13J-5156 | 702 | 472 | 0.008142 |
| T13J-9261+8251 | 780 | 449 | 0.000615 |
| T13J-9251+8151 | 650 | 423 | 0.000638 |
| T13J-5254+8452 | 1017 | 631 | 0.008135 |
| T13J-5253 | 719 | 413 | 0.002043 |
| T13J-5154+8352 | 735 | 463 | 0.008135 |
| T13J-5153 | 565 | 337 | 0.002036 |
| T1E24MY-9156 | 820 | 518 | 以完整导出 CSV 为准 |

七个 T13J 样本共 5168 个候选、3188 个有双孔口支持。这不是人工标注的真实孔数，未据此宣称召回率/准确率。

回归检查：

- 已知圆孔及旋转、平移到大坐标后的孔，恢复半径与孔口间距，输入坐标/索引不变。
- 外圆柱、椭圆、六角孔、破口、锥孔、重合非流形网格、非平面环、星形多次环绕等反例被拒绝；独立 Shape 隔离；单孔口不赋予孔深。
- T1E24MY 的全部 820 个候选重建为 1338 个解析圆及 820 根轴线，并与原 CGR 插入测试装配。原 CGR 与 R45 线面输出 SHA256 相同。
- 选取 12 个双孔口候选生成 24 个整圆及 12 个圆柱参考面，通过 SPA 圆径、圆心、周长、轴长和侧面积验证。

测试文件：`RoundHoleRegression.cs`、`RoundHoleSampleBatch.cs`、`RoundHoleCatiaRegression.cs`、`RoundHoleDirectRegression.cs`。测试可引用生产 DLL 用 .NET Framework C# 编译器运行；CATIA 测试需 STA、相应 CATIA 几何构造许可及已运行的 CATIA。样本文件与生成的客户几何不进入 Git。

```powershell
rtk .\RoundHoleRegression.exe production-bin
rtk .\RoundHoleRegression.exe source.jt matching.jtmesh result.csv production-bin
rtk .\RoundHoleSampleBatch.exe fixture-parent mesh-batch-directory new-output-directory production-bin
rtk .\RoundHoleDirectRegression.exe source.jt new-output-directory production-bin baseline-lineface.cgr
```

解析参考几何数量较多时，CATIA 构造时间会明显增加；它需要串行 COM 调用，不能按 JT 工作线程数并行操作同一个 CATIA 会话。
