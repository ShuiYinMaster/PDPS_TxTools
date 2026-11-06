using System;
using System.Collections.Generic;
using System.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;

namespace TxTools.Common.TreeWorkflow
{
    // Source identity and target labels are independent. All SDK access is on the PS thread.
    public sealed class StructureNode
    {
        public string Key = Guid.NewGuid().ToString("N");
        public string SourceName, PartNumber, TargetName;
        public ITxObject Source;
        public ITxObject BoundPart;
        public StructureNode Parent;
        public readonly List<StructureNode> Children = new List<StructureNode>();
        public bool Include = true, SkipLevel, IsContainer, IsOperation;
        public int Depth { get { return Parent == null ? 0 : Parent.Depth + 1; } }
        public string Label { get { return string.IsNullOrWhiteSpace(TargetName) ? SourceName : TargetName; } }
        public IEnumerable<StructureNode> Walk()
        {
            yield return this;
            foreach (var child in Children) foreach (var n in child.Walk()) yield return n;
        }
        public void Add(StructureNode child) { child.Parent = this; Children.Add(child); }
        public StructureNode Copy()
        {
            var copy = new StructureNode { Key = Key, SourceName = SourceName, PartNumber = PartNumber,
                TargetName = TargetName, Source = Source, BoundPart = BoundPart, Include = Include,
                SkipLevel = SkipLevel, IsContainer = IsContainer, IsOperation = IsOperation };
            foreach (var child in Children) copy.Add(child.Copy());
            return copy;
        }
    }

    public sealed class WorkflowExecution
    {
        public bool WriteAttempted, Completed;
        public int Created, Moved, Unchanged, Reordered;
        public string Current;
        public string Summary { get { return "新建 " + Created + " / 移动 " + Moved + " / 已在位 " + Unchanged; } }
        public string Failure(Exception error)
        {
            return !WriteAttempted ? "检查未通过，未修改场景：" + error.Message
                : "写入未完整完成：" + error.Message + Environment.NewLine + Summary +
                  "；停止于：" + Current + "。请核对场景；本批已有变更可在当前工程 Ctrl+Z 撤销。";
        }
    }

    public sealed class StructureChange
    {
        public StructureNode Node;
        public StructureChange Parent;
        public ITxObject Existing;
        public string Name, Error;
        public bool Container;
        public bool Unchanged;
        public ITxObject Result;
        public string Status { get { return Error != null ? Error : Existing == null ? "新建" : Container ? "复用" : Unchanged ? "已在位" : "将移动"; } }
    }

    public sealed class StructurePlan
    {
        public object Document;
        public ITxObject Target;
        public WorkflowExecution Execution = new WorkflowExecution();
        public bool HasChanges;
        public readonly List<StructureChange> Changes = new List<StructureChange>();
        public readonly List<string> Issues = new List<string>();
        public readonly Dictionary<string, string> Snapshot = new Dictionary<string, string>();
        public readonly Dictionary<string, List<ITxObject>> Candidates = new Dictionary<string, List<ITxObject>>(StringComparer.Ordinal);
    }

    public static class StructureEngine
    {
        public static string Id(ITxObject o) { return o == null ? "" : o.Id; }
        public static string Name(ITxObject o) { return o == null ? "" : o.Name; }
        public static List<ITxObject> Children(ITxObject o)
        {
            var collection = o as ITxObjectCollection;
            if (collection == null) return new List<ITxObject>();
            return collection.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject))).Cast<ITxObject>().ToList();
        }
        public static ITxObject Selected()
        {
            var items = TxApplication.ActiveSelection.GetItems();
            if (items.Count != 1) throw new InvalidOperationException("请在 PS 树中明确选择一个节点。");
            return items[0];
        }
        public static ITxObject Parent(ITxObject o)
        {
            if (o == null) return null;
            foreach (var property in new[] { "Collection", "Parent", "ParentOperation" })
            {
                var p = o.GetType().GetProperty(property);
                if (p != null) { var value = p.GetValue(o, null) as ITxObject; if (value != null) return value; }
            }
            return null;
        }
        public static bool Under(ITxObject child, ITxObject parent)
        {
            var seen = new HashSet<string>();
            for (var n = child; n != null && seen.Add(Id(n)); n = Parent(n))
                if (Id(n) == Id(parent)) return true;
            return false;
        }
        public static string Path(ITxObject o)
        {
            var names = new List<string>(); var seen = new HashSet<string>();
            for (var n = o; n != null && seen.Add(Id(n)); n = Parent(n)) names.Add(Name(n));
            names.Reverse(); return string.Join(" / ", names);
        }
        public static string Stamp(ITxObject o)
        {
            if (o == null || !o.IsValid()) throw new InvalidOperationException("对象已失效，请重新预览。");
            return Name(o) + "|" + Id(Parent(o)) + "|" + string.Join(",", Children(o).Select(Id));
        }
        public static StructureNode Read(ITxObject root)
        {
            if (!(root is ITxCompoundPartCreation) && !(root is TxCompoundPart) && !IsPart(root)) throw new InvalidOperationException("请选择零件或零件容器分支。");
            return Read(root, new HashSet<string>(), 0);
        }
        private static StructureNode Read(ITxObject o, HashSet<string> seen, int depth)
        {
            if (depth > 100 || !seen.Add(Id(o))) throw new InvalidOperationException("来源树存在循环或层级超过 100。");
            var n = new StructureNode { Source = o, SourceName = Name(o), IsContainer = o is TxCompoundPart || o is ITxCompoundPartCreation };
            if (n.IsContainer)
                foreach (var child in Children(o).Where(x => x is TxCompoundPart || IsPart(x))) n.Add(Read(child, seen, depth + 1));
            return n;
        }
        public static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("目标名称不能为空。");
            return new string(value.Trim().Select(c => "\\/:*?\"<>|".Contains(c) || char.IsControl(c) ? '-' : c).ToArray());
        }
        public static bool IsPart(ITxObject o)
        {
            return o != null && !(o is TxCompoundPart) && (o.GetType().Name == "TxPart" || o is TxComponent);
        }
        public static StructurePlan Plan(StructureNode root, ITxObject target, bool includeRoot, bool classify)
        {
            if (root == null || target == null) throw new InvalidOperationException("请先读取来源并拾取目标容器。");
            if (!(target is ITxCompoundPartCreation)) throw new InvalidOperationException("目标不支持创建零件容器。");
            if (root.Walk().Any(n => (n.Source ?? n.BoundPart) != null && Under(target, n.Source ?? n.BoundPart)))
                throw new InvalidOperationException("目标必须位于来源分支之外，避免在来源内递归建树。");
            var plan = new StructurePlan { Document = TxApplication.ActiveDocument, Target = target };
            if (classify && root.Walk().Any(n => !n.IsContainer && n.Source == null))
                foreach (var part in TxApplication.ActiveDocument.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxObject))).Cast<ITxObject>().Where(o => !(o is TxCompoundPart) && IsPart(o)))
                {
                    List<ITxObject> values;
                    if (!plan.Candidates.TryGetValue(Name(part), out values)) plan.Candidates[Name(part)] = values = new List<ITxObject>();
                    values.Add(part);
                }
            Track(plan, target);
            foreach (var n in root.Walk()) if (n.Source != null) Track(plan, n.Source);
            Add(plan, root, null, includeRoot, classify);
            plan.HasChanges = plan.Changes.Any(c => c.Container ? c.Existing == null : !c.Unchanged);
            return plan;
        }
        private static void Track(StructurePlan p, ITxObject o) { p.Snapshot[Id(o)] = Stamp(o); }
        private static void Add(StructurePlan p, StructureNode n, StructureChange parent, bool include, bool classify)
        {
            if (!n.Include) return;
            var next = parent;
            if (include && !n.SkipLevel && n.IsContainer)
            {
                var name = Clean(n.Label);
                var siblings = p.Changes.Where(c => c.Container && c.Parent == parent && c.Name == name).ToList();
                var targetParent = parent == null ? p.Target : parent.Existing;
                var candidates = targetParent == null ? new List<ITxObject>() : Children(targetParent).Where(x => Name(x) == name).ToList();
                next = new StructureChange { Node = n, Parent = parent, Name = name, Container = true };
                if (siblings.Count > 0 || candidates.Count > 1 || candidates.Any(x => !(x is TxCompoundPart)))
                    next.Error = "同名目标不唯一或类型不符";
                else if (candidates.Count == 1) next.Existing = candidates[0];
                if (next.Error != null) p.Issues.Add(name + "：" + next.Error);
                if (next.Existing != null) Track(p, next.Existing);
                p.Changes.Add(next);
            }
            if (classify && !n.IsContainer)
            {
                ITxObject part = n.Source ?? n.BoundPart;
                string error = null;
                if (part == null)
                {
                    var candidates = new List<ITxObject>(); List<ITxObject> values;
                    if (n.SourceName != null && p.Candidates.TryGetValue(n.SourceName, out values)) candidates.AddRange(values);
                    if (!string.IsNullOrEmpty(n.PartNumber) && n.PartNumber != n.SourceName && p.Candidates.TryGetValue(n.PartNumber, out values)) candidates.AddRange(values);
                    if (candidates.Count == 1) part = candidates[0];
                    else error = candidates.Count == 0 ? "未匹配零件" : "多个同名零件，需明确实例";
                }
                var c = new StructureChange { Node = n, Parent = next, Name = n.SourceName, Existing = part, Error = error };
                p.Changes.Add(c);
                if (part != null && p.Changes.Count(x => !x.Container && x.Existing != null && Id(x.Existing) == Id(part)) > 1)
                {
                    foreach (var duplicate in p.Changes.Where(x => !x.Container && x.Existing != null && Id(x.Existing) == Id(part)))
                        duplicate.Error = "同一零件实例被多个来源节点匹配";
                    p.Issues.Add(c.Name + "：同一零件实例被多个来源节点匹配");
                }
                var destination = next == null ? p.Target : next.Existing;
                c.Unchanged = part != null && destination != null && Id(Parent(part)) == Id(destination);
                if (error != null) p.Issues.Add(c.Name + "：" + error);
                else Track(p, part);
            }
            foreach (var child in n.Children) Add(p, child, next, true, classify);
        }
        public static string Apply(StructurePlan p, Action<string> log)
        {
            p.Execution = new WorkflowExecution();
            if (!ReferenceEquals(p.Document, TxApplication.ActiveDocument)) throw new InvalidOperationException("活动文档已改变。");
            if (p.Issues.Count > 0) throw new InvalidOperationException("请明确零件实例或处理有问题的分支后重新检查。");
            var objects = p.Changes.Select(c => c.Existing).Concat(new[] { p.Target }).Concat(p.Changes.Select(c => c.Node.Source)).Where(o => o != null).ToList();
            foreach (var o in objects) if (!p.Snapshot.ContainsKey(Id(o)) || p.Snapshot[Id(o)] != Stamp(o)) throw new InvalidOperationException("场景已变化，请重新预览。");
            var result = new Dictionary<StructureChange, ITxObject>();
            using (SceneUndoScope.Begin("零件树整理"))
            {
                foreach (var c in p.Changes)
                {
                    p.Execution.Current = c.Name;
                    var parent = c.Parent == null ? p.Target : result[c.Parent];
                    var value = c.Existing;
                    if (c.Container)
                    {
                        if (value == null) { p.Execution.WriteAttempted = true; value = PsCompoundHelper.CreatePart(parent, c.Name, c.Name, setTypeName: false); p.Execution.Created++; }
                        result[c] = value;
                    }
                    else if (Children(parent).Any(x => Id(x) == Id(value))) p.Execution.Unchanged++;
                    else
                    {
                        if (Under(parent, value)) throw new InvalidOperationException("禁止把对象移入自身后代。");
                        var original = Parent(value);
                        var originalSiblings = original == null ? new List<ITxObject>() : Children(original);
                        try { dynamic destination = parent; p.Execution.WriteAttempted = true; destination.AddObject(value);
                            if (!Children(parent).Any(x => Id(x) == Id(value))) throw new InvalidOperationException("移动后父级验证失败。"); p.Execution.Moved++; }
                        catch { if (!Restore(original, value, originalSiblings)) log("父级或顺序补偿未完成，请 Ctrl+Z 撤销本批次。"); throw; }
                    }
                    c.Result = value;
                }
            }
            p.Execution.Completed = true;
            return "执行完成：" + p.Execution.Summary;
        }
        public static bool Restore(ITxObject original, ITxObject value, List<ITxObject> siblings)
        {
            if (original == null) return false;
            int index = siblings.FindIndex(o => Id(o) == Id(value));
            try
            {
                dynamic destination = original;
                if (index > 0) destination.AddObjectAfter(value, siblings[index - 1]);
                else if (index == 0 && siblings.Count > 1) destination.AddObjectBefore(value, siblings[1]);
                else destination.AddObject(value);
            }
            catch { try { dynamic destination = original; destination.AddObject(value); } catch { return false; } }
            var actual = Children(original);
            return index >= 0 && actual.FindIndex(o => Id(o) == Id(value)) == index;
        }
    }
}
