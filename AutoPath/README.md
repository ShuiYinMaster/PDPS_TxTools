# AutoPath（Weld Path Planner） — Tecnomatix Process Simulate 插件组件

面向机器人焊接工艺的自动过渡点（Via）与路径编排器，作为 TxTools 插件的一部分。  
在选中焊接操作内部按执行序为相邻焊点生成进/出枪点、插入过渡点并做静态/动态校验与修复，最终把 Via 写入 PS（Process Simulate）操作树中。

本组件的设计目标是：尽可能在不改动工艺端点（焊点、进出枪点、home）的前提下，自动生成安全且节拍友好的过渡路径；对不可自动解决的段产生可读警告，便于人工介入。

主要特性
- 在“操作内”模式对每个选中操作独立规划（不新建全场景操作）。
- 进/出枪点按焊点法向自适应搜索并生成（默认距离 10–20 mm，可配置）。
- 过渡段分层级规划：L0 直连 → L1 枪坐标系定向搜索 → L1.5 经验中继复用 → L2 RRT → 若都失败则标记失败并报错。
- 逐帧动态精修（关节扫掠检测 + 多策略修复阶梯：深退门形 / 细分 / 避让 / 中点兜底）。
- 路径捷径化（shortcutting）以删除不必要的过渡点，基于几何与关节节拍双重判据。
- 可选焊点顺序优化（2-opt / Or-opt），代价使用关节 PTP 时间（有关节数据时），会修改操作内焊点顺序并尝试同步重排 PS 树。
- 支持写入焊钳外部轴（开口量）与运输运动参数（如把过渡点 MotionType 设为 Joint），支持自适应开口搜索。
- 查询缓存（位姿/碰撞）以提升性能（可配置精度/开关）。
- 具备中止（cancel）钩子与界面友好的进度上报接口。

快速上手（使用前须知）
1. 在主界面先为机器人创建/准备好“干涉集（collision set）”（插件以附着模式复用已存在的干涉集；找不到则会跳过操作并报错）。不要期望插件会偷偷新建干涉集。
2. 在 Process Simulate 中选择要处理的焊接操作（按树序进行）。
3. 在界面上选择机器人回退（fallback）或确保操作已关联机器人（op.Robot）。插件优先使用 op.Robot。
4. 启动规划。运行期间可通过 Stop/Cancel 按钮中止（已插入的 Via 会保留）。
5. 规划结果：Via 直接写入到操作中（home、approach/retract、transit），并尽可能把运动参数/开口写好；失败段与警告会在日志中列出，需要人工复核与修正。

公共接口（可在外部调用）
- 类：TxTools.AutoPathPlanner.WeldPathPlanner
  - 构造：new WeldPathPlanner(Action<string> log)
  - 主要方法：PlanningReport ExecuteForOperations(TxRobot fallbackRobot, List<ITxObject> selectedOps)
    - 返回 PlanningReport（统计数据 + 警告列表 + 耗时等）
  - 常用可配置 public 字段（部分重要项与默认值）
    - ApproachRetractDistance = 20.0 （进/出枪最大距离 mm）
    - ApproachRetractMin = 10.0 （进/出枪最小距离 mm）
    - GenerateApproachRetract = true
    - RrtStepSize = 50.0
    - RrtMaxIterations = 5000
    - RrtGoalBias = 0.15
    - EdgeCheckResolution = 12.0
    - GunBackoutStep = 60.0, GunBackoutMax = 800.0, GunSideStep = 60.0, GunSideMax = 800.0
    - GunMinBackoutForSide = 200.0
    - DynamicCheckEnabled = true
    - DynamicJointQuantum = 4.0, DynamicCartesianQuantum = 15.0, MaxSweepSteps = 64
    - OrientationVariantsEnabled = true, MaxVariantTries = 13
    - PruneEnabled = true (路径捷径化)
    - WeldOrderOptEnabled = false, WeldOrderLockFirst/Last = 0
    - GunAxisWriteEnabled = true, TargetGunOpening = 30.0, AdaptiveGunOpening = true, TransitGunOpening = 60.0
    - GunOpenDirectionOverride = 0, GunMaxOpeningOverride = 0
    - SetTransitMotionJoint = true
    - QueryCacheEnabled = true, CacheQuantum = 0.5
    - SampleBoundsInflateXy/ZUp/ZDown 等采样边界扩展控制
    - CollinearTolerance = 3.0
  - 进度上报：通过 public PlanningProgress Progress（可选）接收阶段/百分比更新
  - 取消支持：通过 public Func<bool> IsCancelled 提供外部中止钩子

算法概览（重要行为与安全策略）
- 端点（进/出枪点）为工艺点，默认不随路径移动；若端点本身干涉，则尝试“净空绕行”（在两端上方生成高位中继），若仍不可行则整段标为失败并记录警告。
- 过渡段规划级联（L0–L2）：
  - L0：直接静态连边（采样检查）。
  - L1：基于枪坐标系的定向搜索（模拟人工把枪后退/抬升/横移的序列）。
  - L1.5：复用“经验中继池”（同一操作中已验证的自由点）。
  - L2：在限定边界内使用 RRT（带种子注入和边界膨胀），结果平滑并转为 Via。
- 动态精修（逐帧关节扫掠验证）：
  - 捕获每个节点的关节姿态（失败节点标为“洞”，跳过其相邻边的动态检查）。
  - 对违例边按预算尝试修复，多种策略并优先“深退门形”（沿枪 -X 深退再抬升等），避免浅退 + 现场抬升导致的枪体扫掠。
  - 修复成功则插入新的过渡点并在后续轮次复检；最多若干轮（默认最多 3 轮）。
- 路径捷径化（shortcutting）：
  - 只删除“过渡点”（不动焊点/进出枪/home），对每个候选直连做静态采样 + 动态关节扫掠复验，并用关节节拍模型（CycleCost）判定是否真正节拍更优，避免“笛卡尔更短但需要翻腕反而更慢”的误判。
- 焊点顺序优化：
  - 可选；先捕获每个焊点的关节姿态并构建代价矩阵（优先用 PTP 时间），再用 WeldOrderOptimizer（2-opt/Or-opt）并行求解，变更后尝试重排 PS 中的焊点顺序（会记录警告，需人工确认工艺允许变序）。

文件与模块职责（AutoPath 目录）
- WeldPathPlanner.cs — 核心编排器，包含主流程、策略与大多数配置项（主入口 ExecuteForOperations）。
- RrtPlanner.cs — RRT 路径规划实现（采样、边检查、平滑等）。
- CollisionWorld.cs — 碰撞世界封装，提供静态/动态碰撞判定、关节捕获、姿态搜索等。
- CollisionSetService.cs — 干涉集管理（创建/附着/复用）。
- GunAxisService.cs — 焊钳外部轴（开口）探测与写入逻辑。
- GunFrameSearch.cs — L1 的枪坐标系定向搜索实现。
- CycleCost.cs — 关节 PTP 节拍代价模型（焊序优化与捷径判据使用）。
- PlanningProgress.cs — 进度与阶段上报结构。
- AutoPathPlannerForm.cs / AutoPathPlannerCommand.cs — 与 UI 和命令绑定（界面交互逻辑）。
- WeldOrderOptimizer.cs — 焊序优化算法实现（2-opt / Or-opt）。

日志与诊断
- 插件大量输出可读日志（传入构造的 Action<string>），包含每段 L0/L1/L2 的判定、RRT 迭代、动态精修结果、捷径化统计、焊序变更提示和警告信息。
- PlanningReport 汇总统计项：插入 Via 数、RRT 调用/成功次数、失败段数、动态违例/修复数、碰撞查询次数、耗时、警告列表等。

性能与调优建议
- 碰撞查询是瓶颈：启用 QueryCacheEnabled（默认 true）并合理设置 CacheQuantum（默认 0.5 mm）能显著提速。
- RRT 参数（StepSize、MaxIterations、GoalBias）影响成功率与运行时间；在大场景或复杂夹具上可适当增加 MaxIterations 或调小 StepSize。
- SampleBoundsInflateXy/ZUp/ZDown 控制 RRT 采样体积，过大采样范围会降低命中率但可提高探索能力；可结合 relayPool（经验点）优化。
- 动态检测（DynamicCheckEnabled）能发现并避免枪体扫掠，但会增加关节读写/碰撞查询成本；在性能受限时可临时关闭以快速生成静态路径（需人工复核运动过程）。

常见问题与排查
- “未找到干涉集”：请先在主界面为该机器人创建/附着干涉集，插件不会偷偷创建新干涉集（避免堆积）。
- “自检(进枪点) 逆解摆位失败”：首焊点在其进枪位姿处逆解不可行，通常是机器人配置/夹具导致不可达，需要人工调整焊点姿态或检查机器人 POSE。
- “动态干涉(端点不可达)”：多数是进/出枪工艺点本身与夹具冲突，路径规划无法修复，应人工修改焊点姿态或夹具设计。
- “焊序已改变”提示：若启用焊序优化会修改操作内焊点顺序，务必确认工艺允许（可用 WeldOrderLockFirst/Last 锁定定位焊）。

开发者说明
- 该组件基于 Tecnomatix Process Simulate SDK（Tx* 类型）实现，需在相应环境中编译运行。
- 代码内已标注版本演进说明（v4.x → v6.x 等）与若干行为修复/性能改进的注释，便于维护与扩展。
- 如果要在无 UI 下集成：构造 WeldPathPlanner 时传入日志委托，设置必要的 public 字段与 Progress/IsCancelled，然后调用 ExecuteForOperations。

许可证与作者
- 仓库所有者：ShuiYinMaster（见仓库 README）。
- 本 README 仅为 AutoPath 模块说明；请参阅仓库根目录的 LICENSE / 总说明以获取许可与使用条款。

反馈
- 若发现行为异常或希望额外的日志/开关（例如更细粒度的 RRT 调试、并行化改进或更保守的捷径判据），请在仓库中提交 issue 或联系作者（ShuiYinMaster）。

--- 
感谢使用 AutoPath —— 目标是把“可用、可靠、节拍友好”的自动过渡点生成带到焊接工艺工程师的日常流程中。
