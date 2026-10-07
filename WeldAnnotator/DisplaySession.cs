using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Tecnomatix.Engineering;

namespace TxTools.WeldAnnotator
{
    // 每个窗口拥有独立的快照和自动恢复基线，避免多个窗口覆盖彼此的记录。
    internal sealed class DisplaySession
    {
        private sealed class Node
        {
            internal ITxObject Object;
            internal Node Parent;
        }

        private sealed class State
        {
            internal Node Node;
            internal TxDisplayableObjectVisibility Visibility;
        }

        private sealed class Snapshot
        {
            internal TxDocument Document;
            internal List<State> States;
        }

        private sealed class ObjectComparer : IEqualityComparer<ITxObject>
        {
            internal static readonly ObjectComparer Instance = new ObjectComparer();

            private static string Id(ITxObject obj)
            {
                try { return obj.Id; } catch { return null; }
            }

            public bool Equals(ITxObject x, ITxObject y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                string a = Id(x), b = Id(y);
                return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a == b;
            }

            public int GetHashCode(ITxObject obj)
            {
                string id = Id(obj);
                return string.IsNullOrEmpty(id) ? RuntimeHelpers.GetHashCode(obj) : id.GetHashCode();
            }
        }

        private readonly Action<string> _log;
        private Snapshot _manualSnapshot;
        private Snapshot _baseline;
        internal bool HasPendingChanges { get; private set; }
        internal bool HasSnapshot { get { return _manualSnapshot != null; } }
        internal bool CanRestore { get { return _manualSnapshot != null || _baseline != null; } }

        internal DisplaySession(Action<string> log)
        {
            _log = log ?? delegate { };
        }

        internal int CaptureSnapshot()
        {
            CheckDocument();
            // 读取失败时保留原快照。
            Snapshot snapshot = Capture();
            _manualSnapshot = snapshot;
            _log("[显示] 已记录 " + snapshot.States.Count + " 个对象的 None / Partial / All 状态");
            return snapshot.States.Count;
        }

        internal bool ShowOnly(IEnumerable<ITxObject> whitelist)
        {
            CheckDocument();
            var requested = new HashSet<ITxObject>(ObjectComparer.Instance);
            if (whitelist != null)
                foreach (ITxObject obj in whitelist)
                    if (obj is ITxDisplayableObject) requested.Add(obj);
            if (requested.Count == 0)
                throw new InvalidOperationException("白名单没有可显示的对象，已取消操作。");

            Snapshot current = Capture();
            var keep = new HashSet<ITxObject>(ObjectComparer.Instance);
            var ancestors = new HashSet<ITxObject>(ObjectComparer.Instance);
            var found = new HashSet<ITxObject>(ObjectComparer.Instance);
            foreach (State state in current.States)
            {
                Node node = state.Node;
                if (requested.Contains(node.Object)) found.Add(node.Object);
                for (Node p = node; p != null; p = p.Parent)
                {
                    if (!requested.Contains(p.Object)) continue;
                    keep.Add(node.Object); // 白名单组件的整个子树也应保留。
                    for (Node parent = node.Parent; parent != null; parent = parent.Parent)
                        ancestors.Add(parent.Object);
                    break;
                }
            }
            if (found.Count != requested.Count)
                throw new InvalidOperationException("部分白名单对象不在当前场景树中，已取消操作。请重新选择操作。");

            RememberBaseline(current);
            RunChange("焊点标注：仅显示外观", delegate
            {
                // 子级先隐藏、父级后隐藏：复合操作自身必须显式 Blank，
                // 不能从全部点位为 None 推断操作节点已经隐藏。
                for (int i = current.States.Count - 1; i >= 0; i--)
                {
                    ITxObject obj = current.States[i].Node.Object;
                    if (!keep.Contains(obj) && !ancestors.Contains(obj)) Set(obj, false);
                }
                // 保留的父组件先显示，随后显式显示其子级。保护祖先不调用
                // Display，否则会连带显示不属于白名单的兄弟组件。
                foreach (State state in current.States)
                    if (keep.Contains(state.Node.Object)) Set(state.Node.Object, true);
            });

            return Verify(current, delegate(State state)
            {
                ITxObject obj = state.Node.Object;
                if (keep.Contains(obj)) return TxDisplayableObjectVisibility.All;
                if (ancestors.Contains(obj)) return null;
                return TxDisplayableObjectVisibility.None;
            }, "仅显示外观");
        }

        internal bool Restore()
        {
            CheckDocument();
            Snapshot snapshot = _manualSnapshot ?? _baseline;
            if (snapshot == null)
            {
                _log("[显示] 无恢复记录，场景保持当前状态");
                return true;
            }

            RunChange("焊点标注：恢复显示", delegate
            {
                // Display/Blank 可能影响整个子树，不能逐个比较 bool 后跳过。
                // 先恢复可见父级，再隐藏原先不可见的子级，以重建 Partial。
                foreach (State state in snapshot.States)
                    if (state.Visibility != TxDisplayableObjectVisibility.None)
                        Set(state.Node.Object, true, true);
                for (int i = snapshot.States.Count - 1; i >= 0; i--)
                    if (snapshot.States[i].Visibility == TxDisplayableObjectVisibility.None)
                        Set(snapshot.States[i].Node.Object, false, true);
            });
            bool ok = Verify(snapshot, delegate(State state) { return state.Visibility; }, "恢复显示");
            if (ok)
            {
                _baseline = null;
                HasPendingChanges = false;
            }
            // 失败时保留完整基线以便重试；手动快照始终可重复恢复。
            return ok;
        }

        private void CheckDocument()
        {
            TxDocument doc = TxApplication.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有活动文档。");
            Snapshot existing = _baseline ?? _manualSnapshot;
            if (existing != null && !Equals(existing.Document, doc))
                throw new InvalidOperationException("活动文档已切换，请返回原文档恢复显示，或在当前文档重新打开窗口。");
        }

        private void RememberBaseline(Snapshot current)
        {
            if (_baseline == null) _baseline = current;
        }

        private Snapshot Capture()
        {
            TxDocument doc = TxApplication.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有活动文档。");
            var nodes = Enumerate(doc);
            var states = new List<State>();
            foreach (Node node in nodes)
            {
                var displayable = node.Object as ITxDisplayableObject;
                if (displayable == null) continue;
                // 不修改读不到状态的场景，以免失去恢复依据。
                states.Add(new State { Node = node, Visibility = displayable.Visibility });
            }
            if (states.Count == 0) throw new InvalidOperationException("未读取到可显示对象，已取消操作。");
            return new Snapshot { Document = doc, States = states };
        }

        private static List<Node> Enumerate(TxDocument doc)
        {
            var result = new List<Node>();
            var seen = new HashSet<ITxObject>(ObjectComparer.Instance);
            dynamic d = doc;
            // 根仅用作遍历入口，不切换根的显示状态。
            var roots = new List<ITxObject>();
            try { roots.Add(d.PhysicalRoot as ITxObject); } catch { }
            try { roots.Add(d.ComponentRoot as ITxObject); } catch { }
            try { roots.Add(d.ResourceRoot as ITxObject); } catch { }
            // 操作节点是否可显示由接口判断，TxWeldOperation 实现此接口。
            roots.Add(doc.OperationRoot);
            foreach (ITxObject root in roots)
            {
                if (root == null || !seen.Add(root)) continue;
                var pending = new Stack<Node>();
                PushChildren(root, null, pending);
                while (pending.Count > 0)
                {
                    Node node = pending.Pop();
                    if (!seen.Add(node.Object)) continue;
                    result.Add(node); // 父级在子级前；包含非可显示节点以保留祖先关系。
                    PushChildren(node.Object, node, pending);
                }
            }
            return result;
        }

        private static void PushChildren(ITxObject obj, Node parent, Stack<Node> pending)
        {
            // GetDirectDescendants 是复合操作/组件的标准遍历方式。
            var collection = obj as ITxObjectCollection;
            if (collection == null) return; // 叶节点不提供子级接口。
            // 集合读取失败应中止快照，不能把不完整的子树当成叶节点。
            TxObjectList children = collection.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject)));
            if (children == null) throw new InvalidOperationException("无法读取场景子树：" + Describe(obj));
            for (int i = children.Count - 1; i >= 0; i--)
            {
                ITxObject child = children[i] as ITxObject;
                if (child != null) pending.Push(new Node { Object = child, Parent = parent });
            }
        }

        private void RunChange(string name, Action change)
        {
            _callErrors = 0;
            // 不假设 StartTransaction 接受事务名称：按真实运行时签名选择。
            object undo = TxApplication.ActiveUndoManager;
            if (undo == null) throw new InvalidOperationException("无法获取撤销事务，已取消显示变更。");
            Type undoType = undo.GetType();
            MethodInfo start = FindUndoMethod(undoType, "StartTransaction", Type.EmptyTypes)
                ?? FindUndoMethod(undoType, "StartTransaction", new[] { typeof(string) });
            MethodInfo end = FindUndoMethod(undoType, "EndTransaction", Type.EmptyTypes);
            // 开启之前同时确认关闭方法，避免产生无法关闭的事务。
            if (start == null || end == null)
                throw new InvalidOperationException("撤销管理器 " + undoType.FullName +
                    " 不支持 StartTransaction() / StartTransaction(string) 与 EndTransaction()，已取消显示变更。");
            InvokeUndo(start, undo, start.GetParameters().Length == 0 ? null : new object[] { name });
            try
            {
                HasPendingChanges = true;
                change();
            }
            finally
            {
                try { InvokeUndo(end, undo, null); }
                finally { TxApplication.RefreshDisplay(); }
            }
        }

        private static MethodInfo FindUndoMethod(Type type, string name, Type[] parameters)
        {
            return type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, parameters, null);
        }

        private static void InvokeUndo(MethodInfo method, object manager, object[] arguments)
        {
            try { method.Invoke(manager, arguments); }
            catch (TargetInvocationException ex)
            {
                Exception cause = ex.InnerException ?? ex;
                throw new InvalidOperationException("撤销事务 " + method.Name + " 失败：" + cause.Message, cause);
            }
        }

        private void Set(ITxObject obj, bool visible, bool force = false)
        {
            try
            {
                var displayable = (ITxDisplayableObject)obj;
                TxDisplayableObjectVisibility target = visible
                    ? TxDisplayableObjectVisibility.All : TxDisplayableObjectVisibility.None;
                if (!force && displayable.Visibility == target) return;
                if (visible) displayable.Display(); else displayable.Blank();
            }
            catch (Exception ex)
            {
                // 汇总校验会报告失败数量；逐项错误仅保留前几个示例。
                if (_callErrors++ < 3) _log("[显示] 调用失败 " + Describe(obj) + "：" + ex.Message);
            }
        }

        private int _callErrors;

        private bool Verify(Snapshot snapshot, Func<State, TxDisplayableObjectVisibility?> expected, string action)
        {
            int none = 0, partial = 0, all = 0, failed = 0;
            foreach (State state in snapshot.States)
            {
                ITxObject obj = state.Node.Object;
                try
                {
                    var actual = ((ITxDisplayableObject)obj).Visibility;
                    if (actual == TxDisplayableObjectVisibility.None) none++;
                    else if (actual == TxDisplayableObjectVisibility.Partial) partial++;
                    else if (actual == TxDisplayableObjectVisibility.All) all++;
                    var target = expected(state);
                    if (!target.HasValue || actual == target.Value) continue;
                    if (failed++ < 3)
                        _log("[显示] 状态不符 " + Describe(obj) + "：" + actual + "，目标 " + target.Value);
                }
                catch (Exception ex)
                {
                    if (failed++ < 3) _log("[显示] 校验失败 " + Describe(obj) + "：" + ex.Message);
                }
            }
            _log(string.Format("[显示] {0}：None={1}，Partial={2}，All={3}，未达目标={4}，调用异常={5}",
                action, none, partial, all, failed, _callErrors));
            bool ok = failed == 0 && _callErrors == 0;
            _callErrors = 0;
            return ok;
        }

        private static string Describe(ITxObject obj)
        {
            try { return obj.Name + " [" + obj.Id + "]"; }
            catch { return obj.GetType().Name; }
        }
    }
}
