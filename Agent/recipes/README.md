# 默认小配方

下面 5 个配方随 `TxTools.dll` 内嵌提供。首次读取配方时写入现有 `memory/recipes` 目录，刷新鲸鱼娘的快捷配方面板即可查看。

| 配方 | 使用方式 |
| --- | --- |
| [一键修改对象颜色](defaults/default_set_color.md) | 绑定对象，填写 `#RRGGBB`；可包含下属对象。 |
| [从标记几何中心生成焊点](defaults/default_geometry_weld_points.md) | 绑定标记或组件，输入名称关键字，在 OperationRoot 创建；默认 0.1 mm 去重。 |
| [对象批量编号](defaults/default_number_objects.md) | 按绑定顺序改为前缀 + 序号，可设置起始序号和位数。 |
| [一键显示或隐藏对象](defaults/default_visibility.md) | 勾选表示显示，取消表示隐藏；可包含下属对象。 |
| [查看对象世界坐标](defaults/default_object_coordinates.md) | 表格显示对象原点或几何中心的 X/Y/Z，单位 mm。 |

## 使用与修改

- 不经过模型，运行时沿用参数校验、研究校验、共享执行锁、Undo 和执行日志。
- 普通参数会记忆，对象 ID 不持久保存，换研究后重新取对象。
- 同 ID 的已有配方保持原样；已安装的默认配方被修改或删除后，重启不会覆盖或重新添加。标记记录在 `.default-recipes-installed`。
- 焊点配方旧版若名称、说明、参数和代码均未修改，首次读取时自动升级到关键字匹配版；保留运行记录，原文件备份为 `.md.before-keyword-*`。用户修改过的版本与删除状态均保留。
- 想恢复某个已删除的默认配方，可在“配方管理”中导入对应 Markdown 文件，得到新副本。
- 几何提取针对独立焊点标记，遍历 `Tx2Or3DimensionalGeometry`。直接通过 `OperationRoot.CreateWeldLocationOperation` 创建，制造焊点用中心平移向量、位置操作用完整 `GeometricCenter`。圆柱取中心，不取端面；法向、零件绑定及焊枪工艺参数由使用者设置。
- 筛选按名称包含输入的关键字，不区分大小写，留空匹配全部；不固定排除 CO2，也不要求 DPUB 格式。名称符合 DPUB 时仍整理为工位／零件号／编号，例如 `DPUB-501021339-XWS_00.001_T1EJ-0000116179-RSW` → `T1EJ_501021339_116179`；其他格式沿用几何体原名，无名称时使用 `WP_001` 等编号。关键字不匹配、重复点和同名异位分别记录日志。
- 本目录原有的“批量调整机器人与资源姿态”仍可手动导入。

## 验证范围

`maintenance/DefaultRecipesRegression.cs` 使用真实配方存储、Markdown 解析、参数生成和 C# 编译器验证五份配方；默认参数和另一组显式参数均通过 PS 2402 SDK / C# 5 编译。独立进程验证默认配方安装、旧焊点配方升级／备份、已有修改、删除状态及运行记录保留。

`maintenance/DefaultRecipeLogicRegression.cs` 将实际配方代码运行在内存场景中，验证名称关键字匹配／空值／大小写／无匹配、任意格式名称与无名标记、CO2 关键字、OperationRoot 创建、DPUB 命名、非 TxSolid 几何、几何中心变换、重复选择／距离去重、重复执行、同名异位、坐标预校验及失败清理，以及改色、显示／隐藏、编号与坐标报告。
