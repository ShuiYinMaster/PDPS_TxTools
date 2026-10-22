# TxAgent 桌宠运行组件

桌面实现基于 [PC2005-cloud/dsh-pet](https://github.com/PC2005-cloud/dsh-pet)。上游版本由 `UPSTREAM.json` 锁定，MIT 许可证见 `LICENSE-dsh-pet.txt`。Electron 独立解码透明 WebM，PS 插件只管理状态、窗口入口与生命周期。

## 使用与状态

- 单击展开或收起快捷配方面板；双击或右键菜单打开助手。双击和拖动不同时触发单击入口。
- 快捷配方支持搜索、收藏、参数填写、对象选择、执行与结果详情，复用 `RecipeUiActions` 的校验及执行锁。
- Agent 的桌宠开关控制显示状态，偏好保存在 `prefs.json` 的 `DesktopPetVisible` 字段；关闭聊天窗口不结束桌宠。
- 前台 PS 实例负责配方执行。切换研究或前台实例后清空对象绑定，执行前重新检查目标。

状态通过本地原子 JSON 文件传递；请求、工具执行、等待、完成和失败映射到相应动作。Electron 仅读取状态，不接收模型凭据。多实例协调与接管规则见 [共享桌宠](../../../maintenance/SharedDesktopPet.md)。

## 构建与部署

运行 `Agent/maintenance/Install-DshPetComponents.ps1` 恢复 `electron/` 和 `assets/`，下载内容按固定上游版本校验。`DshPet.Build.targets` 将运行目录复制到构建输出的 `Agent/UI/pet/dsh-pet/`。部署时一起复制主 DLL、依赖及完整运行目录。

`config.json` 控制大小、位置、动作权重和物理参数。`upstream/` 保存适配后的桌面实现，制作工具位于相邻 `dsh-pet-tools/`。重建共享核心需要固定版本的上游源码。

## 启动诊断

只有窗口可见且视频帧推进后，Electron 才报告启动成功。缺失资源、初始化失败、渲染崩溃和超时通过 Agent 状态栏提供错误及日志路径。

日志默认位于 `%TEMP%/TxTools/Agent/DshPet/<PID>/<启动ID>.json.log`；`TXTOOLS_TEMP_ROOT` 可覆盖根目录，非 ASCII 路径可能使用备用临时目录。以界面提供的路径为准。

程序集被影子复制时，资源解析会检查原始 CodeBase。宿主的 Json.NET 版本可能不同，状态写入使用兼容的无参 `JToken.ToString()`；不应为桌宠替换宿主的 Newtonsoft.Json.dll。

验证需覆盖显示开关、动画持续播放、配方入口、研究切换、多实例接管、退出清理及目标显示器的 DPI 行为。
