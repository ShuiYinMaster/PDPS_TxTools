// TxTools.CatiaPartTree / Ps / CatiaPartTreeService.cs
// 服务层：读 CATIA 树 / 在 PS 零件树创建 CompoundPart 层级 / 把已导入零件归类进对应容器。
// 复用 Agent 的 CatiaTreeReader（读树）与 PsCompoundHelper（建 CompoundPart）。
// 所有 PS SDK 调用必须在 PS 主线程；本服务由窗体在 PS 主线程调用。

using System;
using System.Collections;
using System.Collections.Generic;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;
using TxTools.Agent.Core.Catia;
using TxTools.CatiaPartTree.Core;

namespace TxTools.CatiaPartTree.Ps
{
    public sealed class CatiaPartTreeService
    {
        private static void Nop(string s) { }

        /// <summary>最近一次建树建立的「树节点 -> PS CompoundPart 容器」映射。</summary>
        private readonly Dictionary<TreeEditNode, TxCompoundPart> _created =
            new Dictionary<TreeEditNode, TxCompoundPart>();
        private ITxObject _lastParent;

        public Dictionary<TreeEditNode, TxCompoundPart> Created { get { return _created; } }

        public void ClearMapping()
        {
            _created.Clear();
            _lastParent = null;
        }

        // ── 1. 读 CATIA 树（COM 调用，需 STA 线程；由窗体在后台线程调用）──

        public TreeEditNode ReadCatiaTree(int maxDepth)
        {
            var catiaRoot = CatiaTreeReader.ReadActiveTree(maxDepth);
            return TreeEditNode.FromCatia(catiaRoot, null);
        }

        // ── 2. 按调整后的树在 PS 创建 CompoundPart 层级 ──

        public BuildResult BuildTree(TreeEditNode root, string parentName,
            bool includeRoot, bool preferPartNumber, bool createLeafContainers,
            bool reloadAfterBuild, Action<string> log)
        {
            log = log ?? Nop;
            var doc = TxApplication.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有打开的研究文档。");

            ITxObject parent;
            try { parent = PsCompoundHelper.ResolveParent(parentName, typeof(ITxCompoundPartCreation)); }
            catch (Exception ex) { throw new InvalidOperationException("找不到 PS 父对象 - " + ex.Message); }
            _lastParent = parent;

            var result = new BuildResult();
            var um = OpenUndo("从 CATIA 树创建零件树", log);
            try
            {
                if (includeRoot && root.Include)
                {
                    if (IsContainerNode(root, createLeafContainers))
                    {
                        var cp = TryCreate(parent, root, preferPartNumber, log, result);
                        if (cp != null && root.Children.Count > 0)
                            CreateChildren(cp, root, preferPartNumber, createLeafContainers, log, result);
                    }
                    else
                    {
                        result.SkippedLeaf++;
                        log("[创建] ~ 根是叶子零件，不建空集: " + root.Name);
                        CreateChildren(parent, root, preferPartNumber, createLeafContainers, log, result);
                    }
                }
                else
                {
                    CreateChildren(parent, root, preferPartNumber, createLeafContainers, log, result);
                }
                CommitUndo(um, log);

                if (reloadAfterBuild)
                {
                    if (TrySaveAndReload(log))
                        ReResolveAfterReload(parentName, root, includeRoot,
                            preferPartNumber, createLeafContainers, log);
                }
            }
            catch (Exception ex)
            {
                AbortUndo(um, log);
                throw new InvalidOperationException("建树异常: " + ex.Message);
            }
            return result;
        }

        private void CreateChildren(ITxObject psParent, TreeEditNode catiaParent,
            bool preferPartNumber, bool createLeafContainers, Action<string> log, BuildResult result)
        {
            foreach (var child in catiaParent.Children)
            {
                if (!child.Include) { result.Skipped++; continue; }
                // 只有装配/有子节点的节点才建 CompoundPart 空集；CATIA 叶子零件跳过，
                // 其匹配的已导入零件在归类时直接移入最近的上层装配容器。
                if (!IsContainerNode(child, createLeafContainers))
                {
                    result.SkippedLeaf++;
                    log("[创建] ~ 跳过叶子零件(不建空集): " + child.Name);
                    continue;
                }
                var cp = TryCreate(psParent, child, preferPartNumber, log, result);
                if (cp != null && child.Children.Count > 0)
                    CreateChildren(cp, child, preferPartNumber, createLeafContainers, log, result);
            }
        }

        private static bool IsContainerNode(TreeEditNode n, bool createLeafContainers)
        {
            return createLeafContainers || n.IsAssembly || n.Children.Count > 0;
        }

        private TxCompoundPart TryCreate(ITxObject psParent, TreeEditNode node,
            bool preferPartNumber, Action<string> log, BuildResult result)
        {
            var typeName = TypeNameFor(node);
            var desired = DesiredNameFor(node, preferPartNumber);
            try
            {
                // 空零件集:不传 TypeName,否则 PlanningType 丢失变成非标准对象。
                var cp = PsCompoundHelper.CreatePart(psParent, typeName, desired, setTypeName: false);
                _created[node] = cp;
                result.Created++;
                if (result.Created <= 40)
                    log("[创建] + " + SafeName(cp) + "  [" + typeName + "]");
                else if (result.Created == 41)
                    log("[创建] ... (以下省略)");
                return cp;
            }
            catch (Exception ex)
            {
                result.Failed++;
                log("[创建] ! " + (node.Name ?? "?") + " 失败: " + ex.Message);
                return null;
            }
        }

        // ── 3. 归类：把范围内已导入的零件/组件移入匹配的容器 ──

        public ClassifyResult Classify(TreeEditNode root, string scopeName,
            bool moveUnmatched, Action<string> log)
        {
            log = log ?? Nop;
            var doc = TxApplication.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有打开的研究文档。");

            if (_created.Count == 0)
                log("[归类] 提示: 当前会话没有建树映射，可能找不到匹配容器。建议先执行 [创建零件树]。");

            var scope = ResolveScope(scopeName);
            var parts = EnumerateParts(scope, log);
            log("[归类] 范围内零件/组件 " + parts.Count + " 个");

            var index = BuildMatchIndex(root);
            var result = new ClassifyResult();
            var um = OpenUndo("归类已导入零件", log);
            TxCompoundPart unmatchedContainer = null;
            try
            {
                foreach (var part in parts)
                {
                    var node = Match(index, part);
                    if (node == null)
                    {
                        result.Unmatched++;
                        if (moveUnmatched)
                        {
                            if (unmatchedContainer == null)
                                unmatchedContainer = EnsureUnmatchedContainer(root, log);
                            if (unmatchedContainer != null)
                            {
                                if (MoveInto(unmatchedContainer, part, log)) { result.MovedUnmatched++; }
                                else result.Failed++;
                            }
                        }
                        continue;
                    }

                    TxCompoundPart cp = ResolveContainer(node);
                    if (cp != null)
                    {
                        if (MoveInto(cp, part, log))
                        {
                            result.Matched++;
                            log("[归类] ✓ " + SafeName(part) + " → " + node.Name);
                        }
                        else result.Failed++;
                    }
                    else
                    {
                        result.NoContainer++;
                        log("[归类] ~ " + SafeName(part) + " 匹配[" + node.Name + "]但无容器，跳过");
                    }
                }
                CommitUndo(um, log);
            }
            catch (Exception ex)
            {
                AbortUndo(um, log);
                throw new InvalidOperationException("归类异常: " + ex.Message);
            }
            return result;
        }

        private TxCompoundPart EnsureUnmatchedContainer(TreeEditNode root, Action<string> log)
        {
            ITxObject parent = _lastParent;
            TxCompoundPart rootCp;
            if (_created.TryGetValue(root, out rootCp) && rootCp != null) parent = rootCp;
            if (parent == null)
            {
                try { parent = PsCompoundHelper.ResolveParent(null, typeof(ITxCompoundPartCreation)); }
                catch { return null; }
            }
            try
            {
                var cp = PsCompoundHelper.CreatePart(parent, "Unclassified", "未分类", setTypeName: false);
                log("[归类] 创建未分类容器: " + SafeName(cp));
                return cp;
            }
            catch (Exception ex)
            {
                log("[归类] 创建未分类容器失败: " + ex.Message);
                return null;
            }
        }

        // ── 匹配：节点名/PartNumber → 树节点 ──

        private static Dictionary<string, TreeEditNode> BuildMatchIndex(TreeEditNode root)
        {
            var index = new Dictionary<string, TreeEditNode>(StringComparer.OrdinalIgnoreCase);
            Visit(root, delegate (TreeEditNode n)
            {
                AddKey(index, n.Name, n);
                AddKey(index, n.PartNumber, n);
            });
            return index;
        }

        private static void AddKey(Dictionary<string, TreeEditNode> index, string key, TreeEditNode node)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            TreeEditNode existing;
            if (!index.TryGetValue(key, out existing)) { index[key] = node; return; }
            // 优先叶节点作为容器（更具体）
            if (node.IsLeaf && !existing.IsLeaf) index[key] = node;
        }

        private static void Visit(TreeEditNode n, Action<TreeEditNode> action)
        {
            action(n);
            foreach (var c in n.Children) Visit(c, action);
        }

        private static TreeEditNode Match(Dictionary<string, TreeEditNode> index, ITxObject part)
        {
            var name = SafeName(part);
            if (string.IsNullOrEmpty(name)) return null;
            TreeEditNode node;
            if (index.TryGetValue(name.Trim(), out node)) return node;
            // 模糊：节点名/PartNumber 出现在零件名中
            foreach (var kv in index)
            {
                var key = kv.Key;
                if (key.Length < 2) continue;
                if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
            }
            return null;
        }

        // ── 命名统一（建树与重载后重定位必须用同一套结果，避免斜杠等引起保存/重载崩溃）──

        /// <summary>文件名/路径非法字符替换为 '-'，防止保存+重载数据损坏。</summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '<' || c == '>' || c == '|' || c == '\0')
                    sb.Append('-');
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string TypeNameFor(TreeEditNode n)
        {
            return Sanitize(!string.IsNullOrWhiteSpace(n.PartNumber) ? n.PartNumber : n.Name);
        }

        private static string DesiredNameFor(TreeEditNode n, bool preferPartNumber)
        {
            return Sanitize((preferPartNumber && !string.IsNullOrWhiteSpace(n.PartNumber))
                            ? n.PartNumber : n.Name);
        }

        /// <summary>取节点或其最近祖先里已建成的容器（叶子零件归类到上层装配容器）。</summary>
        private TxCompoundPart ResolveContainer(TreeEditNode node)
        {
            for (var cur = node; cur != null; cur = cur.Parent)
            {
                TxCompoundPart cp;
                if (_created.TryGetValue(cur, out cp) && cp != null) return cp;
            }
            return null;
        }

        // ── 保存并重载：重建 PS 树视图（SaveDataToFile → LoadDataFromFile → RefreshDisplay）──

        private static bool TrySaveAndReload(Action<string> log)
        {
            var doc = TxApplication.ActiveDocument;
            if (doc == null) { log("[刷新] 无活动文档，跳过保存/重载。"); return false; }
            var prov = doc.PlatformGlobalServicesProvider;
            if (prov == null) { log("[刷新] PlatformGlobalServicesProvider 不可用，跳过保存/重载。"); return false; }
            string path;
            try { path = doc.FinalDestination; } catch { path = null; }
            if (string.IsNullOrEmpty(path))
            { log("[刷新] 未找到研究保存路径 (FinalDestination 为空)，跳过保存/重载。"); return false; }

            try { prov.SaveDataToFile(path, TxExportStudyAttributesMode.AllAttributes); }
            catch (Exception ex) { log("[刷新] 保存失败: " + ex.Message); return false; }
            log("[刷新] 已保存研究: " + path);

            try { prov.LoadDataFromFile(path); }
            catch (Exception ex) { log("[刷新] 重载失败: " + ex.Message); return false; }
            log("[刷新] 已重载研究，PS 树视图已重建刷新。");

            try { TxApplication.RefreshDisplay(); } catch { }
            return true;
        }

        // ── 重载后按名字重新定位已建容器，恢复建树映射（旧引用已失效）──

        private void ReResolveAfterReload(string parentName, TreeEditNode root,
            bool includeRoot, bool preferPartNumber, bool createLeafContainers, Action<string> log)
        {
            _created.Clear();
            ITxObject parent = null;
            try { parent = PsCompoundHelper.ResolveParent(parentName, typeof(ITxCompoundPartCreation)); }
            catch (Exception ex) { log("[刷新] 重载后父级解析失败: " + ex.Message); }
            _lastParent = parent;
            if (parent == null) { log("[刷新] 建树映射已清空，归类时需先重新创建零件树。"); return; }

            int found = 0;
            if (includeRoot && root.Include)
            {
                if (IsContainerNode(root, createLeafContainers))
                {
                    var cp = FindChildContainer(parent, DesiredNameFor(root, preferPartNumber));
                    if (cp != null)
                    {
                        _created[root] = cp; found++;
                        if (root.Children.Count > 0)
                            WalkResolve(cp, root, preferPartNumber, createLeafContainers, ref found);
                    }
                }
                else
                {
                    WalkResolve(parent, root, preferPartNumber, createLeafContainers, ref found);
                }
            }
            else
            {
                WalkResolve(parent, root, preferPartNumber, createLeafContainers, ref found);
            }
            log("[刷新] 重载后重新定位容器 " + found + " 个。");
        }

        private void WalkResolve(ITxObject psParent, TreeEditNode catiaParent,
            bool preferPartNumber, bool createLeafContainers, ref int found)
        {
            foreach (var child in catiaParent.Children)
            {
                if (!child.Include) continue;
                if (!IsContainerNode(child, createLeafContainers)) continue;
                var cp = FindChildContainer(psParent, DesiredNameFor(child, preferPartNumber));
                if (cp != null)
                {
                    _created[child] = cp;
                    found++;
                    if (child.Children.Count > 0)
                        WalkResolve(cp, child, preferPartNumber, createLeafContainers, ref found);
                }
            }
        }

        private static TxCompoundPart FindChildContainer(ITxObject parent, string desired)
        {
            if (parent == null || string.IsNullOrEmpty(desired)) return null;
            var kids = DirectChildren(parent);
            if (kids == null) return null;
            foreach (var k in kids)
            {
                var o = k as ITxObject;
                if (o == null || !(o is TxCompoundPart)) continue;
                string n;
                try { n = o.Name; } catch { n = null; }
                if (string.Equals(n, desired, StringComparison.OrdinalIgnoreCase))
                    return (TxCompoundPart)o;
            }
            return null;
        }

        private static IEnumerable DirectChildren(ITxObject parent)
        {
            try
            {
                dynamic compound = parent as ITxCompound;
                if (compound == null) return null;
                return compound.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject))) as IEnumerable;
            }
            catch { return null; }
        }

        // ── 枚举范围内待归类的零件/组件 ──

        private static ITxObject ResolveScope(string scopeName)
        {
            var doc = TxApplication.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有打开的研究文档。");
            if (scopeName == "PhysicalRoot") return doc.PhysicalRoot;
            if (scopeName == "当前选中")
            {
                var sel = TxApplication.ActiveSelection;
                if (sel == null || sel.GetItems().Count == 0)
                    throw new InvalidOperationException("当前没有选中对象。");
                return sel.GetItems()[0];
            }
            // 默认：零件树（PrLine / 第一个支持 ITxCompoundPartCreation 的对象）
            return PsCompoundHelper.ResolveParent(null, typeof(ITxCompoundPartCreation));
        }

        private static List<ITxObject> EnumerateParts(ITxObject scope, Action<string> log)
        {
            var result = new List<ITxObject>();
            if (scope == null) return result;

            TxObjectList all = null;
            try
            {
                var coll = scope as ITxObjectCollection;
                if (coll != null)
                    all = coll.GetAllDescendants(new TxTypeFilter(typeof(ITxObject)));
            }
            catch (Exception ex) { log("[归类] 枚举后代失败: " + ex.Message); }

            if (all == null)
            {
                if (IsPartCandidate(scope)) result.Add(scope);
                return result;
            }
            foreach (ITxObject o in all)
                if (IsPartCandidate(o)) result.Add(o);
            return result;
        }

        private static bool IsPartCandidate(ITxObject o)
        {
            if (o == null) return false;
            var t = o.GetType().Name;
            // 排除复合容器（CompoundPart / CompoundResource 是归类的目标，不是被归类的零件）
            if (t.IndexOf("Compound", StringComparison.Ordinal) >= 0) return false;
            if (t == "TxPart") return true;
            if (t == "TxComponent") return true;
            if (t.IndexOf("Part", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ── 移动零件到容器 ──

        private static bool MoveInto(ITxObject target, ITxObject comp, Action<string> log)
        {
            if (target == null || comp == null) return false;
            try { dynamic d = target; d.AddObject(comp); return true; }
            catch (Exception e) { log("[移动] AddObject×: " + e.Message); }
            try
            {
                var l = new TxObjectList();
                l.Add(comp);
                dynamic d = target;
                d.AddObjects(l);
                return true;
            }
            catch (Exception e) { log("[移动] AddObjects×: " + e.Message); }
            return false;
        }

        // ── Undo ──

        private static object OpenUndo(string name, Action<string> log)
        {
            try
            {
                dynamic um = TxApplication.ActiveUndoManager;
                if (um == null) return null;
                try { um.OpenUndoTransaction(name); return um; } catch { }
                try { um.OpenTransaction(name); return um; } catch { }
                try { um.StartTransaction(name); return um; } catch { }
                try { um.BeginUndoTransaction(name); return um; } catch { }
            }
            catch { }
            log("[Undo] 未开启事务（PS 仍可 Ctrl+Z）");
            return null;
        }

        private static void CommitUndo(object um, Action<string> log)
        {
            if (um == null) return;
            try
            {
                dynamic d = um;
                try { d.CommitUndoTransaction(); return; } catch { }
                try { d.CommitTransaction(); return; } catch { }
                try { d.Commit(); return; } catch { }
            }
            catch { }
        }

        private static void AbortUndo(object um, Action<string> log)
        {
            if (um == null) return;
            try
            {
                dynamic d = um;
                try { d.AbortUndoTransaction(); return; } catch { }
                try { d.AbortTransaction(); return; } catch { }
                try { d.Rollback(); return; } catch { }
            }
            catch { }
            log("[Undo] 已尝试回滚");
        }

        private static string SafeName(ITxObject o)
        {
            try { return o != null ? o.Name : null; } catch { return null; }
        }
    }

    public sealed class BuildResult
    {
        public int Created;
        public int Failed;
        public int Skipped;
        public int SkippedLeaf;
    }

    public sealed class ClassifyResult
    {
        public int Matched;
        public int Unmatched;
        public int MovedUnmatched;
        public int NoContainer;
        public int Failed;
    }
}