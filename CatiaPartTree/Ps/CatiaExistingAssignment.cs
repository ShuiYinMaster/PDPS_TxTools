using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tecnomatix.Engineering;
using TxTools.Common.TreeWorkflow;
using TxTools.CatiaPartTree.Core;
using TxTools.Common;
using TxTools.CrossEnvIO;

namespace TxTools.CatiaPartTree.Ps
{
    // The selected compound is the only candidate source. Never fall back to the scene root.
    internal sealed class CatiaExistingAssignment
    {
        internal readonly StructurePlan Plan;
        private readonly ITxObject _scope;
        private readonly Dictionary<string, string> _snapshot;

        internal CatiaExistingAssignment(CatiaImportSource source, CatiaImportSession session, ITxObject scope)
        {
            if (!(scope is TxCompoundPart) || !scope.IsValid())
                throw new InvalidOperationException("请在待分配范围的原生单对象选取框中选择一个有效复合零件。");
            _scope = scope;
            var branch = StructureEngine.Read(scope);
            _snapshot = Snapshot(branch);
            var parts = branch.Walk().Where(n => !n.IsContainer).Select(n => n.Source).ToList();
            Plan = new StructurePlan { Document = TxApplication.ActiveDocument, Target = session.Parent };
            Track(Plan.Target);
            var mapped = new Dictionary<StructureChange, StructureChange>();
            foreach (var item in session.Items)
            {
                var c = new StructureChange { Node = item.Node, Name = item.Name, Container = item.Container,
                    Parent = item.Parent == null ? null : mapped[item.Parent], Existing = item.Container ? item.Result ?? item.Existing : null };
                if (!c.Container)
                {
                    var candidates = parts.Where(p => Equal(p.Name, item.Node.SourceName)).ToList();
                    if (candidates.Count == 0)
                    {
                        var reference = source.Parts[item.Node.Key];
                        var names = reference.GeometryEntries == null ? new[] { Path.GetFileNameWithoutExtension(reference.File) } :
                            reference.GeometryEntries.Select(Path.GetFileNameWithoutExtension).ToArray();
                        candidates = parts.Where(p => Equal(p.Name, item.Node.PartNumber) || names.Any(n => Equal(p.Name, n))).ToList();
                    }
                    if (candidates.Count == 1) { c.Existing = candidates[0]; c.Name = c.Existing.Name; }
                    else c.Error = candidates.Count == 0 ? "所选复合零件下未匹配" : "多个同名零件，无法唯一分配";
                }
                mapped.Add(item, c); Plan.Changes.Add(c);
                if (c.Existing != null) Track(c.Existing);
            }
            foreach (var group in Plan.Changes.Where(c => !c.Container && c.Existing != null).GroupBy(c => StructureEngine.Id(c.Existing)).Where(g => g.Count() > 1))
                foreach (var c in group) c.Error = "同一 PS 零件匹配多个来源实例";
            foreach (var c in Plan.Changes)
            {
                if (!c.Container && c.Existing != null)
                {
                    var target = c.Parent == null ? Plan.Target : c.Parent.Existing;
                    c.Unchanged = target != null && StructureEngine.Id(StructureEngine.Parent(c.Existing)) == StructureEngine.Id(target);
                }
                if (c.Error != null) Plan.Issues.Add(c.Name + "：" + c.Error);
            }
            Plan.HasChanges = Plan.Changes.Any(c => c.Container ? c.Existing == null : !c.Unchanged);
        }
        private static bool Equal(string a, string b)
        { return !string.IsNullOrWhiteSpace(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private void Track(ITxObject value) { Plan.Snapshot[StructureEngine.Id(value)] = StructureEngine.Stamp(value); }
        private static Dictionary<string, string> Snapshot(StructureNode root)
        { return root.Walk().ToDictionary(n => StructureEngine.Id(n.Source), n => StructureEngine.Stamp(n.Source)); }
        internal string Apply(Action<string> log)
        {
            if (!ReferenceEquals(Plan.Document, TxApplication.ActiveDocument) || !_scope.IsValid())
                throw new InvalidOperationException("工程或待分配范围已失效，请重新读取。");
            var current = Snapshot(StructureEngine.Read(_scope));
            if (current.Count != _snapshot.Count || current.Any(p => !_snapshot.ContainsKey(p.Key) || _snapshot[p.Key] != p.Value))
                throw new InvalidOperationException("所选复合零件下的内容已变化，请重新检查预览。");
            var locations = Plan.Changes.Where(c => !c.Container && c.Existing is ITxLocatableObject)
                .ToDictionary(c => c, c => TxMath.WorldValues(((ITxLocatableObject)c.Existing).AbsoluteLocation));
            using (var undo = SceneUndoScope.Begin("CATIA · 分配已有零件"))
            {
                var summary = StructureEngine.Apply(Plan, log);
                foreach (var pair in locations)
                {
                    undo.EnsureDocument();
                    var located = (ITxLocatableObject)pair.Key.Existing;
                    located.AbsoluteLocation = TxMath.FromWorldValues(pair.Value);
                    var actual = TxMath.WorldValues(located.AbsoluteLocation);
                    if (actual.Where((v, i) => Math.Abs(v - pair.Value[i]) > 1e-6).Any())
                        throw new IOException("分配后位置校验失败：" + pair.Key.Name);
                }
                return summary;
            }
        }
    }
}
