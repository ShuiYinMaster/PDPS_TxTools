# WeldPointMatrix — Weld Point Matrix / 焊点矩阵

Compare locations across multiple operations, position associated robots and insert weld or via locations in Process Simulate.

源码核对日期：2026-10-07。本模块已在同步前的仓库中存在，本次补齐使用说明，业务源码未修改。

## 命令

`焊点矩阵`；入口类：`WeldPointMatrixCommand`。

## 功能与使用

1. 打开命令，在左侧选取多个焊接或连续点操作。
2. 右侧按操作列并列显示焊点和过渡点，方便对照点位顺序；外部修改后点击“刷新列表”。
3. 在表格中选择点位，可定位关联机器人；拖动点位用于调整顺序。
4. 点击“插入空行”准备插入位置，再选中目标操作列，使用“新增焊点”或“新增过渡点”填写点位信息。

新增和调整会修改操作点位。使用前保存场景，完成后检查点位位置、姿态、顺序和机器人关联；本次没有进行 PS 实机验证。

## 主要文件

- `WeldPointMatrixCommand.cs`：命令入口。
- `WeldPointMatrixForm.cs`：单例窗口、操作拾取、矩阵和点位交互。
- `PointService.cs`：点位读取、机器人定位与点位创建辅助。

本模块随 `TxTools.csproj` 构建，环境与注册方式见 [项目 README](../README.md)。
