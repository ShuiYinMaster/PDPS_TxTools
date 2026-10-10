# R47 单个 CGR 内的圆柱孔壁重建

生产试用模式：**读取 JT → 圆孔重建 CGR → CATIA（试用）**。
版本标识：`CGR-20261010-R47-cgr-canonical-holes`。
JT 路径和资源放置矩阵在 PS 线程获取；解码、孔识别、端面重建、CGR 编码
在有界工作线程执行。CATIA 装配插入仍在 STA 线程完成。
实现为 .NET Framework 4.8 C#，不需要 Python、CAA SDK 或运行时 NuGet 包。

## 输出内容

每个资源只插入一个 CGR。重建孔壁替换原孔壁三角形，并重新三角化相连的
平面端面区域；圆孔两端与孔壁共用细分边界。原始颜色和透明度保持一致。
不同 Shape、材质和不相连端面分别处理，不通过复制共面面片叠加颜色。

CGR type-13 面属性写入 CATIA 原生圆柱中心、单位轴方向和半径参数，
圆边属性写入圆心、半径。参数格式以 CATIA 自己导出的五个不同轴向圆柱
CGR 为对照验证。测量属性与显示网格同时生成，不附加 CATPart 或参考轴线。
这种 CGR 仍以显示三角网格绘制，不是具有建模历史的 CATPart BRep。
CATIA 各交互捕捉工具是否使用这些属性，由实机操作验收；仅有记录和成功
打开文档不能证明所有工具已识别圆柱轴。

参照 [Dassault CAA CATSurfacicRep 示例](https://www.maruf.ca/files/caadoc/CAAVisUseCases/CAAVisSampleCATSurfacicRep.htm)
中曲面显示几何与持久化 `CATVisMeasurableGP.SetCylinder` 的组合。

## 算法与边界校验

1. 按 JT Shape 建立分析拓扑，只在分析数据中合并完全相同的坐标。
2. 识别完整圆环并拟合圆心、半径和法向，要求至少 10 个采样点、单次环绕，
   检查平面及径向残差。排除椭圆、锥孔、凸台、开放边和非流形连接。
3. 两端圆环必须属于同一个连续向内圆柱壁，重新校验公共轴、半径及深度。
   单孔口候选不会被赋予孔深，也不会替换。
4. 合并两端接近的采样角，相邻圆弧的理论弦高不超过 0.002 mm，至少 96 段，
   最多 2048 段。实际精度还受源网格拟合残差和 float32 坐标精度影响。
5. 按相连、同色、同向且共面的区域构建端面外轮廓与全部内环；用随源码
   编译的 MIT EarCut 重建带孔多边形。保留未识别孔的原始内环。
6. 还原三角化省略的共线边界顶点，检查有向面积、非退化三角形、内部边
   二次邻接和每条边界一次邻接。孔壁与两个端面作为一个整体接受或回退。

拟合容差为 `max(0.01, radius * 0.001)`，坐标单位沿用生产毫米网格。
拟合参数不是原 CAD 尺寸证明，规则多边形也可能与圆采样难以区分。
颜色不同、边界不完整或端面三角化未通过校验时，保留原始孔壁。
`*.cgr.holes.csv` 保存全部拟合候选；返回值和日志的替换数量仅统计实际
进入 CGR 的圆柱孔壁。数量不等于人工标注的真实孔数。

## 2026-10-10 生产 DLL 验证

| 样本 | JT 版本 | 拟合候选 | 双孔口候选 | 实际替换 | CGR 圆柱属性 | 圆边属性 |
|---|---|---:|---:|---:|---:|---:|
| T1E24MY-9156 | 10.6 | 1118 | 710 | 642 | 642 | 1284 |
| T13J-5156 | 10.0 | 1094 | 689 | 548 | 548 | 1096 |
| T13J-5153 | 8.0 | 830 | 478 | 377 | 377 | 754 |

三份完整模型独立回读的有向 XYZ + RGBA 多重集合差异均为零，分别为
946,631、1,183,862、724,591 个三角形。T1E24MY 的生产 JT 解码入口输出
与独立回读文件 SHA256 相同，28,393,591 字节，已作为单 CGR 插入 CATIA，
整模型和孔口局部均保持打开。710 个双孔口候选中有 68 个保留原始几何。
局部实机发现原来的 12 点门槛漏掉阶梯孔的 10 点内层小孔；将门槛调整为
10 点后同时替换两级孔壁及共用端面，正面显示的十边形轮廓变为细分圆形。
该问题另有合成阶梯孔回归，检查内层孔附近的轴向射线不会再被旧端面遮挡。

回归还覆盖圆孔的旋转/大坐标平移、透明度、源数据不变、独立 Shape 隔离，
以及椭圆、外圆柱、锥孔、六角孔、破口、非平面环等拒绝案例。
线面编码的 105 个压缩数据案例、6 个边链案例、3 个面索引置换，以及
独立重合面、细长三角形和 alpha 0/128/255 校验通过。

## 可复现测试

`RoundHoleCgrMeshRegression.cs` 与 `DirectHoleCgrRegression.cs` 引用生产
TxTools.dll 编译；后者还编译 `FeatureTestDecoder.cs`，该独立回读器不调用
CGR 编码器解码。`RoundHoleDirectRegression.cs` 编译时链接 INFITF 和
ProductStructureTypeLib interop，使用 STA，输出单 CGR 并留在 CATIA 中。
`RoundHoleCounterboreRegression.cs` 覆盖 10 点阶梯孔与共用环形端面。

```powershell
rtk proxy .\RoundHoleRegression.exe production-bin
rtk proxy .\RoundHoleCgrMeshRegression.exe production-bin
rtk proxy .\RoundHoleCounterboreRegression.exe production-bin
rtk proxy .\DirectHoleCgrRegression.exe source.jt matching.jtmesh new-output-directory analyze production-bin
rtk proxy .\DirectHoleCgrRegression.exe source.jt matching.jtmesh new-output-directory all production-bin
rtk proxy .\RoundHoleDirectRegression.exe source.jt new-output-directory production-bin verified.cgr
rtk proxy dotnet run --project Tests/CgrLineFace/CgrLineFace.csproj -c Release -- --self-test
```

`RoundHoleCatiaWriter.cs` 和旧 CATPart 参考测试保留作独立实验工具，不参与
R47 生产试用模式。客户 JT、输出 CGR/CATProduct 和截图不进入 Git。
