# RobotReachabilityChecker

检查机器人操作路径点的可达性、关节限位余量、TCP 位置余量和点位干涉状态。

## 命令

- `.可达性检查`

## 检查项目

- 从选定 OP 枚举路径点，并支持“所有类型 / 仅焊点 / 仅 Via”筛选。
- 按机器人品牌（自动、KUKA、ABB、FANUC、其他）应用临界区和奇异点规则。
- 检查各轴软限位余量，默认余量为 10°。
- 检查点位 TCP XYZ 余量，默认余量为 200 mm。
- 可启用静态干涉检查；动态干涉选项当前为不可用状态。
- 结果表显示可达、临界、近限位、奇异和不可达等状态，并支持隐藏正常项、导出结果和定位点位。

## 基本用法

1. 启动 `.可达性检查`，在 PS 中拾取一个 OP 节点。
2. 选择点类型、品牌及余量检查开关/阈值。
3. 按需启用静态干涉检查，点击“开始检查”。
4. 查看结果表和底部日志，必要时导出检查结果。

## 实现与注意事项

- 优先使用 PS 的 `GetPoseAtLocation` 读取点位姿态，失败时再使用 IK 兜底。
- 检查前尝试回到 `HOME` 统一轴值锚点，结束后恢复机器人原始姿态，降低对场景的影响。
- 多 TCP 路径会按点位绑定的工具坐标切换；混合工具路径会在日志中提示。
- 检查结果受机器人配置、工具绑定、HOME 姿态和控制器数据影响，正式使用前应抽查异常点。

## 主要文件

- `Plugin/RobotReachabilityCheckerCmd.cs`：PS 命令入口。
- `Ui/ReachabilityCheckerForm*.cs`：界面、布局和事件处理。
- `Services/ReachabilityChecker.cs`：检查流程编排。
- `Services/AxisAnalyzer.cs`、`Services/InterferenceService.cs`：轴状态与干涉判定。
