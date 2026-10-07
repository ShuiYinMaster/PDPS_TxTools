using System;
using System.Collections.Generic;
using System.IO;

namespace Tecnomatix.Engineering
{
    public interface ITxObject { string Id { get; } void Delete(); }
    public interface ITxObjectCollection : ITxObject { TxObjectList GetDirectDescendants(object filter); }
    public class TxObjectList : List<ITxObject> { }
    public sealed class TxUndoTransactionManager
    {
        public int Starts, Ends;
        public bool FailStart, FailEnd, Open;
        public void StartTransaction()
        {
            if (FailStart) throw new InvalidOperationException("start failed");
            if (Open) throw new InvalidOperationException("nested native transaction");
            Starts++; Open = true;
        }
        public void EndTransaction()
        {
            if (FailEnd) throw new InvalidOperationException("end failed");
            if (!Open) throw new InvalidOperationException("not open");
            Ends++; Open = false;
        }
    }
    public enum TxExportStudyAttributesMode { AllAttributes }
    public sealed class StubProvider
    {
        public int Calls;
        public bool Empty, Missing, Fail;
        public void SaveDataToFile(string path, TxExportStudyAttributesMode mode)
        {
            Calls++;
            if (Fail) throw new IOException("export failed");
            if (!Missing) File.WriteAllText(path, Empty ? "" : "study snapshot");
        }
    }
    public sealed class TxDocument
    {
        public readonly Dictionary<string, ITxObject> Objects = new Dictionary<string, ITxObject>();
        public TxUndoTransactionManager UndoManager { get; set; } = new TxUndoTransactionManager();
        public StubProvider PlatformGlobalServicesProvider { get; set; } = new StubProvider();
        public ITxObject GetObjectById(string id) { ITxObject value; Objects.TryGetValue(id, out value); return value; }
    }
    public static class TxApplication { public static TxDocument ActiveDocument { get; set; } }
    public sealed class SceneObject : ITxObjectCollection
    {
        private readonly TxDocument _document;
        public string Id { get; private set; }
        public SceneObject Parent;
        public TxObjectList Children = new TxObjectList();
        public bool FailDelete, Deleted;
        public SceneObject(TxDocument document, string id, SceneObject parent = null)
        {
            _document = document; Id = id; Parent = parent;
            document.Objects.Add(id, this);
            if (parent != null) parent.Children.Add(this);
        }
        public TxObjectList GetDirectDescendants(object filter) { return Children; }
        public void Delete()
        {
            if (FailDelete) throw new IOException("object locked");
            foreach (var child in new List<ITxObject>(Children)) child.Delete();
            _document.Objects.Remove(Id); Deleted = true;
            if (Parent != null) Parent.Children.Remove(this);
        }
    }
}
