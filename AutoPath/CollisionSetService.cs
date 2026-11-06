using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Tecnomatix.Engineering;

namespace TxTools.AutoPathPlanner
{
    /// <summary>Robot assembly collision sets. Existing lists are read-only;
    /// baselines are diagnostic and installation contacts are geometry-pair specific.</summary>
    public sealed class CollisionSetService : IDisposable
    {
        private const double InstallationTolerance = 0.1; // mm; Contact only, never Collision
        private TxRobot _robot;
        private readonly List<ITxObject> _fixedRoots = new List<ITxObject>();
        private readonly Dictionary<string, ContactRule> _contacts = new Dictionary<string, ContactRule>();

        private static bool SameObject(ITxObject a, ITxObject b)
        {
            if (object.ReferenceEquals(a, b)) return true;
            return a != null && b != null && !string.IsNullOrEmpty(a.Id) &&
                string.Equals(a.Id, b.Id, StringComparison.Ordinal);
        }

        private static string PairName(TxRobot robot) { return "TxTools.AutoPath:" + robot.Id; }

        private sealed class ContactRule
        {
            public ITxLocatableObject A, B;
            public TxTransformation PoseA, PoseB;
        }

        private static string ContactKey(ITxObject a, ITxObject b)
        {
            return string.CompareOrdinal(a.Id, b.Id) < 0
                ? a.Id + "|" + b.Id : b.Id + "|" + a.Id;
        }

        private static bool Unchanged(ITxLocatableObject obj, TxTransformation pose)
        {
            var now = obj.AbsoluteLocation;
            var a = now.Translation; var b = pose.Translation;
            var ra = now.RotationRPY_ZYX; var rb = pose.RotationRPY_ZYX;
            return Math.Abs(a.X-b.X) < 0.001 && Math.Abs(a.Y-b.Y) < 0.001 &&
                Math.Abs(a.Z-b.Z) < 0.001 && Math.Abs(ra.X-rb.X) < 1e-6 &&
                Math.Abs(ra.Y-rb.Y) < 1e-6 && Math.Abs(ra.Z-rb.Z) < 1e-6;
        }

        private bool IsInstallationContact(List<ITxObject> objs)
        {
            if (objs.Count != 2) return false;
            ContactRule rule;
            return _contacts.TryGetValue(ContactKey(objs[0], objs[1]), out rule) &&
                Unchanged(rule.A, rule.PoseA) && Unchanged(rule.B, rule.PoseB);
        }

        private bool InvolvesObstacleSet(List<ITxObject> objs)
        {
            foreach (var o in objs)
                foreach (ITxObject obstacle in _obstacleList)
                    if (SameObject(o, obstacle) || IsDescendantOf(o, obstacle)) return true;
            return false;
        }

        private static TxObjectList ObjectList(IEnumerable<ITxObject> objects)
        {
            var list = new TxObjectList();
            foreach (var o in objects) if (o != null) list.Add(o);
            return list;
        }

        private static TxCollisionQueryParams ContactParams()
        {
            return new TxCollisionQueryParams {
                UseAllowedPenetration = true, AllowedPenetration = InstallationTolerance,
                Mode = TxCollisionQueryParams.TxCollisionQueryMode.All,
                UseNearMiss = false, UsePredefinedAllowedPairs = false,
                StopQueryAfterFirstCollision = false
            };
        }

        private void Attach(TxCollisionPair pair)
        {
            _pair = pair;
            _checkList = pair.FirstList;
            _obstacleList = pair.SecondList;
            if (_checkList == null || _obstacleList == null ||
                _checkList.Count == 0 || _obstacleList.Count == 0)
                throw new InvalidOperationException("已有干涉集列表为空");
            _checkObjects.Clear();
            foreach (ITxObject o in _checkList) AddUnique(_checkObjects, o);
            CheckObjectCount = _checkList.Count;
            ObstacleObjectCount = _obstacleList.Count;
            CaptureInstallationContacts();
            _log("  只读附着干涉集: " + pair.Name + "，保留两侧成员和参数");
        }

        public string DescribeUpdate(CollisionSetService candidate)
        {
            var oldFirst = _checkObjects;
            var oldSecond = _obstacleList.Cast<ITxObject>().ToList();
            var newFirst = candidate._checkObjects;
            var newSecond = candidate._obstacleList.Cast<ITxObject>().ToList();
            Func<string, List<ITxObject>, List<ITxObject>, string> diff = (label, before, after) =>
                label + "\n增加: " + string.Join(", ", after.Where(o => !before.Any(b => SameObject(o,b))).Select(o => o.Name + " [" + o.Id + "]")) +
                "\n移除: " + string.Join(", ", before.Where(o => !after.Any(b => SameObject(o,b))).Select(o => o.Name + " [" + o.Id + "]"));
            return diff("检测方", oldFirst, newFirst) + "\n\n" + diff("障碍方", oldSecond, newSecond);
        }

        public void UpdateOwned(CollisionSetService candidate)
        {
            if (_robot == null || _pair.Name != PairName(_robot))
                throw new InvalidOperationException("仅允许更新插件拥有的自动干涉集");
            var oldFirst = _pair.FirstList; var oldSecond = _pair.SecondList;
            try
            {
                _pair.FirstList = candidate._checkList;
                _pair.SecondList = candidate._obstacleList;
            }
            catch
            {
                _pair.FirstList = oldFirst; _pair.SecondList = oldSecond;
                throw;
            }
            Attach(_pair);
        }

        private bool IsFixedRoot(ITxObject obj)
        {
            // A robot/tool/device is not fixed merely because it happens to touch something.
            if (obj is TxDevice || GunAxisService.IsGunObject(obj)) return false;
            if (_robot != null && (SameObject(obj, _robot) || IsDescendantOf(obj, _robot))) return false;
            if (_robot != null)
                foreach (var tool in CollectMountedTools(_robot))
                    if (SameObject(obj, tool) || IsDescendantOf(obj, tool)) return false;
            return obj is ITxLocatableObject;
        }

        private void CaptureInstallationContacts()
        {
            _contacts.Clear();
            _fixedRoots.Clear();
            if (_robot == null) return;
            foreach (var obj in _checkObjects)
                if (IsFixedRoot(obj)) AddUnique(_fixedRoots, obj);
            if (_fixedRoots.Count == 0) return;
            try
            {
                var results = TxApplication.ActiveDocument.CollisionRoot.GetCollidingObjectsFromLists(
                    ObjectList(_fixedRoots), _obstacleList, ContactParams());
                foreach (TxCollisionState state in results.States)
                {
                    if (state.Type != TxCollisionState.TxCollisionStateType.Contact) continue;
                    var a = state.FirstObject as ITxLocatableObject;
                    var b = state.SecondObject as ITxLocatableObject;
                    if (a == null || b == null) continue;
                    _contacts[ContactKey(state.FirstObject, state.SecondObject)] = new ContactRule {
                        A = a, B = b, PoseA = new TxTransformation(a.AbsoluteLocation),
                        PoseB = new TxTransformation(b.AbsoluteLocation)
                    };
                    _log("  安装接触: " + state.FirstObject.Name + " ↔ " + state.SecondObject.Name +
                        "，仅在双方位置不变且接触≤0.1mm时允许");
                }
            }
            catch (Exception ex)
            {
                _contacts.Clear();
                _log("  [警告] 安装接触识别不可用，不作豁免: " + ex.Message);
            }
        }

        // Distance to the first intersection of the local -Z ray with an AABB.
        // A pedestal may extend above the installation plane (e.g. collars/bolts).
        private static double SupportDistance(double minX, double maxX, double minY,
            double maxY, double minZ, double maxZ)
        {
            if (minX > InstallationTolerance || maxX < -InstallationTolerance ||
                minY > InstallationTolerance || maxY < -InstallationTolerance ||
                minZ > InstallationTolerance) return double.PositiveInfinity;
            return Math.Max(0, -maxZ);
        }

        private bool IsBaseCandidate(ITxObject obj)
        {
            if (!(obj is ITxLocatableObject)) return false;
            for (ITxObject cur = obj; cur != null; cur = GetParent(cur))
                if (cur is TxRobot || GunAxisService.IsGunObject(cur)) return false;
            return true;
        }

        private void DiscoverAssembly(List<ITxObject> check, List<ITxObject> obstacles, bool traceContacts = true)
        {
            // A locator is not proof that a physical pedestal was collected.
            // Always inspect remaining geometry below the robot mounting frame.
            var candidates = new List<KeyValuePair<double, ITxObject>>();
            var worldToRobot = _robot.AbsoluteLocation.Inverse;
            foreach (var obj in obstacles.Where(IsBaseCandidate))
            {
                try
                {
                    var calculator = new TxBoundingBoxCalculator(ObjectList(new[] { obj }));
                    calculator.Orientation = new TxTransformation();
                    var box = calculator.CalculateBoundingBox();
                    if (box.IsEmpty) continue;
                    // Transform all eight world box corners explicitly. The calculator's
                    // Orientation describes orientation, so do not assume it changes origin.
                    double minX = double.PositiveInfinity, minY = minX, minZ = minX;
                    double maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX;
                    for (int mask = 0; mask < 8; mask++)
                    {
                        var corner = new TxTransformation();
                        corner.Translation = new TxVector(
                            (mask & 1) == 0 ? box.Lower.X : box.Higher.X,
                            (mask & 2) == 0 ? box.Lower.Y : box.Higher.Y,
                            (mask & 4) == 0 ? box.Lower.Z : box.Higher.Z);
                        var local = (worldToRobot * corner).Translation;
                        minX = Math.Min(minX, local.X); maxX = Math.Max(maxX, local.X);
                        minY = Math.Min(minY, local.Y); maxY = Math.Max(maxY, local.Y);
                        minZ = Math.Min(minZ, local.Z); maxZ = Math.Max(maxZ, local.Z);
                    }
                    double distance = SupportDistance(minX, maxX, minY, maxY, minZ, maxZ);
                    if (double.IsPositiveInfinity(distance)) continue;
                    candidates.Add(new KeyValuePair<double, ITxObject>(distance, obj));
                }
                catch (Exception ex) { _log("  包围盒不可读，保留障碍 " + obj.Name + ": " + ex.Message); }
            }
            bool found = false;
            foreach (var candidate in candidates.OrderBy(c => c.Key).ThenBy(c => c.Value.Id, StringComparer.Ordinal))
            {
                var results = TxApplication.ActiveDocument.CollisionRoot.GetCollidingObjectsFromLists(
                    ObjectList(new ITxObject[] { _robot }), ObjectList(new[] { candidate.Value }), ContactParams());
                if (!results.States.Cast<TxCollisionState>().Any(st =>
                    st.Type == TxCollisionState.TxCollisionStateType.Contact)) continue;
                AddUnique(check, candidate.Value);
                _log("  底座归入检测方(-Z候选、实际接触确认): " +
                    candidate.Value.Name + " [" + candidate.Value.Id + "]");
                found = true;
                break;
            }
            if (!found)
                _log("  [底座识别] 未确认新增实体底座，向下几何候选 " + candidates.Count +
                    " 项；定位器非空也不会跳过实体搜索。");
            SanitizeObstacles(check, obstacles);
            if (!traceContacts) return;
            // Discover contact relations outward without promoting arbitrary touched geometry.
            var frontier = new List<ITxObject>(check);
            var visited = new HashSet<ITxObject>(check, ReferenceComparer.Instance);
            while (frontier.Count > 0)
            {
                var remaining = obstacles.Where(o => !visited.Contains(o)).ToList();
                if (remaining.Count == 0) break;
                var results = TxApplication.ActiveDocument.CollisionRoot.GetCollidingObjectsFromLists(
                    ObjectList(frontier), ObjectList(remaining), ContactParams());
                var next = new List<ITxObject>();
                foreach (TxCollisionState state in results.States)
                {
                    if (state.Type != TxCollisionState.TxCollisionStateType.Contact &&
                        state.Type != TxCollisionState.TxCollisionStateType.Collision) continue;
                    _log("  接触关系: " + state.FirstObject.Name + " ↔ " + state.SecondObject.Name +
                        " (" + state.Type + ")；环境资源保留为障碍");
                    foreach (var obj in remaining)
                        if (SameObject(state.FirstObject, obj) || IsDescendantOf(state.FirstObject, obj) ||
                            SameObject(state.SecondObject, obj) || IsDescendantOf(state.SecondObject, obj))
                            if (visited.Add(obj)) next.Add(obj);
                }
                frontier = next;
            }
        }

        /// <summary>Temporarily activates only the selected set; restores every original switch.</summary>
        public sealed class PlanningIsolation : IDisposable
        {
            private readonly Dictionary<TxCollisionPair, bool> _states = new Dictionary<TxCollisionPair, bool>();
            private readonly TxCollisionRoot _root;
            private readonly bool _enabled;
            private readonly TxCollisionQueryParams.TxCollisionQueryMode _mode;
            private readonly Action<string> _log;
            public PlanningIsolation(Action<string> log)
            {
                _root = TxApplication.ActiveDocument.CollisionRoot;
                _enabled = _root.CheckCollisions;
                _mode = _root.CollisionQueryMode;
                _log = log ?? delegate { };
                foreach (var p in GetAllPairs()) _states[p] = p.Active;
            }
            public void Activate(CollisionSetService service)
            {
                foreach (var p in GetAllPairs())
                {
                    if (!_states.ContainsKey(p)) _states[p] = p.Active;
                    p.Active = SameObject(p, service._pair);
                }
                _root.CollisionQueryMode = TxCollisionQueryParams.TxCollisionQueryMode.DefinedPairs;
                _root.CheckCollisions = true;
            }
            public void Dispose()
            {
                foreach (var entry in _states)
                    try { entry.Key.Active = entry.Value; }
                    catch (Exception ex) { _log("  [警告] 恢复干涉集状态失败: " + ex.Message); }
                try { _root.CollisionQueryMode = _mode; _root.CheckCollisions = _enabled; }
                catch (Exception ex) { _log("  [警告] 恢复碰撞总开关失败: " + ex.Message); }
            }
        }

        private TxCollisionPair _pair; // v5.0: 强类型 (API 已由 inspect_type 确认)
        private readonly Action<string> _log;

        public bool IsReady { get { return _pair != null; } }
        public int CheckObjectCount { get; private set; }
        public int ObstacleObjectCount { get; private set; }

        private CollisionSetService(Action<string> log)
        {
            _log = log ?? delegate { };
        }

        // ================================================================
        //  工厂方法
        // ================================================================

        /// <summary>
        /// 通用入口: 任意检测方 vs 任意障碍方。
        /// 注意: 此入口不传 robot，不会检测已有干涉集。
        /// </summary>
        public static CollisionSetService Create(
            IEnumerable<ITxObject> checkObjects,
            IEnumerable<ITxObject> obstacles,
            Action<string> log)
        {
            var svc = new CollisionSetService(log);
            svc.BuildPair(checkObjects, obstacles); // robot=null → 不检测已有干涉集
            return svc;
        }

        /// <summary>构建机器人总成与环境干涉集；默认只读复用，forceNew 显式新建，attachOnly 不创建。</summary>
        public static CollisionSetService CreateRobotVsWorld(
            TxRobot robot,
            IEnumerable<ITxObject> extraCheckObjects,
            IEnumerable<ITxObject> customObstacles,
            Action<string> log,
            bool forceNew = false,
            bool attachOnly = false)
        {
            var lg = log ?? delegate { };
            if (robot == null) throw new ArgumentNullException("robot");
            var svc = new CollisionSetService(log);
            svc._robot = robot;
            if (!forceNew)
            {
                var existing = FindExistingPairForRobot(robot, lg);
                if (existing != null) { svc.Attach(existing); return svc; }
                if (attachOnly) return svc;
            }
            List<ITxObject> check, obstacles;
            svc.BuildRobotVsWorldLists(extraCheckObjects, customObstacles, out check, out obstacles);
            svc.BuildPair(check, obstacles);
            return svc;
        }

        /// <summary>仅构建临时双列表查询范围，不创建场景碰撞对或修改全局碰撞设置。</summary>
        public static CollisionSetService CreateTemporaryRobotVsWorld(TxRobot robot, Action<string> log,
            Action<string> stage = null)
        {
            if (robot == null) throw new ArgumentNullException("robot");
            var svc = new CollisionSetService(log) { _robot = robot };
            stage?.Invoke("查找机器人 / 焊钳已有干涉集");
            var bound = CollectBoundResources(robot);
            var tools = CollectMountedTools(robot);
            var checks = new List<ITxObject>(bound);
            var world = new List<ITxObject>();
            bool reused = false;
            foreach (var pair in GetAllPairs())
            {
                var first = pair.FirstList;
                var second = pair.SecondList;
                if (first == null || second == null || first.Count == 0 || second.Count == 0) continue;
                Func<TxObjectList, bool> matches = list => list.Cast<ITxObject>().Any(o =>
                    SameObject(o, robot) || tools.Any(t => SameObject(o, t) || IsDescendantOf(o, t)));
                bool forward = matches(first), reverse = matches(second);
                if (forward == reverse) continue;
                foreach (ITxObject obj in forward ? first : second) AddUnique(checks, obj);
                foreach (ITxObject obj in forward ? second : first) AddUnique(world, obj);
                reused = true;
            }
            if (reused)
            {
                stage?.Invoke("复用已有干涉集，准备安装接触");
                SanitizeObstacles(checks, world);
                // 已有组不完整或两侧全部重叠时，不能把空范围当作检查通过。
                if (world.Count > 0)
                {
                    ExpandAttachedComponents(checks);
                    SanitizeObstacles(checks, world);
                }
                if (world.Count > 0)
                {
                    svc._checkObjects.AddRange(checks);
                    svc._checkList = ObjectList(checks);
                    svc._obstacleList = ObjectList(world);
                    svc.CheckObjectCount = checks.Count;
                    svc.ObstacleObjectCount = world.Count;
                    svc.CaptureInstallationContacts();
                    svc._log("  只读复用关联机器人/焊钳干涉集，补齐机器人绑定总成");
                    return svc;
                }
            }
            stage?.Invoke("收集临时干涉范围");
            List<ITxObject> check, obstacles;
            svc.BuildRobotVsWorldLists(null, null, out check, out obstacles, false);
            svc._checkObjects.AddRange(check);
            svc._checkList = ObjectList(check);
            svc._obstacleList = ObjectList(obstacles);
            svc.CheckObjectCount = check.Count;
            svc.ObstacleObjectCount = obstacles.Count;
            stage?.Invoke("准备安装接触");
            svc.CaptureInstallationContacts();
            return svc;
        }

        /// <summary>轻量场景签名：结构、位姿和组成员变化时使窗口缓存失效。</summary>
        public static string ScopeFingerprint(TxRobot robot)
        {
            var parts = new List<string>();
            foreach (ITxObject obj in TxApplication.ActiveDocument.PhysicalRoot.GetAllDescendants(
                new TxTypeFilter(typeof(ITxComponent))))
            {
                var loc = obj as ITxLocatableObject;
                var pose = loc.AbsoluteLocation;
                var t = pose.Translation; var r = pose.RotationRPY_ZYX;
                parts.Add(obj.Id + ":" + (obj.Collection as ITxObject)?.Id + ":" +
                    (loc.AttachmentParent as ITxObject)?.Id + ":" + string.Join(",", new[] {
                        t.X, t.Y, t.Z, r.X, r.Y, r.Z }.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
            }
            foreach (var pair in GetAllPairs())
                parts.Add("pair:" + pair.Id + ":" + string.Join(",", (pair.FirstList ?? new TxObjectList()).Cast<ITxObject>().Select(o => o.Id))
                    + ":" + string.Join(",", (pair.SecondList ?? new TxObjectList()).Cast<ITxObject>().Select(o => o.Id)));
            parts.Add("mounted:" + string.Join(",", CollectMountedTools(robot).Select(o => o.Id)));
            parts.Add("locator:" + (robot.PrimaryLocator as ITxObject)?.Id);
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }

        private void BuildRobotVsWorldLists(IEnumerable<ITxObject> extraCheckObjects,
            IEnumerable<ITxObject> customObstacles, out List<ITxObject> checkResult, out List<ITxObject> obstacleResult,
            bool traceContacts = true)
        {
            var robot = _robot;
            var check = CollectBoundResources(robot);
            if (extraCheckObjects != null)
                foreach (var o in extraCheckObjects) AddUnique(check, o);
            ExpandAttachedComponents(check);
            var obstacles = CollectWorldObstacles(check);
            if (customObstacles != null)
                foreach (var o in customObstacles) AddUnique(obstacles, o);
            DiscoverAssembly(check, obstacles, traceContacts);
            SanitizeObstacles(check, obstacles);
            foreach (var obj in check)
            {
                if (obstacles.Any(o => SameObject(obj, o) || IsDescendantOf(obj, o) || IsDescendantOf(o, obj)))
                    throw new InvalidOperationException("干涉集两侧存在重复几何覆盖: " + obj.Name + " [" + obj.Id + "]");
                _log("  检测方成员: " + obj.Name + " [" + obj.Id + "] (" + obj.GetType().Name + ")");
            }
            _log("  归属校验: 检测方与障碍方无重复对象及父子覆盖");
            checkResult = check;
            obstacleResult = obstacles;
        }

        private static void ExpandAttachedComponents(List<ITxObject> check)
        {
            var allComponents = TxApplication.ActiveDocument.PhysicalRoot.GetAllDescendants(
                new TxTypeFilter(typeof(ITxComponent)));
            bool expanded;
            do
            {
                expanded = false;
                foreach (ITxObject obj in allComponents)
                {
                    var loc = obj as ITxLocatableObject;
                    if (loc == null) continue;
                    var parent = loc.AttachmentParent as ITxObject;
                    if (parent != null && check.Any(c => SameObject(parent, c) || IsDescendantOf(parent, c)))
                        expanded |= AddUnique(check, obj);
                }
            } while (expanded);
        }

        /// <summary>精确双列表查询；异常返回未知，不回退全场景布尔值。</summary>
        public bool? QueryScopedCollision(out string detail)
        {
            detail = null;
            if (_checkList == null || _obstacleList == null) return null;
            if (_obstacleList.Count == 0) return false;
            try
            {
                var results = TxApplication.ActiveDocument.CollisionRoot.GetCollidingObjectsFromLists(
                    _checkList, _obstacleList, ContactParams());
                if (results == null || results.States == null) return null;
                bool unknown = false;
                foreach (TxCollisionState state in results.States)
                {
                    if (state.Type == TxCollisionState.TxCollisionStateType.Unchecked)
                    { unknown = true; continue; }
                    if (state.Type != TxCollisionState.TxCollisionStateType.Contact &&
                        state.Type != TxCollisionState.TxCollisionStateType.Collision) continue;
                    var objects = new List<ITxObject> { state.FirstObject, state.SecondObject };
                    if (objects.Any(o => o == null)) { unknown = true; continue; }
                    if (!InvolvesCheckSet(objects) || !InvolvesObstacleSet(objects)) continue;
                    if (state.Type == TxCollisionState.TxCollisionStateType.Contact && IsInstallationContact(objects)) continue;
                    detail = Signature(objects);
                    return true;
                }
                return unknown ? (bool?)null : false;
            }
            catch (Exception ex)
            {
                _log("  [警告] 临时干涉查询失败: " + ex.Message);
                return null;
            }
        }

        private static double SquaredDistance(double x, double y, double z, double a, double b, double c)
        {
            return (x-a)*(x-a) + (y-b)*(y-b) + (z-c)*(z-c);
        }

        public static List<ITxObject> CollectOperationAppearances(IEnumerable<ITxObject> operations)
        {
            var ops = operations.Where(o => o != null).ToList();
            var result = new List<ITxObject>();
            var all = TxApplication.ActiveDocument.PhysicalRoot.GetAllDescendants(
                new TxTypeFilter(typeof(ITxPartAppearance)));
            foreach (ITxObject obj in all)
            {
                var appearance = obj as ITxPartAppearance;
                if (appearance == null) continue;
                foreach (ITxObject assigned in appearance.OperationsToWhichAssigned)
                    if (ops.Any(op => op.Equals(assigned) || IsDescendantOf(assigned, op) || IsDescendantOf(op, assigned)))
                    { AddUnique(result, obj); break; }
            }
            return result;
        }

        private static int SanitizeObstacles(List<ITxObject> check, List<ITxObject> obstacles)
        {
            if (check == null || obstacles == null) return 0;

            var checkSet = new HashSet<ITxObject>(ReferenceComparer.Instance);
            foreach (var c in check)
                if (c != null) checkSet.Add(c);

            int before = obstacles.Count;
            obstacles.RemoveAll(o => o == null || checkSet.Contains(o) ||
                check.Any(c => IsDescendantOf(o, c) || IsDescendantOf(c, o)));
            return before - obstacles.Count;
        }

        /// <summary>打印绑定资源收集摘要 (按类型分组, 便于核对 fupa/焊钳是否收全)</summary>
        private static void LogBoundResourceSummary(TxRobot robot, List<ITxObject> check, Action<string> log)
        {
            if (log == null || check == null) return;
            try
            {
                log(string.Format("  绑定资源 (FirstList): {0} 项", check.Count));

                // 按类型名分组统计
                var byType = new Dictionary<string, int>();
                foreach (var o in check)
                {
                    if (o == null) continue;
                    string tn = o.GetType().Name;
                    if (byType.ContainsKey(tn)) byType[tn]++;
                    else byType[tn] = 1;
                }
                var parts = new List<string>();
                foreach (var kv in byType.OrderByDescending(k => k.Value))
                    parts.Add(string.Format("{0}×{1}", kv.Key, kv.Value));
                log("    类型: " + string.Join(", ", parts.ToArray()));

                // 列出关键项 (机器人 / 焊钳 / 底座) 便于人工核对
                foreach (var o in check)
                {
                    if (o == null) continue;
                    bool key = (o is TxRobot) || IsGunType(o);
                    string nm = null;
                    try { nm = o.Name; } catch { }
                    // fupa / 底座 通常名字含 fupa / base
                    if (!key && !string.IsNullOrEmpty(nm))
                    {
                        string low = nm.ToLowerInvariant();
                        if (low.Contains("fupa") || low.Contains("base") || low.Contains("sockel"))
                            key = true;
                    }
                    if (key)
                        log(string.Format("    ★ {0} [{1}]", nm ?? "?", o.GetType().Name));
                }
            }
            catch { }
        }

        // ================================================================
        //  对象收集
        // ================================================================

        /// <summary>收集机器人、已安装工具、明确底座和附着父体；不包含仅注册的工具。</summary>
        public static List<ITxObject> CollectBoundResources(TxRobot robot)
        {
            var result = new List<ITxObject>();
            if (robot == null) return result;

            // ① 机器人本体 — PS 碰撞检测按机器人姿态几何计算(含全部连杆), 无需展开子级
            result.Add(robot);

            // ② MountedTools — 焊钳 (TxServoGun); 仅保留父级, 不展开子级
            try
            {
                TxObjectList tools = robot.MountedTools;
                if (tools != null)
                {
                    foreach (ITxObject tool in tools)
                    {
                        AddUnique(result, tool);
                    }
                }
            }
            catch { }

            try { AddUnique(result, robot.PrimaryLocator as ITxObject); }
            catch { }

            // ⑤ AttachmentParent — 机器人挂在什么上 (滑轨/外部轴/变位机); 仅保留父级
            //    若 AttachmentParent 就是底座 (与 PrimaryLocator 同一对象) → 跳过
            try
            {
                ITxLocatableObject ap = robot.AttachmentParent;
                if (ap != null)
                {
                    var apObj = ap as ITxObject;
                    if (apObj != null)
                    {
                        bool isBase = false;
                        try
                        {
                            ITxObjectCollection ploc = robot.PrimaryLocator;
                            var po = ploc as ITxObject;
                            if (po != null && SameObject(po, apObj)) isBase = true;
                        }
                        catch { }
                        if (!isBase) AddUnique(result, apObj);
                    }
                }
            }
            catch { }

            // ⑥ (v6.6 移除) robot.Links — 运动学连杆 / 各轴关节, 不再加入干涉集
            //   用户反馈: 自动添加干涉集时不应把机器人的各轴关节 (TxKinematicLink) 包含进去。
            //   安全性: TxKinematicLink 不是 ITxComponent → ①AddDescendantComponents 与
            //   CollectWorldObstacles 的 ITxComponent 过滤器都收集不到它 → 删除后 FirstList /
            //   SecondList 两侧均无连杆, 不会引发 "机器人 vs 自己连杆" 自干涉;
            //   机器人本体 (TxRobot, ITxComponent) 仍参与碰撞检测, 检测能力不丢失。

            return result;
        }

        /// <summary>递归收集工具上的二级/三级挂载 (深度上限防环)</summary>
        private static void AddNestedMountedTools(ITxObject tool, List<ITxObject> result, int depth)
        {
            if (tool == null || depth > 4) return;
            try
            {
                dynamic dynTool = tool;
                var subMounted = dynTool.MountedTools as IEnumerable;
                if (subMounted == null) return;
                foreach (var sub in subMounted)
                {
                    var s = sub as ITxObject;
                    if (s == null || !AddUnique(result, s)) continue;
                    AddDescendantComponents(s, result);
                    AddNestedMountedTools(s, result, depth + 1);
                }
            }
            catch { }
        }

        /// <summary>将 ITxComponent 后代添加到列表 (去重)</summary>
        private static void AddDescendantComponents(ITxObject obj, List<ITxObject> result)
        {
            try
            {
                var container = obj as ITxObjectCollection;
                if (container == null) return;

                var filter = new TxTypeFilter(typeof(ITxComponent));
                TxObjectList descendants = container.GetAllDescendants(filter);
                if (descendants == null) return;
                foreach (ITxObject d in descendants)
                    AddUnique(result, d);
            }
            catch { }
        }

        /// <summary>去重添加: 仅引用不重复时才加入</summary>
        private static bool AddUnique(List<ITxObject> list, ITxObject item)
        {
            if (item == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (SameObject(list[i], item)) return false;
            list.Add(item);
            return true;
        }

        /// <summary>仅收集直接挂载工具 (供外部复用, 如 CollisionWorld.ApplyGunOpenPoses)</summary>
        public static List<ITxObject> CollectMountedTools(TxRobot robot)
        {
            var result = new List<ITxObject>();
            if (robot == null) return result;
            try
            {
                TxObjectList mounted = robot.MountedTools;   // v5.1: 强类型
                if (mounted != null)
                {
                    foreach (ITxObject t in mounted)
                    {
                        if (t != null && !result.Any(r => SameObject(r, t)))
                            result.Add(t);
                    }
                }
            }
            catch { }
            return result;
        }


        /// <summary>保留其他机器人、工具、底座及环境；按 ID 排除本总成，合并重复的父子几何覆盖。</summary>
        public static List<ITxObject> CollectWorldObstacles(IList<ITxObject> boundResources)
        {
            var result = new List<ITxObject>();
            var all = TxApplication.ActiveDocument.PhysicalRoot.GetAllDescendants(
                new TxTypeFilter(typeof(ITxComponent)));
            foreach (ITxObject obj in all)
            {
                if (boundResources != null && boundResources.Any(b =>
                    SameObject(obj, b) || IsDescendantOf(obj, b) || IsDescendantOf(b, obj)))
                    continue;
                AddUnique(result, obj);
            }
            return result.Where(o => !result.Any(parent =>
                !SameObject(parent, o) && IsDescendantOf(o, parent))).ToList();
        }

        private sealed class ReferenceComparer : IEqualityComparer<ITxObject>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(ITxObject a, ITxObject b) { return SameObject(a, b); }
            public int GetHashCode(ITxObject o)
            {
                return o == null ? 0 : StringComparer.Ordinal.GetHashCode(o.Id);
            }
        }

        /// <summary>判断 SDK Equipment 标志；该标志不证明对象没有几何。</summary>
        private static bool IsEquipmentContainer(ITxObject obj)
        {
            if (obj == null) return false;
            try
            {
                var comp = obj as ITxComponent;
                if (comp == null) return false;
                return comp.IsEquipment;
            }
            catch { return false; }
        }

        /// <summary>
        /// 判定 obj 本身或其祖先链是否包含 TxRobot / 焊枪 (类型名含 "Gun")。
        /// 命中 = obj 位于机器人或焊枪的子树 → 障碍方一律剔除。
        /// 焊枪匹配用类型名而非精确类型, 覆盖 TxWeldGun / TxServoGun /
        /// TxPneumaticGun / TxSpotGun 等各种子类, 不需知道具体 SDK 类名。
        /// </summary>
        /// <summary>
        /// 祖先链遍历。
        ///
        /// v6.5 重要修复: **ITxObject 没有 Parent 属性** (文档确认) ——
        /// 之前 dynamic cur.Parent 每次都抛异常被 catch 吞掉, 这个函数
        /// **一直返回 false**, 祖先链排除从未真正生效过 (幸亏 v5.1 加的
        /// HashSet 引用级硬排除兜住了)。
        ///
        /// 正确路径: ITxObject.Collection (返回 ITxObjectCollection, 即父容器)。
        /// </summary>
        public static bool IsDescendantOf(ITxObject obj, ITxObject ancestor)
        {
            if (obj == null || ancestor == null) return false;

            ITxObject cur = obj;
            for (int depth = 0; depth < 64; depth++)
            {
                ITxObject parent = GetParent(cur);
                if (parent == null) return false;
                if (SameObject(parent, ancestor)) return true;
                cur = parent;
            }
            return false;
        }

        /// <summary>取父容器 (ITxObject.Collection, 强类型)</summary>
        private static ITxObject GetParent(ITxObject o)
        {
            if (o == null) return null;
            try { return o.Collection as ITxObject; }
            catch { return null; }
        }

        /// <summary>
        /// 检测 CollisionRoot 中已有碰撞对是否**以指定机器人为检测方**。
        ///
        /// v5.0 重写 (强类型 API, 由 TxAgent inspect_type 实测确认):
        ///   TxCollisionRoot.Pairs      : ArrayList        ← 碰撞对列表 (正确属性名!)
        ///   TxCollisionRoot.PairList   : TxObjectList     ← 同上, 另一种形态
        ///   TxCollisionPair.FirstList  : TxObjectList get set
        ///   TxCollisionPair.SecondList : TxObjectList get set
        ///   TxCollisionPair.Active     : Boolean get set
        ///
        /// 致命 bug 修复: v4.2~v4.10 一直用 root.CollisionPairs / root.GetAllCollisionPairs(),
        /// 这两个成员**根本不存在** → dynamic 抛异常被 catch 吞掉 → pairs 恒为 null
        /// → 函数永远 return null → "永远未找到干涉集"。之前所有 First 侧反射/指纹匹配
        /// 代码一行都没被执行过。改用正确的 Pairs 属性后, 强类型直读即可, 反射全部删除。
        ///
        /// 匹配语义: 只看 FirstList (忽略 SecondList), 命中条件 = FirstList 中存在
        /// 与目标 robot 名字相同、位置相同 (1mm 容差) 的 TxRobot。
        /// 用 名字+位置 而非 ReferenceEquals, 兼容 PS 可能返回不同 CLR 代理的情况;
        /// 同名多机器人 (8~18 台) 靠基座位置天然区分。
        /// </summary>
        private static TxCollisionPair FindExistingPairForRobot(TxRobot robot, Action<string> log = null)
        {
            if (robot == null) return null;

            var pairs = GetAllPairs();
            if (pairs.Count == 0)
            {
                if (log != null) log("  [干涉集] CollisionRoot.Pairs 为空 — 场景无任何碰撞对");
                return null;
            }

            var matches = pairs.Where(p => PairFirstListHasRobot(p, robot)).ToList();
            var owned = matches.Where(p => p.Name == PairName(robot)).ToList();
            if (owned.Count == 1) return owned[0];
            if (owned.Count == 0 && matches.Count == 1) return matches[0];
            if (log != null && matches.Count > 1)
                log("  [干涉集] 多个匹配集合，无法唯一选择。请保留一个用于规划的集合。");
            if (matches.Count > 1) throw new InvalidOperationException("机器人存在多个候选干涉集，无法唯一附着");
            return null;
        }

        /// <summary>
        /// 读取 CollisionRoot 的全部碰撞对 (强类型)。
        /// 主路径 Pairs (ArrayList), 兜底 PairList (TxObjectList)。
        /// </summary>
        private static List<TxCollisionPair> GetAllPairs()
        {
            var result = new List<TxCollisionPair>();
            try
            {
                TxCollisionRoot root = TxApplication.ActiveDocument.CollisionRoot;
                if (root == null) return result;

                // 主路径: Pairs (ArrayList)
                try
                {
                    ArrayList arr = root.Pairs;
                    if (arr != null)
                    {
                        foreach (var o in arr)
                        {
                            var p = o as TxCollisionPair;
                            if (p != null) result.Add(p);
                        }
                    }
                }
                catch { }

                // 兜底: PairList (TxObjectList)
                if (result.Count == 0)
                {
                    try
                    {
                        TxObjectList lst = root.PairList;
                        if (lst != null)
                        {
                            foreach (ITxObject o in lst)
                            {
                                var p = o as TxCollisionPair;
                                if (p != null) result.Add(p);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        /// <summary>按文档内对象 ID 检查检测方中的机器人身份。</summary>
        private static bool PairFirstListHasRobot(TxCollisionPair pair, TxRobot robot)
        {
            if (pair == null || robot == null) return false;

            TxObjectList first;
            try { first = pair.FirstList; }
            catch { return false; }
            if (first == null || first.Count == 0) return false;

            foreach (ITxObject o in first)
                if (SameObject(o, robot)) return true;
            return false;
        }

        /// <summary>读取对象的世界坐标 (AbsoluteLocation.Translation)</summary>
        private static bool TryGetPos(ITxObject obj, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (obj == null) return false;
            try
            {
                var loc = obj as ITxLocatableObject;
                if (loc == null) return false;
                var t = loc.AbsoluteLocation.Translation;
                x = t.X; y = t.Y; z = t.Z;
                return true;
            }
            catch { return false; }
        }

        /// <summary>类型名字符串是否含 "Gun" (含继承链) — 覆盖 TxWeldGun/TxServoGun/TxPneumaticGun 等</summary>
        private static bool IsGunType(ITxObject obj)
        {
            if (obj == null) return false;
            var t = obj.GetType();
            while (t != null && t != typeof(object))
            {
                if (t.Name.IndexOf("Gun", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                t = t.BaseType;
            }
            return false;
        }

        private static string TryGetName(object obj)
        {
            try { return ((dynamic)obj).Name as string ?? "?"; }
            catch { return "?"; }
        }

        /// <summary>
        /// UI 层入口: 检测 CollisionRoot 中是否已有以指定机器人为检测方 (FirstList) 的碰撞对。
        /// 无匹配返回 false; 有则返回 true 并输出碰撞对名 (供弹窗提示)。
        ///
        /// v5.0: 底层改用强类型 root.Pairs → pair.FirstList, 修复"永远找不到"的致命 bug。
        /// </summary>
        public static bool TryFindExistingPairName(TxRobot robot, out string pairName,
            Action<string> log = null)
        {
            pairName = null;
            TxCollisionPair pair = FindExistingPairForRobot(robot, log);
            if (pair == null) return false;
            pairName = TryGetName(pair);
            if (string.IsNullOrEmpty(pairName) || pairName == "?")
                pairName = "(未命名碰撞对)";
            return true;
        }

        // ================================================================
        //  碰撞对创建
        // ================================================================

        private readonly List<ITxObject> _checkObjects = new List<ITxObject>();
        private TxObjectList _checkList, _obstacleList;
        private int _fromLists; // 0=未探测 1=可用 -1=不可用
        private bool _ownedPair; // true = 本插件创建, Dispose 时删除; false = 复用已有, 不删除

        /// <summary>
        /// 设为 true 时, Dispose 保留本插件新建的碰撞对 (不 Delete)。
        /// 用于"持久干涉集"场景 —— UI"自动创建"按钮 / 规划完保留供复用。
        /// 对复用得来的碰撞对无影响 (复用的本来就不会被删)。
        /// </summary>
        public bool KeepPairOnDispose { get; set; }

        /// <summary>
        /// 构建/复用碰撞对。
        ///
        /// robot 参数:
        ///   非 null → 尝试 FindExistingPairForRobot 复用; attachOnly=false 时未命中则新建
        ///   null    → 不检测已有, 直接新建 (forceNew 场景)
        ///
        /// attachOnly 参数 (v4.10):
        ///   true  → 纯附着模式: 严格匹配命中则复用; 未命中则尝试宽松查找 (任意含该机器人
        ///           名字的 pair); 再不行才放弃 (不新建, _pair=null)。用于规划器 ——
        ///           规划不应偷偷新建干涉集, 只用用户已建好的。
        ///   false → 原行为: 未命中则新建。用于 UI"自动创建"按钮。
        /// </summary>
        private void BuildPair(IEnumerable<ITxObject> checkObjects, IEnumerable<ITxObject> obstacles,
            TxRobot robot = null, bool attachOnly = false)
        {
            try
            {
                var checkList = new TxObjectList();
                foreach (var o in checkObjects) { checkList.Add(o); _checkObjects.Add(o); }
                var obstacleList = new TxObjectList();
                foreach (var o in obstacles) obstacleList.Add(o);

                CheckObjectCount = checkList.Count;
                ObstacleObjectCount = obstacleList.Count;
                _checkList = checkList;       // FromLists 精确查询用
                _obstacleList = obstacleList;

                _log(string.Format("  干涉集: 检测方 {0} 项, 障碍方 {1} 项",
                    checkList.Count, obstacleList.Count));

                if (checkList.Count == 0 || obstacleList.Count == 0)
                {
                    _log("  [警告] 干涉集为空，跳过创建");
                    return;
                }

                // ---- 已有干涉集检测 (v5.0 强类型: root.Pairs → pair.FirstList) ----
                TxCollisionPair existingPair = FindExistingPairForRobot(robot, _log);

                if (existingPair != null)
                {
                    Attach(existingPair);
                    return;
                }
                else if (attachOnly)
                {
                    // 纯附着模式: 找不到就放弃, 绝不新建 (避免规划偷偷堆积干涉集)
                    _log("  [附着] 未找到现有干涉集 — 规划不新建。请先点\"自动创建干涉集\"");
                    _pair = null;
                    _ownedPair = false;
                    return;
                }
                else
                {
                    // 已固化API (inspect_type 确认): 无参构造 + FirstList/SecondList 属性。
                    var data = new TxCollisionPairCreationData();
                    data.FirstList = checkList;
                    data.SecondList = obstacleList;

                    TxCollisionRoot root = TxApplication.ActiveDocument.CollisionRoot;
                    _pair = root.CreateCollisionPair(data);
                    _ownedPair = true;
                    if (_robot != null) _pair.Name = PairName(_robot);
                    _log("  干涉集创建成功");

                    // 激活 (v5.0: TxCollisionPair.Active 强类型属性, 已确认存在)
                    try
                    {
                        _pair.Active = true;
                        _log("  干涉集激活: pair.Active = true");
                    }
                    catch (Exception ex) { _log("  [警告] 激活失败: " + ex.Message); }

                    // 全局碰撞检查开关 —— 若关闭, 所有 pair 都不参与计算
                    try
                    {
                        if (!root.CheckCollisions)
                        {
                            root.CheckCollisions = true;
                            _log("  [提示] CollisionRoot.CheckCollisions 原为 false, 已开启");
                        }
                    }
                    catch { }
                }

                CaptureInstallationContacts();

                // ---- 基线快照 (仅诊断参考): 记录规划前姿态的接触状态 ----
                // 基线签名不含位置信息, 不能用于碰撞豁免 (会导致机器人移动后
                // 同对象对的碰撞被错误豁免)。仅用于日志对照, 帮助理解碰撞来源。
                DetermineQueryMode();
                if (_queryMode == 1)
                {
                    var baseSigs = QueryOurCollisionSignatures();
                    if (baseSigs != null)
                    {
                        _baseline = baseSigs;
                        _log(string.Format("  基线快照: {0} 项接触 (仅诊断参考, 不豁免)", _baseline.Count));
                        int shown = 0;
                        foreach (var s in _baseline)
                        {
                            if (shown++ >= 5) { _log("    ... 等"); break; }
                            _log("    常驻: " + s);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log("  [警告] 干涉集创建/复用失败: " + ex.Message);
                _pair = null;
                _ownedPair = false;
            }
        }

        // ================================================================
        //  查询 / 清理
        // ================================================================

        /// <summary>
        /// 重拍基线快照 — 在改变了设备形态(如焊枪张开)后调用,
        /// 更新诊断参考基线 (仅用于日志对照, 不再用于碰撞豁免)。
        /// </summary>
        public void RecaptureBaseline()
        {
            if (_queryMode != 1) return;
            var sigs = QueryOurCollisionSignatures();
            if (sigs != null)
            {
                _baseline = sigs;
                _log(string.Format("  基线重拍: {0} 项接触 (仅诊断参考, 不豁免)", _baseline.Count));
            }
        }

        /// <summary>精确结果查询；基线仅诊断。无法验证时按碰撞处理。</summary>
        public bool QueryColliding()
        {
            if (_pair == null)
                return true; // 查询不可用时拒绝放行。
            try
            {
                if (_queryMode == 0) DetermineQueryMode();

                switch (_queryMode)
                {
                    case 1:
                    {
                        var sigs = QueryOurCollisionSignatures();
                        if (sigs == null) return true;
                        bool colliding = sigs.Count > 0;
                        if (_sampleLogBudget > 0)
                        {
                            _sampleLogBudget--;
                            int baselineMatch = 0;
                            foreach (var s in sigs) if (_baseline.Contains(s)) baselineMatch++;
                            _log(string.Format("    [干涉样本] 相关碰撞 {0} 项 (基线内 {1} / 新增 {2}) → {3}",
                                sigs.Count, baselineMatch, sigs.Count - baselineMatch,
                                colliding ? "碰撞" : "安全"));
                        }
                        return colliding;
                    }
                    default:
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (_sampleLogBudget > 0)
                {
                    _sampleLogBudget--;
                    _log("    [干涉查询异常] " + ex.GetType().Name + ": " + ex.Message);
                }
                return true;
            }
        }

        // ---- 查询定型状态 ----
        private int _queryMode;
        private object _queryParams;
        private int _sampleLogBudget = 6;
        private bool _stateDumped;
        private HashSet<string> _baseline = new HashSet<string>();

        /// <summary>v6.7: 被排除的障碍对象 (如被焊工件) — 检测方与它们的接触不算碰撞</summary>
        private void DetermineQueryMode()
        {
            if (_queryMode != 0) return;
            _queryParams = TryBuildQueryParams();

            // ---- 模式1 (主): 结果集过滤 ----
            var probe = QueryOurCollisionSignatures();
            if (probe != null)
            {
                _queryMode = 1;
                _log("  干涉查询定型: 精确双列表/结果过滤，基线仅诊断，安装接触按对象对判断");
                return;
            }

            _queryMode = -1;
            _log("  [错误] 无法读取精确碰撞结果，拒绝以全场景布尔值代替；本次查询按碰撞处理");
            DumpCollisionMembers();
        }

        /// <summary>
        /// 结果集查询: 返回"涉及检测方"的碰撞签名集合; API不可用返回null。
        /// 签名 = 参与对象名排序拼接, 用于与基线比对。
        /// </summary>
        private HashSet<string> QueryOurCollisionSignatures()
        {
            try
            {
                dynamic root = TxApplication.ActiveDocument.CollisionRoot;
                if (_queryParams == null) _queryParams = TryBuildQueryParams();

                object results = null;

                // 首选 (API参考表固化): GetCollidingObjectsFromLists — 精确双列表查询,
                // 免疫场景 Collision Viewer 既有碰撞对的污染
                if (_fromLists >= 0 && _checkList != null && _obstacleList != null)
                {
                    try
                    {
                        results = (object)root.GetCollidingObjectsFromLists(
                            (dynamic)_checkList, (dynamic)_obstacleList, ContactParams());
                        if (_fromLists == 0)
                        {
                            _fromLists = 1;
                            _log("  干涉查询升级: GetCollidingObjectsFromLists (精确双列表)");
                        }
                    }
                    catch
                    {
                        if (_fromLists == 0)
                        {
                            _fromLists = -1;
                            _log("  [信息] FromLists 不可用, 回退碰撞对查询");
                        }
                        results = null;
                    }
                }

                if (results == null)
                {
                    results = _queryParams != null
                        ? (object)root.GetCollidingObjects((dynamic)_queryParams)
                        : (object)root.GetCollidingObjects();
                }
                var sigs = new HashSet<string>();
                if (results == null) return null;

                IEnumerable states = null;
                try { states = ((dynamic)results).States as IEnumerable; } catch { }
                if (states == null) states = results as IEnumerable;
                if (states == null)
                {
                    if (!_stateDumped)
                    {
                        _stateDumped = true;
                        _log("    [诊断] 结果对象不可枚举: " + results.GetType().FullName);
                        DumpMembersOf(results.GetType(), "results");
                    }
                    return null;
                }

                foreach (object state in states)
                {
                    // Type 过滤 (API参考表固化: Unchecked=0/Separate=1/NearMiss=2/Contact=3/Collision=5)
                    // 结果集可能含 Separate/NearMiss 项, 只有 Type>=3 才算碰撞
                    int typeVal = ReadStateType(state);
                    if (typeVal >= 0 && typeVal < 3) continue;

                    var objs = ExtractStateObjects(state);
                    if (objs.Count == 0) return null;
                    if (!InvolvesCheckSet(objs) || !InvolvesObstacleSet(objs)) continue;
                    if (typeVal == 3 && IsInstallationContact(objs)) continue;

                    sigs.Add(Signature(objs));
                }
                return sigs;
            }
            catch (Exception ex)
            {
                if (!_stateDumped)
                {
                    _stateDumped = true;
                    _log("    [诊断] 结果集查询不可用: " + ex.GetType().Name + ": " + ex.Message);
                }
                return null;
            }
        }

        /// <summary>读取碰撞状态 Type 枚举值; 不可读返回 -1 (按碰撞处理, 保守)</summary>
        private static int ReadStateType(object state)
        {
            try { return Convert.ToInt32(((dynamic)state).Type); }
            catch { return -1; }
        }

        /// <summary>
        /// 描述当前姿态下与检测方相关的碰撞 (用于动态违例消息, 指明涉事对象)。
        /// 不再扣除基线 —— 与 QueryColliding() 保持一致。
        /// </summary>
        public string DescribeFreshCollisions(int maxItems = 2)
        {
            try
            {
                var sigs = QueryOurCollisionSignatures();
                if (sigs == null) return "";
                var items = new List<string>();
                foreach (var s in sigs)
                {
                    items.Add(s);
                    if (items.Count >= maxItems) break;
                }
                return items.Count > 0 ? string.Join("; ", items) : "";
            }
            catch { return ""; }
        }

        /// <summary>提取碰撞状态涉及的对象: CollidingObjects 集合优先, 反射兜底</summary>
        private List<ITxObject> ExtractStateObjects(object state)
        {
            var result = new List<ITxObject>();
            var typed = state as TxCollisionState;
            if (typed != null)
            {
                AddUnique(result, typed.FirstObject);
                AddUnique(result, typed.SecondObject);
                return result;
            }
            try
            {
                IEnumerable objs = null;
                try { objs = ((dynamic)state).CollidingObjects as IEnumerable; } catch { }
                if (objs == null) { try { objs = ((dynamic)state).Objects as IEnumerable; } catch { } }
                if (objs != null)
                {
                    foreach (var o in objs)
                    {
                        var t = o as ITxObject;
                        if (t != null) result.Add(t);
                    }
                    if (result.Count > 0) return result;
                }

                // 反射兜底: 收集状态对象上所有 ITxObject 型属性值
                foreach (var prop in state.GetType().GetProperties())
                {
                    if (!typeof(ITxObject).IsAssignableFrom(prop.PropertyType)) continue;
                    try
                    {
                        var v = prop.GetValue(state, null) as ITxObject;
                        if (v != null) result.Add(v);
                    }
                    catch { }
                }

                if (result.Count == 0 && !_stateDumped)
                {
                    _stateDumped = true;
                    _log("    [诊断] 无法从碰撞状态提取对象 — 状态成员清单:");
                    DumpMembersOf(state.GetType(), "state");
                }
            }
            catch { }
            return result;
        }

        /// <summary>碰撞是否涉及检测方 (机器人/焊钳本体或其子树)</summary>
        private bool InvolvesCheckSet(List<ITxObject> objs)
        {
            foreach (var o in objs)
            {
                foreach (var c in _checkObjects)
                {
                    if (SameObject(o, c) || IsDescendantOf(o, c))
                        return true;
                }
            }
            return false;
        }

        /// <summary>使用对象 ID 与名称生成诊断签名。</summary>
        private static string Signature(List<ITxObject> objs)
        {
            var names = new List<string>();
            foreach (var o in objs)
            {
                try { names.Add(o.Name + " [" + o.Id + "]"); }
                catch { names.Add(o.GetType().Name); }
            }
            names.Sort();
            return string.Join("|", names);
        }

        /// <summary>
        /// 构造 TxCollisionQueryParams (反射定位类型)，并尝试把查询范围
        /// 限定为本插件的 pair — 上次运行未找到范围属性, 定型时会dump params成员。
        /// </summary>
        private object TryBuildQueryParams()
        {
            try
            {
                var asm = typeof(TxCollisionPairCreationData).Assembly;
                var pType = asm.GetType("Tecnomatix.Engineering.TxCollisionQueryParams");
                if (pType == null) return null;
                object p = Activator.CreateInstance(pType);
                ((TxCollisionQueryParams)p).Mode = TxCollisionQueryParams.TxCollisionQueryMode.DefinedPairs;
                ((TxCollisionQueryParams)p).UseAllowedPenetration = true;
                ((TxCollisionQueryParams)p).AllowedPenetration = InstallationTolerance;
                ((TxCollisionQueryParams)p).UsePredefinedAllowedPairs = false;

                try
                {
                    var listType = asm.GetType("Tecnomatix.Engineering.TxCollisionPairList");
                    if (listType != null && _pair != null)
                    {
                        object list = Activator.CreateInstance(listType);
                        var add = listType.GetMethod("Add");
                        if (add != null) add.Invoke(list, new[] { _pair });
                        foreach (var prop in pType.GetProperties())
                        {
                            if (prop.CanWrite && prop.PropertyType.IsAssignableFrom(listType))
                            {
                                prop.SetValue(p, list, null);
                                _log("  干涉查询范围限定: params." + prop.Name + " = 本插件pair");
                                break;
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    var stopProp = pType.GetProperty("StopQueryAfterFirstCollision");
                    if (stopProp != null && stopProp.CanWrite)
                        stopProp.SetValue(p, false, null); // 基线扣除需要完整结果集
                }
                catch { }

                if (!_paramsDumped)
                {
                    _paramsDumped = true;
                    _log("  [信息] TxCollisionQueryParams 可写属性: ");
                    foreach (var prop in pType.GetProperties())
                        if (prop.CanWrite)
                            _log("    params.prop " + prop.PropertyType.Name + " " + prop.Name);
                }
                return p;
            }
            catch { return null; }
        }
        private bool _paramsDumped;

        private void DumpCollisionMembers()
        {
            try
            {
                object rootObj = TxApplication.ActiveDocument.CollisionRoot;
                DumpMembersOf(rootObj.GetType(), "root", "Coll");
                if (_pair != null)
                {
                    _log("    pair 类型: " + _pair.GetType().FullName);
                    DumpMembersOf(_pair.GetType(), "pair");
                }
            }
            catch (Exception ex)
            {
                _log("    dump 失败: " + ex.Message);
            }
        }

        private void DumpMembersOf(Type t, string tag, string nameFilter = null)
        {
            try
            {
                foreach (var m in t.GetMethods())
                {
                    if (m.IsSpecialName) continue;
                    if (nameFilter != null &&
                        m.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var ps = m.GetParameters()
                        .Select(x => x.ParameterType.Name + " " + x.Name);
                    _log("    " + tag + "." + m.Name + "(" + string.Join(", ", ps) + ") : "
                        + m.ReturnType.Name);
                }
                foreach (var p in t.GetProperties())
                {
                    if (nameFilter != null &&
                        p.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    _log("    " + tag + ".prop " + p.PropertyType.Name + " " + p.Name);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_ownedPair && !KeepPairOnDispose)
            {
                try
                {
                    if (_pair != null) _pair.Delete();   // v5.0: 强类型
                }
                catch { }
            }
            // 复用的碰撞对不删除 — 它属于场景原有配置
            // KeepPairOnDispose=true 时, 本次创建的也不删 (持久干涉集)
            _pair = null;
            _checkList = null;
            _obstacleList = null;
            _checkObjects.Clear();
            _contacts.Clear();
        }
    }
}
