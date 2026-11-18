// SpotWriter.cs  —  C# 7.3
// A 原地更新制造焊点。B/C 复制参考轨迹，替换其中的焊接位置操作。

using TxTools.ExportGun;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Tecnomatix.Engineering;

namespace TxTools.WeldSpotAllocator
{
    public sealed class WriteReport
    {
        public int Updated, Created, ViasMirrored, Consumed, Copied, RotationsCopied, ParameterLocationsCopied, Failed;
        public readonly List<string> Errors = new List<string>();
        public override string ToString()
            => $"更新 {Updated} · 挪入新轨迹 {Consumed} · 复制到新轨迹 {Copied} · 删参考占位 {Created} · 姿态 {RotationsCopied} · 参数 {ParameterLocationsCopied} · 镜像过渡点 {ViasMirrored} · 失败 {Failed}";
    }

    public static class SpotWriter
    {
        public static WriteReport ApplyPlan(AllocPlan plan, bool aPosOnly, Action<string> log)
        {
            log = log ?? (s => { });
            if (plan.WriteBlockers.Count > 0)
            {
                var report = new WriteReport();
                foreach (var blocker in plan.WriteBlockers) Fail(report, blocker);
                return report;
            }
            return plan.Mode == AllocMode.UpdatePosition ? ApplyUpdate(plan, aPosOnly, log)
                                                         : ApplyAssignment(plan, log);
        }

        // ════════════════════════════════════════════════════════════
        //  A 位置更新（原地写 ref ← target）
        // ════════════════════════════════════════════════════════════
        private static WriteReport ApplyUpdate(AllocPlan plan, bool posOnly, Action<string> log)
        {
            var rep = new WriteReport();
            object um = OpenUndo("焊点位置更新", log);
            using ((TxTools.Common.SceneUndoScope)um)
            try
            {
                foreach (var om in plan.OpMatches)
                    foreach (var mt in om.Matches)
                    {
                        try
                        {
                            var wp = mt.Ref.Raw as TxWeldPoint;
                            if (wp == null) { Fail(rep, $"[{mt.Ref.Name}] 非 TxWeldPoint"); continue; }
                            double[] m = posOnly ? MergePosKeepRot(wp, mt.Target.Position) : mt.Target.Matrix;
                            wp.AbsoluteLocation = PsReader.ArrToTxPublic(m);
                            rep.Updated++;
                        }
                        catch (Exception ex) { Fail(rep, $"[{mt.Ref?.Name}] {ex.Message}"); }
                    }
                CommitUndo(um, log);
            }
            catch (Exception ex) { AbortUndo(um, log); Fail(rep, "事务异常：" + ex.Message); }
            log("[Writer] " + rep);
            return rep;
        }

        private static WriteReport ApplyAssignment(AllocPlan plan, Action<string> log)
        {
            var rep = new WriteReport();
            using (TxTools.Common.SceneUndoScope.Begin("焊点分配"))
            {
                foreach (var operation in plan.OpMatches)
                {
                    if (operation.Matches.Count == 0) continue;
                    try { MapOperation(operation, plan, rep, log); }
                    catch (Exception ex) { Fail(rep, $"[{operation.RefOp.Name}] {ex.Message}"); }
                }
            }
            log("[Writer] " + rep);
            return rep;
        }

        private static void MapOperation(OpMatch operation, AllocPlan plan, WriteReport rep, Action<string> log)
        {
            var source = operation.RefOp.Raw;
            var parent = source.Collection ?? throw new InvalidOperationException("参考轨迹没有父集合。");
            var mapped = (ITxOrderedObjectCollection)PasteCopy(parent, source, out var copies);
            var placeholders = new List<TxWeldLocationOperation>();
            var matches = new Dictionary<string, SpotMatch>();
            try
            {
                var name = plan.GetMappedName(operation.RefOp);
                mapped.Name = name;
                if (!string.Equals(mapped.Name, name, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Process Simulate 未接受新轨迹名称「{name}」。");
                for (int i = 0; i < mapped.Count; i++)
                    if (mapped.GetChildAt(i) is TxWeldLocationOperation location)
                        placeholders.Add(location);
                foreach (var match in operation.Matches)
                {
                    if (!(copies[match.Ref.LocOp] is TxWeldLocationOperation placeholder) ||
                        !placeholders.Contains(placeholder))
                        throw new InvalidOperationException($"无法定位参考点位 {match.Ref.Name} 的副本。");
                    matches.Add(placeholder.Id, match);
                }
            }
            catch
            {
                mapped.Delete();
                throw;
            }

            foreach (var placeholder in placeholders)
            {
                var name = placeholder.Name;
                try
                {
                    if (matches.TryGetValue(placeholder.Id, out var match))
                        AssignLocation(mapped, placeholder, match, plan, rep);
                }
                catch (Exception ex) { Fail(rep, $"[{name}] 分配点位失败：{ex.Message}"); }

                try
                {
                    placeholder.Delete();
                    rep.Created++;
                }
                catch (Exception ex) { Fail(rep, $"[{name}] 删除参考占位失败：{ex.Message}"); }
            }

            if (plan.Mode == AllocMode.Symmetric && plan.CopyVias)
                MirrorViasInPlace(mapped, operation, copies, plan, rep);

            UnbindAppearance(mapped, log);
        }

        private static void AssignLocation(ITxOrderedObjectCollection mapped, TxWeldLocationOperation placeholder,
            SpotMatch match, AllocPlan plan, WriteReport rep)
        {
            var target = (TxWeldLocationOperation)match.Target.LocOp;
            var destination = target;
            double[] pose = null;
            if (plan.CopyRotation)
            {
                var rotation = match.RotationInTargetWorld ??
                    throw new InvalidOperationException("缺少预览计算的目标世界姿态，请重新预览。");
                var translation = target.AbsoluteLocation.Translation;
                pose = MergeRotKeepPos(rotation, new[] { translation.X, translation.Y, translation.Z });
            }

            TxRoboticParam[] parameters = null;
            double? duration = null;
            if (plan.CopyParams)
            {
                var source = (ITxRoboticOperation)match.Ref.LocOp;
                parameters = source.Parameters.Cast<TxRoboticParam>().ToArray();
                if (source.CanChangeDuration()) duration = source.Duration;
            }

            if (plan.ConsumeTargets)
            {
                mapped.AddObjectAfter(target, placeholder);
                rep.Consumed++;
            }
            else
            {
                destination = (TxWeldLocationOperation)PasteCopy(mapped, target, out _);
                try { mapped.MoveChildAfter(destination, placeholder); }
                catch
                {
                    destination.Delete();
                    throw;
                }
                rep.Copied++;
            }

            if (parameters != null) CopyTrajectoryParams(parameters, duration, destination, rep);

            if (pose != null)
            {
                try
                {
                    WriteLocationPose(destination, pose);
                    rep.RotationsCopied++;
                }
                catch (Exception ex) { Fail(rep, $"[{match.Target.Name}] 复制旋转姿态失败：{ex.Message}"); }
            }
        }

        private static void WriteLocationPose(ITxLocatableObject destination, double[] pose)
        {
            destination.AbsoluteLocation = PsReader.ArrToTxPublic(pose);
            var actual = destination.AbsoluteLocation;
            if (!MatricesAgree(actual, pose))
                throw new InvalidOperationException("轨迹点位的读回位姿与写入值不一致。");
        }

        private static bool MatricesAgree(TxTransformation actual, double[] expected)
        {
            if (actual == null) return false;
            for (int i = 0; i < 12; i++)
                if (!(Math.Abs(actual[i / 4, i % 4] - expected[i]) <= 1e-6)) return false;
            return true;
        }

        private static void MirrorViasInPlace(ITxOrderedObjectCollection mapped, OpMatch operation,
            Hashtable copies, AllocPlan plan, WriteReport rep)
        {
            var reference = operation.RefOp;
            if (reference.Vias.Count == 0) return;
            var center = operation.ViaReferenceFrame;
            if (center == null || (plan.DiffFrame && operation.ViaTargetFrame == null))
            {
                Fail(rep, $"[{reference.Name}] 焊点缺少一致的坐标基准，无法镜像过渡点。");
                return;
            }
            foreach (var via in reference.Vias)
            {
                try
                {
                    if (!(copies[via.Raw] is TxRoboticViaLocationOperation copy) || !mapped.Equals(copy.Collection))
                        throw new InvalidOperationException("无法定位过渡点副本。");
                    var pose = SymmetryMath.ResolveWritePose(via.Matrix, center, operation.ViaTargetFrame,
                        plan.Mode, plan.DiffFrame, plan.Flip);
                    WriteLocationPose(copy, pose);
                    rep.ViasMirrored++;
                }
                catch (Exception ex) { Fail(rep, $"[{via.Name}] 镜像过渡点失败：{ex.Message}"); }
            }
        }

        private static void CopyTrajectoryParams(IEnumerable<TxRoboticParam> parameters, double? duration,
            ITxRoboticOperation destination, WriteReport rep)
        {
            int previousFailures = rep.Failed;
            foreach (var parameter in parameters)
            {
                try
                {
                    destination.SetParameter(parameter);
                    if (!ParametersAgree(parameter, destination.GetParameter(parameter.Type)))
                        throw new InvalidOperationException("读回值与参考参数不一致。");
                }
                catch (Exception ex) { Fail(rep, $"[{destination.Name}] 复制轨迹参数 {parameter?.Type} 失败：{ex.Message}"); }
            }
            if (duration.HasValue)
            {
                try
                {
                    if (destination.Duration != duration.Value) destination.Duration = duration.Value;
                    if (!(Math.Abs(destination.Duration - duration.Value) <= 1e-6))
                        throw new InvalidOperationException("读回持续时间与参考值不一致。");
                }
                catch (Exception ex) { Fail(rep, $"[{destination.Name}] 复制持续时间失败：{ex.Message}"); }
            }
            if (rep.Failed == previousFailures) rep.ParameterLocationsCopied++;
        }

        private static bool ParametersAgree(TxRoboticParam expected, TxRoboticParam actual)
        {
            if (actual == null || actual.GetType() != expected.GetType()) return false;
            switch (expected)
            {
                case TxRoboticBoolParam value: return value.Value == ((TxRoboticBoolParam)actual).Value;
                case TxRoboticIntParam value: return value.Value == ((TxRoboticIntParam)actual).Value;
                case TxRoboticDoubleParam value: return value.Value.Equals(((TxRoboticDoubleParam)actual).Value);
                case TxRoboticStringParam value: return value.Value == ((TxRoboticStringParam)actual).Value;
                case TxRoboticXmlParam value: return value.Value?.OuterXml == ((TxRoboticXmlParam)actual).Value?.OuterXml;
                case TxRoboticTxObjectParam value: return value.Value?.Id == ((TxRoboticTxObjectParam)actual).Value?.Id;
                case TxRoboticTransformationParam value:
                    return value.Value == null ? ((TxRoboticTransformationParam)actual).Value == null
                        : MatricesAgree(((TxRoboticTransformationParam)actual).Value, PsReader.TxToArr(value.Value));
                default: return actual.Type == expected.Type;
            }
        }

        private static ITxObject PasteCopy(ITxObjectCollection parent, ITxObject source, out Hashtable copies)
        {
            var origins = new TxObjectList { source };
            if (!parent.CanPasteList(origins))
                throw new InvalidOperationException($"目标集合无法复制 {source.Name}。");
            parent.Paste(origins, out copies);
            if (!(copies[source] is ITxObject copy) || copy.Id == source.Id || !parent.Equals(copy.Collection))
                throw new InvalidOperationException($"SDK 未返回 {source.Name} 的副本。");
            return copy;
        }

        // 解绑零件分身：API 不确定，尽力尝试，失败仅提示（文档允许用户手动绑）
        private static void UnbindAppearance(ITxObject mapped, Action<string> log)
        {
            try { dynamic d = mapped; d.ClearPartAppearances(); return; } catch { }
            try { dynamic d = mapped; d.DetachAll(); return; } catch { }
            log($"[Writer] 提示：[{mapped.Name}] 未自动解绑零件分身，请按需手动绑定。");
        }

        // ── Undo 事务（经核验的公共场景分组）──────────────────────────────────
        private static object OpenUndo(string name, Action<string> log)
        {
            return TxTools.Common.SceneUndoScope.Begin(name);
        }
        private static void CommitUndo(object um, Action<string> log)
        {
            if (um != null) ((TxTools.Common.SceneUndoScope)um).Dispose();
        }
        private static void AbortUndo(object um, Action<string> log)
        {
            if (um != null) ((TxTools.Common.SceneUndoScope)um).Dispose();
            log("[Undo] 执行未完整完成，已关闭撤销分组；如已有部分场景变更，请在当前 PS 工程按 Ctrl+Z 撤销本批次。未自动回滚。");
        }

        // ── 矩阵小工具 ────────────────────────────────────────────────────
        private static double[] MergePosKeepRot(TxWeldPoint wp, double[] newPos)
        {
            double[] cur = PsReader.TxToArr(wp.AbsoluteLocation);
            cur[3] = newPos[0]; cur[7] = newPos[1]; cur[11] = newPos[2];
            return cur;
        }
        private static double[] MergeRotKeepPos(double[] rotSrc, double[] pos)
        {
            double[] m = (double[])rotSrc.Clone();
            m[3] = pos[0]; m[7] = pos[1]; m[11] = pos[2];
            return m;
        }

        private static void Fail(WriteReport rep, string msg) { rep.Failed++; rep.Errors.Add(msg); }
    }
}
