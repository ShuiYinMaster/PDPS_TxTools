// SpotMatchEngine.cs  —  C# 7.3
// 匹配条件（对齐第三方规格）：两个独立开关 焊点距离匹配 / 焊点名匹配
//   仅距离：距离不超过阈值。仅名称：规范化后的名称在两侧均唯一。
//   都勾：同名且距离不超过阈值。都不勾：按距离。
//
//   A 位置更新：操作配对（数量→名→质心）+ 操作内焊点匹配（双开关）；写 ref ← target 坐标
//   B 新焊点分配：待分配操作集展平成焊点池；遍历每条参考轨迹的焊点 → 池里找匹配
//   C 对称分配  ：同 B，但参考焊点先关于对称中心 XZ 镜像后再比距离
//   B/C 输出：每条参考轨迹一个 OpMatch（写入层据此 Paste 出 _Mapped 轨迹）

using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.WeldSpotAllocator
{
    public sealed class NameOptions
    {
        public bool IgnoreCase = true;
        public bool StripDigits = false;
        public string StripPrefix = null;
        public string StripSuffix = null;

        public string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Trim();
            var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.IsNullOrEmpty(StripPrefix) && t.StartsWith(StripPrefix, comparison)) t = t.Substring(StripPrefix.Length);
            if (!string.IsNullOrEmpty(StripSuffix) && t.EndsWith(StripSuffix, comparison)) t = t.Substring(0, t.Length - StripSuffix.Length);
            t = t.Trim();
            if (StripDigits) t = t.TrimEnd('0','1','2','3','4','5','6','7','8','9','_','-',' ');
            if (IgnoreCase) t = t.ToUpperInvariant();
            return t;
        }
        public bool Eq(string a, string b)
        {
            var normalized = Norm(a);
            return normalized.Length > 0 && normalized == Norm(b);
        }
    }

    public sealed class MatchSettings
    {
        public NameOptions Names = new NameOptions();
        public bool ByDistance = true;
        public bool ByName = false;
        public double MaxDistMm = 5.0;
        public bool CountStrict = true;
        public int CountTolerance = 0;
        public bool DiffFrame = false;
        internal double[] ReferenceFrame;
        internal double[] TargetFrame;
        internal string TargetFrameSource;

        public bool BothOn => ByDistance && ByName;
        public bool DistOnly => ByDistance && !ByName;
        public bool NameOnly => !ByDistance && ByName;
    }

    public static class SpotMatchEngine
    {
        // ── 入口 ──────────────────────────────────────────────────────────
        public static AllocPlan Build(AllocMode mode,
            IList<OpData> refOps, IList<OpData> targetOps,
            MatchSettings cfg, FlipAxis flip,
            bool copyVias, bool copyRot, bool copyParams, bool consume, Action<string> log)
        {
            log = log ?? (s => { });
            bool assign = mode != AllocMode.UpdatePosition;
            bool symmetric = mode == AllocMode.Symmetric;
            var plan = new AllocPlan
            {
                Mode = mode, Flip = symmetric ? flip : FlipAxis.None, CopyVias = symmetric && copyVias,
                CopyRotation = assign && copyRot, CopyParams = assign && copyParams,
                ConsumeTargets = assign && consume, DiffFrame = assign && cfg.DiffFrame
            };

            switch (mode)
            {
                case AllocMode.UpdatePosition: BuildUpdate(plan, refOps, targetOps, cfg, log); break;
                case AllocMode.NewSpot:        BuildAssign(plan, refOps, targetOps, cfg, log); break;
                case AllocMode.Symmetric:      BuildAssign(plan, refOps, targetOps, cfg, log); break;
            }
            return plan;
        }

        private static HashSet<string> FindAmbiguousNames(IEnumerable<SpotData> references,
            IEnumerable<SpotData> targets, NameOptions names)
        {
            var groups = references.GroupBy(s => names.Norm(s.Name))
                .Concat(targets.GroupBy(s => names.Norm(s.Name)));
            return new HashSet<string>(groups.Where(g => g.Key.Length > 0 && g.Skip(1).Any()).Select(g => g.Key));
        }

        private static SpotMatch MatchSpot(SpotData reference, double[] referencePosition,
            IEnumerable<SpotData> targets, HashSet<SpotData> used, HashSet<string> ambiguous,
            MatchSettings cfg, AllocPlan plan, IReadOnlyDictionary<SpotData, double[]> localTargetPositions = null)
        {
            if (ambiguous != null && ambiguous.Contains(cfg.Names.Norm(reference.Name)))
            {
                plan.Warnings.Add($"焊点[{reference.Name}] 规范化后存在重名，仅名称匹配已跳过。");
                return null;
            }

            SpotData best = null;
            double bestDistance = double.PositiveInfinity;
            foreach (var target in targets)
            {
                if (used.Contains(target) || (cfg.ByName && !cfg.Names.Eq(reference.Name, target.Name))) continue;
                var targetPosition = localTargetPositions == null ? target.Position : localTargetPositions[target];
                double distance = referencePosition == null || targetPosition == null ? double.NaN
                    : Math.Sqrt(SymmetryMath.Dist2(referencePosition, targetPosition));
                if (!cfg.NameOnly && !(distance <= cfg.MaxDistMm && distance < bestDistance)) continue;
                best = target;
                bestDistance = distance;
                if (cfg.NameOnly) break;
            }
            if (best == null) return null;
            used.Add(best);
            var by = cfg.NameOnly ? MatchBy.Name : cfg.BothOn ? MatchBy.NameAndPosition : MatchBy.Position;
            return new SpotMatch { Ref = reference, Target = best, Dist = bestDistance, By = by };
        }

        // ── A 位置更新 ────────────────────────────────────────────────────
        private static void BuildUpdate(AllocPlan plan, IList<OpData> refOps, IList<OpData> targetOps, MatchSettings cfg, Action<string> log)
        {
            if (refOps == null || targetOps == null) { plan.Warnings.Add("参考/目标操作集为空"); return; }
            var usedT = new HashSet<OpData>();

            foreach (var rOp in refOps)
            {
                OpData best = PickOpPartner(rOp, targetOps, usedT, cfg);
                if (best == null) { plan.Warnings.Add($"操作[{rOp.Name}] 未找到配对（数量 {rOp.SpotCount}）"); continue; }
                usedT.Add(best);

                var om = new OpMatch { RefOp = rOp, TargetOp = best };
                MatchSpotsWithin(rOp.Spots, best.Spots, cfg, om, plan);
                plan.OpMatches.Add(om);
            }
            foreach (var t in targetOps) if (!usedT.Contains(t))
                plan.Warnings.Add($"新版操作[{t.Name}] 未被匹配（数量 {t.SpotCount}）");
        }

        private static OpData PickOpPartner(OpData rOp, IList<OpData> targets, HashSet<OpData> used, MatchSettings cfg)
        {
            IEnumerable<OpData> pool = targets.Where(t => !used.Contains(t));
            pool = cfg.CountStrict ? pool.Where(t => t.SpotCount == rOp.SpotCount)
                                   : pool.Where(t => Math.Abs(t.SpotCount - rOp.SpotCount) <= cfg.CountTolerance);
            var cand = pool.ToList();
            if (cand.Count == 0) return null;
            if (cand.Count == 1) return cand[0];

            var operationNames = new NameOptions();
            var byName = cand.FirstOrDefault(t => operationNames.Eq(t.Name, rOp.Name));
            if (byName != null) return byName;

            var rc = rOp.Centroid();
            if (rc == null) return cand[0];
            OpData best = null; double bd = double.MaxValue;
            foreach (var t in cand) { var tc = t.Centroid(); if (tc == null) continue; double d = SymmetryMath.Dist2(rc, tc); if (d < bd) { bd = d; best = t; } }
            return best ?? cand[0];
        }

        // 操作内焊点匹配，方向为 ref ← target。
        private static void MatchSpotsWithin(IList<SpotData> refs, IList<SpotData> targets,
            MatchSettings cfg, OpMatch om, AllocPlan plan)
        {
            var usedT = new HashSet<SpotData>();
            var ambiguous = cfg.NameOnly ? FindAmbiguousNames(refs, targets, cfg.Names) : null;
            foreach (var r in refs)
            {
                var match = MatchSpot(r, r.Position, targets, usedT, ambiguous, cfg, plan);
                if (match != null) om.Matches.Add(match);
            }
        }

        // B/C 的所有目标操作共享焊点池。
        private static void BuildAssign(AllocPlan plan, IList<OpData> refOps, IList<OpData> targetOps, MatchSettings cfg, Action<string> log)
        {
            var pool = new List<SpotData>();
            var targetOwners = plan.DiffFrame ? new Dictionary<SpotData, OpData>() : null;
            if (targetOps != null)
                foreach (var op in targetOps)
                {
                    pool.AddRange(op.Spots);
                    if (targetOwners != null)
                        foreach (var spot in op.Spots) targetOwners[spot] = op;
                }
            if (refOps == null || pool.Count == 0) { plan.Warnings.Add("参考操作集 / 待分配焊点为空"); return; }

            var localTargetPositions = plan.DiffFrame ? ReadLocalTargetPositions(targetOwners, cfg, plan) : null;
            var ambiguous = cfg.NameOnly ? FindAmbiguousNames(refOps.SelectMany(op => op.Spots), pool, cfg.Names) : null;
            var usedT = new HashSet<SpotData>();
            foreach (var refOp in refOps)
            {
                var om = new OpMatch { RefOp = refOp };
                foreach (var r in refOp.Spots)
                {
                    double[] referenceFrame = cfg.ReferenceFrame ?? (plan.DiffFrame ? refOp.OperationFrame : r.SymCenter);
                    bool missingFrame = plan.UsesReferenceFrame && referenceFrame == null;
                    string frameSource = cfg.ReferenceFrame != null ? "指定的参考车件坐标"
                        : (plan.DiffFrame ? refOp.OperationFrameName : r.ReferenceFrameSource) ?? "缺少坐标基准";
                    if (missingFrame && !cfg.NameOnly)
                    {
                        plan.Warnings.Add($"参考焊点[{r.Name}] {frameSource}，无法按距离匹配。");
                        continue;
                    }

                    double[] rPos = missingFrame ? null : r.Position;
                    double[] resolved = null;
                    if (plan.UsesReferenceFrame && !missingFrame)
                    {
                        resolved = SymmetryMath.ResolveRef(r.Matrix, referenceFrame, plan.Mode, plan.DiffFrame, FlipAxis.None);
                        rPos = new[] { resolved[3], resolved[7], resolved[11] };
                    }
                    var match = MatchSpot(r, rPos, pool, usedT, ambiguous, cfg, plan, localTargetPositions);
                    if (match == null) continue;
                    match.Mirrored = resolved != null;
                    match.RefMirror = resolved;
                    var targetOp = plan.DiffFrame ? targetOwners[match.Target] : null;
                    double[] targetFrame = cfg.TargetFrame ?? targetOp?.OperationFrame;
                    if (plan.UsesReferenceFrame)
                    {
                        match.FrameDetails = $"参考操作「{refOp.Name}」  {frameSource}";
                        if (targetOp != null)
                        {
                            string targetSource = cfg.TargetFrameSource ?? (cfg.TargetFrame != null ? "指定的目标车件坐标"
                                : targetOp.OperationFrameName ?? "缺少坐标基准");
                            match.FrameDetails += $"\n目标操作「{targetOp.Name}」  {targetSource}";
                        }
                    }
                    om.Matches.Add(match);
                    if (plan.CopyRotation)
                        PrepareRotation(match, referenceFrame, targetFrame, plan);
                }
                if (om.Matches.Count > 0 && plan.CopyVias && refOp.Vias.Count > 0)
                {
                    om.ViaReferenceFrame = cfg.ReferenceFrame ?? (plan.DiffFrame ? refOp.OperationFrame
                        : SymmetryMath.SharedFrame(refOp.Spots.Select(s => s.SymCenter)));
                    if (plan.DiffFrame)
                    {
                        var frames = om.Matches.Select(m => cfg.TargetFrame ?? targetOwners[m.Target].OperationFrame).ToList();
                        if (frames.All(frame => frame != null)) om.ViaTargetFrame = SymmetryMath.SharedFrame(frames);
                    }
                    if (om.ViaReferenceFrame == null || (plan.DiffFrame && om.ViaTargetFrame == null))
                        AddWriteBlocker(plan, $"轨迹[{refOp.Name}] 缺少一致的参考或目标车件坐标，无法镜像过渡点。");
                }
                plan.OpMatches.Add(om);
            }
            foreach (var t in pool) if (!usedT.Contains(t)) plan.Warnings.Add($"待分配焊点[{t.Name}] 未匹配到参考焊点");
            log($"[Match] 命中 {plan.TotalMatches} 点，参考轨迹 {plan.OpMatches.Count} 条");
        }

        private static Dictionary<SpotData, double[]> ReadLocalTargetPositions(Dictionary<SpotData, OpData> owners,
            MatchSettings cfg, AllocPlan plan)
        {
            var positions = new Dictionary<SpotData, double[]>();
            foreach (var entry in owners)
            {
                var target = entry.Key;
                var frame = cfg.TargetFrame ?? entry.Value.OperationFrame;
                positions[target] = frame == null ? null : SymmetryMath.LocalPosition(target.Matrix, frame);
                if (frame == null && !cfg.NameOnly)
                    plan.Warnings.Add($"目标焊点[{target.Name}] {entry.Value.OperationFrameName ?? "缺少操作坐标基准"}，无法按距离匹配。");
            }
            return positions;
        }

        private static void PrepareRotation(SpotMatch match, double[] referenceFrame, double[] targetFrame, AllocPlan plan)
        {
            string failure = null;
            if (plan.UsesReferenceFrame && referenceFrame == null) failure = "缺少参考侧车件坐标";
            else if (plan.DiffFrame && targetFrame == null) failure = "缺少目标侧车件坐标";
            else if (match.Ref.TrajectoryMatrix == null) failure = "无法读取参考轨迹点位的姿态";
            else if (match.Target.TrajectoryMatrix == null) failure = "无法读取目标轨迹点位的位姿";
            else
            {
                try
                {
                    match.RotationInTargetWorld = SymmetryMath.ResolveWritePose(match.Ref.TrajectoryMatrix,
                        referenceFrame, targetFrame, plan.Mode, plan.DiffFrame, plan.Flip);
                }
                catch (Exception ex) { failure = ex.Message; }
            }
            if (failure != null)
                AddWriteBlocker(plan, $"焊点[{match.Ref.Name} → {match.Target.Name}] 无法复制旋转姿态。{failure}。");
        }

        private static void AddWriteBlocker(AllocPlan plan, string message)
        {
            plan.WriteBlockers.Add(message);
            plan.Warnings.Add(message);
        }
    }
}
