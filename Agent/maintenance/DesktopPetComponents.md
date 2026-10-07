# 补齐桌面宠物组件

`Install-DshPetComponents.ps1` 使用 PowerShell 7 下载并安装便携版 Electron 43.3.0 和 dsh-pet v0.3.0 的动画、图片。Electron 使用官方 SHA-256 清单校验；素材使用固定上游提交的 Git blob 哈希校验，部署后再比对全部文件的 SHA-256。

先在工作区准备和验证：

```powershell
pwsh -File Agent/maintenance/Install-DshPetComponents.ps1
node Agent/UI/pet/dsh-pet-tools/validate.cjs
```

将已校验组件复制到实际插件的桌宠目录；若需替换已有组件，先关闭 Process Simulate：

```powershell
pwsh -File Agent/maintenance/Install-DshPetComponents.ps1 -SkipDownload -Destination 'G:\Program Files\Tecnomatix_2402\eMPower\DotNetCommands\TxTools\Agent\UI\pet\dsh-pet'
node Agent/UI/pet/dsh-pet-tools/validate.cjs 'G:\Program Files\Tecnomatix_2402\eMPower\DotNetCommands\TxTools\Agent\UI\pet\dsh-pet'
```

安装仅写入目标的 `electron` 和 `assets` 目录，不更换 TxTools DLL、适配脚本、桌宠配置或工程数据。相同文件直接跳过；替换不同文件前备份到工作区缓存。目标须已包含 TxAgent 的桌宠适配文件，且目标桌宠未运行。新增完全缺失的目录会先复制完整内容再重命名发布，可在 PS 运行时补齐；如果需要替换已有组件，则须先关闭 PS。

下载缓存、逐文件校验清单和部署报告位于 `Agent/artifacts/pet-components`。组件也安装到工作区 `Agent/UI/pet/dsh-pet`，后续插件构建会沿用既有 `DshPet.Build.targets` 将资源复制到输出目录。
