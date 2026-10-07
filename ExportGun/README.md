# ExportGun — Weld Gun Export to CATIA / 焊枪与点云导出

Export weld guns and weld-point clouds from Process Simulate to CATIA, with TCP and reference-frame options.

将 Process Simulate 中的插枪、焊枪和焊点云数据导出到 CATIA，便于布局检查和外部交付。

## 命令

- `.导插枪`

## 功能

- 读取 PS 中的插枪/焊枪设备和焊点数据。
- 支持使用 MFG 名称作为输出名称。
- 支持自定义焊枪数模、机器人当前 TCP，以及以 TCP 作为焊枪原点。
- 可选择参考 `Component` 或 `Frame`，并显示当前坐标系/TCP 信息。
- 将焊枪及点云数据写出到 CATIA，并在界面日志中显示处理状态。

## 基本用法

1. 启动 `.导插枪`。
2. 在 PS 中选择目标 Component/Frame，确认参考坐标系。
3. 按需启用 MFG 名称、自定义数模、TCP 导出和 TCP 原点选项。
4. 设置 CATIA 产品/输出信息，执行导出并检查结果。

## 注意事项

- CATIA COM 接口必须可用，CATIA 进程和目标产品应处于可写状态。
- 自定义焊枪数模、TCP 和参考 Frame 的选择会直接影响导出坐标。
- 导出前建议确认点云数量和坐标状态；出现“坐标控件不可用”时应先检查 PS 选择对象。

## 主要文件

- `ExportGunCmd.cs`：PS 命令入口。
- `ExportGunForm.cs`：设备、坐标、点云和输出选项界面。
- 其余导出辅助类负责 PS 对象读取、CATIA 写出及数据转换。

## 2026-10-07 更新与源码核对

导插枪窗口接入 `PickAwareTxForm`，TCP 与参考坐标系下拉控件接入 `PickFocus`。拾取中按 Esc 保留已选值并退出，重新进入控件可继续拾取。CATIA 写出和坐标转换流程沿用现有实现。

本模块随 `TxTools.csproj` 构建。环境、注册方式和本次完整更新日志见 [项目 README](../README.md)。

公共拾取实现位于 `SRC/PickFocus.cs` 和 `SRC/PickAwareTxForm.cs`；回归工程为 `Tests/PickFocus/PickFocus.csproj`。测试使用 SDK 控件替身和真实 WinForms 焦点/键盘消息，仍需在 PS 内确认实际拾取效果。
