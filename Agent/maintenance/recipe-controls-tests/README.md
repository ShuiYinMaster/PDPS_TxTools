# 配方控件回归

本套件编译真实配方存储、Markdown 解析、保存工具、参数生成、偏好与共享执行锁。`HostStubs.cs` 仅替换 Process Simulate 和模型边界，不执行工程代码。文件写入限于工作区 `artifacts/recipe-review`。

安装 .NET 8 SDK 时可运行：

```powershell
dotnet run --project Agent/maintenance/recipe-controls-tests/RecipeControlsRegression.csproj --configuration Release
```

仅安装 .NET 10 的对应离线引用包时：

```powershell
dotnet msbuild Agent/maintenance/recipe-controls-tests/RecipeControlsRegression.csproj -restore -p:TargetFramework=net10.0 -p:Configuration=Release
dotnet artifacts/recipe-review/RecipeControlsRegression.dll
```

套件覆盖中文名称与 API 名分离、按钮覆盖、选项校验、旧格式兼容、导入导出、更新记录保留、原始默认配方升级及用户修改保护，并运行既有分享回归。它不能替代插件的 .NET Framework 4.8 构建或 Process Simulate 实机验证。

界面验证与模拟预览：

```powershell
node Agent/maintenance/RecipeSidebarRegression.js
node Agent/maintenance/QuickRecipeControlsRegression.js
node Agent/maintenance/QuickMarkdownRegression.js
node Agent/maintenance/RecipeMarkdownRegression.js
node Agent/maintenance/BuildRecipeSidebarPreview.js
```

打开生成的 `Agent/artifacts/recipe-preview/controls.html` 可查看两个真实界面；选取和执行使用示例数据，不连接实际工程。

## VS 编译后的配方资源校验

`DefaultRecipes.Build.targets` 明确列出默认配方和版本迁移基准，并在 .NET Framework 插件编译后运行 `VerifyRecipeResources.ps1`，核对 DLL 中每份定义的名称和内容。回归测试也导入同一份资源声明，避免测试与实际插件使用不同的资源清单。

如果 VS 工程在新增迁移文件之前就已打开，请重新加载项目后再“全部重新生成”。缺少或过期的资源会使构建失败并提示重新加载。新增默认配方或迁移版本时，应同时补充 `DefaultRecipes.Build.targets` 的显式资源清单。

也可单独检查已安装的插件：

```powershell
powershell -NoProfile -File Agent/maintenance/VerifyRecipeResources.ps1 -AssemblyPath 'G:\Program Files\Tecnomatix_2402\eMPower\DotNetCommands\TxTools\TxTools.dll'
```

色盘和对象查询回归还覆盖：颜色格式校验、类型与名称组合查找、当前选择过滤、研究切换、结果去重、空条件拦截，以及条件改变后两个界面清除旧对象绑定。

动态类别回归覆盖真实类去重、未知类别自动出现、基类与子类分开筛选、场景类别删除、参数类型限制和研究切换。界面测试同时验证列表/收起侧栏不扫描场景。

启动响应测试提取真实 DshPetController，在 .NET Framework WinForms 消息循环中模拟耗时 650 ms 的进程启动，验证界面计时器仍在运行、重复启动去重、启动中隐藏/退出清理和失败回主线程。生成测试源码：

```powershell
node Agent/maintenance/StartupResponsivenessRegression.js
```

生成的 artifacts/startup-review/StartupResponsivenessRegression.cs 使用 VS Roslyn csc 编译，引用 System.Windows.Forms.dll、System.Drawing.dll 和构建输出中的 Newtonsoft.Json.dll；把 Newtonsoft.Json.dll 放在测试 exe 同目录后运行。测试仅创建不可见调度控件和模拟 helper，不启动真实桌面宠物或修改工程。
`dotnet run --project Agent/maintenance/startup-tests/VerifyCompiledStartup.csproj -- artifacts/dynamic-object-build/TxTools.dll` 可直接检查实际 DLL 的注册和后台启动调用，不加载 SDK、不执行插件。


2026-10-07 构建修正：`DefaultRecipes.Build.targets` 自动选择 64 位 Windows PowerShell；32 位 MSBuild 使用 Sysnative 路径，避免加载 x64 DLL 时的 `BadImageFormatException`。本次回归与完整构建结果见 [总更新日志](../../../README.md#十三更新日志)。
