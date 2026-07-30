// ============================================================================
// RobotFinder.cs
//
// 三套接口：
//   FindAssociatedRobot(op, doc, log)        — 6 种策略找操作关联的机器人
//   FindAssociatedRobotSilent(op)            — 静默版本（UI 加载阶段用）
//
// 唯一标识策略：机器人/操作一律用对象实例（GetHashCode 可作日志标识）直接引用，
// 不做按名查找 / 同名消歧，避免同名对象拿错副本。
// ============================================================================
using System;
using Tecnomatix.Engineering;
using TxTools.RobotReachabilityChecker.Diagnostics;

namespace TxTools.RobotReachabilityChecker.Services
{
    public static class RobotFinder
    {
        // =====================================================================
        // 找操作关联的机器人 — 6 种策略
        // =====================================================================
        public static TxRobot FindAssociatedRobot(ITxObject operation, TxDocument doc, ILogger log = null)
        {
            log = log ?? NullLogger.Instance;
            if (operation == null) return null;

            // 方式1：.Robot 属性
            try
            {
                dynamic dop = operation;
                var r = dop.Robot as TxRobot;
                if (r != null) { log.Log($"  关联机器人(.Robot): '{r.Name}' (HashCode={r.GetHashCode()})"); return r; }
            }
            catch { }

            // 方式2：.Device 属性
            try { dynamic dop = operation; var r = dop.Device as TxRobot; if (r != null) { log.Log($"  关联机器人(.Device): {r.Name}", "DEBUG"); return r; } } catch { }

            // 方式3：.RobotDevice 属性
            try { dynamic dop = operation; var r = dop.RobotDevice as TxRobot; if (r != null) { log.Log($"  关联机器人(.RobotDevice): {r.Name}", "DEBUG"); return r; } } catch { }

            // 方式4：Parent 链上溯找 TxRobot
            try
            {
                dynamic cur = operation;
                for (int depth = 0; depth < 10; depth++)
                {
                    object parent = null;
                    try { parent = cur.Parent; } catch { break; }
                    if (parent == null) break;
                    if (parent is TxRobot rp) { log.Log($"  关联机器人(Parent链 depth={depth}): {rp.Name}", "DEBUG"); return rp; }
                    cur = parent;
                }
            }
            catch { }

            // 方式5：ParentOperation.Robot
            try
            {
                dynamic dop = operation;
                object parentCompound = dop.ParentOperation;
                if (parentCompound != null)
                {
                    dynamic dc = parentCompound;
                    var r = dc.Robot as TxRobot;
                    if (r != null) { log.Log($"  关联机器人(ParentOperation.Robot): {r.Name}"); return r; }
                }
            }
            catch { }

            // 方式6：复合操作 → 遍历子操作的 .Robot
            if (operation is TxCompoundOperation compound)
            {
                try
                {
                    var children = compound.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject)));
                    if (children != null)
                    {
                        foreach (ITxObject child in children)
                        {
                            if (child == null) continue;
                            try
                            {
                                dynamic dc = child;
                                var r = dc.Robot as TxRobot;
                                if (r != null) { log.Log($"  关联机器人(子操作.Robot [{child.Name}]): {r.Name}"); return r; }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            log.Log("  所有方式均未能找到关联机器人", "WARN");
            return null;
        }

        /// <summary>FindAssociatedRobot 的静默版本（不打"未找到"警告日志）—— 用于 UI 加载阶段。</summary>
        public static TxRobot FindAssociatedRobotSilent(ITxObject operation)
        {
            if (operation == null) return null;
            try { dynamic dop = operation; var r = dop.Robot as TxRobot; if (r != null) return r; } catch { }
            try { dynamic dop = operation; var r = dop.Device as TxRobot; if (r != null) return r; } catch { }
            try { dynamic dop = operation; var r = dop.RobotDevice as TxRobot; if (r != null) return r; } catch { }
            try
            {
                dynamic cur = operation;
                for (int depth = 0; depth < 10; depth++)
                {
                    object parent = null;
                    try { parent = cur.Parent; } catch { break; }
                    if (parent == null) break;
                    if (parent is TxRobot rp) return rp;
                    cur = parent;
                }
            }
            catch { }
            try
            {
                dynamic dop = operation;
                object parentCompound = dop.ParentOperation;
                if (parentCompound != null)
                {
                    dynamic dc = parentCompound;
                    var r = dc.Robot as TxRobot;
                    if (r != null) return r;
                }
            }
            catch { }
            if (operation is TxCompoundOperation compound)
            {
                try
                {
                    var children = compound.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject)));
                    if (children != null)
                    {
                        foreach (ITxObject child in children)
                        {
                            if (child == null) continue;
                            try { dynamic dc = child; var r = dc.Robot as TxRobot; if (r != null) return r; } catch { }
                        }
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
