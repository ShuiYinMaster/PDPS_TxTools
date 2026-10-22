# 窗口裁切回归

运行前从当前生产源码提取布局构建方法，避免用手写简化排布掩盖实际的布局问题：

```powershell
rtk proxy python Tests/LayoutRegression/generate.py
rtk proxy MSBuild.exe Tests/LayoutRegression/LayoutRegression.csproj /t:Build /p:WarningLevel=0
rtk proxy Tests/LayoutRegression/bin/LayoutRegression.exe
rtk proxy Tests/LayoutRegression/bin/LayoutRegression.exe --no-logs
rtk proxy python Tests/LayoutRegression/gallery.py
```

从仓库根目录运行。生成源码位于 `obj/Fixtures.cs`，预览写入 `docs/ui-preview`。

覆盖套件所有自定义原生主窗口的布局构建方法，另保留围栏参数栏、CATIA 树编辑区的独立检查。
`more_fixtures.py` 包含各窗口的场景和业务边界替身；向导保留生产 UI 布局回调，并替代场景拾取回调。
100%、175%、200% 下检查每个可见输入控件和按钮相对于所有祖先容器的边界。
检查所有现有页签和向导步骤，以及日志开启/统一隐藏两种状态。高 DPI 用例限制可用客户区高度；按页面收紧尺寸的窗口另外执行生产代码中的高度调整。
选择、预览及点列表检查最低可用高度；参数区外的空白另外通过完整窗口预览人工复核。
导插枪同时提取生产日志方法；注入 30 把长名称焊钳，检查说明高度和导出按钮位置不变，并验证正常摘要中的“失败 0”不会自动展开日志。

场景访问、按钮业务回调、PS 原生拾取器和主题绘制使用替身；这不是 PS 宿主实机测试。
可达性主窗口来自 `Tests/UiStandards` 的生产 Layout/Helpers；Agent 与快捷配方由 `verify-web.cjs --preview` 使用离线示例渲染。
运行 gallery.py 前先生成以上预览；结果索引为 `docs/ui-preview/review.md`，总览为 `overview.png`。
