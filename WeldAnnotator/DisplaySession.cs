using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
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
            internal string Id;
            internal TxDisplayableObjectVisibility? Visibility;
            internal bool Keep, Protected, Covered;
            internal bool? Applied;
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
            internal List<Node> Nodes;
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
            var timer = Stopwatch.StartNew();
            CheckDocument();
            var requested = new HashSet<string>(StringComparer.Ordinal);
            if (whitelist != null)
                foreach (ITxObject obj in whitelist)
                    if (obj is ITxDisplayableObject) requested.Add(obj.Id);
            if (requested.Count == 0)
                throw new InvalidOperationException("白名单没有可显示的对象，已取消操作。");

            Snapshot current = Capture();
            long captureMs = timer.ElapsedMilliseconds;
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (Node node in current.Nodes)
            {
                bool selected = requested.Contains(node.Id);
                node.Keep = selected || (node.Parent != null && node.Parent.Keep);
                if (selected)
                {
                    found.Add(node.Id);
                    for (Node parent = node.Parent; parent != null; parent = parent.Parent)
                    {
                        if (parent.Protected) break;
                        parent.Protected = true;
                    }
                }
            }
            if (found.Count != requested.Count)
                throw new InvalidOperationException("部分白名单对象不在当前场景树中，已取消操作。请重新选择操作。");

            RememberBaseline(current);
            RunChange("焊点标注：仅显示外观", delegate
            {
                // Display/Blank apply to a whole subtree. Only call at its boundary.
                // Non-displayable nodes pass coverage through to their children.
                foreach (Node node in current.Nodes)
                {
                    node.Covered = node.Parent != null && node.Parent.Covered;
                    if (node.Covered || !node.Visibility.HasValue ||
                        node.Visibility == TxDisplayableObjectVisibility.CannotBeDisplayed) continue;
                    if (!node.Keep && node.Protected) continue;
                    var target = node.Keep ? TxDisplayableObjectVisibility.All : TxDisplayableObjectVisibility.None;
                    node.Covered = node.Visibility == target || Set(node.Object, node.Keep, true);
                }
            });

            long changeMs = timer.ElapsedMilliseconds;
            bool ok = Verify(current, delegate(State state)
            {
                if (state.Node.Keep) return TxDisplayableObjectVisibility.All;
                if (state.Node.Protected) return null;
                return TxDisplayableObjectVisibility.None;
            }, "仅显示外观");
            _log("[显示] 仅显示外观总耗时 " + timer.ElapsedMilliseconds + " ms，快照 " + captureMs +
                " ms，规划及变更 " + (changeMs - captureMs) + " ms，校验 " +
                (timer.ElapsedMilliseconds - changeMs) + " ms，显示/隐藏调用 " + _setCalls);
            return ok;
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
                foreach (Node node in snapshot.Nodes)
                {
                    node.Applied = node.Parent == null ? null : node.Parent.Applied;
                    if (!node.Visibility.HasValue ||
                        node.Visibility == TxDisplayableObjectVisibility.CannotBeDisplayed) continue;
                    // Partial requires a visible parent followed by hidden child branches.
                    bool visible = node.Visibility != TxDisplayableObjectVisibility.None;
                    if (node.Applied == visible || Set(node.Object, visible, true))
                        node.Applied = visible;
                    else node.Applied = null;
                }
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
                var visibility = displayable.Visibility;
                node.Visibility = visibility;
                states.Add(new State { Node = node, Visibility = visibility });
            }
            if (states.Count == 0) throw new InvalidOperationException("未读取到可显示对象，已取消操作。");
            return new Snapshot { Document = doc, States = states, Nodes = nodes };
        }

        private static List<Node> Enumerate(TxDocument doc)
        {
            var result = new List<Node>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
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
                if (root == null || !seen.Add(root.Id)) continue;
                var pending = new Stack<Node>();
                PushChildren(root, null, pending);
                while (pending.Count > 0)
                {
                    Node node = pending.Pop();
                    node.Id = node.Object.Id;
                    if (!seen.Add(node.Id)) continue;
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
            _setCalls = 0;
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

        private bool Set(ITxObject obj, bool visible, bool force = false)
        {
            try
            {
                var displayable = (ITxDisplayableObject)obj;
                TxDisplayableObjectVisibility target = visible
                    ? TxDisplayableObjectVisibility.All : TxDisplayableObjectVisibility.None;
                if (!force && displayable.Visibility == target) return true;
                _setCalls++;
                if (visible) displayable.Display(); else displayable.Blank();
                return true;
            }
            catch (Exception ex)
            {
                // 汇总校验会报告失败数量；逐项错误仅保留前几个示例。
                if (_callErrors++ < 3) _log("[显示] 调用失败 " + Describe(obj) + "：" + ex.Message);
                return false;
            }
        }

        private int _callErrors;
        private int _setCalls;

        private bool Verify(Snapshot snapshot, Func<State, TxDisplayableObjectVisibility?> expected, string action)
        {
            int none = 0, partial = 0, all = 0, failed = 0, excluded = 0;
            foreach (State state in snapshot.States)
            {
                ITxObject obj = state.Node.Object;
                try
                {
                    if (state.Visibility == TxDisplayableObjectVisibility.CannotBeDisplayed)
                    {
                        excluded++;
                        continue;
                    }
                    var actual = ((ITxDisplayableObject)obj).Visibility;
                    if (actual == TxDisplayableObjectVisibility.CannotBeDisplayed) { excluded++; continue; }
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
            _log(string.Format("[显示] {0}：None={1}，Partial={2}，All={3}，未达目标={4}，调用异常={5}，不可显示={6}",
                action, none, partial, all, failed, _callErrors, excluded));
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
