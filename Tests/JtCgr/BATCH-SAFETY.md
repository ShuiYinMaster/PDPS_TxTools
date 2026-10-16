# R56：原型子项与完整转码隔离（2026-10-12）

生产标识：`CGR-20261012-R56-prototype-isolated-batch`。

## 审查结论与修复

旧实现已捕获普通转换异常，但仍存在影响整批的等待路径：

1. 所有 `ITxStorable` 被当作叶子，EquipmentPrototype 会提前停止展开。
2. FIFO 等待首个资源，已完成的兄弟任务不能发布，工作槽也不能及时补入新资源。
3. 解码超时后的 `WaitForExit()`、正常退出时为排空日志调用的 `WaitForExit()` 没有上限。
4. 圆柱重建和 CGR 编码运行在宿主进程，无法独立结束其中卡住的资源。
5. 从 Task 取结果位于单项异常边界之外，整批异常处理还会同步等待剩余任务。
6. 不重建的直接 JT 模式仍含 PS 几何回退，可能再次等待主线程。

修复后：原型按类型及普通组件名称识别为容器；缺少独立 JT 的资源继续展开。
只在有独立 JT 的实际资源处停止，按 PS 对象身份去重，保留同路径的不同实例。
从每个实际子项读取绝对放置矩阵与目录树，不重复叠加父级矩阵。
SDK 元数据通过主线程 Post 获取，协调线程等待上限 120 秒，过期且尚未执行的请求不再执行。
命令在 PS UI 线程创建 WinForms 上下文以处理当前上下文为空的情况；直接 JT 元数据路径拒绝
普通 SynchronizationContext，防止其 Post 将 SDK 访问调度到线程池。

每资源启动 `TxTools.JtCgrWorker.exe`，仅传路径、矩阵、方案和重建开关。
先加入私有 Windows Job，再发出启动标记，因此解码子进程归属同一个资源任务。
工作进程崩溃或超时只清理这个 Job；不按进程名称终止任何宿主、CATIA 或其他资源。
默认完整资源超时 900 秒（环境变量可设 30–3600 秒），内层解码保持 300 秒上限；
退出/管道清理每阶段额外最多 5 秒，无参数的进程等待已移除。
异步同时接收 stdout/stderr，回调不抛日志写入异常，不在 writer 关闭后继续写入。
日志统一 UTF-8。

完成队列使用 WhenAny，故障、取消和成功任务都进入单项处理。
异常协调器不再同步排空后台任务；剩余任务在自己的期限内输出恢复文件，异常被观察。
工作进程先写 `.pending.cgr`，干净退出并给出成功回执后父进程才发布 `.cgr` 和源诊断 CSV。
不解析生成 CGR；失败/超时产物不会插入 CATIA。不再回退 PS 几何采集。

## 验证

`BatchSafetyRegression.cs` 与生产的展开、完成队列、进程管理代码一起编译：

- 原型即使存在误导性的 backing 标志仍展开；嵌套、循环、父子重复选择、独立实例、无文件叶子通过。
- 单个 SDK 枚举异常不丢弃健康兄弟资源。
- 首项尚未完成时，后项 Faulted/Cancelled 可以先被处理。
- 故意退出码 13、永久等待及解码子进程、两个健康工作任务同时运行：健康 2，失败 1，超时 1；约 1.5 秒结束，后代进程全部退出。
- stdout/stderr 同时各输出 10000 行，无管道死锁；UTF-8 中文日志检查通过。
- 父进程已退出但后代仍持有日志管道：约 5.1 秒后超时并清理后代，未无限等待 EOF。

`PsDispatchRegression.cs` 通过反射调用正式 DLL 的元数据封送方法，使用独立模拟 UI 线程：
调用确实在提供的 UI 线程执行，单项异常后下一项成功；普通及只重写 Send 的上下文均被拒绝。
该测试不访问 PS 场景。

`IsolatedConversionRegression.cs` 调用正式 bin 的新工作进程，最多并发 2 个资源：

| 输入 | 结果 | 重建圆柱 |
|---|---|---|
| FFM170-X-0417-6-CLOSE-A3-2024.11.14.jt（JT 9.5） | 成功 | 1024 内孔 + 158 外圆柱 = 1182 |
| 故意截断的 malformed.jt | 约 0.36 秒后失败，未发布 CGR | — |
| FFM130-X-0759.jt（JT 10.6） | 在失败后继续启动，约 56.8 秒完成 | 829 内孔 + 51 外圆柱 = 880 |

整批约 153.5 秒结束，2 成功、1 失败；较慢首项未阻止后续健康资源先完成。
测试输出目录：`D:\Users\hp\CGR_work\r56-real-isolated`。
最终版（统一 UTF-8 日志）增加同一 JT 10.6 的不重建 Compact 模式：四项批次 3 成功、1 失败，
约 155.7 秒结束；坏文件约 0.23 秒失败，JT 10.6 重建约 57.9 秒完成，Compact 约 63.1 秒时完成。
最终输出目录：`D:\Users\hp\CGR_work\r56-real-isolated-final`。两份重建计数均与上一批一致。
仅检查输出文件存在和转换回执/源参数 CSV；未读取生成 CGR 内容。
`SourceCylinderParameterRegression.cs` 比较 R55 与最终 R56 的 FFM170 源诊断 CSV，
1182 段的 Shape、内外分类、中心、轴向、半径、深度逐字段完全一致。
最终核对没有残留 JT 转码/解码工作进程。

## 部署与范围

正式源码为 `E:\ProcessSimulatePlugin\Process Simulate\TxTools`；
Release DLL 为 `E:\ProcessSimulatePlugin\Process Simulate\bin\TxTools.dll`。
主项目构建同步编译/部署 `JtDirectCs\TxTools.JtCgrWorker.exe` 和 `TxTools.JtDecoder.exe`。
新工作进程通过相邻上一级 bin 加载同版 TxTools.dll，不能只复制主 DLL 而漏部署工作进程。
全部为 .NET Framework 4.8 / C#，无 Python 环境要求。

PS 场景中的 EquipmentPrototype 路径/子项属于 SDK 实机验收范围，本轮验证了通用遍历规则及实际文件转换，未声称执行了 PS 场景导出。
CATIA COM 插入继续单 STA，普通 COM 异常按资源记录失败；本轮未新增 CATIA 插入测试。
CATIA 自身阻塞、模态对话框或不返回的 COM 调用不属于转码工作进程超时覆盖范围；不会通过终止 CATIA 来解决。
本轮未上传 GitHub。
