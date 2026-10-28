# 构建与目录布局

源码仓库保存生产代码、运行脚本、资源声明、构建配置和技术文档。测试、下载依赖、编译输出及本地资料分别放在仓库外。

默认布局以源码仓库的同级目录 `TxTools.Local` 为根：

| 路径 | 内容 |
|---|---|
| `tests/` | 独立测试工程与运行入口 |
| `build/bin/TxTools/<Configuration>/` | 主插件、MCP 桥与完整运行资源 |
| `build/bin/JtDecoder/Release/` | JT 解码器 |
| `build/bin/JtCgrWorker/Release/` | CGR 工作进程 |
| `build/obj/<Project>/<Configuration>/` | 编译中间文件及 COM 包装引用 |
| `build/logs/` | 构建日志 |
| `dependencies/nuget/` | `packages.config` 包 |
| `dependencies/jt/` | JT 解码器 DLL 依赖 |
| `dependencies/desktop-pet/` | Electron 与动画运行组件 |
| `backups/` | 备份与源码快照 |
| `design/`、`experiments/` | 制作资料与试验资源 |

`Directory.Build.props` 导入 `build/Workspace.props`，主项目和子项目共享目录属性。`NuGet.Config` 将还原目录指向外部依赖目录。源码目录不需要 `bin`、`obj` 或 `packages` 链接。

## 构建

需要 .NET Framework 4.8 构建工具、PS 2402 SDK、CATIA COM 类型库和已还原的依赖。默认 SDK 位置为源码仓库的同级 `2402dll`。

在仓库根目录运行：

```powershell
rtk proxy powershell -NoProfile -ExecutionPolicy Bypass -File tools/Build.ps1 -Configuration Release
```

`-SdkDirectory`、`-LocalRoot` 和 `-MSBuildPath` 可指定对应位置。Debug 与 Release 的输出和中间文件分别保存。构建只生成文件，安装与命令注册须单独执行。

下载桌宠组件时运行 `Agent/maintenance/Install-DshPetComponents.ps1`，需要 PowerShell 7。组件按固定版本校验后写入外部依赖目录；构建时与源码适配脚本一起复制到主插件输出目录。

## 独立测试

测试目录内的 `Directory.Build.props`、`TestPaths.props` 和 `workspace.json` 指定源码及输出位置。统一入口为 `Run-Checks.ps1`：

```powershell
$testRoot = '..\TxTools.Local\tests'
rtk proxy powershell -NoProfile -ExecutionPolicy Bypass -File "$testRoot\Run-Checks.ps1" -Suite All
```

`Core` 执行跨环境、导出、CGR 和配方回归，`Ui` 执行无宿主界面回归。测试引用生产源码，输出写入外部 `build` 目录。PS/CATIA 交互、实际模型加载、显示、拾取与测量仍需目标宿主验收。
