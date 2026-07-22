// TxTools.Agent / Ps / DeviceZAlignService.cs
// 无界面的设备 Z 向落地对齐，忠实复刻 DeviceZAligner 的多策略实现，作用于"当前选中"。
// 与 select_objects 配合：先选中要对齐的设备，再调 align_devices_z。
// 全程包在 Undo 块里 -> 对齐后可 Ctrl+Z 撤销。
//
// 注意：跳过枪/机器人/工具/夹爪/输送线等末端或非落地对象(同 DeviceZAligner 的 CheckShouldSkip)。

using System;
using System.Collections;
using System.Collections.Generic;
using Tecnomatix.Engineering;

namespace TxTools.Agent.Ps
{
    public static class DeviceZAlignService
    {
        private static readonly string[] SkipTypes =
            { "TxWeldGun", "TxGun", "TxGripper", "TxTool", "TxRobot", "TxHumanModel", "TxConveyor" };

        /// <summary>把当前选中的设备最低点对齐到世界 Z=0。返回结果摘要。</summary>
        public static string AlignSelection()
        {
            TxDocument doc = TxApplication.ActiveDocument;
            if (doc == null) return "ActiveDocument 为 null，无法对齐。";

            var targets = new List<ITxObject>();
            try
            {
                dynamic sel = TxApplication.ActiveSelection;
                dynamic items = sel.GetItems();
                var en = items as IEnumerable;
                if (en != null) foreach (var o in en) { var t = o as ITxObject; if (t != null) targets.Add(t); }
            }
            catch (Exception ex) { return "读取选中失败: " + ex.Message; }

            if (targets.Count == 0) return "当前没有选中对象。请先用 select_objects 选中要对齐的设备。";

            int aligned = 0, already = 0, skipped = 0, failed = 0;
            var notes = new List<string>();

            bool undo = BeginUndoBlock(doc, "设备Z向对齐(" + targets.Count + "个)");
            try
            {
                foreach (var obj in targets)
                {
                    string skip = CheckShouldSkip(obj);
                    if (!string.IsNullOrEmpty(skip)) { skipped++; notes.Add(SafeName(obj) + " 跳过(" + skip + ")"); continue; }

                    TxTransformation absTx = GetAbsoluteLocation(obj);
                    if (absTx == null) { failed++; notes.Add(SafeName(obj) + " 无法获取位置"); continue; }

                    string method;
                    double minZ = GetDeviceMinZ(obj, absTx, out method);
                    if (Math.Abs(minZ) < 0.01) { already++; continue; }

                    if (ApplyZOffset(obj, minZ)) { aligned++; notes.Add(SafeName(obj) + " 下移 " + minZ.ToString("F1") + "mm(" + method + ")"); }
                    else { failed++; notes.Add(SafeName(obj) + " 写入失败"); }
                }
            }
            finally { if (undo) EndUndoBlock(doc); }

            try { TxApplication.RefreshDisplay(); } catch { }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("对齐完成：成功 " + aligned + "，已在Z=0 " + already + "，跳过 " + skipped + "，失败 " + failed
                          + (undo ? "（可 Ctrl+Z 撤销）" : "（注意：Undo 未启动，不可撤销）"));
            int cap = Math.Min(notes.Count, 20);
            for (int i = 0; i < cap; i++) sb.AppendLine("• " + notes[i]);
            if (notes.Count > cap) sb.AppendLine("…(其余省略)");
            return sb.ToString();
        }

        // ───────── 以下为 DeviceZAligner 同款多策略实现 ─────────

        private static string CheckShouldSkip(ITxObject obj)
        {
            string typeName = obj.GetType().Name;
            foreach (var st in SkipTypes)
                if (typeName.IndexOf(st, StringComparison.OrdinalIgnoreCase) >= 0) return "类型 " + typeName;

            try
            {
                foreach (var iface in obj.GetType().GetInterfaces())
                {
                    var n = iface.Name;
                    if (n.Contains("Gun") || n.Contains("Gripper") || n.Contains("Tool") || n.Contains("Robot"))
                        return "接口 " + n;
                }
            }
            catch { }

            try
            {
                dynamic dobj = obj;
                dynamic parent = dobj.Parent;
                if (parent != null)
                {
                    string pt = parent.GetType().Name;
                    if (pt.Contains("Robot") || pt.Contains("Flange")) return "父级 " + pt + "(末端工具)";
                }
            }
            catch { }

            return "";
        }

        private static TxTransformation GetAbsoluteLocation(ITxObject obj)
        {
            try { if (obj is ITxLocatableObject loc) { var tx = loc.AbsoluteLocation; if (tx != null) return tx; } } catch { }
            try { dynamic d = obj; var tx = d.AbsoluteLocation as TxTransformation; if (tx != null) return tx; } catch { }
            try { dynamic d = obj; var tx = d.Location as TxTransformation; if (tx != null) return tx; } catch { }
            try { dynamic d = obj; var tx = d.AbsoluteFrame as TxTransformation; if (tx != null) return tx; } catch { }
            try { dynamic d = obj; var tx = d.LocationInWorld as TxTransformation; if (tx != null) return tx; } catch { }
            return null;
        }

        private static double ExtractZ(TxTransformation tx)
        {
            try { dynamic d = tx; return Convert.ToDouble(d[2, 3]); } catch { }
            try { dynamic d = tx; return Convert.ToDouble(d.Translation.Z); } catch { }
            try { dynamic d = tx; return Convert.ToDouble(d.Z); } catch { }
            try { dynamic d = tx; var v = d.TranslationVector; return Convert.ToDouble(v.Z); } catch { }
            return 0;
        }

        private static double[] ExtractTranslation(TxTransformation tx)
        {
            try { dynamic d = tx; return new double[] { Convert.ToDouble(d[0, 3]), Convert.ToDouble(d[1, 3]), Convert.ToDouble(d[2, 3]) }; } catch { }
            try { dynamic d = tx; dynamic t = d.Translation; return new double[] { Convert.ToDouble(t.X), Convert.ToDouble(t.Y), Convert.ToDouble(t.Z) }; } catch { }
            return null;
        }

        private static double GetDeviceMinZ(ITxObject obj, TxTransformation absTx, out string method)
        {
            method = "坐标原点";
            try
            {
                if (obj is TxComponent comp)
                {
                    dynamic dComp = comp;
                    try { dynamic pts = dComp.GetLocationAxisIntersectionPoints(2); double m = ExtractMinZFromPoints(pts); if (m < double.MaxValue) { method = "轴交点"; return m; } } catch { }
                    try { dynamic pts = dComp.GetLocationAxisIntersectionPoints(); double m = ExtractMinZFromPoints(pts); if (m < double.MaxValue) { method = "轴交点"; return m; } } catch { }
                }
            }
            catch { }
            return ExtractZ(absTx); // 回退：设备原点 Z（PS 中设备原点通常在底部）
        }

        private static double ExtractMinZFromPoints(object points)
        {
            double minZ = double.MaxValue;
            if (points == null) return minZ;
            try
            {
                var en = points as IEnumerable;
                if (en != null) { foreach (object pt in en) { double z = ExtractZFromPoint(pt); if (z < minZ) minZ = z; } return minZ; }
            }
            catch { }
            double s = ExtractZFromPoint(points);
            if (s < minZ) minZ = s;
            return minZ;
        }

        private static double ExtractZFromPoint(object pt)
        {
            if (pt == null) return double.MaxValue;
            try { dynamic d = pt; return Convert.ToDouble(d.Z); } catch { }
            try { dynamic d = pt; return Convert.ToDouble(d[2, 3]); } catch { }
            try { dynamic d = pt; return Convert.ToDouble(d.Translation.Z); } catch { }
            return double.MaxValue;
        }

        private static bool ApplyZOffset(ITxObject obj, double offsetZ)
        {
            if (Math.Abs(offsetZ) < 0.01) return true;
            TxTransformation curTx = GetAbsoluteLocation(obj);
            if (curTx == null) return false;
            double[] xyz = ExtractTranslation(curTx);
            if (xyz == null) return false;
            double newZ = xyz[2] - offsetZ;

            bool written = false;
            try { dynamic d = curTx; d[2, 3] = newZ; written = true; } catch { }
            if (!written) try { dynamic d = curTx; d.Translation = new TxVector(xyz[0], xyz[1], newZ); written = true; } catch { }
            if (!written) try { dynamic d = curTx; d.Z = newZ; written = true; } catch { }
            if (!written) return false;

            bool applied = false;
            try { if (obj is ITxLocatableObject loc) { loc.AbsoluteLocation = curTx; applied = true; } } catch { }
            if (!applied) try { dynamic d = obj; d.AbsoluteLocation = curTx; applied = true; } catch { }
            if (!applied) try { dynamic d = obj; d.Location = curTx; applied = true; } catch { }
            if (!applied) try { dynamic d = obj; d.SetAbsoluteLocation(curTx); applied = true; } catch { }
            return applied;
        }

        private static bool BeginUndoBlock(TxDocument doc, string desc)
        {
            try { dynamic d = doc; dynamic ur = d.UndoRedo; if (ur != null) { ur.BeginCommand(desc); return true; } } catch { }
            try { dynamic d = doc; dynamic ctx = d.UndoContext; if (ctx != null) { ctx.Open(desc); return true; } } catch { }
            try { dynamic d = TxApplication.ActiveDocument; dynamic um = d.UndoManager; if (um != null) { um.BeginUndoStep(desc); return true; } } catch { }
            return false;
        }

        private static void EndUndoBlock(TxDocument doc)
        {
            try { dynamic d = doc; d.UndoRedo.EndCommand(); return; } catch { }
            try { dynamic d = doc; d.UndoContext.Close(); return; } catch { }
            try { dynamic d = doc; d.UndoManager.EndUndoStep(); return; } catch { }
        }

        private static string SafeName(ITxObject o)
        {
            try { return string.IsNullOrEmpty(o.Name) ? o.GetType().Name : o.Name; } catch { return "?"; }
        }
    }
}
