using System;
using System.Collections.Generic;
using System.Linq;
using Tecnomatix.Engineering;
using TxTools.Common;
using TxTools.Common.TreeWorkflow;

namespace TxTools.WeldSpotGrouper
{
    public sealed class SequenceEntry
    {
        public string Key;
        public string Name;
        public SequenceEntry Parent;
        public bool IsGroup;
        public ITxObject Existing;
        public ITxObject Result;
        public StructureNode SourceNode;
        public readonly HashSet<string> ExpectedBindings = new HashSet<string>();
        public readonly List<SpotItem> Spots = new List<SpotItem>();
    }
    public sealed class OperationPreview
    {
        public string Name;
        public object Item;
        public readonly List<OperationPreview> Children = new List<OperationPreview>();
    }
    public sealed class SequencePlan
    {
        public object Document;
        public ITxObject Target;
        public WorkflowExecution Execution = new WorkflowExecution();
        public readonly List<string> Blockers = new List<string>();
        public readonly Dictionary<string, string> Mismatches = new Dictionary<string, string>();
        public OperationPreview Preview;
        public bool HasChanges;
        public readonly Dictionary<string, List<string>> PointOrders = new Dictionary<string, List<string>>();
        public readonly Dictionary<string, string> Automatic = new Dictionary<string, string>();
        public readonly Dictionary<string, string> OriginalPaths = new Dictionary<string, string>();
        public readonly HashSet<string> Written = new HashSet<string>();
        public readonly HashSet<string> Kept = new HashSet<string>();
        public readonly List<SequenceEntry> Entries = new List<SequenceEntry>();
        public readonly List<string> Issues = new List<string>();
        public readonly Dictionary<SpotItem, string> Pending = new Dictionary<SpotItem, string>();
        public readonly Dictionary<ITxObject, string> Snapshot = new Dictionary<ITxObject, string>();
        public readonly Dictionary<SpotItem, string> Bindings = new Dictionary<SpotItem, string>();
        public readonly Dictionary<ITxObject, string> Contexts = new Dictionary<ITxObject, string>();
        public readonly HashSet<string> Locations = new HashSet<string>();
    }

    public sealed class SequenceEditSnapshot
    {
        public Dictionary<string, string> Assignments;
        public Dictionary<string, List<string>> PointOrders;
    }
    // User decisions survive recomputation within a source/document session.
    public sealed class SequenceDraft
    {
        public readonly Dictionary<string, string> Assignments = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Names = new Dictionary<string, string>();
        public readonly List<SequenceEntry> Custom = new List<SequenceEntry>();
        public readonly List<string> Order = new List<string>();
        public readonly Dictionary<string, List<string>> PointOrders = new Dictionary<string, List<string>>();
        public SequenceEditSnapshot CaptureEdit()
        {
            return new SequenceEditSnapshot { Assignments = new Dictionary<string, string>(Assignments),
                PointOrders = PointOrders.ToDictionary(p => p.Key, p => p.Value.ToList()) };
        }
        public void RestoreEdit(SequenceEditSnapshot snapshot)
        {
            Assignments.Clear(); PointOrders.Clear();
            foreach (var pair in snapshot.Assignments) Assignments[pair.Key] = pair.Value;
            foreach (var pair in snapshot.PointOrders) PointOrders[pair.Key] = pair.Value.ToList();
        }
        public void Restore(SequencePlan plan)
        {
            foreach (var entry in plan.Entries.Where(e => !e.IsGroup))
                foreach (var spot in entry.Spots) plan.Automatic[spot.LocOp.Id] = entry.Key;
            foreach (var custom in Custom)
            {
                var copy = new SequenceEntry { Key = custom.Key, Name = custom.Name };
                copy.ExpectedBindings.UnionWith(custom.ExpectedBindings); plan.Entries.Add(copy);
            }
            foreach (var entry in plan.Entries)
            { string name; if (Names.TryGetValue(entry.Key, out name)) entry.Name = name; }
            foreach (var spot in plan.Bindings.Keys.ToList())
            {
                string key;
                if (!Assignments.TryGetValue(spot.LocOp.Id, out key)) continue;
                foreach (var entry in plan.Entries) entry.Spots.Remove(spot);
                plan.Pending.Remove(spot);
                var destination = plan.Entries.FirstOrDefault(e => !e.IsGroup && e.Key == key);
                if (key == null) plan.Kept.Add(spot.LocOp.Id);
                else if (destination != null) destination.Spots.Add(spot);
                else plan.Pending[spot] = "人工指定工序已失效，请重新指定；当前保留原位";
            }
            if (Order.Count > 0)
            {
                var result = new List<SequenceEntry>();
                AppendChildren(plan.Entries, null, result);
                plan.Entries.Clear(); plan.Entries.AddRange(result);
            }
            foreach (var pair in PointOrders) plan.PointOrders[pair.Key] = pair.Value.ToList();
        }
        private void AppendChildren(List<SequenceEntry> entries, SequenceEntry parent, List<SequenceEntry> result)
        {
            foreach (var entry in entries.Where(e => e.Parent == parent).OrderBy(e =>
                Order.Contains(e.Key) ? Order.IndexOf(e.Key) : int.MaxValue))
            { result.Add(entry); AppendChildren(entries, entry, result); }
        }
    }
    public static class SequenceEngine
    {
        private static string Binding(SpotItem s)
        {
            return string.Join("|", s.Parts.Select(StructureEngine.Id).OrderBy(x => x, StringComparer.Ordinal));
        }
        public static SequencePlan Plan(StructureNode root, IEnumerable<ITxObject> scopes, ITxObject target,
            bool byCombination, bool allowCommonAssembly, Action<string> log, SequenceDraft draft = null)
        {
            if (target == null) throw new InvalidOperationException("请明确选择目标复合操作。");
            if (GroupWriter.ResolveCreator(target, GroupWriter.ScoreCompound, "能力检查", log) == null)
                throw new InvalidOperationException("目标不支持创建复合操作。");
            var p = new SequencePlan { Target = target, Document = TxApplication.ActiveDocument };
            var selected = root == null || byCombination ? new List<StructureNode>() : root.Walk().Where(n => n.IsOperation && Included(n)).ToList();
            if (!byCombination && selected.Count == 0) throw new InvalidOperationException("请勾选作为工序的零件节点。");
            var entries = new Dictionary<StructureNode, SequenceEntry>();
            foreach (var n in selected) EnsureEntry(n, entries, p, true);
            var boundNodes = root == null || byCombination ? new List<StructureNode>() : root.Walk().Where(Included).ToList();
            var identityNodes = root == null || byCombination ? new List<StructureNode>() : root.Walk().ToList();
            var byId = identityNodes.Where(n => n.Source != null || n.BoundPart != null).GroupBy(n => StructureEngine.Id(n.Source ?? n.BoundPart)).ToDictionary(g => g.Key, g => g.ToList());
            var byName = new Dictionary<string, List<StructureNode>>(StringComparer.Ordinal);
            foreach (var node in boundNodes.Where(n => n.Source == null && n.BoundPart == null && !n.IsContainer))
                foreach (var key in new[] { node.SourceName, node.PartNumber }.Where(k => !string.IsNullOrEmpty(k)).Distinct())
                { List<StructureNode> values; if (!byName.TryGetValue(key, out values)) byName[key] = values = new List<StructureNode>(); values.Add(node); }
            // A single CATIA leaf must not silently absorb several PS instances.
            var namedInstances = byName.Values.SelectMany(n => n).Distinct().ToDictionary(n => n, n => new HashSet<string>());
            var namedSpots = new Dictionary<SpotItem, HashSet<StructureNode>>();
            if (byName.Count > 0)
                foreach (var obj in TxApplication.ActiveDocument.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxObject))).Cast<ITxObject>())
                {
                    List<StructureNode> nodes;
                    if (StructureEngine.IsPart(obj) && byName.TryGetValue(obj.Name, out nodes))
                        foreach (var node in nodes) namedInstances[node].Add(obj.Id);
                }
            foreach (var n in boundNodes) { Track(p, n.Source); Track(p, n.BoundPart); }
            Track(p, target);
            var byKey = new Dictionary<string, SequenceEntry>(StringComparer.Ordinal);
            foreach (var scope in scopes)
            {
                Track(p, scope);
                foreach (var spot in SpotGrouper.CollectSpots(scope, log))
                {
                    if (!p.Locations.Add(StructureEngine.Id(spot.LocOp))) continue;
                    p.OriginalPaths[spot.LocOp.Id] = StructureEngine.Path(StructureEngine.Parent(spot.LocOp));
                    SpotGrouper.ReadPartNames(spot, log);
                    foreach (var part in spot.Parts) Track(p, part);
                    if (spot.Parts.Count == 0 || spot.Parts.Count != spot.PartNames.Count)
                    { Defer(p, spot, "无可识别绑定零件，保留原位"); continue; }
                    SequenceEntry destination = null;
                    if (byCombination)
                    {
                        var key = Binding(spot);
                        if (!byKey.TryGetValue(key, out destination))
                        {
                            destination = new SequenceEntry { Key = "combination:" + key, Name = StructureEngine.Clean("焊接_" + (byKey.Count + 1).ToString("D2") + "_" + string.Join("+", spot.PartNames)) };
                            byKey[key] = destination; p.Entries.Add(destination);
                        }
                    }
                    else
                    {
                        var matches = new List<StructureNode>();
                        foreach (var part in spot.Parts)
                        {
                            List<StructureNode> exact = null;
                            var seen = new HashSet<string>();
                            for (var current = part; current != null && seen.Add(StructureEngine.Id(current)); current = StructureEngine.Parent(current))
                                if (byId.TryGetValue(StructureEngine.Id(current), out exact)) { exact = exact.Where(Included).ToList(); break; }
                            if (exact == null && byName.TryGetValue(part.Name, out exact) && exact.Count == 1)
                            {
                                namedInstances[exact[0]].Add(part.Id);
                                HashSet<StructureNode> nodes;
                                if (!namedSpots.TryGetValue(spot, out nodes)) namedSpots[spot] = nodes = new HashSet<StructureNode>();
                                nodes.Add(exact[0]);
                            }
                            if (exact == null) exact = new List<StructureNode>();
                            if (exact.Count != 1) { matches.Clear(); break; }
                            matches.Add(exact[0]); Track(p, part);
                        }
                        if (matches.Count != spot.Parts.Count) { Defer(p, spot, "绑定零件超出来源范围或实例不唯一"); continue; }
                        // Each selected assembly includes its complete subset. Intersect
                        // the bound parts' selected ancestors, choosing the deepest one.
                        // An OP10/OP05 joint belongs to OP10 when OP05 is inside OP10.
                        var owner = matches[0];
                        while (owner != null && (!owner.IsOperation || !matches.All(n => IsDescendant(n, owner)))) owner = owner.Parent;
                        if (owner != null) destination = entries[owner];
                        else
                        {
                            var common = matches[0];
                            while (common != null && !matches.All(n => IsDescendant(n, common))) common = common.Parent;
                            if (!allowCommonAssembly || common == null || common == root)
                            { Defer(p, spot, "跨分支连接点，待指定合装工序"); continue; }
                            destination = EnsureEntry(common, entries, p, true);
                        }
                    }
                    destination.Spots.Add(spot); p.Bindings[spot] = Binding(spot);
                    Track(p, spot.LocOp); Track(p, spot.Wp);
                    var original = StructureEngine.Parent(spot.LocOp);
                    if (original != null) { Track(p, original); p.Contexts[original] = ContextStamp(original); }
                }
            }
            if (p.Locations.Count == 0) throw new InvalidOperationException("范围内没有可移动的焊点位操作；仅焊点特征不会自动生成操作。");
            var ambiguous = new HashSet<StructureNode>(namedInstances.Where(pair => pair.Value.Count > 1).Select(pair => pair.Key));
            foreach (var entry in p.Entries)
                foreach (var spot in entry.Spots.ToList())
                {
                    HashSet<StructureNode> nodes;
                    if (namedSpots.TryGetValue(spot, out nodes) && nodes.Overlaps(ambiguous))
                    { entry.Spots.Remove(spot); Defer(p, spot, "CATIA 名称/编号对应多个 PS 实例，请在零件树整理中明确绑定实例"); }
                }
            // Existing target weld operations remain available as manual destinations.
            foreach (var existing in StructureEngine.Children(target).OfType<TxWeldOperation>())
                if (!p.Entries.Any(e => e.Name == existing.Name))
                    p.Entries.Add(new SequenceEntry { Key = "existing:" + existing.Id, Name = existing.Name, Existing = existing });
            // Physical depth determines default execution order; operation parents stay flat.
            var ordered = p.Entries.OrderByDescending(e => e.SourceNode == null ? 0 : e.SourceNode.Depth).ToList();
            p.Entries.Clear(); p.Entries.AddRange(ordered);
            foreach (var entry in p.Entries)
                foreach (var spot in entry.Spots) { entry.ExpectedBindings.Add(Binding(spot)); p.Automatic[spot.LocOp.Id] = entry.Key; }
            if (draft != null) draft.Restore(p);
            Validate(p);
            p.Preview = BuildPreview(p);
            p.HasChanges = p.Entries.Any(e => e.Spots.Any(s => e.Existing == null || StructureEngine.Id(StructureEngine.Parent(s.LocOp)) != e.Existing.Id))
                || !p.Preview.Children.Select(n => n.Item is SequenceEntry ? StructureEngine.Id(((SequenceEntry)n.Item).Existing) : n.Item is SpotItem ? ((SpotItem)n.Item).LocOp.Id : StructureEngine.Id(n.Item as ITxObject))
                    .SequenceEqual(StructureEngine.Children(p.Target).Select(StructureEngine.Id))
                || p.Preview.Children.Where(n => n.Item is SequenceEntry && ((SequenceEntry)n.Item).Existing != null)
                    .Any(n => !n.Children.Select(PreviewId).SequenceEqual(StructureEngine.Children(((SequenceEntry)n.Item).Existing).Select(StructureEngine.Id)));
            return p;
        }
        public static void Validate(SequencePlan p)
        {
            p.Blockers.Clear();
            p.Mismatches.Clear();
            var viaCache = new Dictionary<string, bool>();
            var targetChildren = StructureEngine.Children(p.Target).GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var duplicateNames = new HashSet<string>(p.Entries.Where(e => e.Spots.Count > 0).GroupBy(e => e.Name).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.Ordinal);
            // Empty placeholders are not written.
            // Keep marked empty operations as destinations for manual assignment.
            foreach (var e in p.Entries.Where(entry => entry.Spots.Count > 0))
            {
                e.Existing = null;
                List<ITxObject> candidates;
                if (!targetChildren.TryGetValue(e.Name, out candidates)) candidates = new List<ITxObject>();
                if (duplicateNames.Contains(e.Name) || candidates.Count > 1)
                { p.Blockers.Add("目标名称不唯一：" + e.Name); continue; }
                if (candidates.Count == 1)
                {
                    var existing = candidates[0];
                    if (e.IsGroup ? !(existing is TxCompoundOperation) : !(existing is TxWeldOperation))
                    { p.Blockers.Add("同名目标类型不符：" + e.Name); continue; }
                    e.Existing = existing; Track(p, existing);
                    p.Contexts[existing] = ContextStamp(existing);
                }
                if (!e.IsGroup && e.Spots.Count > 0)
                    try { ValidateContext(e, viaCache); } catch (Exception ex) { p.Blockers.Add(e.Name + "：" + ex.Message); }
                foreach (var spot in e.Spots)
                    if (!e.ExpectedBindings.Contains(Binding(spot)))
                    {
                        bool countDiffers = e.ExpectedBindings.Count > 0 && !e.ExpectedBindings.Any(b => b.Split('|').Length == spot.Parts.Count);
                        p.Mismatches[spot.LocOp.Id] = countDiffers ? "绑定零件数量与目标工序不一致" : "绑定零件实例/编号与目标工序不一致（或目标未定义对应零件）";
                    }
            }
        }
        public static OperationPreview BuildPreview(SequencePlan p)
        {
            var active = p.Entries.Where(e => e.Spots.Count > 0).ToList();
            var assigned = new HashSet<string>(active.SelectMany(e => e.Spots).Select(s => s.LocOp.Id));
            var spots = p.Bindings.Keys.ToDictionary(s => s.LocOp.Id);
            Func<ITxObject, OperationPreview> read = null;
            read = o =>
            {
                Track(p, o);
                SpotItem spot; var node = new OperationPreview { Name = o.Name, Item = spots.TryGetValue(o.Id, out spot) ? (object)spot : o };
                foreach (var child in StructureEngine.Children(o)) if (!assigned.Contains(child.Id)) node.Children.Add(read(child));
                return node;
            };
            var root = new OperationPreview { Name = p.Target.Name, Item = p.Target };
            foreach (var child in StructureEngine.Children(p.Target))
                if (!active.Any(e => e.Existing != null && e.Existing.Id == child.Id) && !assigned.Contains(child.Id)) root.Children.Add(read(child));
            foreach (var entry in active)
            {
                var node = new OperationPreview { Name = entry.Name, Item = entry };
                if (entry.Existing != null)
                    foreach (var child in StructureEngine.Children(entry.Existing))
                        if (!assigned.Contains(child.Id) || entry.Spots.Any(s => s.LocOp.Id == child.Id))
                        {
                            var childNode = read(child); node.Children.Add(childNode);
                        }
                foreach (var spot in entry.Spots)
                    if (!node.Children.Any(c => c.Item is SpotItem && ((SpotItem)c.Item).LocOp.Id == spot.LocOp.Id))
                        node.Children.Add(new OperationPreview { Name = spot.LocOp.Name, Item = spot });
                List<string> pointOrder;
                if (p.PointOrders.TryGetValue(entry.Key, out pointOrder))
                {
                    var indices = pointOrder.Select((id, i) => new { id, i }).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
                    var ordered = node.Children.OrderBy(c => { int i; return indices.TryGetValue(PreviewId(c), out i) ? i : int.MaxValue; }).ToList();
                    node.Children.Clear(); node.Children.AddRange(ordered);
                }
                root.Children.Add(node);
            }
            return root;
        }
        public static string PreviewId(OperationPreview node)
        {
            if (node.Item is SpotItem) return ((SpotItem)node.Item).LocOp.Id;
            if (node.Item is SequenceEntry) return StructureEngine.Id(((SequenceEntry)node.Item).Existing);
            return StructureEngine.Id(node.Item as ITxObject);
        }
        private static void Defer(SequencePlan p, SpotItem spot, string reason)
        {
            var message = spot.LocOp.Name + "：" + reason;
            p.Issues.Add(message); p.Pending[spot] = reason;
            p.Bindings[spot] = Binding(spot); Track(p, spot.LocOp); Track(p, spot.Wp);
            foreach (var part in spot.Parts) Track(p, part);
            var original = StructureEngine.Parent(spot.LocOp);
            if (original != null) { Track(p, original); p.Contexts[original] = ContextStamp(original); }
        }
        private static object ContextValue(ITxObject operation, string name)
        {
            if (operation == null) return null;
            var property = operation.GetType().GetProperty(name);
            return property == null ? null : property.GetValue(operation, null);
        }
        private static string ValueKey(object value)
        {
            var obj = value as ITxObject;
            return obj == null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : obj.Id;
        }
        private static string ContextStamp(ITxObject operation)
        {
            return string.Join("|", new[] { "Robot", "Tool" }.Select(n => ValueKey(ContextValue(operation, n))));
        }
        private static Dictionary<string, object> ValidateContext(SequenceEntry entry, Dictionary<string, bool> viaCache)
        {
            var contexts = new Dictionary<string, object>();
            foreach (var spot in entry.Spots)
            {
                var original = StructureEngine.Parent(spot.LocOp);
                if (original == null) throw new InvalidOperationException("无法确定点位原操作：" + spot.Name);
                if (entry.Existing != null && original.Id == entry.Existing.Id) continue;
                bool hasVia;
                if (!viaCache.TryGetValue(original.Id, out hasVia))
                {
                    hasVia = StructureEngine.Children(original).Any(o => o.GetType().Name.IndexOf("Via", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (o is ITxRoboticLocationOperation && SpotGrouper.WpFromLocOp(o) == null));
                    viaCache[original.Id] = hasVia;
                }
                if (hasVia)
                    throw new InvalidOperationException(original.Name + " 含过渡点，请先在焊点矩阵中整理轨迹，再拆分焊点。");
                foreach (var name in new[] { "Robot", "Tool" })
                {
                    var value = ContextValue(original, name); object previous;
                    if (contexts.TryGetValue(name, out previous) && ValueKey(previous) != ValueKey(value))
                        throw new InvalidOperationException(entry.Name + " 的来源操作关联了不同的机器人或工具，请拆分工序。");
                    contexts[name] = value;
                    if (entry.Existing != null && ValueKey(ContextValue(entry.Existing, name)) != ValueKey(value))
                        throw new InvalidOperationException(entry.Name + " 与来源操作的 " + name + " 关联不一致。");
                }
            }
            return contexts;
        }
        private static bool Included(StructureNode n) { for (; n != null; n = n.Parent) if (!n.Include) return false; return true; }
        private static bool IsDescendant(StructureNode n, StructureNode parent) { for (; n != null; n = n.Parent) if (n == parent) return true; return false; }
        private static SequenceEntry EnsureEntry(StructureNode n, Dictionary<StructureNode, SequenceEntry> map, SequencePlan p, bool weld)
        {
            SequenceEntry entry;
            if (weld && map.TryGetValue(n, out entry)) return entry;
            entry = new SequenceEntry { Key = "weld:" + n.Key, Name = StructureEngine.Clean(n.Label), SourceNode = n };
            map[n] = entry; p.Entries.Add(entry); return entry;
        }
        private static void Track(SequencePlan p, ITxObject o) { if (o != null && !p.Snapshot.ContainsKey(o)) p.Snapshot[o] = StructureEngine.Stamp(o); }
        public static string Apply(SequencePlan p, Action<string> log)
        {
            p.Execution = new WorkflowExecution();
            if (p.Blockers.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, p.Blockers));
            if (!ReferenceEquals(p.Document, TxApplication.ActiveDocument)) throw new InvalidOperationException("活动文档已改变。");
            foreach (var pair in p.Snapshot) if (StructureEngine.Stamp(pair.Key) != pair.Value) throw new InvalidOperationException("场景已变化，请重新预览。");
            foreach (var pair in p.Contexts) if (ContextStamp(pair.Key) != pair.Value) throw new InvalidOperationException("机器人或工具关联已变化，请重新预览。");
            foreach (var pair in p.Bindings)
            {
                var current = new SpotItem { Wp = pair.Key.Wp }; SpotGrouper.ReadPartNames(current, log);
                if (Binding(current) != pair.Value) throw new InvalidOperationException("焊点绑定已变化，请重新预览。");
            }
            var viaCache = new Dictionary<string, bool>();
            var contexts = p.Entries.Where(e => !e.IsGroup).ToDictionary(e => e, e => ValidateContext(e, viaCache));
            var actual = new Dictionary<SequenceEntry, ITxObject>();
            using (SceneUndoScope.Begin("快速分序"))
            {
                foreach (var entry in p.Entries)
                {
                    if (!HasSpots(p, entry)) continue;
                    p.Execution.Current = entry.Name;
                    var parent = entry.Parent == null ? p.Target : actual[entry.Parent];
                    var op = entry.Existing;
                    if (op == null)
                    {
                        var creator = GroupWriter.ResolveCreator(parent, entry.IsGroup ? (Func<System.Reflection.MethodInfo, int>)GroupWriter.ScoreCompound : GroupWriter.ScoreWeld, "创建", log);
                        if (creator == null) throw new InvalidOperationException("目标不支持所需操作类型：" + entry.Name);
                        p.Execution.WriteAttempted = true;
                        op = creator(parent, entry.Name, log);
                        if (op == null || (entry.IsGroup ? !(op is TxCompoundOperation) : !(op is TxWeldOperation))) throw new InvalidOperationException("创建结果类型不符：" + entry.Name);
                        p.Execution.Created++;
                        entry.Result = op;
                        if (!entry.IsGroup)
                            foreach (var pair in contexts[entry])
                            {
                                if (pair.Value == null) continue;
                                var property = op.GetType().GetProperty(pair.Key);
                                if (property == null || !property.CanWrite) throw new InvalidOperationException("无法继承来源操作的 " + pair.Key + " 关联。");
                                property.SetValue(op, pair.Value, null);
                                if (ValueKey(ContextValue(op, pair.Key)) != ValueKey(pair.Value)) throw new InvalidOperationException("关联写入验证失败：" + pair.Key);
                            }
                    }
                    actual[entry] = op;
                    entry.Result = op;
                    ITxObject after = StructureEngine.Children(op).LastOrDefault();
                    foreach (var spot in entry.Spots)
                    {
                        if (StructureEngine.Children(op).Any(x => x.Id == spot.LocOp.Id)) { p.Execution.Unchanged++; p.Written.Add(spot.LocOp.Id); continue; }
                        p.Execution.Current = entry.Name + " / " + spot.Name;
                        var original = StructureEngine.Parent(spot.LocOp);
                        var siblings = original == null ? new List<ITxObject>() : StructureEngine.Children(original);
                        try
                        {
                            p.Execution.WriteAttempted = true;
                            if (!GroupWriter.MoveLocationInto(op, spot.LocOp, after, log) || !StructureEngine.Children(op).Any(x => x.Id == spot.LocOp.Id))
                                throw new InvalidOperationException("移动验证失败：" + spot.Name);
                            var written = StructureEngine.Children(op);
                            if (after != null && written.FindIndex(x => x.Id == spot.LocOp.Id) != written.FindIndex(x => x.Id == after.Id) + 1)
                                throw new InvalidOperationException("点位顺序验证失败：" + spot.Name);
                            after = spot.LocOp; p.Execution.Moved++; p.Written.Add(spot.LocOp.Id);
                        }
                        catch
                        {
                            if (!StructureEngine.Restore(original, spot.LocOp, siblings)) log("父级或顺序补偿未完成，请 Ctrl+Z 撤销本批次。");
                            throw;
                        }
                    }
                }
                // Match the displayed final sibling order, including reused operations.
                var desired = p.Preview.Children.Select(n => n.Item is SequenceEntry ? actual[(SequenceEntry)n.Item] : n.Item is SpotItem ? ((SpotItem)n.Item).LocOp : n.Item as ITxObject).ToList();
                var currentOrder = StructureEngine.Children(p.Target);
                if (!currentOrder.Select(StructureEngine.Id).SequenceEqual(desired.Select(StructureEngine.Id)))
                {
                    p.Execution.Current = "目标工序顺序"; p.Execution.WriteAttempted = true;
                        for (int i = 0; i < desired.Count; i++)
                        {
                            dynamic target = p.Target;
                            if (i == 0 && desired.Count > 1) target.AddObjectBefore(desired[i], StructureEngine.Children(p.Target).First(o => o.Id != desired[i].Id));
                            else if (i > 0) target.AddObjectAfter(desired[i], desired[i - 1]);
                        }
                    if (!StructureEngine.Children(p.Target).Select(StructureEngine.Id).SequenceEqual(desired.Select(StructureEngine.Id))) throw new InvalidOperationException("工序顺序验证失败。");
                }
                foreach (var node in p.Preview.Children.Where(n => n.Item is SequenceEntry))
                {
                    var operation = actual[(SequenceEntry)node.Item];
                    var desiredPoints = node.Children.Select(n => n.Item is SpotItem ? ((SpotItem)n.Item).LocOp : n.Item as ITxObject).ToList();
                    if (StructureEngine.Children(operation).Select(StructureEngine.Id).SequenceEqual(desiredPoints.Select(StructureEngine.Id))) continue;
                    p.Execution.Current = operation.Name + " / 点位顺序"; p.Execution.WriteAttempted = true;
                    for (int i = 0; i < desiredPoints.Count; i++)
                    {
                        dynamic ordered = operation;
                        if (i == 0 && desiredPoints.Count > 1) ordered.AddObjectBefore(desiredPoints[i], StructureEngine.Children(operation).First(o => o.Id != desiredPoints[i].Id));
                        else if (i > 0) ordered.AddObjectAfter(desiredPoints[i], desiredPoints[i - 1]);
                    }
                    p.Execution.Reordered++;
                }
                VerifyPreview(p.Preview, p.Target, actual);
            }
            p.Execution.Completed = true;
            return "执行完成：新建操作 " + p.Execution.Created + " / 移动点 " + p.Execution.Moved +
                " / 已在位 " + p.Execution.Unchanged + " / 点序调整 " + p.Execution.Reordered + " / 保留原位 " + (p.Pending.Count + p.Kept.Count);
        }
        public static bool HasSpots(SequencePlan p, SequenceEntry e)
        {
            return e.Spots.Count > 0 || p.Entries.Any(c => c.Parent == e && HasSpots(p, c));
        }
        private static void VerifyPreview(OperationPreview preview, ITxObject actual, Dictionary<SequenceEntry, ITxObject> entries)
        {
            var children = StructureEngine.Children(actual);
            if (actual.Name != preview.Name || children.Count != preview.Children.Count) throw new InvalidOperationException("写入结果与预览不一致：" + preview.Name);
            for (int i = 0; i < children.Count; i++)
            {
                var expected = preview.Children[i];
                var value = expected.Item is SequenceEntry ? entries[(SequenceEntry)expected.Item] : expected.Item is SpotItem ? ((SpotItem)expected.Item).LocOp : expected.Item as ITxObject;
                if (children[i].Id != value.Id) throw new InvalidOperationException("写入顺序与预览不一致：" + preview.Name);
                VerifyPreview(expected, children[i], entries);
            }
        }
    }
}
