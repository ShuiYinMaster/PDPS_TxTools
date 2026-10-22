# JT / CGR 回归源码

本目录保留可复用的算法与进程隔离回归。几何用例在程序内生成圆管、圆柱、端口与材质网格；外部转换用例通过命令行接收输入路径，不包含工程模型。

- `RoundCylinder*Regression.cs`、`RoundHoleCgrMeshRegression.cs`、`RoundHoleCounterboreRegression.cs`：圆柱拟合、拓扑、端口、材质及输入不变性。
- `AdaptiveCylinderDensityRegression.cs`：显示细分、弦高、闭合拓扑及参数保持。
- `BatchSafetyRegression.cs`：资源展开、完成顺序、子进程超时与清理。
- `PsDispatchRegression.cs`：SDK 请求的 UI 线程封送和上下文验证。
- `RgbaCompatibilityRegression.cs`、`FeatureTopologyRegression.cs`：颜色、透明度及面域关系。
- `FeatureTestDecoder.cs`：供独立编码器回归使用的读取辅助。

生产代码通过 `TxTools.csproj` 构建。Framework 算法回归可使用 `Tests/PsCgrPipeline/CylinderSafetyRegression.csproj`，将 `PluginDir` 指向完整插件输出目录，并以 `RegressionCase` 指定本目录内相应测试类名。该工程只适用于不依赖 CATIA COM 的测试类。

使用新的隔离输出目录执行转换测试。文件编码检查不能替代目标 Process Simulate / CATIA 的加载、坐标、显示、拾取与测量验收。实现与依赖见 [ExportByColor](../../ExportByColor/README.md)。
