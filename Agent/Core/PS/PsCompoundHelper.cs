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
            try
            {
                var p = data.GetType().GetProperty("Collection");
                if (p != null && p.CanWrite) p.SetValue(data, parent as ITxObjectCollection, null);
            }
            catch { }
            if (!string.IsNullOrEmpty(typeName)) data.TypeName = typeName;

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
                    " \u4e0d\u652f\u6301\u521b\u5efa CompoundPart(\u672a\u5b9e\u73b0 ITxCompoundPartCreation)\u3002" +
                    "\u5e38\u89c1\u53ef\u7528\u7236\u7ea7: PhysicalRoot \u3001 TxCompoundPart\u3002");

            var data = new TxCompoundPartCreationData();
            try
            {
                var p = data.GetType().GetProperty("Collection");
                if (p != null && p.CanWrite) p.SetValue(data, parent as ITxObjectCollection, null);
            }
            catch { }
            if (setTypeName && !string.IsNullOrEmpty(typeName)) data.TypeName = typeName;

            var result = creator.CreateCompoundPart(data);
            var cp = result as TxCompoundPart;
            if (cp == null) throw new InvalidOperationException("\u521b\u5efa\u540e\u8fd4\u56de\u5bf9\u8c61\u4e3a\u7a7a\u3002");

            if (!string.IsNullOrEmpty(desiredName))
                TryRename(cp, desiredName);

            return cp;
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
