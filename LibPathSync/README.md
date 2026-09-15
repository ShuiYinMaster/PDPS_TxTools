# LibPathSync

同步 Process Simulate 库路径相关的“上次访问目录”，减少不同资源对话框之间反复浏览目录的问题。

## 命令

- `.路径同步`：将所有目标目录同步为 `System Root Path`。
- `.同步到所选组件`：将所有目标目录同步为当前选中组件的库存储目录。

## 同步目标

插件从当前用户注册表 `HKCU` 读取/写入以下 PS 配置：

- `Define Component Type` 的 `LastFolder`
- `Insert Component` 的 `GET_COMPONENT_DIALOG_LAST_FOLDER`
- `Import Mfgs` 的 `LastBrowsedDirectory`

`.路径同步` 的源为 `Software\TECNOMATIX\TUNE\NewAssembler\Options\EMS` 下的 `System Root Path`。

## 使用说明

1. 需要按系统库根同步时，直接执行 `.路径同步`。
2. 需要按某个组件所在库目录同步时，先在 PS 树或视图中选中组件，再执行 `.同步到所选组件`。
3. 操作完成后，消息框会回读并显示各目标键的实际值。

## 注意事项

- 插件修改的是当前 Windows 用户的 HKCU 注册表，不会修改 PS 场景。
- 所选对象必须能够解析出 `StorageObject/FullPath`、`Path` 或文件路径；否则不会写入。
- `.cojt`、`.co`、`.jt` 容器路径会尝试归一化到其上级文件夹。
- 修改后若 PS 对话框仍缓存旧路径，需重新打开对应对话框或重启 PS。

## 主要文件

- `LibPathTargets.cs`：注册表键和值的集中配置。
- `SyncLibPathCommand.cs`：同步到 System Root。
- `SyncToSelectedComponentCommand.cs`：同步到所选组件。
