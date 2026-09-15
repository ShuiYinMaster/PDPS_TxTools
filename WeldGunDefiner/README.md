# WeldGunDefiner

通过向导在 Process Simulate 中快速创建 X 型焊枪的运动学定义和驱动关系。

## 命令

- `.快速创建 / X枪运动学`

## 向导流程

1. 选取焊枪设备根节点（Component 层级），检测并按需启用运动学建模状态。
2. 选取 O/A/C 铰点、静电极帽、动电极帽、TCP 及 Link 几何体。
3. 根据几何点计算机构参数，确认臂长、开口、活塞行程、磨量等数值。
4. 预览并确认 J1、J2、`input_j1` 等关节名称和公式。
5. 生成 Link、Joint、Pose、限位和驱动链，查看生成摘要后完成。

## 功能特点

- 使用三点投影建立机构参考平面并计算铰点几何关系。
- 生成曲柄滑块到动臂的 RPRR 驱动链。
- 支持 OPEN 状态适配：厂家模型处于最大开口状态时，可先驱动回闭合并重新定义零位/限位。
- 自动创建或使用焊枪 TCP Frame，并将焊钳模型几何体绑定到对应 Link。
- 提供 SDK 诊断和日志，便于定位 Joint/Link 创建失败。

## 注意事项

- 生成会修改焊枪设备的运动学模型；执行前建议复制设备或保存文档。
- O/A/C 点、静/动电极帽和 TCP 必须选取正确，错误几何会导致参数或开口量失真。
- 需要确认焊枪处于允许运动学建模的状态；向导会提示并可尝试启用。
- 生成后应检查关节方向、限位、TCP 位置以及 OPEN/CLOSE 运动是否符合实际焊枪。

## 主要文件

- `WeldGunDefinerCommand.cs`：命令入口。
- `UI/WeldGunWizardForm.cs`：四步创建向导。
- `Core/WeldGunService.cs`：模型收集、参数计算和生成编排。
- `Core/Rprrbuilder.cs`：RPRR 运动学结构和公式生成。
- `Core/PsSdkHelper.cs`：PS SDK 对象、Frame、Joint 和 Link 辅助。
- `Math/GunMechanism.cs`：几何和机构计算。
