// SpotReader.cs  —  C# 7.3
// 从操作树读取焊接操作 → 有序焊点/过渡点，持有底层 TxWeldPoint / via 引用（写入层需要）。
// 复用 PsReader 的静态工具：TxToArr / GetAppearancesFromWeldPoint。
//
// 通过 SDK 有序集合读取焊接位置操作和过渡点。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Tecnomatix.Engineering;
using TxTools.ExportGun;

namespace TxTools.WeldSpotAllocator
{
    public static class SpotReader
    {
        private static readonly TxTypeFilter AnyFilter = new TxTypeFilter(typeof(ITxObject));

        // ── 读取一条焊接操作 ────────────────────────────────────────────────
        public static OpData ReadOneWeldOp(ITxObject weldOp, Action<string> log)
        {
            var od = new OpData { Name = SafeName(weldOp), Raw = weldOp };
            foreach (ITxObject child in EnumChildrenOrdered(weldOp))
            {
                try
                {
                    if (child is TxWeldLocationOperation weld)
                    {
                        var wp = weld.WeldPoint;
                        if (wp == null) continue;
                        var sd = MakeWeldSpot(wp, weld);
                        od.Spots.Add(sd); od.Ordered.Add(sd);
                    }
                    else if (child is TxRoboticViaLocationOperation via)
                    {
                        var sd = MakeViaSpot(via);
                        if (sd != null) { od.Vias.Add(sd); od.Ordered.Add(sd); }
                    }
                }
                catch (Exception ex) { log($"[Reader] [{od.Name}] 子项[{child.GetType().Name}] 解析异常：{ex.Message}"); }
            }
            return od;
        }

        // 有序集合使用 SDK 的索引顺序。
        private static IEnumerable<ITxObject> EnumChildrenOrdered(ITxObject weldOp)
        {
            if (weldOp is ITxOrderedObjectCollection ordered)
            {
                int count = ordered.Count;
                for (int i = 0; i < count; i++)
                    yield return ordered.GetChildAt(i);
                yield break;
            }
            var kids = DirectKids(weldOp);
            if (kids != null) foreach (ITxObject k in kids) yield return k;
        }

        // ── 构造 SpotData ───────────────────────────────────────────────────
        private static SpotData MakeWeldSpot(TxWeldPoint wp, TxWeldLocationOperation locOp)
        {
            var tx = wp.AbsoluteLocation;
            if (tx == null) throw new InvalidOperationException("无法读取制造焊点的位姿。");
            var m = PsReader.TxToArr(tx);
            var trajectory = SafeTx(() => locOp.AbsoluteLocation);
            var frame = ReadSymCenter(wp, locOp, out var frameSource);
            var sd = new SpotData
            {
                Name = wp.Name,
                Kind = PointType.WeldPoint,
                Matrix = m,
                TrajectoryMatrix = trajectory == null ? null : PsReader.TxToArr(trajectory),
                Position = new[] { m[3], m[7], m[11] },
                SymCenter = frame,
                ReferenceFrameSource = frameSource,
                Raw = wp,
                LocOp = locOp
            };
            return sd;
        }

        private static SpotData MakeViaSpot(TxRoboticViaLocationOperation via)
        {
            var tx = via.AbsoluteLocation;
            if (tx == null) return null;
            double[] m = PsReader.TxToArr(tx);
            return new SpotData
            {
                Name = SafeName(via),
                Kind = PointType.PathPoint,
                Matrix = m,
                Position = new[] { m[3], m[7], m[11] },
                Raw = via,
                LocOp = via
            };
        }

        // ── 小工具 ──────────────────────────────────────────────────────────
        private static double[] ReadSymCenter(TxWeldPoint wp, TxWeldLocationOperation locOp, out string source)
        {
            var frame = ReadAttachmentFrame(locOp, "轨迹点位", out source);
            if (frame != null) return frame;

            try
            {
                var candidates = wp.AssignedParts?.OfType<ITxLocatableObject>()
                    .Select(part => new { Part = part, Pose = SafeTx(() => part.AbsoluteLocation) })
                    .Where(part => part.Pose != null).ToList();
                if (candidates != null && candidates.Count > 0)
                {
                    frame = SymmetryMath.SharedFrame(candidates.Select(part => PsReader.TxToArr(part.Pose)));
                    source = frame == null ? "制造焊点关联零件的坐标基准不一致"
                        : $"制造焊点关联零件「{SafeName(candidates[0].Part)}」";
                    return frame;
                }
            }
            catch { }

            return ReadAttachmentFrame(wp, "制造焊点", out source);
        }

        private static double[] ReadAttachmentFrame(ITxLocatableObject point, string pointKind, out string source)
        {
            source = "未找到参考坐标基准";
            try
            {
                var parent = point.AttachmentParent;
                var pose = parent?.AbsoluteLocation;
                if (pose == null) return null;
                source = $"{pointKind}附着父对象「{SafeName(parent)}」";
                return PsReader.TxToArr(pose);
            }
            catch { return null; }
        }

        private static TxObjectList DirectKids(ITxObject node)
        {
            try { dynamic d = node; return d.GetDirectDescendants(AnyFilter) as TxObjectList; } catch { }
            // 兜底：可枚举
            try { if (node is IEnumerable ie) { var l = new TxObjectList(); foreach (ITxObject c in ie) l.Add(c); return l; } } catch { }
            return null;
        }

        private static TxTransformation SafeTx(Func<TxTransformation> f) { try { return f(); } catch { return null; } }
        private static string SafeName(ITxObject o) { try { dynamic d = o; return (d.Name as string) ?? o.GetType().Name; } catch { return o.GetType().Name; } }
    }
}
