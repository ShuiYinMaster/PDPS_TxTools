# RobotBaseChecker — Robot BASE0 Validation / 基座校验

Check robot BASE0 consistency and correct detected offsets in Process Simulate, with configurable tolerances and brand handling.

检查场景中机器人 `BASE0` 与期望基准的一致性，并支持对存在偏差的机器人批量校正。

## 命令

- `.BASE0 一致性检查校正`

## 功能

- 扫描活动文档中的机器人并读取自身坐标、当前 `BASE0` 和机器人品牌信息。
- 支持自动检测、通用底座面和 FANUC 运动学交点三种品牌处理模式。
- 使用位置容差和旋转容差判定“一致/存在偏差/无法比较”等结果。
- 表格展示机器人、品牌、坐标差值和结论；双击行可定位机器人。
- 将偏差机器人同步为期望 `BASE0`，并导出检查结果 CSV。
- 提供详细日志，记录缺少 BASE0、无法读取姿态和校正失败等情况。

## 基本用法

1. 启动 `.BASE0 一致性检查校正`。
2. 设置位置容差、旋转容差和品牌模式。
3. 点击“开始检查”，逐行查看结果。
4. 确认目标后点击“同步全部 BASE0”，或导出 CSV 留档。

## 注意事项

- “同步全部 BASE0”会批量修改场景数据，执行前必须确认机器人和品牌判断正确，并建议保存文档。
- FANUC 期望值使用 J1/J2 运动学计算；其他品牌主要按机器人底座安装面/自身坐标处理。
- 未挂控制器、没有系统帧或姿态无法读取的机器人可能只能显示为不可比较。

## 主要文件

- `RobotBaseCheckerCommand.cs`、`RobotBaseCheckerForm.cs`：命令和界面。
- `RobotBaseReader.cs`：机器人、BASE0 和姿态读取。
- `RobotKinematics.cs`：品牌识别和期望基准计算。

## 2026-10-07 更新与源码核对

核对 BASE0 检查、品牌处理和偏差校正流程。本次该模块业务源码未修改；正式校正前先查看容差及检查结果，校正后复核机器人坐标。

本模块随 `TxTools.csproj` 构建。环境、注册方式和本次完整更新日志见 [项目 README](../README.md)。


## 撤销与恢复（2026-10-07）

只读检查不需要模型撤销；同步全部 BASE0 使用一个场景撤销分组，部分失败报告明细，需在当前 PS 工程按 Ctrl+Z 撤销本次同步。

详见 [统一撤销与恢复说明](../docs/undo-safety.md)。
