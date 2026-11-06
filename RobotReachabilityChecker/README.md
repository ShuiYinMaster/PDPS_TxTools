# RobotReachabilityChecker — Robot Reachability Check / 机器人可达性检查

Check robot reachability and joint-limit conditions for welding locations in Tecnomatix Process Simulate.

检查机器人操作路径点的可达性、关节限位余量、TCP 位置余量和点位干涉状态。

## 命令

- `.可达性检查`

## 检查项目

- 从选定 OP 枚举路径点，并支持“所有类型 / 仅焊点 / 仅 Via”筛选。
- 按机器人品牌（自动、KUKA、ABB、FANUC、其他）应用临界区和奇异点规则。
- 检查各轴软限位余量，默认余量为 10°。
- 检查点位 TCP XYZ 余量，默认余量为 200 mm。
- 可启用静态干涉检查；动态干涉选项当前为不可用状态。
- 静态干涉优先只读复用关联机器人或焊钳的已有干涉集；未找到有效组时，按自动规划的总成归属规则构建临时双列表范围，不修改场景碰撞对或全局碰撞设置。
- 窗口内同一操作重复检查复用范围；文档、操作、机器人、对象结构、位姿或组成员变化时重建。重置窗口和关闭窗口会释放范围。
- 干涉框内显示准备阶段提示及点位检查进度；发生干涉的结果整行标红，未知状态保留警告色。
- 干涉与轴限位独立统计；结果及导出区分存在干涉、无干涉、干涉未知和未检查。干涉本身不会触发“接近极限”。
- 检查覆盖总成与环境之间的碰撞，安装接触仅在固定对象对位置不变且接触深度不超过 0.1 mm 时豁免；不以起始姿态的碰撞作为豁免基线。
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

## 界面与构建

可达性窗口接入 `PickAwareTxForm`，操作节点对象框接入 `PickFocus`。拾取中按 Esc 保留所选节点并退出拾取。

本模块随 `TxTools.csproj` 构建。环境与注册方式见 [项目 README](../README.md)。

公共拾取实现位于 `SRC/PickFocus.cs` 和 `SRC/PickAwareTxForm.cs`；焦点回归位于独立测试目录。测试使用 SDK 控件替身和真实 WinForms 焦点/键盘消息，仍需在 PS 内确认实际拾取效果。

## 撤销与恢复（2026-10-07）

IK/余量检查保存并尝试还原临时关节、工具帧与 TCPF 状态，原生命令编辑由 PS 管理撤销。临时状态恢复不是调用 Ctrl+Z，真实撤销历史需在宿主中核验。

详见 [统一撤销与恢复说明](../docs/undo-safety.md)。
