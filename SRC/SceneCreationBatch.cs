using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;

namespace TxTools.Common
{
    /// <summary>只清理明确记录的本批新建对象；已有容器永远不加入所有权记录。</summary>
    public sealed class SceneCreationBatch
    {
        private readonly TxDocument _document;
        private readonly HashSet<string> _objects = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _containers = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>创建与当前工程绑定的批次记录。</summary>
        public SceneCreationBatch() { _document = TxApplication.ActiveDocument; }
        /// <summary>尚未清理的对象及容器数量。</summary>
        public int Count { get { return _objects.Count + _containers.Count; } }
        /// <summary>登记本工具创建的对象；不以名称寻找对象。</summary>
        public void Add(ITxObject obj)
        {
            if (obj != null) _objects.Add(obj.Id);
        }
        /// <summary>只登记本次新建的容器，复用的建模资源不能传入。</summary>
        public void AddNewContainer(ITxObject container)
        {
            if (container != null) _containers.Add(container.Id);
        }
        /// <summary>清理失败保留 ID 以便重试；原生撤销后已不存在的对象视为已清理。</summary>
        public bool RemoveCreated(Action<string> log)
        {
            log = log ?? delegate { };
            if (_document == null || !Equals(_document, TxApplication.ActiveDocument))
                throw new InvalidOperationException("工程已切换，不能清理原工程的生成物。");
            using (var undo = SceneUndoScope.Begin(_document, "清理上次生成物"))
            {
                foreach (string id in new List<string>(_objects))
                {
                    try
                    {
                        ITxObject obj = _document.GetObjectById(id);
                        if (obj != null) obj.Delete();
                        _objects.Remove(id);
                    }
                    catch (Exception ex) { log("[清理] 对象未删除，保留记录以便重试: " + id + " - " + ex.Message); }
                }
                bool removedContainer;
                do
                {
                    removedContainer = false;
                    foreach (string id in new List<string>(_containers))
                    {
                        try
                        {
                            ITxObject obj = _document.GetObjectById(id);
                            if (obj != null)
                            {
                                var collection = obj as ITxObjectCollection;
                                if (collection == null || collection.GetDirectDescendants(null).Count != 0)
                                    continue;
                                obj.Delete();
                            }
                            _containers.Remove(id);
                            removedContainer = true;
                        }
                        catch (Exception ex) { log("[清理] 容器未删除，保留记录以便重试: " + id + " - " + ex.Message); }
                    }
                }
                while (removedContainer);
                foreach (string id in _containers)
                    log("[清理] 容器仍有内容或无法删除，保留容器及记录: " + id);
            }
            return Count == 0;
        }
    }
}
