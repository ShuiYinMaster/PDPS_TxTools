# JT → CGR 并行转码验证

生产入口为 `ExportByColorService.RunJtToCgrAsync`。

资源名称、JT 路径、资源整体放置矩阵和装配层级由 PS 同步上下文一次读取。纯 C# 工作任务使用独立解码进程，将 JT 解析、放置变换和 CGR 写入并行执行。工作任务没有 PS/COM 对象；CATIA 插入由单个 STA 协调线程按选择顺序完成。待处理任务数受限，不会一次启动全部资源。单个资源失败后继续处理其余资源。

64 位进程默认双路，32 位进程单路；环境变量 `TXTOOLS_JT_WORKERS=1` 可回到串行。64 位进程可设置 1–4 路，实际值不超过处理器数量；提高并发会增加内存和磁盘压力。不支持的 JT 格式仍由协调线程发起 PS 原生兼容采集，再交给工作任务编码；兼容路线使用当前 PS 姿态，直接路线使用 JT 文件姿态加资源整体放置。

本次同时提交 JT 可见面修复：过滤 `faceGrp < 0` 辅助拓扑，只选择最高细节 LOD，并按源法向校正绕序。直接 CGR 写入保留 JT 绕序，不合成背面，避免辅助面覆盖真实面颜色。

## 实测结果（2026-10-10）

使用 KR210 和 FFM130 各两个实例，共四个资源，并分别施加不同平移：

| 转码方式 | 总耗时 | 实际同时工作数 |
|---|---:|---:|
| 串行 | 21,017 ms | 1 |
| 双路并行 | 13,987 ms | 2 |

本机 20 逻辑处理器，双路约快 1.50 倍。仅统计解码、变换和 CGR 写入，不包含 PS 元数据读取或 CATIA 插入；其他机器的收益取决于 CPU、文件位置和内存。

四组串行/并行 CGR SHA-256 完全一致，覆盖几何、颜色、绕序、放置和容器输出。一组缺失 JT 加两组有效资源的测试只报告一个失败，后续资源仍完成。没有使用 Python 或 PS 几何采集。

四个并行转码 CGR 已在 CATIA 新建 Product 中由单个 STA 线程逐个插入，组件数为 4，并成功保存 CATProduct。此测试不经过 PS；PS 中的实际多选入口仍需现场验收。

## C# 回归入口

`ParallelRegression.cs` 是 .NET Framework 4.8 控制台程序，引用正式 `TxTools.dll` 编译。运行参数依次为机器人 JT 路径、焊枪 JT 路径、全新测试输出目录、正式 bin 路径。要求 bin 中存在 `JtDirectCs/TxTools.JtDecoder.exe` 及其依赖。测试目录必须全新，以免覆盖上次产物。

```powershell
rtk proxy csc /nologo /platform:x64 /r:"E:\ProcessSimulatePlugin\Process Simulate\bin\TxTools.dll" /out:ParallelRegression.exe ParallelRegression.cs
rtk proxy .\ParallelRegression.exe "robot.jt" "gun.jt" "D:\JT_test_new" "E:\ProcessSimulatePlugin\Process Simulate\bin"
```

实际部署已编译至用户指定的正式 bin。PS 插件选择多个资源的现场操作与进度显示仍需重新加载 DLL 后验收；上述性能数字来自独立 JT→CGR 实测。
