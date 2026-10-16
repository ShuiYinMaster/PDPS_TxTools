# PS 网格到 CGR 管线回归

`Program.cs` 生成合成网格并调用生产 `PsCgrPipeline`，检查圆柱拟合、颜色、方向、源快照不变、椭圆回退和几何身份隔离。`FrameworkRegression.csproj` 直接引用完整插件输出，`PsCgrPipeline.csproj` 链接独立算法源码。

```powershell
rtk proxy dotnet run --project Tests/PsCgrPipeline/PsCgrPipeline.csproj -- artifacts/ps-cgr-regression
rtk proxy MSBuild.exe Tests/PsCgrPipeline/FrameworkRegression.csproj /p:PluginDir=插件输出目录
```

每次执行使用新的输出目录。`CylinderSafetyRegression.csproj` 通过 `RegressionCase` 选择 `Tests/JtCgr/` 中的算法回归类。

生产管线只接收脱离 SDK 的三角网格快照；SDK 采集、加载级别和显示状态恢复仍受宿主 UI 线程约束。圆柱拟合来自显示网格，不等于恢复完整 CAD 特征历史。文件检查不能替代 PS 场景采集及目标 CATIA 的显示、测量验收。
