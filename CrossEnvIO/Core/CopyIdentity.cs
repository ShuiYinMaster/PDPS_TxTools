using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.CrossEnvIO
{
    /// <summary>复制结果必须来自本次新增对象；不以名称或枚举顺序选择已有对象。</summary>
    internal static class CopyIdentity
    {
        internal static T FindNew<T>(T source, IEnumerable<string> beforeIds, IEnumerable<T> after,
            Func<T, string> id, Func<T, string> name, Func<T, Type> type) where T : class
        {
            var previous = new HashSet<string>(beforeIds);
            var candidates = after.Where(obj => obj != null && !previous.Contains(id(obj))
                && type(obj) == type(source)).ToList();
            string sourceName = name(source) ?? "";
            var named = candidates.Where(obj => name(obj) == sourceName
                || (name(obj) ?? "").StartsWith(sourceName + "_", StringComparison.Ordinal)).ToList();
            if (named.Count == 1) return named[0];
            return named.Count == 0 && candidates.Count == 1 ? candidates[0] : null;
        }
    }
}
