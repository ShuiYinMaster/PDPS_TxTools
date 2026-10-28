# 桌宠组件准备

桌宠适配脚本保留在源码目录；Electron 和动画组件保存在仓库外的 `TxTools.Local/dependencies/desktop-pet/`。

```powershell
rtk proxy pwsh -NoProfile -File Agent/maintenance/Install-DshPetComponents.ps1
rtk proxy powershell -NoProfile -ExecutionPolicy Bypass -File tools/Build.ps1 -Configuration Release
```

安装工具需要 PowerShell 7，按固定上游版本校验下载和逐文件哈希。缓存及清单位于外部 `dependencies/downloads/pet-components/`，被替换文件备份到外部 `backups/pet-components/`。`-SkipDownload` 仅使用已校验缓存；`-Destination`、`-Cache` 和 `-BackupDirectory` 可覆盖位置。

`DshPet.Build.targets` 将源码适配文件及外部组件合并复制到主插件输出的 `Agent/UI/pet/dsh-pet/`。部署时复制完整插件输出，不要只复制 DLL。替换已有组件或部署 DLL 前应关闭对应宿主；组件安装和构建不会自动部署插件。
