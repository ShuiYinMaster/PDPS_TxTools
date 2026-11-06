# 主题调整与插件信息

主题调整命令包含主题设置和关于信息，共用窗体内容区。主题设置支持预设、自定义颜色、扁平样式及日志显示开关。

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

WebView2、Process Simulate SDK、CATIA COM 和 C1 控件使用各自专有许可，不适用本项目 MIT 许可。来源和用途清单位于 `ThemeTunerForm.About.cs`；更新请求和版本处理位于 `ReleaseChecker.cs`。
