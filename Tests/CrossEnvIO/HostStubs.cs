// 仅模拟场景存储和创建接口。生产 SDK 签名另由完整 CrossEnvIO 源码编译校验。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tecnomatix.Engineering
{
    public interface ITxObject { string Id { get; } string Name { get; set; } }
    public interface ITxObjectCollection : ITxObject, IEnumerable
    { TxObjectList GetAllDescendants(TxTypeFilter filter); }
    public interface ITxLeadingPart { }
    public interface ITxComponent : ITxObject { }
    public interface ITxStorable { object StorageObject { get; } }
    public interface ITxCompoundPartCreation { TxCompoundPart CreateCompoundPart(TxCompoundPartCreationData data); }
    public interface ITxCompoundResourceCreation { TxCompoundResource CreateCompoundResource(TxCompoundResourceCreationData data); }
    public sealed class TxObjectList : List<ITxObject> { }
    public sealed class TxTypeFilter { public Type Type; public TxTypeFilter(Type type) { Type = type; } }
    public sealed class TxVector
    { public double X, Y, Z; public TxVector(double x, double y, double z) { X = x; Y = y; Z = z; } }
    public sealed class TxTransformation
    {
        private readonly double[,] _m = new double[4, 4];
        public TxTransformation() { for (int i = 0; i < 4; i++) _m[i, i] = 1; }
        public TxVector Translation { get => new TxVector(_m[0, 3], _m[1, 3], _m[2, 3]);
            set { _m[0, 3] = value.X; _m[1, 3] = value.Y; _m[2, 3] = value.Z; } }
        public double this[int row, int col] { get => _m[row, col]; set => _m[row, col] = value; }
    }
    public class TxObject : ITxObject, ITxStorable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public TxTransformation AbsoluteLocation { get; set; } = new TxTransformation();
        public TxContainer Parent;
        public object StorageObject { get; set; }
        public TxObject(string name = "") { Name = name; }
    }
    public class TxContainer : TxObject, ITxObjectCollection, ITxCompoundPartCreation, ITxCompoundResourceCreation
    {
        public readonly List<ITxObject> Children = new List<ITxObject>();
        public TxContainer(string name = "") : base(name) { }
        public IEnumerator GetEnumerator() => Children.GetEnumerator();
        public void AddObject(ITxObject obj)
        {
            var item = (TxObject)obj;
            item.Parent?.Children.Remove(obj); item.Parent = this;
            if (!Children.Contains(obj)) Children.Add(obj);
        }
        public void AddObjects(TxObjectList items) { foreach (var item in items) AddObject(item); }
        public TxObjectList GetAllDescendants(TxTypeFilter filter)
        {
            var list = new TxObjectList();
            foreach (var item in Children)
            {
                if (filter.Type.IsInstanceOfType(item)) list.Add(item);
                if (item is ITxObjectCollection child) list.AddRange(child.GetAllDescendants(filter));
            }
            return list;
        }
        public TxCompoundPart CreateCompoundPart(TxCompoundPartCreationData data)
        { var obj = new TxCompoundPart(data.Name); AddObject(obj); return obj; }
        public TxCompoundResource CreateCompoundResource(TxCompoundResourceCreationData data)
        { var obj = new TxCompoundResource(data.Name); AddObject(obj); return obj; }
        public TxCompoundOperation CreateCompoundOperation(TxCompoundOperationCreationData data)
        { var obj = new TxCompoundOperation(data.Name); AddObject(obj); return obj; }
        public TxWeldOperation CreateWeldOperation(TxWeldOperationCreationData data)
        { var obj = new TxWeldOperation(data.Name); AddObject(obj); return obj; }
        public TxWeldLocationOperation CreateWeldLocationOperation(TxWeldLocationOperationCreationData data)
        {
            var obj = new TxWeldLocationOperation(data.Name) { WeldPoint = new TxWeldPoint(data.WeldPointCreationData.Name) };
            AddObject(obj); return obj;
        }
    }
    public sealed class TxPhysicalRoot : TxContainer { public TxPhysicalRoot(string name = "Root") : base(name) { } }
    public sealed class TxCompoundPart : TxContainer { public TxCompoundPart(string name) : base(name) { } }
    public sealed class TxCompoundResource : TxContainer { public TxCompoundResource(string name) : base(name) { } }
    public class TxComponent : TxObject, ITxComponent, ITxLeadingPart { public TxComponent(string name) : base(name) { } }
    public sealed class Storage { public string FullPath { get; set; } }
    public sealed class TxOperationRoot : TxContainer { public TxOperationRoot() : base("Operations") { } }
    public class TxCompoundOperation : TxContainer { public TxCompoundOperation(string name) : base(name) { } }
    public sealed class TxWeldOperation : TxCompoundOperation { public TxWeldOperation(string name) : base(name) { } }
    public sealed class TxWeldLocationOperation : TxObject
    {
        public TxWeldPoint WeldPoint { get; set; }
        public bool FailProject;
        public TxWeldLocationOperation(string name) : base(name) { }
        public void Project(TxComponent part, bool change)
        { if (FailProject) throw new InvalidOperationException("projection failure"); }
    }
    public sealed class TxWeldPoint : TxObject
    {
        public TxObjectList AssignedParts { get; private set; } = new TxObjectList();
        public ITxLeadingPart LeadingPart { get; set; }
        public TxWeldPoint(string name) : base(name) { }
        public void ClearAssignedParts() { AssignedParts = new TxObjectList(); }
        public void AssignParts(TxObjectList items) { AssignedParts = new TxObjectList(); AssignedParts.AddRange(items); }
    }
    public sealed class TxCompoundPartCreationData { public string Name; public TxCompoundPartCreationData(string name) { Name = name; } }
    public sealed class TxCompoundResourceCreationData { public string Name; public TxCompoundResourceCreationData(string name) { Name = name; } }
    public sealed class TxCompoundOperationCreationData { public string Name = ""; }
    public sealed class TxWeldOperationCreationData { public string Name; public TxWeldOperationCreationData(string name) { Name = name; } }
    public sealed class TxWeldLocationOperationCreationData
    { public string Name; public DataTypes.ITxWeldPointCreationData WeldPointCreationData; public TxTransformation ProjectedLocation; }
    public static class TxMfgCreationDataFactory
    {
        public static DataTypes.ITxWeldPointCreationData CreateWeldPointCreationData(string name, TxVector location)
            => new DataTypes.WeldCreationData { Name = name };
    }
    public sealed class TxDocument
    {
        public TxPhysicalRoot PhysicalRoot = new TxPhysicalRoot(); public TxOperationRoot OperationRoot = new TxOperationRoot();
        public TxObject CurrentStudy = new TxObject("Study"); public string FinalDestination = "";
    }
    public static class TxApplication
    { public static TxDocument ActiveDocument; public static string SystemRootDirectory; }
}
namespace Tecnomatix.Engineering.DataTypes
{
    public interface ITxWeldPointCreationData { string Name { get; set; } }
    public sealed class WeldCreationData : ITxWeldPointCreationData { public string Name { get; set; } }
}
namespace TxTools.Common
{
    public sealed class SceneUndoScope : IDisposable
    { public static SceneUndoScope Begin(string label) => new SceneUndoScope(); public void Dispose() { } }
}
namespace TxTools.CrossEnvIO
{
    public static class EnvLibrary { public static string CurrentRoot() => Tecnomatix.Engineering.TxApplication.SystemRootDirectory; }
    public static class ComponentIO
    {
        public static bool FixPszAfterInsert(Action<string> log) => true;
        public static bool IsPathReferencedByScene(string directory)
        {
            return Tecnomatix.Engineering.TxApplication.ActiveDocument.PhysicalRoot
                .GetAllDescendants(new Tecnomatix.Engineering.TxTypeFilter(typeof(Tecnomatix.Engineering.TxComponent)))
                .Cast<Tecnomatix.Engineering.TxObject>().Any(obj => (obj.StorageObject as Tecnomatix.Engineering.Storage)?.FullPath == directory);
        }
        public static void ResetInsertedPaths() { }
        public static string NormalizeStoragePath(string path) => string.IsNullOrEmpty(path) ? "" : Path.GetFullPath(path);
        public static Tecnomatix.Engineering.ITxObject CloneResource(Tecnomatix.Engineering.ITxObject source,
            Tecnomatix.Engineering.ITxObject parent, string name, Action<string> log)
        {
            var clone = new Tecnomatix.Engineering.TxComponent(name) { StorageObject = ((Tecnomatix.Engineering.TxObject)source).StorageObject };
            ((Tecnomatix.Engineering.TxContainer)parent).AddObject(clone); return clone;
        }
        public static bool InsertCojt(string path, string name, string proto, string library, double x, double y, double z,
            Action<string> log, out string summary, out Tecnomatix.Engineering.ITxObject inserted)
        {
            summary = "";
            inserted = new Tecnomatix.Engineering.TxComponent(name) { StorageObject = new Tecnomatix.Engineering.Storage { FullPath = path } };
            Tecnomatix.Engineering.TxApplication.ActiveDocument.PhysicalRoot.AddObject(inserted); return true;
        }
    }
}
namespace TxTools.Agent.Core
{
    public abstract class TxAgentToolBase
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract bool IsReadOnly { get; }
        public abstract Newtonsoft.Json.Linq.JObject InputSchema { get; }
        public abstract string Execute(Newtonsoft.Json.Linq.JObject input);
        protected static string GetString(Newtonsoft.Json.Linq.JObject input, string key) => (string)input[key];
    }
}
