// ============================================================================
// ToolFrameSwitcher.cs  (P0-3)
//
// TCPF 切换助手。修复审查报告 2.x：
//   · 引用级优先：对"属于本机器人"的工具帧，尝试 robot.Toolframe 引用级切换
//     （若 SDK 允许，语义更正确）；失败或非本机帧 → 回退 TCPF.AbsoluteLocation。
//   · 切换失败 → 返回 null + error，调用方必须中断该点（不再静默继续）。
//   · ValidateFrameOwnership：判断点位工具帧是否属于本机器人，外帧给显式警告
//     （覆盖"点位引用了非本机器人系统帧"场景）。
// ============================================================================
using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;
using TxTools.RobotReachabilityChecker.Diagnostics;

namespace TxTools.RobotReachabilityChecker.Services
{
    public static class ToolFrameSwitcher
    {
        /// <summary>
        /// 切换机器人工具到指定帧。
        /// 成功返回恢复动作（调用以还原 Toolframe/TCPF）；失败返回 null 并给出 error。
        /// </summary>
        public static Action SwitchToFrame(TxRobot robot, TxFrame frame, ILogger log, out string error)
        {
            error = null;
            if (robot == null || frame == null) { error = "robot/frame 为空"; return null; }

            // 读取原始引用/位置（恢复用）
            object savedTool = null;
            bool readTool = false;
            try { savedTool = robot.Toolframe; readTool = true; } catch { }

            TxTransformation savedAbs = null;
            try { savedAbs = robot.TCPF.AbsoluteLocation; } catch { }

            // ── 方式1：引用级切换（仅本机器人工具帧，避免把外部系统帧设成工具定义） ──
            if (readTool && IsUnder(robot, frame))
            {
                try
                {
                    dynamic d = robot;
                    d.Toolframe = frame;
                    log?.Log($"  ✓ 工具已引用级切换到 [{frame.Name}]", "DEBUG");
                    return () =>
                    {
                        try { dynamic d2 = robot; d2.Toolframe = savedTool; } catch { }
                        try { if (savedAbs != null) robot.TCPF.AbsoluteLocation = savedAbs; } catch { }
                    };
                }
                catch
                {
                    // 只读或引用切换失败 → 回退 AbsoluteLocation
                }
            }

            // ── 方式2：AbsoluteLocation 切换 ──
            try
            {
                var abs = frame.AbsoluteLocation;
                if (abs == null) { error = $"工具帧 [{frame.Name}] 无 AbsoluteLocation"; return null; }
                robot.TCPF.AbsoluteLocation = abs;
                return () =>
                {
                    try { if (savedAbs != null) robot.TCPF.AbsoluteLocation = savedAbs; } catch { }
                    try { if (readTool && savedTool != null) { dynamic d2 = robot; d2.Toolframe = savedTool; } } catch { }
                };
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        /// <summary>校验工具帧归属。返回警告文案；属于本机或名称匹配本机已知工具帧时返回 null。</summary>
        public static string ValidateFrameOwnership(TxRobot robot, TxFrame frame)
        {
            if (robot == null || frame == null) return null;
            try
            {
                if (IsUnder(robot, frame)) return null;

                var known = new HashSet<string>();
                try { AddName(known, robot.Toolframe); } catch { }
                try { AddName(known, robot.TCPF); } catch { }
                try { AddName(known, robot.Baseframe); } catch { }
                try { AddName(known, robot.Referenceframe); } catch { }
                try
                {
                    var mt = robot.MountedTools;
                    if (mt != null) for (int i = 0; i < mt.Count; i++) AddName(known, mt[i]);
                }
                catch { }

                if (!string.IsNullOrEmpty(frame.Name) && known.Contains(frame.Name)) return null;
                return $"警告：点位工具帧 [{frame.Name}] 不在本机器人已安装工具/帧中，可达性结果可能不可信";
            }
            catch { return null; }
        }

        // =====================================================================
        // 辅助
        // =====================================================================
        private static bool IsUnder(TxRobot robot, TxFrame frame)
        {
            try
            {
                dynamic cur = frame;
                for (int depth = 0; depth < 30; depth++)
                {
                    object parent = null;
                    try { parent = cur.Parent; } catch { break; }
                    if (parent == null) break;
                    if (parent is TxRobot) return true;
                    if (ReferenceEquals(parent, robot)) return true;
                    try { if (((ITxObject)parent)?.Name == robot.Name) return true; } catch { }
                    cur = parent;
                }
            }
            catch { }
            return false;
        }

        private static void AddName(HashSet<string> set, object obj)
        {
            try { if (obj is ITxObject t && !string.IsNullOrEmpty(t.Name)) set.Add(t.Name); } catch { }
        }
    }
}
