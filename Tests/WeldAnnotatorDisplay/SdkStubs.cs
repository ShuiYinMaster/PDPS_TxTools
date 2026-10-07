// 本回归模型覆盖 Display/Blank 的子树连带效果和操作节点自身残留的 Partial。
// 不替代真实 Process Simulate SDK 集成测试。
using System;
using System.Collections.Generic;

namespace Tecnomatix.Engineering
{
    public enum TxDisplayableObjectVisibility { All, Partial, None }
    public interface ITxObject { string Id { get; } string Name { get; } }
    public interface ITxDisplayableObject
    {
        TxDisplayableObjectVisibility Visibility { get; }
        void Display();
        void Blank();
    }
    public interface ITxObjectCollection
    {
        TxObjectList GetDirectDescendants(TxTypeFilter filter);
    }
    public class TxTypeFilter { public TxTypeFilter(Type type) { } }
    public class TxObjectList : List<ITxObject> { }

    public class SceneNode : ITxObject, ITxObjectCollection
    {
        public string Id { get; private set; }
        public string Name { get; set; }
        public TxObjectList Children { get; private set; }
        public bool FailEnumeration;
        public SceneNode(string id)
        {
            Id = id;
            Name = id;
            Children = new TxObjectList();
        }
        public TxObjectList GetDirectDescendants(TxTypeFilter filter)
        {
            if (FailEnumeration) throw new InvalidOperationException("enumeration failed");
            return Children;
        }
    }

    public class DisplayNode : SceneNode, ITxDisplayableObject
    {
        public bool OwnVisible;
        public bool FailBlank, FailDisplay, FailRead;
        public int BlankCalls;
        public DisplayNode(string id, bool visible = true) : base(id) { OwnVisible = visible; }
        public TxDisplayableObjectVisibility Visibility
        {
            get
            {
                if (FailRead) throw new InvalidOperationException("read failed");
                bool all = OwnVisible, none = !OwnVisible;
                foreach (ITxObject child in Children)
                {
                    var d = child as ITxDisplayableObject;
                    if (d == null) continue;
                    all &= d.Visibility == TxDisplayableObjectVisibility.All;
                    none &= d.Visibility == TxDisplayableObjectVisibility.None;
                }
                return all ? TxDisplayableObjectVisibility.All : none
                    ? TxDisplayableObjectVisibility.None : TxDisplayableObjectVisibility.Partial;
            }
        }
        public void Display()
        {
            if (FailDisplay) throw new InvalidOperationException("display failed");
            OwnVisible = true;
            foreach (ITxObject child in Children)
                if (child is ITxDisplayableObject d) d.Display();
        }
        public void Blank()
        {
            BlankCalls++;
            if (FailBlank) throw new InvalidOperationException("blank failed");
            OwnVisible = false;
            foreach (ITxObject child in Children)
                if (child is ITxDisplayableObject d) d.Blank();
        }
    }

    public class TxDocument
    {
        public SceneNode PhysicalRoot { get; set; } = new SceneNode("physical-root");
        public SceneNode ComponentRoot { get; set; }
        public SceneNode ResourceRoot { get; set; }
        public SceneNode OperationRoot { get; set; } = new SceneNode("operation-root");
    }
    public class UndoManagerState
    {
        public int Starts, Ends;
        public bool Open, FailStart;
        public string LastName;
        protected void Start()
        {
            if (FailStart) throw new InvalidOperationException("start failed");
            if (Open) throw new Exception("nested transaction");
            Starts++;
            Open = true;
        }
    }
    public class UndoManager : UndoManagerState
    {
        public void StartTransaction() { Start(); }
        public void EndTransaction() { Ends++; Open = false; }
    }
    public class NamedUndoManager : UndoManagerState
    {
        public void StartTransaction(string name) { Start(); LastName = name; }
        public void EndTransaction() { Ends++; Open = false; }
    }
    public class UnsupportedUndoManager : UndoManagerState
    {
        public void StartTransaction(int value) { Start(); }
        public void EndTransaction() { Ends++; Open = false; }
    }
    public class MissingEndUndoManager : UndoManagerState
    {
        public void StartTransaction() { Start(); }
    }
    public static class TxApplication
    {
        public static TxDocument ActiveDocument { get; set; }
        public static UndoManagerState ActiveUndoManager { get; set; }
        public static void RefreshDisplay() { }
    }
}
