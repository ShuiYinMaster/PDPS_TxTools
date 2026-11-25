# ThemeTuner — 主题调整与插件信息

命令：`.主题调整`（类别 `TxTools`）。窗口内分「主题设置」和「关于」两块内容区：主题设置支持预设、自定义颜色、扁平样式及日志显示开关，选择按 Windows 用户保存，重启宿主后保持。

## 关于与更新

- 项目：<https://github.com/ShuiYinMaster/PDPS_TxTools>
- 发行版：<https://github.com/ShuiYinMaster/PDPS_TxTools/releases>
- 问题反馈：<https://github.com/ShuiYinMaster/PDPS_TxTools/issues>
- 更新检查仅由按钮触发，访问 GitHub 公开 `releases/latest` 接口；不在启动时联网、弹窗、下载或安装。
- 查询超时为 12 秒，窗口关闭取消请求，响应体上限 2 MiB。网络异常、无稳定版及限流在页面内显示。
- 本机版本使用 `AssemblyInformationalVersion`。没有正式版本元数据时显示本地构建及程序集版本，仅显示最新稳定版，不宣称当前构建需要升级。
- 比较支持 `v3.0.1`、`TxTools_3.0.0` 等稳定版本标签；未识别的标签及开发版本不参与自动比较。
- 复制诊断信息只包含插件版本、CLR 版本、系统版本、进程位数及项目地址，不包含日志、场景、路径、用户名或凭据。

## 第三方组件

界面提供项目来源、用途和许可类型，并内嵌项目许可、宿主声明、EarCut、JTReader、dsh-pet、Lobe Icons、NuGet 包及 Electron 可用的原文。许可证可以离线查看。Chromium 等运行时的完整第三方声明随运行时目录分发。

开源引用包括 Newtonsoft.Json、Open XML SDK、ONNX Runtime、JTReader、EarCut、dsh-pet、Lobe Icons、SharpCompress、Electron 及配套 .NET 库。JTReader 和 dsh-pet 含本地修改。品牌图标的商标权归相应权利人。

STEP 导入使用 Open CASCADE 7.9.2（LGPL 2.1 加 OCCT 附加例外）。地脚图转换器使用 ezdxf、NumPy、CPython，并通过 PyInstaller 打包。随附 Python 支持库，以及 OCCT 的 oneTBB、FreeType、FreeImage、FFmpeg、jemalloc、OpenVR 也列入鸣谢。随附组件不一定在每条转换路径中使用。对应版权、许可及例外原文保存在 `Licenses/Runtime`，构建时嵌入 DLL 供离线阅读。

WebView2、Process Simulate SDK、CATIA COM 和 C1 控件使用各自专有许可，不适用本项目 MIT 许可。来源和用途清单位于 `ThemeTunerForm.About.cs`；更新请求和版本处理位于 `ReleaseChecker.cs`。

## 许可证文件

`Licenses/` 存放可离线查看的许可原文，构建时嵌入 DLL：

| 位置 | 覆盖内容 |
| --- | --- |
| `Licenses/OpenXml.txt` | Open XML SDK |
| `Licenses/Runtime/` | 随转换器分发的运行时：CPython、PyInstaller、NumPy、lxml、pywin32、pyparsing、packaging、psutil、PyYAML、ezdxf、fonttools、MarkupSafe、typing_extensions、Open CASCADE 7.9.2、oneTBB、FreeType、FreeImage、FFmpeg、jemalloc、OpenVR |

新增或升级随附组件时同步更新对应文本，否则「关于」页展示的离线许可会与发行包不一致。

## 文件

| 文件 | 职责 |
| --- | --- |
| `ThemeTunerCmd.cs` | 命令注册与窗体启动 |
| `ThemeTunerForm.cs` | 主题设置（预设、自定义色、扁平样式、日志开关） |
| `ThemeTunerForm.About.cs` | 关于页、第三方组件清单、诊断信息复制 |
| `ReleaseChecker.cs` | GitHub `releases/latest` 查询与版本比较 |
