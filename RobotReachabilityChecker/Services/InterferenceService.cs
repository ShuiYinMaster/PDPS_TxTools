// ============================================================================
// InterferenceService.cs  (P0-1 重写)
//
// 静态干涉检测服务 — 基于 PS 真实 API：
//
//   · TxDocument.CollisionRoot                              → TxCollisionRoot
//   · CollisionRoot.HasCollidingObjectsFromLists(A, B, params) → bool  ★ 核心查询
//   · TxCollisionQueryParams.StopQueryAfterFirstCollision   → bool
//
// v2 相对 v1 的变化（审查报告 P0-1）：
//   · 弃用"自动建全局干涉对 + 全局 HasCollidingObjects" —— 会污染场景干涉对、
//     且无法只对机器人做检测
//   · 改为无副作用的"两列表查询"：A = 机器人 + 挂载工具，B = 附近资源
//   · CheckCollisionAtCurrentPose 返回 bool? —— null 表示查询异常/未就绪（三态）
//   · 附近资源过滤增强（P1-9）：原点距离 ≤ 2m；原点超 2m 但包围盒进入 2.5m 的也纳入
// ============================================================================
using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;
using TxTools.RobotReachabilityChecker.Diagnostics;

namespace TxTools.RobotReachabilityChecker.Services
{
    /// <summary>干涉查询上下文（Prepare 阶段构造一次，逐点查询复用）。</summary>
    public sealed class InterferenceContext
    {
        public TxCollisionRoot Root { get; set; }
        public TxObjectList FirstList { get; set; }
        public TxObjectList SecondList { get; set; }

        public bool Ready => Root != null
            && FirstList != null && FirstList.Count > 0
            && SecondList != null && SecondList.Count > 0;
    }

    public static class InterferenceService
    {
        private const double NEARBY_RADIUS_MM = 2000.0;
        private const double BOX_GAP_MM = 500.0;

        // =====================================================================
        // 准备：构造"机器人+挂载工具 × 附近资源"查询上下文（无副作用，不建干涉对）
        // =====================================================================
        public static InterferenceContext Prepare(TxRobot robot, TxDocument doc, ILogger log = null)
        {
            log = log ?? NullLogger.Instance;
            if (robot == null || doc == null) return null;

            TxCollisionRoot root = null;
            try { root = doc.CollisionRoot; } catch { }
            if (root == null) { log.Log("  CollisionRoot 不可用，跳过干涉检查", "WARN"); return null; }

            var ctx = new InterferenceContext { Root = root };

            // A 侧：机器人 + 挂载工具
            ctx.FirstList = new TxObjectList();
            ctx.FirstList.Add(robot);
            try
            {
                var mounted = robot.MountedTools;
                if (mounted != null)
                    for (int i = 0; i < mounted.Count; i++)
                        if (mounted[i] != null) ctx.FirstList.Add(mounted[i]);
            }
            catch { }

            // B 侧：附近资源（自动排除机器人与挂载工具）
            ctx.SecondList = CollectNearbyObjects(robot, doc, log);
            if (ctx.SecondList == null || ctx.SecondList.Count == 0)
                log.Log("  附近未找到其他资源，干涉检查只对机器人×工具内部生效", "WARN");

            return ctx;
        }

        // =====================================================================
        // 静态查询当前姿态是否发生碰撞（A × B 两列表查询）
        //   返回 true=碰撞 / false=无碰撞 / null=查询异常或未就绪（三态）
        // =====================================================================
        public static bool? CheckCollisionAtCurrentPose(InterferenceContext ctx, ILogger log = null)
        {
            log = log ?? NullLogger.Instance;
            if (ctx == null || !ctx.Ready) return null;

            try
            {
                var queryParams = new TxCollisionQueryParams();
                queryParams.StopQueryAfterFirstCollision = true;  // 性能优化：见到一个就返回
                return ctx.Root.HasCollidingObjectsFromLists(ctx.FirstList, ctx.SecondList, queryParams);
            }
            catch (Exception ex)
            {
                log.Log($"  HasCollidingObjectsFromLists 异常: {ex.Message}", "WARN");
                return null;
            }
        }

        // =====================================================================
        // 收集机器人附近资源对象（B 侧）
        //   实现：遍历 PhysicalRoot 下 ITxLocatableObject 的直系子节点，按 AbsoluteLocation
        //   距机器人位置过滤。原始过滤 = 原点距离 ≤ 2m；增强 = 原点超 2m 但包围盒进入
        //   2m+500mm 半径的对象也纳入（覆盖细长夹具"原点远但本体近"的场景）。
        // =====================================================================
        private static TxObjectList CollectNearbyObjects(TxRobot robot, TxDocument doc, ILogger log)
        {
            var result = new TxObjectList();
            if (robot == null || doc == null) return result;

            TxVector robotPos = null;
            try
            {
                ITxLocatableObject lobj = robot as ITxLocatableObject;
                if (lobj != null && lobj.AbsoluteLocation != null)
                {
                    var tx = lobj.AbsoluteLocation;
                    try { robotPos = tx.Translation; } catch { }
                }
            }
            catch { }

            if (robotPos == null)
                log.Log("  无法读取机器人位置，跳过 2m 过滤，包含整个 PhysicalRoot 直系", "WARN");

            // 排除机器人自身和它的挂载工具（这些应当在 A 侧）
            var excludeNames = new HashSet<string>();
            if (!string.IsNullOrEmpty(robot.Name)) excludeNames.Add(robot.Name);
            try
            {
                TxObjectList mt = robot.MountedTools;
                if (mt != null)
                {
                    for (int i = 0; i < mt.Count; i++)
                    {
                        var t = mt[i];
                        if (t != null && !string.IsNullOrEmpty(t.Name)) excludeNames.Add(t.Name);
                    }
                }
            }
            catch { }

            try
            {
                var direct = doc.PhysicalRoot.GetDirectDescendants(
                    new TxTypeFilter(typeof(ITxLocatableObject)));
                if (direct == null) return result;

                for (int i = 0; i < direct.Count; i++)
                {
                    ITxObject obj = direct[i];
                    if (obj == null) continue;
                    if (excludeNames.Contains(obj.Name)) continue;

                    if (robotPos != null)
                    {
                        TxVector pos = null;
                        try
                        {
                            ITxLocatableObject lobj = obj as ITxLocatableObject;
                            if (lobj != null && lobj.AbsoluteLocation != null)
                                pos = lobj.AbsoluteLocation.Translation;
                        }
                        catch { }
                        if (pos == null) continue;

                        double dx = pos.X - robotPos.X;
                        double dy = pos.Y - robotPos.Y;
                        double dz = pos.Z - robotPos.Z;
                        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        // 原点超 2m 但包围盒进入扩展半径 → 也纳入
                        if (dist > NEARBY_RADIUS_MM
                            && !BoxWithinReach(obj, robotPos, NEARBY_RADIUS_MM + BOX_GAP_MM))
                            continue;
                    }

                    result.Add(obj);
                }
            }
            catch (Exception ex)
            {
                log.Log($"  枚举 PhysicalRoot 异常: {ex.Message}", "WARN");
            }
            return result;
        }

        // =====================================================================
        // 包围盒增强：对象包围盒是否有任何角落进入以 center 为中心、radius 的球内
        // =====================================================================
        private static bool BoxWithinReach(ITxObject obj, TxVector center, double radius)
        {
            if (obj == null || center == null) return false;
            try
            {
                dynamic d = obj;
                var box = d.BoundingBox;
                if (box == null) return false;

                double minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;
                bool ok = false;
                try
                {
                    minX = (double)box.MinX; maxX = (double)box.MaxX;
                    minY = (double)box.MinY; maxY = (double)box.MaxY;
                    minZ = (double)box.MinZ; maxZ = (double)box.MaxZ;
                    ok = true;
                }
                catch { }

                if (!ok) return false;

                double dx = DistanceToInterval(center.X, minX, maxX);
                double dy = DistanceToInterval(center.Y, minY, maxY);
                double dz = DistanceToInterval(center.Z, minZ, maxZ);
                return dx * dx + dy * dy + dz * dz <= radius * radius;
            }
            catch { return false; }
        }

        private static double DistanceToInterval(double v, double lo, double hi)
        {
            if (v < lo) return lo - v;
            if (v > hi) return v - hi;
            return 0;
        }
    }
}
