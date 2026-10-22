# TxAgent 停靠 Viewer 部署说明

源码已把聊天界面抽为 `TxAgentControl : UserControl`，停靠宿主是
`TxTools.Agent.UI.TxAgentDockViewer`，独立窗口仍由 `TxAgentForm` 承载。
三者编译在原来的 `TxTools.dll` 中，不增加运行时依赖。

## 目标机器上的注册

仅替换 DLL 不会注册新的 Viewer。现有安装器的 `DotNetCommand` 注册也不等于
Viewer 注册。需要按照目标机器 PS 2402 的自定义 Viewer 配置格式添加以下信息：

| 配置含义 | 值 |
|---|---|
| Viewer 内部名称 | `TxAgentDockViewer` |
| 程序集 | 已部署的 `TxTools.dll`，路径按目标配置中的程序集解析规则填写 |
| 命名空间 | `TxTools.Agent.UI` |
| 控件类名 | `TxAgentDockViewer` |
| 完整类型名 | `TxTools.Agent.UI.TxAgentDockViewer` |
| 实例模式 | 单实例，非 Open With |

`TxAgentDockViewer.registration.json` 是上述注册信息的交付清单，**不是 PS 可直接导入的配置文件**。
应以目标 PS 自带示例或 SDK 自定义 Viewer 文档为模板，
保留其配置结构，填写本表的值；如模板含 Viewer/ControlBar ID，分配未占用的唯一 ID，
不要复制内置面板的 ID。默认停靠位置等配置也应遵循目标版本的模板。

部署 DLL 和注册配置后重启 PS。可在目标 PS 的进程内 C# 环境中检查：

```csharp
var manager = Tecnomatix.Engineering.TxApplication.ViewersManager;
bool registered = manager.IsViewer("TxAgentDockViewer");
bool singleInstance = registered && manager.IsNonOpenWithViewer("TxAgentDockViewer");
return "registered=" + registered + ", singleInstance=" + singleInstance;
```

两项都为 `true` 时，点击原来的 **TxAgent** 命令会打开或显示 PS Viewer。
没有注册时，命令继续打开独立窗口；注册了错误的类或多实例类型会明确报错。
不需要把用户已有的模型、Key、历史或配方迁移到其他目录。

## 行为与验收

- PS 原生容器负责拖拽、停靠及浮动；自动隐藏由 `CanAutoHide` 声明支持。
- 收起自动隐藏面板不会结束聊天会话；真正的 `Uninitialize`/Dispose 会保存会话、
  取消模型请求、释放审批和提问等待，并清理轮询与事件订阅。
- 浮动时打开配方栏会尝试扩宽窗口，收起时扣回实际增加的宽度。
  停靠时不修改 PS 分栏或主窗口大小；窄面板用覆盖式配方栏，宽面板可并列显示。
- 原命令、桌宠打开助手、快捷配方进入管理界面均使用同一个内容控件，
  保留跨进程窗口独占约束，不创建第二份聊天会话。
- 目标机器需要验证：首次打开、拖拽到左右边缘、浮动、自动隐藏/展开、
  配方栏开关、关闭时取消确认、重新打开历史，以及双 PS 实例下的窗口独占。

注册解析、拖拽停靠和 WebView2 宿主行为应在目标 PS 环境验收。

## 源码与回归

- `UI/TxAgentForm.cs`：共享内容、独立/停靠宿主和侧栏布局。
- `TxAgentCommand.cs`：已注册 Viewer 优先、未注册兼容入口和会话独占。
- `maintenance/DockingRegression.js`：生成实际生命周期/布局代码的无宿主测试。
- `maintenance/DockingEntryRegression.js`：生成实际命令入口/工厂的无宿主测试。

生成脚本分别输出到 `artifacts/docking-tests/`，使用 MSBuild 自带 Roslyn `csc.exe`
编译相应 `.cs` 文件后运行测试；布局测试需要引用 `System.Drawing.dll` 和
`System.Windows.Forms.dll`，入口测试只需引用 `System.Windows.Forms.dll`。
