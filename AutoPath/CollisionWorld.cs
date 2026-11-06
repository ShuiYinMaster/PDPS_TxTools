using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Tecnomatix.Engineering;
using TxTools.RobotReachabilityChecker.Services;
using TxTools.RobotReachabilityChecker.Diagnostics;
using Tecnomatix.Engineering.Olp;

namespace TxTools.AutoPathPlanner
{
    /// <summary>
    /// 碰撞世界: 封装 "TCP摆位 → 干涉查询" 的状态判定。
    ///
    /// 摆位后端 (双后端，自检时鉴别选择):
    ///   A) CalcInverseSolutions(TxRobotInverseData) → CurrentPose  (既定模式)
    ///   B) 探针Via + robot.GetPoseAtLocation(via) → CurrentPose
    ///      (GetPoseAtLocation 是已验证的可达性信号，走PS内部完整链路，
    ///       可规避 Destination 坐标系约定/TCPF 等调用层面的差异)
    ///
    /// 姿态传递: 优先整矩阵复制 (排除 RPY 欧拉角往返编码歧义)。
    /// 规划前保存姿态，Dispose 时恢复并清理探针。
    /// </summary>
    public sealed class CollisionWorld : IDisposable
    {
        private TxRobotConfigurationData _requiredConfig;
        private bool _enforceConfiguration;
        private bool _solverFrameKnown;
        private Action _restoreOperationTool;
        private TxFrame _operationTool;

        private double[] _anchorJoints;
        private GunAxisService _activeGun;
        private double _searchOpening;
        public string LastPoseRejection { get; private set; }
        public Func<bool> ShouldAbortMotion;
        public double JointLimitMarginDegrees = 1;
        public double JointPlanningStepDegrees = 12, JointPlanningLinearStep = 25;
        public double WristSingularityMarginDegrees = 10;
        public double WristCenterPreferenceWeight = 2.0;

        private static bool SameConfiguration(TxRobotConfigurationData expected, TxRobotConfigurationData actual)
        {
            if (expected == null || actual == null) return false;
            if (expected.OverheadState != TxRobotConfigurationData.TxOverheadState.OverheadIrrelevant &&
                expected.OverheadState != actual.OverheadState) return false;
            var a = expected.JointsConfigurations;
            var b = actual.JointsConfigurations;
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i] as TxJointConfigurationData; var y = b[i] as TxJointConfigurationData;
                if (x == null || y == null) return false;
                if (x.State != TxJointConfigurationData.TxJointState.Irrelevant && x.State != y.State) return false;
                if (x.Turn != null && x.Turn.HasValue &&
                    (y.Turn == null || !y.Turn.HasValue || x.Turn.Value != y.Turn.Value)) return false;
            }
            return true;
        }

        private bool HasSixRevoluteAxes()
        {
            var joints = _robot.DrivingJoints;
            return joints != null && joints.Count == 6 &&
                joints.Cast<TxJoint>().All(j => j.Type == TxJoint.TxJointType.Revolute);
        }

        private bool SameArmConfiguration(TxRobotConfigurationData expected, TxRobotConfigurationData actual)
        {
            if (!HasSixRevoluteAxes()) return SameConfiguration(expected, actual);
            if (expected == null || actual == null) return false;
            if (expected.OverheadState != TxRobotConfigurationData.TxOverheadState.OverheadIrrelevant &&
                expected.OverheadState != actual.OverheadState) return false;
            var a = expected.JointsConfigurations; var b = actual.JointsConfigurations;
            if (a == null || b == null || a.Count != b.Count || a.Count < 3) return false;
            // Preserve shoulder/elbow and arm turns; wrist branch/turns are selectable.
            for (int i = 0; i < 3; i++)
            {
                var x = a[i] as TxJointConfigurationData; var y = b[i] as TxJointConfigurationData;
                if (x == null || y == null) return false;
                if (x.State != TxJointConfigurationData.TxJointState.Irrelevant && x.State != y.State) return false;
                if (x.Turn != null && x.Turn.HasValue &&
                    (y.Turn == null || !y.Turn.HasValue || x.Turn.Value != y.Turn.Value)) return false;
            }
            return true;
        }

        private static double WristCenterCost(double j4, double j6)
        {
            // Actual unwrapped radians, not normalized angles. Both axes matter.
            return Math.Abs(j4) + Math.Abs(j6) + Math.Max(Math.Abs(j4), Math.Abs(j6));
        }

        private static double NearestEquivalentToZero(double value, double lower, double upper)
        {
            const double turn = 2 * Math.PI;
            double first = Math.Ceiling((lower-value)/turn);
            double last = Math.Floor((upper-value)/turn);
            if (first > last) return value;
            double k = Math.Max(first, Math.Min(last, Math.Round(-value/turn)));
            return value + k*turn;
        }

        private TxPoseData CenterWristTurns(TxPoseData pose)
        {
            if (!HasSixRevoluteAxes()) return pose;
            var values = ReadJoints(pose);
            if (values == null || values.Length != 6) return pose;
            var centered = new ArrayList(pose.JointValues);
            foreach (int axis in new[] {3, 5})
            {
                var joint = (TxJoint)_robot.DrivingJoints[axis];
                double margin = JointLimitMarginDegrees * Math.PI/180;
                centered[axis] = NearestEquivalentToZero(values[axis],
                    joint.LowerSoftLimit+margin, joint.UpperSoftLimit-margin);
            }
            return new TxPoseData { JointValues = centered };
        }

        private bool IsHealthyPose(TxPoseData pose)
        {
            try
            {
                var values = ReadJoints(pose);
                var joints = _robot.DrivingJoints;
                if (values == null || joints == null || joints.Count != values.Length)
                    throw new InvalidOperationException("关节维度不可验证");
                var cfg = _robot.GetPoseConfiguration(pose);
                if (cfg == null || cfg.OverheadState == TxRobotConfigurationData.TxOverheadState.Overhead)
                    throw new InvalidOperationException("禁止 Overhead/OH+ 反折构型");
                for (int i = 0; i < values.Length; i++)
                {
                    var joint = joints[i] as TxJoint;
                    if (joint == null || double.IsNaN(values[i]) || double.IsInfinity(values[i]))
                        throw new InvalidOperationException("关节值无效");
                    double margin = joint.Type == TxJoint.TxJointType.Revolute
                        ? JointLimitMarginDegrees * Math.PI / 180 : 1.0;
                    if (values[i] <= joint.LowerSoftLimit + margin || values[i] >= joint.UpperSoftLimit - margin)
                        throw new InvalidOperationException("关节 " + (i+1) + " 进入软限位余量");
                }
                // Wrist singularity is a preference for joint/PTP planning, not an
                // automatic rejection of a finite, collision-free joint trajectory.
                LastPoseRejection = null;
                return true;
            }
            catch (Exception ex) { LastPoseRejection = ex.Message; return false; }
        }

        private double PosePreferenceScore(TxPoseData pose, double[] reference)
        {
            var values = ReadJoints(pose);
            double score = reference == null ? 0 : JointMaxDelta(values, reference);
            var joints = _robot.DrivingJoints;
            if (values != null && values.Length == 6 && joints.Count == 6 &&
                joints.Cast<TxJoint>().All(j => j.Type == TxJoint.TxJointType.Revolute))
            {
                score += WristCenterPreferenceWeight * WristCenterCost(values[3], values[5]);
                double wristDistance = Math.Abs(Math.Asin(Math.Sin(values[4])));
                score += 2 * Math.Max(0, WristSingularityMarginDegrees * Math.PI / 180 - wristDistance);
            }
            if (values != null)
                for (int i = 0; i < Math.Min(values.Length, joints.Count); i++)
                {
                    var joint = joints[i] as TxJoint;
                    if (joint == null || joint.Type != TxJoint.TxJointType.Revolute) continue;
                    double gap = Math.Min(values[i]-joint.LowerSoftLimit, joint.UpperSoftLimit-values[i]);
                    score += 2 * Math.Max(0, 5*Math.PI/180-gap);
                }
            return score;
        }

        public void ConfigureGun(GunAxisService gun, double opening)
        {
            _activeGun = gun;
            if (gun != null && gun.HasGunAxis)
            {
                var dev = gun.ActiveGun as ITxDevice;
                if (dev == null) throw new InvalidOperationException("活动焊钳设备不可用");
                if (!_savedToolPoses.Any(p => ((ITxObject)p.Key).Id == ((ITxObject)dev).Id))
                    _savedToolPoses.Add(new KeyValuePair<object, object>(dev, dev.CurrentPose));
            }
            SetSearchOpening(opening);
        }

        public double SearchOpening { get { return _searchOpening; } }

        public bool SetSearchOpening(double opening)
        {
            if (Math.Abs(_searchOpening - opening) > 1e-6) InvalidateCache();
            _searchOpening = opening;
            return _activeGun == null || !_activeGun.HasGunAxis || _activeGun.ApplyOpeningToDevice(opening);
        }

        private void SelectOperationTool(List<ITxRoboticLocationOperation> locations)
        {
            if (_restoreOperationTool != null) _restoreOperationTool();
            _restoreOperationTool = null;
            _operationTool = null;
            if (locations.Count == 0) return;
            var frames = locations.Select(loc => ToolFrameReader.ReadLocationToolFrame((ITxObject)loc)).ToList();
            var first = frames[0];
            if (frames.Any(f => (f == null) != (first == null) ||
                (f != null && first != null && f.Id != first.Id)))
                throw new InvalidOperationException("同一操作含不同 TCP 工具定义，需按工具拆分规划");
            if (first == null)
            {
                _log("  求解工具: 使用机器人当前默认 TCPF (源点无 RRS_TOOL_FRAME)");
                return;
            }
            string error;
            _restoreOperationTool = ToolFrameSwitcher.SwitchToFrame(_robot, first, NullLogger.Instance, out error);
            if (_restoreOperationTool == null)
                throw new InvalidOperationException("操作 TCP 切换失败: " + error);
            _operationTool = first;
            _log("  求解工具: " + first.Name + " [" + first.Id + "]");
        }

        private void SetLocationTool(ITxRoboticLocationOperation location)
        {
            if (_operationTool == null) return;
            ((ITxRoboticOperation)location).SetParameter(
                new TxRoboticTxObjectParam(ToolFrameReader.TYPE_RRS_TOOL_FRAME, _operationTool));
            var actual = ToolFrameReader.ReadLocationToolFrame((ITxObject)location);
            if (actual == null || actual.Id != _operationTool.Id)
                throw new InvalidOperationException("位置 TCP 参数读回不一致");
        }

        private bool MatchesTarget(TxPoseData pose, TxTransformation target)
        {
            try
            {
                var actual = _robot.GetTCPFByPoseData(pose);
                double positionError, rotationError;
                if (actual == null) throw new InvalidOperationException("正向运动学无结果");
                if (!TargetErrorsWithinTolerance(actual, target, out positionError, out rotationError))
                    throw new InvalidOperationException(string.Format(
                        "正解不匹配: 位置偏差 {0:F3}mm / 姿态偏差 {1:F3}°", positionError, rotationError));
                return true;
            }
            catch (Exception ex) { LastPoseRejection = ex.Message; return false; }
        }

        private static bool TargetErrorsWithinTolerance(TxTransformation actual, TxTransformation target,
            out double positionError, out double rotationError)
        {
            var a = actual.Translation; var b = target.Translation;
            double dx = a.X-b.X, dy = a.Y-b.Y, dz = a.Z-b.Z;
            positionError = Math.Sqrt(dx*dx + dy*dy + dz*dz);
            rotationError = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double dot = 0;
                for (int row = 0; row < 3; row++) dot += actual[row,axis]*target[row,axis];
                rotationError = Math.Max(rotationError, Math.Acos(Math.Max(-1, Math.Min(1, dot))) * 180 / Math.PI);
            }
            return !double.IsNaN(positionError) && !double.IsNaN(rotationError) &&
                positionError <= 1.0 && rotationError <= 0.5;
        }

        public void PrepareConfigurations(IEnumerable<ITxRoboticLocationOperation> locations)
        {
            var sourceLocations = locations.ToList();
            SelectOperationTool(sourceLocations);
            _requiredConfig = null;
            _enforceConfiguration = true;
            _anchorJoints = null;
            _solverFrameKnown = false;
            _useLocalFrame = false;
            _backend = PoseBackend.Inverse;
            var changed = new List<KeyValuePair<ITxRoboticLocationOperation, TxRobotConfigurationData>>();
            try
            {
                foreach (var loc in sourceLocations)
                {
                    TxRobotConfigurationData existing = null;
                    try { existing = loc.RobotConfigurationData; } catch (TxRobotConfigurationDoesNotExistException) { }
                    // Preserve arm configuration; compare wrist branches and record the selected full Config.
                    _requiredConfig = existing;
                    var target = ((ITxLocatableObject)loc).AbsoluteLocation;
                    if (!TryPoseTcpAt(target)) throw new InvalidOperationException(
                        "源点 Config/姿态不可用: " + ((ITxObject)loc).Name + " / " + LastPoseRejection);
                    var pose = _robot.CurrentPose;
                    var cfg = _robot.GetPoseConfiguration(pose);
                    if (existing == null || !SameConfiguration(existing, cfg))
                    {
                        changed.Add(new KeyValuePair<ITxRoboticLocationOperation, TxRobotConfigurationData>(loc, existing));
                        LockLocation(loc, pose);
                        var j = ReadJoints(pose);
                        if (HasSixRevoluteAxes() && j != null)
                            _log(string.Format("  Config 腕部优选 {0}: J4={1:F2}° / J5={2:F2}° / J6={3:F2}°",
                                ((ITxObject)loc).Name, j[3]*180/Math.PI, j[4]*180/Math.PI, j[5]*180/Math.PI));
                    }
                    if (_anchorJoints == null)
                    {
                        _anchorJoints = ReadJoints(pose);
                        _requiredConfig = cfg;
                    }
                }
                // Search uses a stable operation anchor, never the previous random sample.
                if (_anchorJoints != null)
                {
                    var anchor = new TxPoseData { JointValues = new ArrayList(_anchorJoints) };
                    _requiredConfig = _robot.GetPoseConfiguration(anchor);
                }
                _enforceConfiguration = false;
                InvalidateCache();
            }
            catch
            {
                _enforceConfiguration = false;
                foreach (var entry in changed) entry.Key.RobotConfigurationData = entry.Value;
                throw;
            }
        }

        public TxRobotConfigurationData LockLocation(ITxRoboticLocationOperation location, TxPoseData validatedPose)
        {
            if (validatedPose == null || !IsHealthyPose(validatedPose))
                throw new InvalidOperationException("位置姿态未验证: " + LastPoseRejection);
            var cfg = _robot.GetPoseConfiguration(validatedPose);
            SetLocationTool(location);
            location.RobotConfigurationData = cfg;
            var actual = _robot.GetPoseAtLocation(location);
            if (!SameConfiguration(cfg, _robot.GetPoseConfiguration(actual)) ||
                JointMaxDelta(ReadJoints(validatedPose), ReadJoints(actual)) > 1e-4 ||
                !IsHealthyPose(actual))
                throw new InvalidOperationException("Config 写入后关节解读回不一致");
            return cfg;
        }

        private enum PoseBackend { Inverse, ProbeLocation }

        private readonly TxRobot _robot;
        private readonly CollisionSetService _collisionSet;
        private readonly Action<string> _log;
        private TxPoseData _savedPose;
        // TxPoseData stores revolute joint values in radians (and prismatic
        // values in length units).  Keep the joint kind so UI thresholds,
        // which are expressed in degrees for revolute joints, can be applied
        // without silently under-sampling wrist rotations.
        private bool[] _jointIsRevolute = new bool[0];
        private int _diagBudget = 4;
        private PoseBackend _backend = PoseBackend.Inverse;

        // ---- 后端A坐标系约定 (参考可达性验证插件): 解算基于机器人基座局部坐标系 ----
        private TxTransformation _baseInv;  // 世界 → 基座局部 变换
        private bool _useLocalFrame; // world first; accept a convention only after forward validation

        // ---- 姿态引用 (整矩阵优先, RPY 兜底) ----
        private TxTransformation _orientRef;
        private TxVector _orientRpy;

        // ---- 探针 (后端B) ----
        private TxContinuousRoboticOperation _probeOp;
        private TxRoboticViaLocationOperation _probeVia;

        public int QueryCount { get; private set; }

        public CollisionWorld(TxRobot robot, CollisionSetService collisionSet, Action<string> log)
        {
            _robot = robot;
            _collisionSet = collisionSet;
            _log = log ?? delegate { };

            ReadJointKinds();

            try { _savedPose = _robot.CurrentPose; }
            catch (Exception ex) { _log("  [警告] 保存机器人当前姿态失败: " + ex.Message); }
        }

        private void ReadJointKinds()
        {
            try
            {
                var joints = _robot.DrivingJoints as IEnumerable;
                if (joints == null) return;
                var kinds = new List<bool>();
                foreach (object joint in joints)
                {
                    bool revolute = true; // robot axes are normally revolute;
                                         // fail closed if the SDK omits Type.
                    try
                    {
                        var typedJoint = joint as TxJoint;
                        if (typedJoint != null)
                            revolute = typedJoint.Type == TxJoint.TxJointType.Revolute;
                        else
                        {
                            object type = ((dynamic)joint).Type;
                            if (type != null)
                            {
                                string name = type.ToString();
                                revolute = name.EndsWith("Revolute",
                                    StringComparison.OrdinalIgnoreCase);
                            }
                        }
                    }
                    catch { }
                    kinds.Add(revolute);
                }
                _jointIsRevolute = kinds.ToArray();
            }
            catch { _jointIsRevolute = new bool[0]; }
        }

        /// <summary>设置后续检测所用姿态: 整矩阵优先, RPY 兜底 (单姿态模式)</summary>
        public void SetOrientation(TxTransformation fullTransform, TxVector rpyZyxFallback)
        {
            _blendActive = false;
            _orientRef = fullTransform;
            _orientRpy = rpyZyxFallback;
            if (_rpyConv == 0 && fullTransform != null)
                CalibrateRpyConvention(fullTransform);
            InvalidateCache();   // v6.3: 姿态上下文变了, 缓存结果不再适用
        }

        // ---- 姿态混合 (过渡段专用): 沿 start→goal 行进进度插值 A/B 姿态 ----
        // 起点处 = A 姿态 (出枪Via实际姿态), 终点处 = B 姿态 (进枪Via实际姿态),
        // 消除"解析时用自身姿态、检测时用对方姿态"的不一致。
        private bool _blendActive;
        private TxVector _blendFrom, _blendTo;
        private Vec3 _blendStart, _blendGoal;

        public void SetOrientationBlend(TxVector fromRpy, TxVector toRpy, Vec3 start, Vec3 goal)
        {
            _blendActive = true;
            _blendFrom = fromRpy != null ? fromRpy : toRpy;
            _blendTo = toRpy != null ? toRpy : fromRpy;
            _blendStart = start;
            _blendGoal = goal;
            InvalidateCache();   // v6.3
        }

        private double ProgressAlong(Vec3 p)
        {
            Vec3 d = _blendGoal - _blendStart;
            double len2 = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
            if (len2 < 1e-9) return 0;
            Vec3 v = p - _blendStart;
            double t = (v.X * d.X + v.Y * d.Y + v.Z * d.Z) / len2;
            return t < 0 ? 0 : (t > 1 ? 1 : t);
        }

        private static double LerpAngle(double a, double b, double t)
        {
            double diff = b - a;
            while (diff > Math.PI) diff -= 2 * Math.PI;
            while (diff < -Math.PI) diff += 2 * Math.PI;
            return a + diff * t;
        }

        // ================================================================
        //  姿态变体: 多方位欧拉角检查, 择优选取 (偏离额定姿态最小者优先)
        // ================================================================

        /// <summary>姿态变体开关 (基础姿态可行时零额外开销)</summary>
        public bool OrientationVariantsEnabled = true;

        /// <summary>单次查询最多尝试的变体数</summary>
        public int MaxVariantTries = 13;

        // 变体表: (rx, ry, rz) 枪局部系偏转, 弧度; 按偏离量升序 = 择优顺序
        private static readonly double[][] OrientVariants = BuildVariantTable();
        private int _lastGoodVariant; // 时间相干性: 相邻查询优先复用上次成功变体

        private static double[][] BuildVariantTable()
        {
            var list = new List<double[]> { new[] { 0.0, 0.0, 0.0 } };
            double D = Math.PI / 180.0;
            foreach (double s in new[] { 15, -15, 30, -30, 45, -45, 60, -60, 90, -90 })
                list.Add(new[] { 0, 0, s * D });                 // 绕枪Z自旋
            foreach (double t in new[] { 10, -10, 20, -20 })
                list.Add(new[] { t * D, 0, 0 });                 // 绕枪X倾角
            foreach (double t in new[] { 10, -10, 20, -20 })
                list.Add(new[] { 0, t * D, 0 });                 // 绕枪Y倾角
            return list.ToArray();
        }

        // ════════════════════════════════════════════════════════════
        //  v6.3 位姿查询缓存 —— 性能大头
        //
        //  实测: 碰撞查询 37000+ 次 / 410s ≈ 11ms/次, 这是**唯一**的真瓶颈。
        //  而 SDK 不是线程安全的 (机器人只有一个状态, 两个线程不能同时摆位),
        //  碰撞查询**无法并行**。所以提速只能靠"少查"。
        //
        //  阶梯搜索 (后撤 40/80/150/250/400/600...)、动态复验、捷径验证
        //  会大量重复查询同一个 (位置, 姿态上下文)。缓存命中直接返回。
        //
        //  Key 必须包含姿态上下文 —— SetOrientationBlend 改了混合状态后,
        //  同一个位置的结果会不同。
        // ════════════════════════════════════════════════════════════

        /// <summary>启用位姿查询缓存</summary>
        public bool CacheEnabled = true;

        /// <summary>缓存位置量化精度 (mm) — 小于此距离视为同一点</summary>
        public double CacheQuantum = 0.5;

        private readonly Dictionary<long, bool> _freeCache = new Dictionary<long, bool>();
        private int _cacheHits, _cacheMisses;
        private int _orientEpoch;   // 姿态上下文变更计数 — 变了就换 key 空间

        /// <summary>缓存命中率 (诊断用)</summary>
        public string CacheStats
        {
            get
            {
                int tot = _cacheHits + _cacheMisses;
                if (tot == 0) return "缓存未启用";
                return string.Format("缓存 {0}/{1} 命中 ({2:P0}), 省下 ~{3:F0}s",
                    _cacheHits, tot, (double)_cacheHits / tot, _cacheHits * 0.011);
            }
        }

        /// <summary>把位置 + 姿态上下文 打成缓存键</summary>
        private long CacheKey(Vec3 p)
        {
            double q = Math.Max(0.1, CacheQuantum);
            long x = (long)Math.Round(p.X / q);
            long y = (long)Math.Round(p.Y / q);
            long z = (long)Math.Round(p.Z / q);
            // 混入姿态上下文 epoch, 避免跨上下文误命中
            long h = _orientEpoch;
            h = h * 1000003 + x;
            h = h * 1000003 + y;
            h = h * 1000003 + z;
            return h;
        }

        /// <summary>RRT/L1 状态有效性判定: 任一姿态变体可摆位且无干涉 → true</summary>
        public bool IsPositionFree(Vec3 p)
        {
            if (!CacheEnabled)
            {
                TxVector d0;
                return TryFindFreePose(p, out d0);
            }

            long k = CacheKey(p);
            bool cached;
            if (_freeCache.TryGetValue(k, out cached))
            {
                _cacheHits++;
                return cached;
            }

            _cacheMisses++;
            TxVector dummy;
            bool r = TryFindFreePose(p, out dummy);
            _freeCache[k] = r;
            return r;
        }

        /// <summary>清空缓存 (姿态上下文变更时自动调用)</summary>
        public void InvalidateCache()
        {
            _orientEpoch++;
            if (_freeCache.Count > 200000) _freeCache.Clear();  // 防无限膨胀
        }

        public enum PositionState { Free, NoIk, Colliding }

        /// <summary>端点诊断 (变体感知): 全部变体无逆解→NoIk; 有变体到碰撞阶段→Colliding</summary>
        public PositionState Classify(Vec3 p)
        {
            TxVector dummy;
            bool anyIk;
            if (TryFindFreePoseCore(p, out dummy, out anyIk))
                return PositionState.Free;
            return anyIk ? PositionState.Colliding : PositionState.NoIk;
        }

        /// <summary>
        /// 严格分类: 只按基础姿态判定, 不启用变体。
        /// 用于进出枪点 — 工艺位置必须以焊点原姿态成立。
        /// </summary>
        public PositionState ClassifyStrict(Vec3 p)
        {
            bool prev = OrientationVariantsEnabled;
            OrientationVariantsEnabled = false;
            try { return Classify(p); }
            finally { OrientationVariantsEnabled = prev; }
        }

        /// <summary>
        /// 择优姿态搜索: 基础姿态(混合/单一)优先, 失败则按变体表逐个尝试,
        /// 返回实际采用的 RPY (供 Via 写入)。
        /// </summary>
        public bool TryFindFreePose(Vec3 p, out TxVector rpyUsed)
        {
            bool anyIk;
            return TryFindFreePoseCore(p, out rpyUsed, out anyIk);
        }

        private bool TryFindFreePoseCore(Vec3 p, out TxVector rpyUsed, out bool anyIk)
        {
            rpyUsed = null;
            anyIk = false;
            var trans = new TxVector(p.X, p.Y, p.Z);

            // ---- 变体0 (严格基础姿态): 走无RPY往返的原生路径 ----
            // 单姿态=整矩阵拷贝, 混合=直接设置SDK来源的RPY数值 — 与变体机制解耦,
            // 保证严格姿态判定与旧版行为逐位一致 (回归修复)。
            QueryCount++;
            TxTransformation basePose = BuildPose(p);
            if (TryPoseTcpAt(basePose))
            {
                anyIk = true;
                if (!_collisionSet.QueryColliding())
                {
                    _lastGoodVariant = 0;
                    try { rpyUsed = basePose.RotationRPY_ZYX; } catch { }
                    return true;
                }
            }

            // ---- 非恒等变体: 需要RPY↔矩阵数学, 依赖约定标定结果 ----
            if (!OrientationVariantsEnabled) return false;
            if (_rpyConv <= 0) return false; // 未标定成功 → 变体停用

            double[,] baseRot = BaseRotationAt(p);

            int tries = Math.Min(MaxVariantTries, OrientVariants.Length);
            for (int k = 1; k < tries; k++)
            {
                // 简化: 先试上次成功变体(时间相干), 然后按表序遍历(去重)
                int idx;
                if (k == 1 && _lastGoodVariant > 0 && _lastGoodVariant < OrientVariants.Length)
                    idx = _lastGoodVariant;
                else
                    idx = k;

                // 去重: 跳过已试过的(变体0已试, 上次成功变体在k=1已试)
                if (idx == 0) idx = k + 1;
                if (idx == _lastGoodVariant && k > 1) idx = k;
                if (idx <= 0 || idx >= OrientVariants.Length) continue;

                QueryCount++;
                var v = OrientVariants[idx];
                double[,] rot = MatMul3(baseRot, RotFromRpy(v[0], v[1], v[2])); // 局部系右乘
                TxVector rpy = RpyFromRot(rot);

                var pose = new TxTransformation();
                pose.Translation = trans;
                pose.RotationRPY_ZYX = rpy;

                if (!TryPoseTcpAt(pose)) continue;
                anyIk = true;
                if (_collisionSet.QueryColliding()) continue;

                _lastGoodVariant = idx;
                rpyUsed = rpy;
                return true;
            }
            return false;
        }

        /// <summary>当前模式下, 位置 p 处的基础旋转矩阵 (约定感知)</summary>
        private double[,] BaseRotationAt(Vec3 p)
        {
            if (_blendActive && _blendFrom != null && _blendTo != null)
            {
                double t = ProgressAlong(p);
                return RotFromRpy(
                    LerpAngle(_blendFrom.X, _blendTo.X, t),
                    LerpAngle(_blendFrom.Y, _blendTo.Y, t),
                    LerpAngle(_blendFrom.Z, _blendTo.Z, t));
            }
            if (_orientRef != null)
            {
                try { return ReadRot(_orientRef); } catch { }
            }
            if (_orientRpy != null)
                return RotFromRpy(_orientRpy.X, _orientRpy.Y, _orientRpy.Z);
            return new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        }

        // ================================================================
        //  RPY 约定运行时标定
        //  SDK RotationRPY_ZYX 的实际矩阵约定不可凭空假设 (上次回归的根因):
        //  读一个已知变换的 SDK RPY 与矩阵, 双约定比对, 匹配者胜出。
        // ================================================================
        private int _rpyConv; // 0=未标定 1=Rz·Ry·Rx 2=Rx·Ry·Rz -1=均不匹配(变体停用)

        private void CalibrateRpyConvention(TxTransformation sample)
        {
            try
            {
                TxVector rpy = sample.RotationRPY_ZYX;
                double[,] m = ReadRot(sample);

                if (MatClose(m, RotZyx(rpy.X, rpy.Y, rpy.Z)))
                {
                    _rpyConv = 1;
                    _log("  [标定] RPY_ZYX 约定 = Rz·Ry·Rx — 姿态变体启用");
                }
                else if (MatClose(m, RotXyz(rpy.X, rpy.Y, rpy.Z)))
                {
                    _rpyConv = 2;
                    _log("  [标定] RPY_ZYX 约定 = Rx·Ry·Rz — 姿态变体启用(已适配)");
                }
                else
                {
                    _rpyConv = -1;
                    _log("  [标定] RPY 约定与两种已知模式均不匹配 — 姿态变体停用, 仅用严格姿态");
                }
            }
            catch (Exception ex)
            {
                _rpyConv = -1;
                _log("  [标定] RPY 标定异常: " + ex.Message + " — 姿态变体停用");
            }
        }

        private static bool MatClose(double[,] a, double[,] b)
        {
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    if (Math.Abs(a[i, j] - b[i, j]) > 1e-3) return false;
            return true;
        }

        private double[,] RotFromRpy(double rx, double ry, double rz)
        {
            return _rpyConv == 2 ? RotXyz(rx, ry, rz) : RotZyx(rx, ry, rz);
        }

        private TxVector RpyFromRot(double[,] r)
        {
            return _rpyConv == 2 ? RpyFromRotXyz(r) : RotToRpyZyx(r);
        }

        /// <summary>R = Rx·Ry·Rz 约定下的矩阵构造</summary>
        private static double[,] RotXyz(double rx, double ry, double rz)
        {
            double cx = Math.Cos(rx), sx = Math.Sin(rx);
            double cy = Math.Cos(ry), sy = Math.Sin(ry);
            double cz = Math.Cos(rz), sz = Math.Sin(rz);
            return new double[3, 3]
            {
                { cy * cz,                 -cy * sz,                 sy      },
                { sx * sy * cz + cx * sz,  -sx * sy * sz + cx * cz,  -sx * cy },
                { -cx * sy * cz + sx * sz, cx * sy * sz + sx * cz,   cx * cy  }
            };
        }

        /// <summary>R = Rx·Ry·Rz 约定下的欧拉角提取</summary>
        private static TxVector RpyFromRotXyz(double[,] r)
        {
            double r02 = Math.Max(-1.0, Math.Min(1.0, r[0, 2]));
            double ry = Math.Asin(r02);
            double rx, rz;
            if (Math.Abs(Math.Cos(ry)) > 1e-6)
            {
                rx = Math.Atan2(-r[1, 2], r[2, 2]);
                rz = Math.Atan2(-r[0, 1], r[0, 0]);
            }
            else
            {
                rx = Math.Atan2(r[2, 1], r[1, 1]);
                rz = 0;
            }
            return new TxVector(rx, ry, rz);
        }

        /// <summary>RPY_ZYX (弧度) → 旋转矩阵, R = Rz·Ry·Rx (与 RotToRpyZyx 互逆)</summary>
        private static double[,] RotZyx(double rx, double ry, double rz)
        {
            double cx = Math.Cos(rx), sx = Math.Sin(rx);
            double cy = Math.Cos(ry), sy = Math.Sin(ry);
            double cz = Math.Cos(rz), sz = Math.Sin(rz);
            return new double[3, 3]
            {
                { cz * cy, cz * sy * sx - sz * cx, cz * sy * cx + sz * sx },
                { sz * cy, sz * sy * sx + cz * cx, sz * sy * cx - cz * sx },
                { -sy,     cy * sx,                cy * cx                }
            };
        }

        private static double[,] MatMul3(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 3; k++) s += a[i, k] * b[k, j];
                    r[i, j] = s;
                }
            return r;
        }

        /// <summary>
        /// 构造目标位姿: 混合模式按行进进度插值 A/B 姿态 (最短角);
        /// 单姿态模式复制引用变换的完整旋转，仅覆盖平移。
        /// </summary>
        private TxTransformation BuildPose(Vec3 p)
        {
            var trans = new TxVector(p.X, p.Y, p.Z);

            if (_blendActive && _blendFrom != null && _blendTo != null)
            {
                double t = ProgressAlong(p);
                var rpy = new TxVector(
                    LerpAngle(_blendFrom.X, _blendTo.X, t),
                    LerpAngle(_blendFrom.Y, _blendTo.Y, t),
                    LerpAngle(_blendFrom.Z, _blendTo.Z, t));
                var tb = new TxTransformation();
                tb.Translation = trans;
                tb.RotationRPY_ZYX = rpy;
                return tb;
            }

            if (_orientRef != null)
            {
                try
                {
                    var t = new TxTransformation(_orientRef); // 整矩阵复制
                    t.Translation = trans;
                    return t;
                }
                catch { /* 拷贝构造不可用 → RPY 路径 */ }
            }

            var t2 = new TxTransformation();
            t2.Translation = trans;
            if (_orientRpy != null)
                t2.RotationRPY_ZYX = _orientRpy;
            return t2;
        }

        // ================================================================
        //  三段式自检 / 鉴别诊断
        // ================================================================

        /// <summary>
        /// 规划前自检:
        ///   段1: 基座/TCPF/距离信息 (布局问题一眼可见)
        ///   段2: 后端A 在目标点摆位
        ///   段3: 后端A失败 → GetPoseAtLocation(参照焊点) 参照实验;
        ///        参照可达 → 切换后端B(探针)重测
        /// 返回 "通过" / "摆位成功但该位姿有干涉" / "逆解摆位失败"
        /// </summary>
        public string SelfTest(Vec3 p, TxVector rpyZyx, ITxObject referenceWeldLoc)
        {
            _blendActive = false;
            _orientRpy = rpyZyx;
            var target = BuildPose(p);
            if (!TryPoseTcpAt(target))
            {
                _log("  [自检] 目标完整位姿求解失败: " + LastPoseRejection);
                return "逆解摆位失败";
            }
            return _collisionSet.QueryColliding() ? "摆位成功但该位姿有干涉" : "通过";
        }

        // ================================================================
        //  摆位: 后端路由
        // ================================================================
        public bool TryPoseTcpAt(TxTransformation target)
        {
            if (ShouldAbortMotion != null && ShouldAbortMotion())
            { LastPoseRejection = "本段求解预算已到"; return false; }
            LastPoseRejection = null;
            bool ok;
            if (_backend == PoseBackend.ProbeLocation) ok = PoseViaProbe(target);
            else
            {
                ok = PoseViaInverse(target);
                if (!ok && !_solverFrameKnown)
                {
                    string firstFailure = LastPoseRejection;
                    _useLocalFrame = !_useLocalFrame;
                    ok = PoseViaInverse(target);
                    if (!ok)
                    {
                        LastPoseRejection = "世界坐标: " + firstFailure + "；基座局部坐标: " + LastPoseRejection;
                        _useLocalFrame = false;
                    }
                }
                if (!ok && !_solverFrameKnown)
                {
                    string inverseFailure = LastPoseRejection;
                    ok = PoseViaProbe(target) && MatchesTarget(_robot.CurrentPose, target);
                    if (ok)
                    {
                        _backend = PoseBackend.ProbeLocation;
                        _log("  求解后端: 原生位置探针，完整位姿正解核实通过");
                    }
                    else LastPoseRejection = inverseFailure + "；原生位置探针: " +
                        (LastPoseRejection ?? "无满足 Config/姿态约束的解");
                }
                if (ok && !_solverFrameKnown)
                {
                    _solverFrameKnown = true;
                    _log("  求解坐标已确认: " + (_backend == PoseBackend.ProbeLocation ? "原生位置" : _useLocalFrame ? "基座局部" : "世界") +
                        "，完整位姿正解误差≤1mm/0.5°，Config与姿态约束通过");
                }
            }
            if (!ok || !IsHealthyPose(_robot.CurrentPose)) return false;
            if (_activeGun != null && _activeGun.HasGunAxis &&
                !_activeGun.ApplyOpeningToDevice(_searchOpening))
            {
                LastPoseRejection = "活动焊钳开口应用/读回失败";
                return false;
            }
            // Recheck after gun actuation in case its motion changes the effective TCP.
            return MatchesTarget(_robot.CurrentPose, target);
        }

        // ---- 后端A: CalcInverseSolutions (Destination = 基座局部坐标) ----
        private bool PoseViaInverse(TxTransformation target)
        {
            string stage = "初始化";
            try
            {
                // 坐标系约定 (可达性验证插件的解算方式):
                // Destination 需给出基座局部坐标, 世界目标先经 baseInv 复合转换
                stage = "世界→局部坐标转换";
                TxTransformation dest = _useLocalFrame ? WorldToRobotLocal(target) : target;

                stage = "TxRobotInverseData构造";
                TxRobotInverseData inv = null;
                try
                {
                    inv = new TxRobotInverseData();
                    inv.Destination = dest;
                }
                catch (Exception ex1)
                {
                    Diag(stage + " 无参+属性路径失败: " + ex1.Message + " → 尝试构造器传参");
                    inv = (TxRobotInverseData)Activator.CreateInstance(
                        typeof(TxRobotInverseData), new object[] { dest });
                }
                // v6.5.1: InverseFullReach 既不是 TxRobotInverseData 上的布尔属性
                // (原来 inv.InverseFullReach = true 走 dynamic 静默失败),
                // 也不存在名为 TxRobotInverseType 的顶层类型 (CS0103)。
                //
                // 用反射: 找到 InverseType 属性 → 取它的枚举类型 → 按名字解析
                // "InverseFullReach" → 赋值。不硬编码枚举类型名, 版本兼容。
                inv.InverseType = TxRobotInverseData.TxInverseType.InverseFullReach;

                stage = "CalcInverseSolutions";
                ArrayList sols = _robot.CalcInverseSolutions(inv);
                if (sols == null || sols.Count == 0)
                {
                    LastPoseRejection = string.Format("InverseFullReach 无逆解 ({0}目标 {1:F0},{2:F0},{3:F0})",
                        _useLocalFrame ? "基座局部" : "世界",
                        dest.Translation.X, dest.Translation.Y, dest.Translation.Z);
                    Diag(LastPoseRejection);
                    return false;
                }

                stage = "构型及姿态约束选解";
                var reference = _anchorJoints ?? ReadJoints(_savedPose);
                var candidates = new List<TxPoseData>();
                foreach (var item in sols)
                {
                    var original = item as TxPoseData;
                    if (original == null) continue;
                    var centered = CenterWristTurns(original);
                    var variants = JointMaxDelta(ReadJoints(original), ReadJoints(centered)) < 1e-9
                        ? new[] { original } : new[] { original, centered };
                    foreach (var pose in variants)
                    {
                        if (!MatchesTarget(pose, target) || !IsHealthyPose(pose)) continue;
                        if (_enforceConfiguration && _requiredConfig != null &&
                            !SameArmConfiguration(_requiredConfig, _robot.GetPoseConfiguration(pose)))
                        { LastPoseRejection = "逆解与要求的肩肘/Overhead Config 不一致"; continue; }
                        candidates.Add(pose);
                    }
                }
                if (candidates.Count == 0)
                {
                    LastPoseRejection = LastPoseRejection ?? "无满足 Config 状态及圈数的健康逆解";
                    return false;
                }
                object chosen = candidates.OrderBy(p => PosePreferenceScore(p, reference)).First();

                stage = "解应用";
                return ApplySolution(chosen);
            }
            catch (Exception ex)
            {
                LastPoseRejection = "阶段[" + stage + "] " + ex.GetType().Name + ": " + ex.Message;
                Diag(string.Format("阶段[{0}] 异常: {1}: {2}",
                    stage, ex.GetType().Name, ex.Message));
                return false;
            }
        }

        /// <summary>世界坐标 → 机器人基座局部坐标 (localT = baseInv ∘ worldT)</summary>
        private TxTransformation WorldToRobotLocal(TxTransformation world)
        {
            _baseInv = ((ITxLocatableObject)_robot).AbsoluteLocation.Inverse;
            return Compose(_baseInv, world);
        }

        /// <summary>
        /// 变换复合 a∘b。候选: dynamic 运算符* / Multiply → 手动4x4复合兜底
        /// (手动路径只依赖索引器读 + Translation/RotationRPY_ZYX 写)
        /// </summary>
        private TxTransformation Compose(TxTransformation a, TxTransformation b)
        {
            try { return (TxTransformation)((dynamic)a * (dynamic)b); }
            catch { }
            try { return (TxTransformation)((dynamic)a).Multiply((dynamic)b); }
            catch { }

            // 手动复合: r = Ra·Rb, t = Ra·tb + ta
            try
            {
                double[,] ra = ReadRot(a); double[] ta = ReadTrans(a);
                double[,] rb = ReadRot(b); double[] tb = ReadTrans(b);

                var r = new double[3, 3];
                var t = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    t[i] = ta[i];
                    for (int k = 0; k < 3; k++) t[i] += ra[i, k] * tb[k];
                    for (int j = 0; j < 3; j++)
                    {
                        double s = 0;
                        for (int k = 0; k < 3; k++) s += ra[i, k] * rb[k, j];
                        r[i, j] = s;
                    }
                }

                var res = new TxTransformation();
                res.Translation = new TxVector(t[0], t[1], t[2]);
                res.RotationRPY_ZYX = RotToRpyZyx(r);
                return res;
            }
            catch (Exception ex)
            {
                Diag("变换复合全部失败: " + ex.Message + " — 退回世界坐标");
                return b;
            }
        }

        private static double[,] ReadRot(TxTransformation t)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i, j] = t[i, j];
            return r;
        }

        private static double[] ReadTrans(TxTransformation t)
        {
            return new[] { t.Translation.X, t.Translation.Y, t.Translation.Z };
        }

        /// <summary>旋转矩阵 → RPY_ZYX (弧度): R = Rz·Ry·Rx 分解</summary>
        private static TxVector RotToRpyZyx(double[,] r)
        {
            double r20 = Math.Max(-1.0, Math.Min(1.0, r[2, 0]));
            double ry = Math.Asin(-r20);
            double rx, rz;
            if (Math.Abs(Math.Cos(ry)) > 1e-6)
            {
                rx = Math.Atan2(r[2, 1], r[2, 2]);
                rz = Math.Atan2(r[1, 0], r[0, 0]);
            }
            else
            {
                rx = Math.Atan2(-r[1, 2], r[1, 1]);
                rz = 0;
            }
            return new TxVector(rx, ry, rz);
        }

        private bool ApplySolution(object sol)
        {
            var pose = sol as TxPoseData;
            if (pose != null)
            {
                _robot.CurrentPose = pose;
                _lastPoseJoints = ReadJoints(pose); // 供就近取解与动态检查
                return true;
            }

            Diag("解元素类型不是 TxPoseData: "
                + (sol == null ? "null" : sol.GetType().FullName));

            try
            {
                dynamic d = sol;
                object jv = d.JointValues;
                if (jv != null)
                {
                    var p = new TxPoseData();
                    ((dynamic)p).JointValues = (dynamic)jv;
                    _robot.CurrentPose = p;
                    Diag("候选2成功: 经由 JointValues 构造 TxPoseData");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Diag("候选2(JointValues) 失败: " + ex.Message);
            }

            if (sol != null && _diagBudget > 0)
            {
                try
                {
                    var sb = new System.Text.StringBuilder("解元素成员: ");
                    foreach (var prop in sol.GetType().GetProperties())
                        sb.Append(prop.PropertyType.Name).Append(" ").Append(prop.Name).Append("; ");
                    Diag(sb.ToString());
                }
                catch { }
            }
            return false;
        }

        // ---- 后端B: 探针Via + GetPoseAtLocation ----

        private bool EnsureProbe()
        {
            if (_probeVia != null) return true;
            try
            {
                var opData = new TxContinuousRoboticOperationCreationData("RRT_Probe_Tmp");
                _probeOp = TxApplication.ActiveDocument.OperationRoot
                    .CreateContinuousRoboticOperation(opData);
                try { _probeOp.Robot = _robot; } catch { }

                var viaData = new TxRoboticViaLocationOperationCreationData("RRT_Probe_Via");
                _probeVia = _probeOp.CreateRoboticViaLocationOperation(viaData);
                return _probeVia != null;
            }
            catch (Exception ex)
            {
                Diag("探针创建异常: " + ex.Message);
                return false;
            }
        }

        private bool PoseViaProbe(TxTransformation target)
        {
            try
            {
                if (!EnsureProbe()) { LastPoseRejection = "原生位置探针创建失败"; return false; }

                ((ITxLocatableObject)_probeVia).AbsoluteLocation = target;
                SetLocationTool(_probeVia);
                _probeVia.RobotConfigurationData = _enforceConfiguration ? _requiredConfig : null;

                // GetPoseAtLocation: 既定可达性信号，返回非null即可达
                // (实参转 dynamic — 让运行时类型参与重载决策)
                dynamic result = ((dynamic)_robot).GetPoseAtLocation((dynamic)_probeVia);
                if (result == null) { LastPoseRejection = "原生位置 GetPoseAtLocation 无解"; return false; }

                var pose = result as TxPoseData;
                if (pose != null)
                {
                    if (!MatchesTarget(pose, target) || !IsHealthyPose(pose)) return false;
                    if (_enforceConfiguration && _requiredConfig != null && !SameArmConfiguration(_requiredConfig, _robot.GetPoseConfiguration(pose)))
                    { LastPoseRejection = "原生位置解与要求的 Config 状态/圈数不一致"; return false; }
                    _robot.CurrentPose = pose;
                    return true;
                }

                LastPoseRejection = "GetPoseAtLocation 返回类型不是 TxPoseData: " + ((object)result).GetType().FullName;
                return false;
            }
            catch (Exception ex)
            {
                LastPoseRejection = "原生位置探针异常: " + ex.GetType().Name + ": " + ex.Message;
                Diag(LastPoseRejection);
                return false;
            }
        }

        private TxPoseData TryGetPoseAtLocation(ITxObject loc)
        {
            try
            {
                // 实参必须转 (dynamic): 静态类型 ITxObject 参与重载决策会报
                // RuntimeBinderException "best overloaded method match" (上次运行实证)
                dynamic result = ((dynamic)_robot).GetPoseAtLocation((dynamic)loc);
                if (result == null) return null;
                var pose = result as TxPoseData;
                if (pose == null)
                    Diag("参照实验: GetPoseAtLocation 返回类型 "
                        + ((object)result).GetType().FullName);
                return pose;
            }
            catch (Exception ex)
            {
                Diag("参照实验异常: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 焊枪张开 (RobotReachabilityChecker 既定经验: 碰撞查询前 ApplyGunOpening —
        /// 闭合枪臂在进枪点会真实夹住钣金造成恒碰撞)。
        /// 查找挂载工具上名为 OPEN/SEMIOPEN 的 Pose 并应用; 原姿态存档, Dispose 恢复。
        /// </summary>
        /// <summary>
        /// 从 PoseList 条目里取 TxPoseData。
        /// 条目类型未在文档中确认 —— 可能是 TxPose (带 .PoseData) 或 TxPoseData 本身。
        /// 这是一处**必要**的 dynamic 兜底。
        /// </summary>
        private static TxPoseData ExtractPoseData(object poseItem)
        {
            if (poseItem == null) return null;

            // ① 条目本身就是 TxPoseData
            var direct = poseItem as TxPoseData;
            if (direct != null) return direct;

            // ② 条目带 PoseData 属性
            try
            {
                var pd = ((dynamic)poseItem).PoseData as TxPoseData;
                if (pd != null) return pd;
            }
            catch { }

            return null;
        }

        /// <summary>在机器人自身POSE列表中精确匹配(区分大小写, Ordinal)并应用</summary>
        public bool TryApplyNamedPose(string poseName)
        {
            try
            {
                dynamic dev = _robot;
                IEnumerable poses = null;
                try { poses = dev.PoseList as IEnumerable; } catch { }
                if (poses == null) { try { poses = dev.Poses as IEnumerable; } catch { } }
                if (poses == null) { try { poses = dev.GetPoses() as IEnumerable; } catch { } }
                if (poses == null) return false;

                foreach (object p in poses)
                {
                    string name = "";
                    try { name = (string)((dynamic)p).Name; } catch { }
                    if (!string.Equals(name, poseName, StringComparison.Ordinal)) continue;

                    // v6.5: 删除不存在的 JumpToPose 分支
                    TxPoseData pd = ExtractPoseData(p);
                    if (pd == null) continue;
                    try { dev.CurrentPose = pd; return true; } catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>读取当前 TCPF 绝对位置与姿态</summary>
        public bool TryReadTcpPose(out Vec3 pos, out TxVector rpyZyx)
        {
            pos = default(Vec3);
            rpyZyx = null;
            try
            {
                dynamic tcpf = ((dynamic)_robot).TCPF;
                TxTransformation loc = tcpf.AbsoluteLocation;
                pos = new Vec3(loc.Translation.X, loc.Translation.Y, loc.Translation.Z);
                try { rpyZyx = loc.RotationRPY_ZYX; } catch { }
                return true;
            }
            catch { return false; }
        }

        private readonly List<KeyValuePair<object, object>> _savedToolPoses
            = new List<KeyValuePair<object, object>>();

        // ================================================================
        //  动态干涉检查 (关节扫掠)
        //  静态检查只验证离散摆位; 机器人在两点间按关节插值运动,
        //  肘/腕可能扫过障碍。这里在关节空间插值逐步直写 CurrentPose
        //  (无需逆解, 单步成本仅为一次干涉查询) 并检测构型突变。
        // ================================================================

        /// <summary>动态检查开关</summary>
        public bool DynamicCheckEnabled = true;

        /// <summary>关节步进量子(旋转轴为度, 移动轴为mm): 扫掠步数 = 关节最大跨度/此值</summary>
        public double DynamicJointQuantum = 4.0;

        /// <summary>
        /// v5.4 笛卡尔步进量子 (mm): 扫掠步数 = TCP位移/此值。
        /// 与关节判据取大者 —— 枪长, 腕部小角度旋转也会让枪尖扫过很远。
        /// </summary>
        public double DynamicCartesianQuantum = 15.0;

        /// <summary>v5.4 扫掠步数上限 (原硬编码 24 → 可调, 默认 64)</summary>
        public int MaxSweepSteps = 256;

        private int _dynCalibBudget = 2;

        /// <summary>构型突变阈值(旋转轴为度, 移动轴为mm): 相邻点跨度超此值判定构型翻转</summary>
        public double ConfigJumpThreshold = 120.0;

        private double[] _lastPoseJoints;
        private int _dynDiagBudget = 8;   // v5.2: 3→8, 容纳自检+备用路径诊断

        /// <summary>
        /// v5.4 测量两个关节姿态下 TCP 的笛卡尔距离 (用于自适应步数)。
        /// 摆到 a 读 TCP → 摆到 b 读 TCP → 求距离。失败返回 0 (退回纯关节判据)。
        /// </summary>
        private double TcpDistanceBetweenPoses(double[] ja, double[] jb, int n)
        {
            try
            {
                if (!ApplyJoints(ja)) return 0;
                Vec3 pa;
                if (!TryReadTcp(out pa)) return 0;

                if (!ApplyJoints(jb)) return 0;
                Vec3 pb;
                if (!TryReadTcp(out pb)) return 0;

                return Vec3.Distance(pa, pb);
            }
            catch { return 0; }
        }

        /// <summary>读取当前 TCPF 世界位置</summary>
        private bool TryReadTcp(out Vec3 p)
        {
            p = default(Vec3);
            try
            {
                var t = _robot.TCPF.AbsoluteLocation.Translation;
                p = new Vec3(t.X, t.Y, t.Z);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// v5.4 供 GunFrameSearch 使用的动态边验证:
        /// a→b 两点各自摆位 → 关节扫掠 → 全程无干涉返回 true。
        ///
        /// 这是把"枪体扫掠检测"提前到 L1 生成阶段的关键接口 ——
        /// IsEdgeFree 只采样 TCP 点位, 看不见枪体扫过夹具;
        /// 这个函数会把整个枪体的运动包络算进去。
        ///
        /// 注意: 调用前需已 SetOrientationBlend, 与 IsEdgeFree 语义保持一致。
        /// </summary>
        public bool IsEdgeSafeDynamic(Vec3 a, Vec3 b)
        {
            if (!DynamicCheckEnabled) return true;   // 动态不可用时不阻塞搜索
            try
            {
                TxPoseData ja, jb;
                if (!TryGetJointPoseAt(a, out ja)) return false;
                if (!TryGetJointPoseAt(b, out jb)) return false;
                return CheckJointMotion(ja, jb) == null;
            }
            catch { return false; }
        }

        /// <summary>
        /// v5.2 动态检查能力自检: 关节 读→写→读 往返验证。
        /// 在进入阶段2精修前调用一次, 确认 TxPoseData.JointValues 读写真的可用。
        /// 不可用则明确关闭 DynamicCheckEnabled 并告知用户 —— 而不是"看起来在跑
        /// 但每条边都静默当作通过"。
        ///
        /// 返回 true = 动态检查可用。
        /// </summary>
        public bool SelfTestJointIO()
        {
            try
            {
                TxPoseData cur = null;
                try { cur = _robot.CurrentPose; } catch { }
                if (cur == null)
                {
                    _log("    [动态自检] robot.CurrentPose 读取失败 — 动态检查停用");
                    DynamicCheckEnabled = false;
                    return false;
                }

                double[] j0 = ReadJoints(cur);
                if (j0 == null || j0.Length == 0)
                {
                    _log("    [动态自检] TxPoseData.JointValues 不可读 — 动态检查停用");
                    DynamicCheckEnabled = false;
                    return false;
                }

                // 写回原值 (无副作用的往返测试)
                if (!ApplyJoints(j0))
                {
                    _log("    [动态自检] 关节写入失败 — 动态检查停用");
                    DynamicCheckEnabled = false;
                    return false;
                }

                // 复读校验
                TxPoseData back = null;
                try { back = _robot.CurrentPose; } catch { }
                double[] j1 = ReadJoints(back);
                if (j1 == null || j1.Length != j0.Length)
                {
                    _log("    [动态自检] 关节复读不一致 — 动态检查停用");
                    DynamicCheckEnabled = false;
                    return false;
                }

                double maxDiff = 0;
                for (int i = 0; i < j0.Length; i++)
                    maxDiff = Math.Max(maxDiff, Math.Abs(j1[i] - j0[i]));

                if (maxDiff > 0.5)
                {
                    _log(string.Format(
                        "    [动态自检] 关节写入未生效 (读回偏差 {0:F2}) — 动态检查停用", maxDiff));
                    DynamicCheckEnabled = false;
                    return false;
                }

                _log(string.Format("    [动态自检] 关节读写往返 OK ({0} 轴, 偏差 {1:F3}) — 动态检查可用",
                    j0.Length, maxDiff));
                return true;
            }
            catch (Exception ex)
            {
                _log("    [动态自检] 异常: " + ex.Message + " — 动态检查停用");
                DynamicCheckEnabled = false;
                return false;
            }
        }

        /// <summary>
        /// 按节点自身最终姿态精确摆位并捕获关节 (阶段2逐帧精修用):
        /// 无变体、无混合 — 姿态即 Via 将持有的确切姿态。
        /// </summary>
        /// <summary>Joint-space adapter. Robot and external axes are explicit state;
        /// the same swept-motion validator is used in search and final verification.</summary>
        public sealed class JointSearchSpace : IDisposable
        {
            private readonly CollisionWorld _world;
            private readonly int _robotCount;
            private readonly List<TxRobotExternalAxisData> _axes = new List<TxRobotExternalAxisData>();
            private readonly Dictionary<TxJoint, double> _savedAxes = new Dictionary<TxJoint, double>();
            private readonly TxPoseData _savedRobot;
            public double[] Start, Goal, Lower, Upper, Scale;
            public string LastMotionIssue;
            public double LastViolation = -1;

            private static string Key(TxRobotExternalAxisData a)
            { return a.Device.Id + "|" + a.Joint.Id; }

            public JointSearchSpace(CollisionWorld world, TxPoseData start, TxPoseData goal,
                TxRobotExternalAxisData[] from, TxRobotExternalAxisData[] to)
            {
                _world = world; _savedRobot = world.SnapshotCurrentPose();
                var a = world.ReadJoints(start); var b = world.ReadJoints(goal);
                if (a == null || b == null || a.Length != b.Length)
                    throw new InvalidOperationException("关节端点维度不一致");
                _robotCount = a.Length;
                var first = new Dictionary<string, TxRobotExternalAxisData>(StringComparer.Ordinal);
                var last = new Dictionary<string, TxRobotExternalAxisData>(StringComparer.Ordinal);
                foreach (var item in from ?? new TxRobotExternalAxisData[0])
                {
                    if (item == null || item.Joint == null || item.Device == null)
                        throw new InvalidOperationException("起点外部轴定义不完整");
                    first.Add(Key(item), item);
                }
                foreach (var item in to ?? new TxRobotExternalAxisData[0])
                {
                    if (item == null || item.Joint == null || item.Device == null)
                        throw new InvalidOperationException("终点外部轴定义不完整");
                    last.Add(Key(item), item);
                }
                if (first.Count != last.Count || first.Keys.Any(k => !last.ContainsKey(k)))
                    throw new InvalidOperationException("两端外部轴集合不同，拒绝猜测设备状态");
                foreach (var key in first.Keys.OrderBy(k => k, StringComparer.Ordinal)) _axes.Add(first[key]);
                int n = _robotCount + _axes.Count;
                Start = new double[n]; Goal = new double[n]; Lower = new double[n]; Upper = new double[n]; Scale = new double[n];
                var joints = world._robot.DrivingJoints;
                if (joints.Count != _robotCount) throw new InvalidOperationException("机器人驱动轴维度不一致");
                for (int i = 0; i < n; i++)
                {
                    var joint = i < _robotCount ? (TxJoint)joints[i] : _axes[i-_robotCount].Joint;
                    bool rotary = joint.Type == TxJoint.TxJointType.Revolute;
                    double margin = i < _robotCount ? (rotary ? world.JointLimitMarginDegrees*Math.PI/180 : 1.0) : 0;
                    Lower[i] = joint.LowerSoftLimit + margin;
                    Upper[i] = joint.UpperSoftLimit - margin;
                    Scale[i] = rotary ? world.JointPlanningStepDegrees*Math.PI/180 : world.JointPlanningLinearStep;
                    Start[i] = i < _robotCount ? a[i] : first[Key(_axes[i-_robotCount])].JointValue;
                    Goal[i] = i < _robotCount ? b[i] : last[Key(_axes[i-_robotCount])].JointValue;
                    if (i >= _robotCount) _savedAxes[joint] = joint.CurrentValue;
                }
            }

            private TxPoseData RobotPose(double[] state)
            { return new TxPoseData { JointValues = new ArrayList(state.Take(_robotCount).ToArray()) }; }

            private bool ApplyExternal(double[] state)
            {
                for (int i = 0; i < _axes.Count; i++)
                {
                    double value = state[_robotCount+i];
                    _axes[i].Joint.CurrentValue = value;
                    if (Math.Abs(_axes[i].Joint.CurrentValue-value) > 1e-4) return false;
                }
                return true;
            }

            private bool Apply(double[] state)
            {
                if (state == null || state.Length != Lower.Length) return false;
                for (int i = 0; i < state.Length; i++)
                    if (double.IsNaN(state[i]) || double.IsInfinity(state[i]) ||
                        state[i] < Lower[i] || state[i] > Upper[i]) return false;
                _world._robot.CurrentPose = RobotPose(state);
                if (!ApplyExternal(state)) return false;
                return _world.JointMaxDeltaForState(RobotPose(state)) < 1e-4;
            }

            public bool EndpointMatches(double[] state, Vec3 position, TxVector rotation)
            {
                if (!Apply(state)) return false;
                var target = new TxTransformation();
                target.Translation = new TxVector(position.X, position.Y, position.Z);
                if (rotation != null) target.RotationRPY_ZYX = rotation;
                return _world.MatchesTarget(_world._robot.CurrentPose, target);
            }

            public bool IsFree(double[] state)
            {
                if (_world.ShouldAbortMotion != null && _world.ShouldAbortMotion()) return false;
                if (!Apply(state) || !_world.IsHealthyPose(_world._robot.CurrentPose)) return false;
                _world.QueryCount++;
                return !_world._collisionSet.QueryColliding();
            }

            public bool IsMotionFree(double[] a, double[] b)
            {
                if (!Apply(a)) return false;
                var previous = _world._applyExternalAt;
                int previousSteps = _world._externalSteps;
                bool previousDynamic = _world.DynamicCheckEnabled;
                try
                {
                    int steps = 0;
                    for (int i = 0; i < _axes.Count; i++)
                    {
                        double quantum = _axes[i].Joint.Type == TxJoint.TxJointType.Revolute
                            ? Math.PI/180 : 1.0;
                        steps = Math.Max(steps, (int)Math.Ceiling(Math.Abs(b[_robotCount+i]-a[_robotCount+i])/quantum));
                    }
                    _world._externalSteps = steps;
                    _world._applyExternalAt = t =>
                    {
                        var q = (double[])a.Clone();
                        for (int i = _robotCount; i < q.Length; i++) q[i] += t*(b[i]-a[i]);
                        return ApplyExternal(q);
                    };
                    _world.DynamicCheckEnabled = true;
                    double violation;
                    LastMotionIssue = _world.CheckJointMotion(RobotPose(a), RobotPose(b), out violation);
                    LastViolation = violation;
                    return LastMotionIssue == null;
                }
                finally
                {
                    _world._applyExternalAt = previous;
                    _world._externalSteps = previousSteps;
                    _world.DynamicCheckEnabled = previousDynamic;
                }
            }

            public PlanPoint Waypoint(double[] state, string source)
            {
                if (!Apply(state) || !_world.IsHealthyPose(_world._robot.CurrentPose))
                    throw new InvalidOperationException("关节节点回放失败");
                var transform = new TxTransformation(_world._robot.TCPF.AbsoluteLocation);
                var data = new List<TxRobotExternalAxisData>();
                double opening = _world.SearchOpening;
                for (int i = 0; i < _axes.Count; i++)
                {
                    var axis = _axes[i];
                    data.Add(new TxRobotExternalAxisData { Device = axis.Device, Joint = axis.Joint, JointValue = state[_robotCount+i] });
                    if (_world._activeGun != null && _world._activeGun.ActiveGun != null &&
                        axis.Device.Id == _world._activeGun.ActiveGun.Id)
                        opening = _world._activeGun.JointValueToOpening(state[_robotCount+i]);
                }
                return new PlanPoint {
                    Position = new Vec3(transform.Translation.X, transform.Translation.Y, transform.Translation.Z),
                    AbsTransform = transform, RpyZyx = transform.RotationRPY_ZYX,
                    JointPose = RobotPose(state), Configuration = _world._robot.GetPoseConfiguration(RobotPose(state)),
                    ExternalAxes = data.ToArray(), DepartureExternalAxes = data.ToArray(), GunOpening = opening,
                    Kind = PlanPointKind.Transit, Source = source, Note = "关节空间 RRT-Connect", Verified = true
                };
            }

            public void Dispose()
            {
                foreach (var item in _savedAxes)
                    try { item.Key.CurrentValue = item.Value; }
                    catch (Exception ex) { _world._log("  [警告] 恢复外部轴失败: " + ex.Message); }
                try { _world._robot.CurrentPose = _savedRobot; }
                finally { _world.InvalidateCache(); }
            }
        }

        private double JointMaxDeltaForState(TxPoseData expected)
        { return JointMaxDelta(ReadJoints(expected), ReadJoints(_robot.CurrentPose)); }

        public void SetJointReference(TxPoseData pose)
        {
            var values = ReadJoints(pose);
            if (values != null && JointMaxDelta(values, _anchorJoints) > 1e-8)
            {
                _anchorJoints = values;
                InvalidateCache();
            }
        }

        public TxPoseData SnapshotCurrentPose()
        {
            var values = _robot.CurrentPose.JointValues;
            if (values == null) throw new InvalidOperationException("当前关节姿态不可读");
            return new TxPoseData { JointValues = new ArrayList(values) };
        }

        public TxPoseData ReadLocationJointPose(ITxRoboticLocationOperation location)
        {
            var pose = _robot.GetPoseAtLocation(location);
            if (pose == null || pose.JointValues == null)
                throw new InvalidOperationException("已有 home 的关节姿态不可读");
            return new TxPoseData { JointValues = new ArrayList(pose.JointValues) };
        }

        public bool TryCaptureJointPose(Vec3 p, TxVector rpyZyx, out TxPoseData jointPose,
            TxPoseData preferredPose = null)
        {
            jointPose = null;
            QueryCount++;
            var target = new TxTransformation();
            target.Translation = new TxVector(p.X, p.Y, p.Z);
            if (rpyZyx != null) target.RotationRPY_ZYX = rpyZyx;
            if (preferredPose == null)
            {
                if (!TryPoseTcpAt(target)) return false;
            }
            else
            {
                // A named joint pose owns its Config. Do not re-solve it in the weld branch.
                if (!IsHealthyPose(preferredPose)) return false;
                _robot.CurrentPose = new TxPoseData { JointValues = new ArrayList(preferredPose.JointValues) };
                if (_activeGun != null && _activeGun.HasGunAxis &&
                    !_activeGun.ApplyOpeningToDevice(_searchOpening))
                { LastPoseRejection = "活动焊钳开口应用/读回失败"; return false; }
                if (JointMaxDelta(ReadJoints(preferredPose), ReadJoints(_robot.CurrentPose)) > 1e-4)
                { LastPoseRejection = "home 关节姿态写入后读回不一致"; return false; }
                if (!MatchesTarget(_robot.CurrentPose, target)) return false;
            }
            if (_collisionSet.QueryColliding())
            {
                LastPoseRejection = "碰撞: " + _collisionSet.DescribeFreshCollisions(4);
                return false;
            }
            jointPose = SnapshotCurrentPose();
            LastPoseRejection = null;
            return true;
        }

        /// <summary>在 p 处摆位(含变体)并捕获关节姿态</summary>
        public bool TryGetJointPoseAt(Vec3 p, out TxPoseData jointPose)
        {
            jointPose = null;
            TxVector rpy;
            if (!TryFindFreePose(p, out rpy)) return false;
            try { jointPose = _robot.CurrentPose; } catch { }
            return jointPose != null;
        }

        /// <summary>关节扫掠检查 (忽略违例位置的便捷重载)</summary>
        public string CheckJointMotion(TxPoseData a, TxPoseData b)
        {
            double t;
            return CheckJointMotion(a, b, out t);
        }

        /// <summary>
        /// 关节扫掠检查: a→b 关节线性插值逐步查干涉。
        /// 步数按关节跨度自适应 (跨度大→步数密)。
        /// 返回 null=通过; violationT=违例进度(构型突变为-1); 消息附涉事对象。
        /// </summary>
        public string CheckJointMotion(TxPoseData a, TxPoseData b, out double violationT)
        {
            TxPoseData saved = _robot.CurrentPose;
            try { return CheckJointMotionCore(a, b, out violationT); }
            finally { _robot.CurrentPose = saved; }
        }

        private Func<double, bool> _applyExternalAt;
        private int _externalSteps;

        public string CheckWrittenMotion(ITxRoboticLocationOperation from, ITxRoboticLocationOperation to)
        {
            var saved = new Dictionary<TxJoint, double>();
            TxPoseData robotSaved = _robot.CurrentPose;
            bool dynamicBefore = DynamicCheckEnabled;
            DynamicCheckEnabled = true;
            try
            {
                using (var service = new TxPathPlanningRCSService())
                    if (service.GetLocationMotionType((ITxRoboticOperation)to) != TxMotionType.Joint)
                        return "非Joint运动，关节插值不能作为实际轨迹终验";
                if (from.RobotConfigurationData == null || to.RobotConfigurationData == null)
                    return "位置缺少 Config(未验证)";
                var start = from.RobotDepartureExternalAxesData;
                if (start == null || start.Length == 0) start = from.RobotExternalAxesData;
                var end = to.RobotExternalAxesData;
                var av = new Dictionary<TxJoint, double>();
                var bv = new Dictionary<TxJoint, double>();
                foreach (var item in start ?? new TxRobotExternalAxisData[0])
                    if (item.Joint != null) av[item.Joint] = item.JointValue;
                foreach (var item in end ?? new TxRobotExternalAxisData[0])
                    if (item.Joint != null) bv[item.Joint] = item.JointValue;
                foreach (var joint in av.Keys)
                    if (!bv.ContainsKey(joint)) return "离开/到达外部轴不完整(未验证)";
                foreach (var joint in bv.Keys)
                {
                    if (!av.ContainsKey(joint)) return "离开/到达外部轴不完整(未验证)";
                    saved[joint] = joint.CurrentValue;
                    _externalSteps = Math.Max(_externalSteps,
                        (int)Math.Ceiling(Math.Abs(bv[joint] - av[joint]) / 1.0));
                }
                _applyExternalAt = t =>
                {
                    foreach (var joint in av.Keys)
                    {
                        double value = av[joint] + (bv[joint] - av[joint]) * t;
                        joint.CurrentValue = value;
                        if (Math.Abs(joint.CurrentValue - value) > 1e-4) return false;
                    }
                    return true;
                };
                if (!_applyExternalAt(0)) return "外部轴起点设置失败";
                var a = _robot.GetPoseAtLocation(from);
                if (!_applyExternalAt(1)) return "外部轴终点设置失败";
                var b = _robot.GetPoseAtLocation(to);
                double violation;
                return CheckJointMotion(a, b, out violation);
            }
            catch (Exception ex) { return "写入路径终验失败: " + ex.Message; }
            finally
            {
                DynamicCheckEnabled = dynamicBefore;
                _applyExternalAt = null;
                _externalSteps = 0;
                foreach (var pair in saved) pair.Key.CurrentValue = pair.Value;
                _robot.CurrentPose = robotSaved;
                InvalidateCache();
            }
        }

        private string CheckJointMotionCore(TxPoseData a, TxPoseData b, out double violationT)
        {
            violationT = -1;
            if (!IsHealthyPose(a) || !IsHealthyPose(b))
                return "端点异常姿态: " + LastPoseRejection;

            double[] ja = ReadJoints(a);
            double[] jb = ReadJoints(b);
            if (ja == null || jb == null)
            {
                // v5.2: 不再静默降级为"通过" —— 关节读不出来是能力缺失, 必须暴露。
                // 返回非 null 让上层走修复/警告路径, 而不是误以为这条边安全。
                DynDiag("关节值不可读 (TxPoseData.JointValues) — 该边无法做扫掠验证");
                return "关节不可读(未验证)";
            }

            if (ja.Length != jb.Length || ja.Length == 0) return "关节维度不一致(未验证)";
            int n = ja.Length;
            double maxDelta = 0;
            for (int i = 0; i < n; i++)
            {
                double delta = Math.Abs(jb[i] - ja[i]);
                // If the SDK does not expose the joint table, conservatively
                // assume a rotational robot axis so we do not under-sample.
                if (i >= _jointIsRevolute.Length || _jointIsRevolute[i])
                    delta *= 180.0 / Math.PI; // SDK rotational values are radians
                maxDelta = Math.Max(maxDelta, delta);
            }

            // Large travel is not itself a configuration jump. Validate the full
            // unwrapped joint motion; limits, forbidden posture and collision still apply.
            if (maxDelta > ConfigJumpThreshold)
                DynDiag(string.Format("大关节行程 {0:F0}，执行完整扫掠验证", maxDelta));

            // ---- v5.4 自适应步数: 关节角判据 + 笛卡尔判据, 取大者 ----
            // 原来只按关节角 (maxDelta / 4°, 上限 24 步)。问题: 枪很长, 腕部转 5°
            // 枪尖可能扫过 100mm —— 5° 一步足以让枪尖穿过一整块夹具板而不被采样到。
            // 现在同时按 TCP 笛卡尔位移算一遍 (每 DynamicCartesianQuantum mm 一步),
            // 取两者较大值, 上限从 24 放宽到 64。
            int stepsJoint = 0;
            for (int i = 0; i < n; i++)
            {
                double delta = Math.Abs(jb[i] - ja[i]);
                if (i >= _jointIsRevolute.Length || _jointIsRevolute[i])
                    delta *= 180.0 / Math.PI;
                stepsJoint = Math.Max(stepsJoint,
                    (int)Math.Ceiling(delta / Math.Max(0.01, DynamicJointQuantum)));
            }

            int stepsCart = 0;
            double cartDist = TcpDistanceBetweenPoses(ja, jb, n);
            if (cartDist > 0)
                stepsCart = (int)Math.Ceiling(cartDist / Math.Max(1.0, DynamicCartesianQuantum));

            // 不以采样上限降低精度；超预算明确标记未验证。
            int steps = Math.Max(6, Math.Max(_externalSteps, Math.Max(stepsJoint, stepsCart)));
            if (steps > Math.Max(6, MaxSweepSteps)) return "扫掠采样预算不足(未验证)";

            if (_dynCalibBudget > 0)
            {
                _dynCalibBudget--;
                DynDiag(string.Format(
                    "关节跨度 {0:F1}° (→{1}步) / TCP位移 {2:F0}mm (→{3}步) ⇒ 扫掠 {4} 步",
                    maxDelta, stepsJoint, cartDist, stepsCart, steps));
            }

            var interp = new double[n];
            for (int s = 0; s <= steps; s++)
            {
                if (ShouldAbortMotion != null && ShouldAbortMotion())
                    return "本段运动验证预算已到(未验证)";
                double t = (double)s / steps;
                for (int i = 0; i < n; i++)
                    interp[i] = ja[i] + (jb[i] - ja[i]) * t;

                // v5.2: 写入失败同样不能当"通过"
                if (!ApplyJoints(interp))
                {
                    violationT = t;
                    return "关节写入失败(未验证)";
                }
                if (!IsHealthyPose(_robot.CurrentPose))
                {
                    violationT = t;
                    return "异常姿态@" + t.ToString("P0") + ": " + LastPoseRejection;
                }
                if (_applyExternalAt != null && !_applyExternalAt(t))
                    return "外部轴写入失败(未验证)";
                QueryCount++;
                if (DynamicCheckEnabled && _collisionSet.QueryColliding())
                {
                    violationT = t;
                    string who = "";
                    try
                    {
                        string d = _collisionSet.DescribeFreshCollisions();
                        if (!string.IsNullOrEmpty(d)) who = " [" + d + "]";
                    }
                    catch { }
                    return string.Format("扫掠干涉@{0:P0}{1}", t, who);
                }
            }
            return null;
        }

        private double[] ReadJoints(TxPoseData p)
        {
            if (p == null) return null;
            try
            {
                var en = ((dynamic)p).JointValues as IEnumerable;
                if (en == null) return null;
                var list = new List<double>();
                foreach (object v in en) list.Add(Convert.ToDouble(v));
                return list.Count > 0 ? list.ToArray() : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 关节直写。v5.2:
        ///  - 失败不再全局停用 DynamicCheckEnabled (原行为: 一次失败 → 整个规划的
        ///    动态检查永久关闭, 且静默当作"全部通过", 极其危险)
        ///  - 多路径尝试: TxPoseData.JointValues (ArrayList) → SetJointValue 逐轴
        /// </summary>
        /// <summary>
        /// 关节直写。
        /// v6.5: TxPoseData.JointValues 是 ArrayList (强类型确认), 直接赋值。
        ///       删除了"SetJointValue 逐轴"备用路径 —— TxPoseData 上**没有这个方法**,
        ///       那段是死代码 (只会抛异常)。
        /// 失败不停用 DynamicCheckEnabled (原行为: 一次失败 → 整个规划的动态检查
        /// 永久关闭且静默当作"全部通过", 极其危险), 由调用方按"未验证"处理。
        /// </summary>
        private bool ApplyJoints(double[] joints)
        {
            try
            {
                var al = new ArrayList();
                foreach (double j in joints) al.Add(j);

                var pd = new TxPoseData();
                pd.JointValues = al;
                _robot.CurrentPose = pd;
                return JointMaxDelta(joints, ReadJoints(_robot.CurrentPose)) <= 1e-4;
            }
            catch (Exception ex)
            {
                DynDiag("关节直写失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 设置 IK 为"全域求解"模式。
        ///
        /// v6.5.1: 两次踩坑后改用反射 ——
        ///   ✗ inv.InverseFullReach = true          (不存在该布尔属性, dynamic 静默失败)
        ///   ✗ inv.InverseType = TxRobotInverseType.InverseFullReach  (CS0103: 无此类型)
        ///   ✓ 反射找 InverseType 属性 → 取其枚举类型 → 按名解析 → 赋值
        ///
        /// 首次调用会 dump 出该枚举的所有真实取值, 便于确认。
        /// </summary>
        private void TrySetInverseFullReach(TxRobotInverseData inv)
        {
            if (inv == null) return;
            try
            {
                var pi = inv.GetType().GetProperty("InverseType");
                if (pi == null)
                {
                    if (_invTypeDiagBudget-- > 0)
                        Diag("TxRobotInverseData 无 InverseType 属性 — IK 用默认模式");
                    return;
                }

                Type enumType = pi.PropertyType;
                if (!enumType.IsEnum)
                {
                    if (_invTypeDiagBudget-- > 0)
                        Diag("InverseType 不是枚举 (" + enumType.Name + ") — 跳过");
                    return;
                }

                // 首次: dump 真实取值
                if (_invTypeDiagBudget > 0)
                {
                    _invTypeDiagBudget--;
                    try
                    {
                        Diag(string.Format("IK InverseType 枚举 ({0}): {1}",
                            enumType.Name, string.Join(", ", Enum.GetNames(enumType))));
                    }
                    catch { }
                }

                // 按名解析 (兼容几种可能的命名)
                foreach (string want in new[]
                    { "InverseFullReach", "FullReach", "Full", "InverseFull" })
                {
                    if (!Enum.IsDefined(enumType, want)) continue;
                    object val = Enum.Parse(enumType, want);
                    pi.SetValue(inv, val, null);
                    return;
                }

                if (_invTypeDiagBudget-- > 0)
                    Diag("InverseType 无 FullReach 类取值 — 保持默认");
            }
            catch (Exception ex)
            {
                if (_invTypeDiagBudget-- > 0)
                    Diag("InverseType 设置失败: " + ex.Message);
            }
        }

        private int _invTypeDiagBudget = 2;

        private static double JointMaxDelta(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return double.PositiveInfinity;
            int n = a.Length;
            double m = 0;
            for (int i = 0; i < n; i++)
                m = Math.Max(m, Math.Abs(a[i] - b[i]));
            return m;
        }

        private void DynDiag(string msg)
        {
            if (_dynDiagBudget <= 0) return;
            _dynDiagBudget--;
            _log("    [动态诊断] " + msg);
        }

        private void Diag(string msg)
        {
            if (_diagBudget <= 0) return;
            _diagBudget--;
            _log("    [诊断] " + msg);
        }

        public void Dispose()
        {
            try { if (_restoreOperationTool != null) _restoreOperationTool(); }
            catch (Exception ex) { _log("  [警告] 恢复操作 TCP 失败: " + ex.Message); }
            _restoreOperationTool = null;
            // 恢复焊枪原姿态
            foreach (var kv in _savedToolPoses)
            {
                try { ((dynamic)kv.Key).CurrentPose = (dynamic)kv.Value; }
                catch { }
            }
            _savedToolPoses.Clear();

            // 删除探针 (整个临时操作)
            try
            {
                if (_probeOp != null) ((dynamic)_probeOp).Delete();
            }
            catch { }
            _probeOp = null;
            _probeVia = null;

            // 恢复原始姿态
            try { if (_savedPose != null) _robot.CurrentPose = _savedPose; }
            catch { }
        }
    }
}
