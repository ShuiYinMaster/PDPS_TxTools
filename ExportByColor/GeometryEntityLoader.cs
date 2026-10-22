using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;

namespace TxTools.ExportByColor
{
    // Shared by ordinary resources and appearance-linked parts. Showing an
    // object does not load its entity-level geometry: reload before enumeration.
    internal static class GeometryEntityLoader
    {
        internal static void Load(ITxObject node, HashSet<TxLibraryStorage> loaded,
            Action<string> log, Action<ITxObject, TxLibraryStorage> beforeReload = null)
        {
            var stored = node as ITxStorable;
            if (stored == null) return;
            try
            {
                var storage = stored.StorageObject as TxLibraryStorage;
                if (storage == null || !loaded.Add(storage)) return;
                if (beforeReload != null) beforeReload(node, storage);
                if (log != null) log("[Load Entity Level 开始] " + node.Name);
                storage.Reload(TxRepresentationLevel.Detailed);
                if (log != null) log("[Load Entity Level 完成] " + node.Name + "；Detailed");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("实体级几何加载失败：" + node.Name, ex);
            }
        }
    }
}
