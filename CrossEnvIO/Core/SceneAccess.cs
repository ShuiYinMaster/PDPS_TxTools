using System.Collections.Generic;
using System.Linq;
using Tecnomatix.Engineering;

namespace TxTools.CrossEnvIO
{
    /// <summary>SDK 集合通过查询接口读取；场景节点本身不保证实现 IEnumerable。</summary>
    internal static class SceneAccess
    {
        internal static List<ITxObject> Children(ITxObject node)
        {
            var collection = node as ITxObjectCollection;
            return collection == null ? new List<ITxObject>()
                : collection.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject))).Cast<ITxObject>().Where(o => o != null).ToList();
        }

        internal static ITxObject Parent(ITxObject node) => node?.Collection as ITxObject;
    }
}
