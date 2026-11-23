// TxTools.Agent / Core / Ps / PsCompoundHelper.cs
// 创建 TxCompoundResource / TxCompoundPart 的共用底层 helper。
//
// 关键 API 摸清路径:
//   parent 需要实现 ITxCompoundResourceCreation / ITxCompoundPartCreation
//     没有 Name —— 创建出来的对象 name 是自动生成的,如需自定义名字要额外 rename。
//   Rename 通过 ITxObject.Name setter (虽然接口 doc 说是 get-only,但实现类通常有 setter,
//     用反射尝试; 失败就返回警告)。
//
// 父级查找:
//   parent_name 为空 → 用 doc.PhysicalRoot (根,大多数场景合理)
//   parent_name 指定 → find_objects(name) 匹配, 优先返回类型为 TxCompound* 的
//   parent_name = "PhysicalRoot"/"PrLine" 等 → 特殊处理

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Collections;
using Tecnomatix.Engineering;

namespace TxTools.Agent.Core
{
    public static class PsCompoundHelper
    {
        // ── 父级解析 ──

        /// <summary>
        /// 根据 name 找 parent 对象。null/空 = 智能默认:
        ///   1) 先看 PhysicalRoot 本身是否实现 wantInterface, 是则用它
        ///   2) 否则从 PhysicalRoot 递归找第一个实现 wantInterface 的对象 (通常是 PrLine 之类)
        /// wantInterface 传 ITxCompoundResourceCreation 或 ITxCompoundPartCreation。
        /// </summary>
        public static ITxObject ResolveParent(string parentName, Type wantInterface, string parentId = null)
        {
            var doc = TxApplication.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("\u65e0\u6d3b\u52a8\u6587\u6863\u3002");

            if (!string.IsNullOrWhiteSpace(parentId) || !string.IsNullOrWhiteSpace(parentName)) {
                ITxObject chosen;
                if (string.IsNullOrWhiteSpace(parentId) && string.Equals(parentName, "PhysicalRoot", StringComparison.OrdinalIgnoreCase)) chosen = doc.PhysicalRoot;
                else {
                    string error;
                    if (!TxTools.Agent.Ps.PsBridge.TryResolve(parentName, parentId, out chosen, out error)) throw new ArgumentException(error);
                }
                if (wantInterface != null && !wantInterface.IsInstanceOfType(chosen)) throw new ArgumentException("指定父级不支持 " + wantInterface.Name);
                return chosen;
            }

            // 智能默认:先 PhysicalRoot, 不 support 再往下找
            ITxObject root = doc.PhysicalRoot;
            if (wantInterface == null || wantInterface.IsAssignableFrom(root.GetType()))
                return root;

            // 递归 BFS 找第一个 support wantInterface 的
            var queue = new Queue<ITxObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                var compound = cur as ITxCompound;
                if (compound == null) continue;
                // GetDirectDescendants 可能是具体类型的方法，通过 dynamic 调用以兼容不同 SDK
                IEnumerable children = null;
                try { dynamic d = compound; children = d.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject))) as IEnumerable; } catch { }
                if (children == null) continue;
                foreach (var obj in children)
                {
                    var child = obj as ITxObject;
                    if (child == null) continue;
                    if (wantInterface.IsAssignableFrom(child.GetType()))
                        return child;
                    queue.Enqueue(child);
                }
            }

            throw new InvalidOperationException(
                "PhysicalRoot \u53ca\u5176\u540e\u4ee3\u6ca1\u6709\u652f\u6301 " + wantInterface.Name + " \u7684\u5bf9\u8c61\u3002");
        }

        // ── 创建 CompoundResource ──

        public static TxCompoundResource CreateResource(ITxObject parent, string typeName, string desiredName)
        {
            var creator = parent as ITxCompoundResourceCreation;
            if (creator == null)
                throw new InvalidOperationException(
                    "\u7236\u5bf9\u8c61 " + parent.GetType().Name +
                    " \u4e0d\u652f\u6301\u521b\u5efa CompoundResource(\u672a\u5b9e\u73b0 ITxCompoundResourceCreation)\u3002" +
                    "\u5e38\u89c1\u53ef\u7528\u7236\u7ea7: PhysicalRoot \u3001 TxCompoundResource\u3002");

            var data = new TxCompoundResourceCreationData();
            // CreationData.Collection 在 2402 standalone 下被忽略，父级只由
            // “在哪个对象上调用 Create”决定，不再写这个无效字段。
            if (ShouldSetTypeName(typeName)) data.TypeName = typeName;

            var result = creator.CreateCompoundResource(data);
            var cr = result as TxCompoundResource;
            if (cr == null) throw new InvalidOperationException("\u521b\u5efa\u540e\u8fd4\u56de\u5bf9\u8c61\u4e3a\u7a7a\u3002");

            if (!string.IsNullOrEmpty(desiredName))
                TryRename(cr, desiredName);

            return cr;
        }

        // ── 创建 CompoundPart ──
        // setTypeName=false：空零件集场景。传了 TypeName 会设置 TypeName 导致 PlanningType
        // 丢失变成非标准对象；默认创建才是标准 CompoundPart (PlanningType = PmCompoundPart)。

        public static TxCompoundPart CreatePart(ITxObject parent, string typeName, string desiredName,
            bool setTypeName = true)
        {
            var creator = parent as ITxCompoundPartCreation;
            if (creator == null)
                throw new InvalidOperationException(
                    "\u7236\u5bf9\u8c61 " + parent.GetType().Name +
                    " \u4e0d\u652f\u6301\u521b\u5efa\u6a21\u62df\u590d\u5408\u96f6\u4ef6(ITxCompoundPartCreation)\u3002" +
                    "\u5e38\u89c1\u53ef\u7528\u7236\u7ea7: PhysicalRoot \u3001 TxCompoundPart\u3002");

            var data = new TxCompoundPartCreationData();
            // CreationData.Collection 在 2402 standalone 下被忽略，父级只由
            // “在哪个对象上调用 Create”决定，不再写这个无效字段。
            if (setTypeName && ShouldSetTypeName(typeName)) data.TypeName = typeName;

            var result = creator.CreateCompoundPart(data);
            var cp = result as TxCompoundPart;
            if (cp == null) throw new InvalidOperationException("\u521b\u5efa\u540e\u8fd4\u56de\u5bf9\u8c61\u4e3a\u7a7a\u3002");

            if (!string.IsNullOrEmpty(desiredName))
                TryRename(cp, desiredName);

            return cp;
        }

        // 只有当前平台注册过的类型名才能安全写入 CreationData.TypeName。
        // standalone 下 BaseTypeNames 为空，写入 Assembly/Station 等名字会让对象树
        // 图标变成带叉方块；此时应保持为空，使用默认 CompoundPart/CompoundResource。
        private static bool ShouldSetTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            try
            {
                var document = TxApplication.ActiveDocument;
                var provider = document == null ? null : document.PlatformGlobalServicesProvider;
                if (provider == null) return false;
                var property = provider.GetType().GetProperty("BaseTypeNames");
                var names = property == null ? null : property.GetValue(provider, null) as IEnumerable;
                if (names == null) return false;
                foreach (var value in names)
                    if (string.Equals(Convert.ToString(value), typeName, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return false; }
        }

        // ── 原生对象树关系 ──
        // Create/AddObject 只写托管 Collection，不写原生对象库的树关系；PS 对象树
        // (Object Viewer) 按原生关系建层，因此新层级会平铺在 Parts/Resources 根下。
        // Paste 是当前 SDK 中唯一会写入原生关系并复制整棵子树的托管入口。

        public static TxObjectList PasteSubtree(ITxObjectCollection target, IList<ITxObject> roots)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (roots == null || roots.Count == 0) return new TxObjectList();
            var source = new TxObjectList();
            foreach (var root in roots)
            {
                if (root == null) throw new InvalidOperationException("\u539f\u751f\u6811\u91cd\u5efa\u7684\u6e90\u5bf9\u8c61\u4e3a\u7a7a\u3002");
                source.Add(root);
            }
            var copies = target.Paste(source);
            if (copies == null || copies.Count != source.Count)
                throw new InvalidOperationException("Paste \u8fd4\u56de\u7684\u5bf9\u8c61\u6570\u91cf\u4e0e\u8f93\u5165\u4e0d\u4e00\u81f4\u3002");
            return copies;
        }

        /// <summary>在子对象列表里按名称找唯一对应项；名称重复时回退到顺序。</summary>
        public static List<ITxObject> MatchChildren(IList<ITxObject> source, IList<ITxObject> target)
        {
            if (source.Count != target.Count) return null;
            var used = new bool[target.Count];
            var result = new List<ITxObject>(source.Count);
            foreach (var item in source)
            {
                int index = -1;
                for (int i = 0; i < target.Count; i++)
                    if (!used[i] && string.Equals(target[i].Name, item.Name, StringComparison.Ordinal)) { index = i; break; }
                for (int i = 0; index < 0 && i < target.Count; i++) if (!used[i]) { index = i; break; }
                if (index < 0) return null;
                used[index] = true;
                result.Add(target[index]);
            }
            return result;
        }

        /// <summary>
        /// 把已建好的子树搬到 target 并通过 Paste 写入原生树关系，随后删除原对象。
        /// 只适用于本次新建、没有外部引用的对象；已有场景对象不能被替换。
        /// </summary>
        public static TxObjectList MoveToNativeTree(ITxObjectCollection target, IList<ITxObject> roots, Action<string> log = null)
        {
            var copies = PasteSubtree(target, roots);
            foreach (var root in roots)
            {
                try { root.Delete(); }
                catch (Exception error) { log?.Invoke("删除原对象失败：" + root.Name + "；" + error.Message); }
            }
            return copies;
        }

        // ── 尽力 rename (creationData 没 Name 字段,只能创建完之后再改) ──

        /// <summary>
        /// 尽力把对象重命名 —— ITxObject.Name 声明是 get-only,但实现类通常有 setter。
        /// 用反射尝试 SetName 方法 / Name setter, 都失败就静默 (调用方决定要不要日志)。
        /// </summary>
        public static bool TryRename(ITxObject obj, string newName)
        {
            if (obj == null || string.IsNullOrEmpty(newName)) return false;

            try {
                if (!obj.CanBeRenamed) return false;
                obj.Name = newName;
                return obj.Name == newName;
            } catch { return false; }
        }
    }
}
