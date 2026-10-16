# R57：PS 三角网格 → 内外圆柱重建与线面压缩 → CGR → CATIA

日期：2026-10-16。主版本 `CGR-20261016-R57-ps-cylinder-pipeline`，场景字段版本仍为 R7，CFV2 容器版本仍为 R4。

## 用户入口

插件下拉列表移除了 PS 和 JT 两处“逐平面兼容”。原 PS 线面压缩入口现在为：

**PS 三角面 → CGR → CATIA（内外圆柱重建与压缩）**

该选项采集 PS 已加载资源的三角网格，调用与 JT 重建路线相同的内外圆柱拟合、安全重建、显示细分和连续面域压缩算法，然后由原 CATIA STA 协调线程插入 CGR。原方案、直出 3DXML 和已有 JT 入口保留。内部 LineFacePlanar 枚举/编码器供旧接口及回归使用，插件列表不再提供该选项。

列表项改为携带 Format、Backend 和 ReconstructCylinders 的对象；运行时读取选中对象，不再按选项数字位置判断 PS/JT，避免移除选项后错走读取 JT 的路径。

## 接入点和处理规则

- `ExportByColorForm` 将 PS 新选项映射为 CGR、LineFace、重建开启；JT 两条线面路线仍各自携带是否重建的值。
- `ExportByColorService.RunAsync` 新增带布尔参数的重载，旧签名保留。仅 CGR + LineFace 允许启用 PS 重建。PS SDK 采集和原点/世界坐标变换仍在 PS 同步上下文执行。
- 新 `PsCgrPipeline` 只接收脱离 SDK 的 ColorGroup 三角形快照，后台识别和编码；每个设备输出一个 CGR。
- 在重建路线中，同一正值 Surface 的不同颜色组共享几何身份，保留每个三角形的颜色。不同 PS 几何身份保持隔离；缺少身份的组独立处理。旧路线继续采用原先每个颜色组独立的规则。
- 先过滤精确零面积面、按源几何焊接相同坐标顶点、整理连通分量绕序。闭合分量按有符号体积选择外向；原始快照数组不修改。
- 拟合前不添加合成反面，避免一条圆柱壁被重复统计。本重建路线保留整理后的面，不人为添加反向副本；开放薄片的背面显示仍需实际 PS 模型验收。
- 开启内外圆柱、过渡端口、开放端口及裁剪端口识别；调用既有 `RoundHoleCgrMesh.Replace`。孔口连接、材质、穿孔边界、裁剪和细分保护继续生效。
- 接受的圆柱进入 `BuildFileWithCylinders`，写入原生圆柱/有效圆边属性，再执行连续面域、三角包和边链压缩。普通圆柱显示弦高目标仍为 0.02 mm、至少 24 段；裁剪/相交等区域沿用既有更严格保护。
- 无可接受圆柱时直接编码整理后的原网格；不能安全替换的其他区域也保留，不用理想圆柱覆盖。输出旁的 `.cylinders.log` 保存保护诊断，普通日志给出候选及内/外圆柱计数。

重建依据是显示网格拟合，不是恢复原始 CAD 特征历史。半径/轴线参数、细分后的显示网格和 CATIA BRep 是不同层次；不能据此宣称恢复全部精确 CAD 曲面。

## 验证

独立 .NET 8 回归和**直接引用完整 Release TxTools.dll 的 .NET Framework 4.8 回归**均通过：

| 快照案例 | 输入三角面 | 输出三角面 | 接受内/外圆柱 |
| --- | ---: | ---: | ---: |
| 单个三色圆管 | 256 | 640 | 1 / 1 |
| 同圆管，部分三角形反向 | 256 | 640 | 1 / 1 |
| 椭圆管，无法作为圆柱拟合 | 256 | 256 | 0 / 0 |
| 两个坐标重合但身份独立的圆管 | 512 | 1280 | 2 / 2 |
| 圆管与无关开放平面片 | 258 | 642 | 1 / 1 |

三角数量增加来自圆柱显示细分，与面域/索引压缩同时存在，不表示对几何做删面简化。回归逐项检查源快照未改动、内外方向、颜色、椭圆回退的全部 XYZ 三角形、开放片未丢失/未复制，以及输出包含圆柱和圆边属性。错误后端组合在写入前被拒绝。

两批各 5 个 CGR 均通过 CATIA COM 独立 Product 加载、体积、保存检查，共 10 个 Product；直接来自 Release DLL 的结果如下：

| 案例 | Analyze.Volume / mm³ |
| --- | ---: |
| 圆管 / 异常绕序 / 圆管加平面片 | 18841.70459755046 |
| 两个独立圆管 | 37683.40919510092 |
| 椭圆回退 | 22474.405147866502 |

上述为离散网格测量值，不等于解析圆管精确体积。COM 各批恢复原活动文档、告警状态及文档数量。圆管捕获图已查看，内孔和外壁显示完整，端面、内壁和外壁颜色保留。

.NET 8 与 .NET Framework 的五个文件并非逐字节相同，因此两批分别执行了 COM 验收，没有以一个运行时的结果替代另一个。两个运行时的各项几何/颜色/方向检查分别通过。

既有 CGR 编码器的 **38 组默认输出与 R7 基线逐字节一致**，24 个容器重建、4 种分段布局及 **58 项 Python 字段测试**继续通过。

## 完整构建与部署

完整 Release 主插件、MCP 桥、JT 解码器及 CGR 工作进程构建成功。构建读取本机 `E:\ProcessSimulatePlugin\Process Simulate\2402dll` SDK、现有 NuGet 包及已生成的 89 个 CATIA COM 包装引用；源文件来自当前工作区。临时构建工程和详细日志保存在证据目录。缺失的 JT 解码依赖从本机同版 Dependencies 复制到工作区，没有引入新的网络依赖。最终构建存在既有 Vendor 警告，0 错误。

已将通过回归的主 DLL/PDB 部署到 `E:\ProcessSimulatePlugin\Process Simulate\bin`，部署时未检测到 Process Simulate 进程；下次启动加载。覆盖前备份及前后 SHA-256 已保存，主 DLL 部署哈希为：

```text
bd2d30ef74a236b7163d4432510ab5258a572b277dad95397526b7457226c817
```

本轮验证了实际 Release DLL 的纯网格处理及 CATIA COM 读取，**未在运行中的 PS 项目里执行 SDK 几何采集或点击新菜单**。实际资源的采集、坐标、拾取与圆柱测量交互仍需宿主验收。

证据目录：`analysis_cgr/ps_pipeline_20261016/`。

```text
regression/variants.json                    .NET 8 的五组网格/属性检查
framework_regression/variants.json          Release DLL 的五组检查
com_acceptance/com_results.json             .NET 8 输出的 COM 结果
framework_com_acceptance/com_results.json   Release DLL 输出的 COM 结果
encoder_verification/verification.json      38 组默认输出兼容性
release/TxTools.dll
full_build_final.log
deployment.json                            目标路径、备份、部署哈希
deployment_backup/TxTools.dll
deployment_backup/TxTools.pdb
summary.json
```

重跑时使用新目录：

```powershell
rtk dotnet run --project Tests/PsCgrPipeline/PsCgrPipeline.csproj -- ../analysis_cgr/ps_pipeline_new
rtk proxy MSBuild.exe Tests/PsCgrPipeline/FrameworkRegression.csproj /p:PluginDir=完整插件目录 /p:OutDir=新的测试程序目录\
rtk proxy PsCgrPipelineRegression.exe 新的输出目录
```
