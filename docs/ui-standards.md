# 插件 UI 规范

## 按钮和窗口

统一入口为 `SRC/PluginUiPolicy.cs`，由 `FormUiKit.ApplyDpiScaling` 在 96 DPI 设计阶段应用一次。

- 操作按钮以“机器人路径点位检查 → 开始检查”为基准，高度 26，基准宽度 86。
- 长文字按当前按钮字体测量，增加左右留白；文字改变时重新计算宽度。
- 随 Windows DPI 等比调整，不另加控件压缩比例。
- 窗口使用各插件的设计尺寸，不再统一把外框缩至 85%；仅在超出屏幕工作区时限制尺寸。表格中的按钮行以所在单元格为边界，内嵌的“标签＋输入框”保持紧凑。
- 固定窗口边框，关闭最大化和状态栏尺寸调整手柄；程序仍可按屏幕工作区定位、按日志展开状态调整尺寸。
- 参数区通过对齐、短标签、换行和分组排列适配；不新增分页或页签。可达性检查的五个分组始终单行。
- 列表、表格、树及明确登记的列表区域保留滚动；其他区域不显示横纵滚动条。不要修改 PS 原生拾取控件内部的按钮。

新增普通对话框调用 `PluginUiPolicy.Attach(form)`；已经调用 `ApplyDpiScaling` 的主窗体无需重复挂接。
自绘的列表滚动容器需调用 `PluginUiPolicy.AllowListScrolling(container)`。WebView 操作按钮和滚动条样式来自 `Agent/UI/plugin-ui.css`，会话、配方、模型及工具列表保留滚动条；参数/详情区域可通过滚轮和键盘访问。

## 统一日志开关

开关为 `TxTools.Common.PluginLog.Enabled`。主题调整窗口的“显示插件日志”可以同时控制所有已注册日志栏。
默认显示日志，启动前设置环境变量 `TXTOOLS_SHOW_LOGS=false` 可以统一隐藏。

发行构建使用：

```powershell
rtk proxy MSBuild.exe TxTools.csproj /p:Configuration=Release /p:TxToolsShowLogs=false
```

该属性定义 `TXTOOLS_HIDE_LOGS`，编译后的日志开关始终为 false，界面开关和错误自动展开均不能恢复日志栏。
只隐藏日志视图及其入口；操作进度、结果摘要和错误消息仍可见，后台诊断记录不受影响。

新增日志视图必须 `PluginLog.Register(region, initiallyVisible, toggleControls...)`，登记整个日志容器，而非只登记内部文本框。
主窗体加载时统一应用；独占的 TableLayoutPanel 日志行会同步折叠至零，恢复时保留原行类型和高度。
本地展开/收起使用 `PluginLog.SetVisible(region, visible)`，自动展开也必须经过此入口。

## 图标

图标目录和命令映射见 `Image/plugin-icons.json`。保留已有大图标，为缺失/错误的图标新增对应图形；所有 25 个参与构建的正式命令均配置大小图标。
`DeviceZAligner` 另有配套图标和源代码引用；该模块未列入主项目构建。

根据本地 Process Simulate 2402 SDK XML 文档：

- `LargeBitmap`：32×32 PNG，32bpp BGRA，透明背景。
- `Bitmap`：16×16 BMP，背景 `#c0c0c0`，无透明通道。

新图形保留 SVG 源文件（`Image/Svg`），采用与已有文件相近的灰色金属、青色/绿色功能标记。
绘制和导出使用 SVG + sharp/librsvg；已有大图标仅生成小图标配套文件。资源通过 EmbeddedResource 打包。

```powershell
# sharp 可使用本机依赖目录，通过 NODE_PATH 指定。
rtk proxy node tools/generate-plugin-icons.cjs
```

预览文件为 `Image/plugin-icons-preview.png`。它只用于审阅，不作为 Ribbon 图标资源。

## 验证

```powershell
rtk proxy MSBuild.exe Tests/UiStandards/UiStandards.csproj /t:Build
rtk proxy Tests/UiStandards/bin/UiStandardsRegression.exe build-check/TxTools.dll Image/plugin-icons.json
```

回归检查按钮尺寸、长文字宽度、固定边框、列表滚动保留、日志总开关/自动展开/行高恢复，以及程序集内每个图标的存在、尺寸和像素格式。
可达性布局直接链接生产 Layout/Helpers，在 100%/175%/200% 下检查单行排列和控件边界并生成预览。
测试宿主以 WinForms 替代 PS 原生拾取器和结果网格，因此预览用于检查排布，不代表 Siemens 皮肤或实际场景数据效果；最终仍需 PS 实机复测。

新增窗口裁切回归见 `Tests/LayoutRegression/README.md`：从生产代码提取导插枪、围栏、焊点分配和 CATIA 目录树的布局方法，检查按钮/参数相对于全部祖先容器的可见边界及选择、预览列表的可用高度。
导插枪保持等宽三列，中间整列用于点列表；导出点球只保留零件名称输入。焊点分配日志默认收起，可通过“日志”按钮展开，仍受统一日志开关控制。
导插枪的 96 DPI 默认窗口为 960×600，左侧操作列表随列内剩余空间伸缩；不再保留 800 像素高的外框或将操作列表封顶为 130 像素。布局回归同时检查左列底部未使用的高度。
左侧提示改为悬停说明，参考坐标状态与重置按钮同行，点类型与 MFG 选项同行；操作选择框在 100% 预览中高 188 像素，并检查 100%/175%/200% 下至少保留 150 个逻辑像素。
焊钳说明固定单行高度，多焊钳显示数量，长内容省略并通过悬停查看全文。后台日志按文字推断的级别只用于着色，不自动展开日志；显式错误仍可展开，并受日志总开关约束。裁切回归包含 30 把长名称焊钳及“成功定位 30/30，失败 0”等正常摘要，检查导出按钮位置和日志栏状态。

构造窗体时应保留控件引用，不通过 `GetControlFromPosition` 重新取得已隐藏的日志控件；隐藏控件可能不参与表格位置查询。
