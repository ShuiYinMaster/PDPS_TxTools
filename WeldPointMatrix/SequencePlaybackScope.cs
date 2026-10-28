using System;
using System.Collections.Generic;
using System.Linq;
using Tecnomatix.Engineering;

namespace TxTools.WeldPointMatrix
{
    internal static class SequencePlaybackScope
    {
        // Resolve an existing scope. Never reparent, paste or retime source operations:
        // inherited part appearances and their weld locations depend on that hierarchy.
        internal static ITxOperation Resolve(IList<ITxObject> selected)
        {
            if (selected == null || selected.Count == 0)
                throw new InvalidOperationException("请先添加要播放的操作。");
            if (selected.Any(o => !(o is ITxOperation)))
                throw new InvalidOperationException("播放列表包含非操作对象。");
            if (selected.Count == 1) return (ITxOperation)selected[0];

            ITxObject ancestor = selected[0].Collection as ITxObject;
            for (int depth = 0; ancestor != null && depth < 64; depth++)
            {
                if (ancestor is ITxCompoundOperation compound)
                {
                    var covered = new List<ITxObject>();
                    if (CoversOnlySelection(compound, selected, covered, 0)
                        && selected.All(s => covered.Any(c => Same(s, c))))
                        return compound;
                }
                ancestor = ancestor.Collection as ITxObject;
            }
            throw new InvalidOperationException("所选操作没有可直接播放的共同复合范围，或共同范围包含未选中的其他操作。" +
                "请在列表中选择现有复合操作的完整播放范围。插件不会移动操作建立临时组，以免丢失继承的外观绑定。");
        }

        private static bool CoversOnlySelection(ITxObject node, IList<ITxObject> selected,
            List<ITxObject> covered, int depth)
        {
            if (depth > 64) return false;
            if (selected.Any(s => Same(s, node))) { covered.Add(node); return true; }
            var collection = node as ITxObjectCollection;
            if (collection == null) return false;
            var children = collection.GetDirectDescendants(new TxTypeFilter(typeof(ITxOperation)));
            if (children == null || children.Count == 0) return false;
            foreach (ITxObject child in children)
                if (!CoversOnlySelection(child, selected, covered, depth + 1)) return false;
            return true;
        }

        private static bool Same(ITxObject left, ITxObject right)
            => ReferenceEquals(left, right) || (left != null && right != null && left.Equals(right));
    }
}
