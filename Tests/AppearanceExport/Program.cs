using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using Tecnomatix.Engineering;
using Tecnomatix.Planning;
using TxTools.ExportByColor;

// Real SDK interfaces; managed proxies provide scene state without starting PS.
public interface IAppearanceCollection : ITxPartAppearance, ITxObjectCollection, ITxLocatableObject { }
public interface ISkidCollection : ITxSkidAppearance, ITxObjectCollection, ITxLocatableObject { }
public interface ISourceCollection : ITxObjectCollection, ITxDisplayableObject, ITxLocatableObject { }
public interface IGeometry : ITxGeometry, ITxDisplayableObject, ITxLocatableObject { }

internal sealed class SceneProxy : RealProxy
{
    internal readonly ITxObject Object;
    internal readonly TxObjectList Children = new TxObjectList();
    internal TxObjectList Descendants;
    internal ITxPlanningObject Origin;
    internal ITxObject Representation;
    internal bool FailDirect, FailAll, FailDisplay, FailBlank, FailApproximation, RequireVisible, AggregateVisibility;
    internal int UnexpectedCalls, DisplayCalls, BlankCalls, ChildReads;
    internal TxDisplayableObjectVisibility State = TxDisplayableObjectVisibility.All;
    private readonly string id;

    internal SceneProxy(Type type, string id) : base(type)
    {
        this.id = id;
        Object = (ITxObject)GetTransparentProxy();
    }

    internal TxDisplayableObjectVisibility Visibility()
    {
        if (!AggregateVisibility || Children.Count == 0) return State;
        bool all = true, none = true;
        foreach (ITxObject child in Children)
        {
            var state = ((SceneProxy)RemotingServices.GetRealProxy(child)).Visibility();
            all &= state == TxDisplayableObjectVisibility.All;
            none &= state == TxDisplayableObjectVisibility.None;
        }
        return all ? TxDisplayableObjectVisibility.All : none ? TxDisplayableObjectVisibility.None : TxDisplayableObjectVisibility.Partial;
    }

    private void SetTree(TxDisplayableObjectVisibility state, HashSet<SceneProxy> seen)
    {
        if (!seen.Add(this)) return;
        State = state;
        foreach (ITxObject child in Children)
            ((SceneProxy)RemotingServices.GetRealProxy(child)).SetTree(state, seen);
    }

    public override IMessage Invoke(IMessage message)
    {
        var call = (IMethodCallMessage)message;
        try
        {
            object value = null;
            switch (call.MethodName)
            {
                case "get_Id": value = id; break;
                case "get_Name": value = "same display name"; break;
                case "GetType": value = typeof(SceneProxy); break;
                case "get_OriginPart": case "get_OriginSkid": value = Origin; break;
                case "get_EngineeringRepresentation": value = Representation; break;
                case "get_Visibility": value = Visibility(); break;
                case "Display":
                    DisplayCalls++;
                    SetTree(TxDisplayableObjectVisibility.All, new HashSet<SceneProxy>());
                    if (FailDisplay) throw new InvalidOperationException("display failed after changing children");
                    break;
                case "Blank":
                    BlankCalls++;
                    if (FailBlank) throw new InvalidOperationException("blank failed");
                    SetTree(TxDisplayableObjectVisibility.None, new HashSet<SceneProxy>());
                    break;
                case "get_Approximation":
                    if (State != TxDisplayableObjectVisibility.All || FailApproximation)
                        throw new InvalidOperationException("approximation unavailable");
                    break;
                case "GetDirectDescendants":
                    ChildReads++;
                    if (FailDirect || (RequireVisible && State == TxDisplayableObjectVisibility.None))
                        throw new InvalidOperationException("direct failed or source hidden");
                    value = Children; break;
                case "GetAllDescendants":
                    ChildReads++;
                    if (FailAll || (RequireVisible && State == TxDisplayableObjectVisibility.None))
                        throw new InvalidOperationException("all failed or source hidden");
                    value = Descendants ?? Children; break;
                default:
                    UnexpectedCalls++;
                    throw new InvalidOperationException("Unexpected scene access: " + call.MethodName);
            }
            return new ReturnMessage(value, null, 0, call.LogicalCallContext, call);
        }
        catch (Exception ex) { return new ReturnMessage(ex, call); }
    }
}

internal static class Program
{
    private static int checks;
    private static readonly Type Service = typeof(ExportByColorService);
    private static readonly Type Scope = Service.Assembly.GetType("TxTools.ExportByColor.AppearanceGeometrySource+ReadScope");
    private static readonly Type Instance = Service.Assembly.GetType("TxTools.ExportByColor.AppearanceGeometrySource+GeometryInstance");
    private static readonly MethodInfo Collect = Scope.GetMethod("Collect", BindingFlags.Instance | BindingFlags.NonPublic);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    private static object Call(MethodInfo method, object target, params object[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static object Invoke(MethodInfo method, params object[] args) { return Call(method, null, args); }
    private static IDisposable OpenScope()
    {
        return (IDisposable)Activator.CreateInstance(Scope, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { null }, null);
    }
    private static IList Read(IDisposable scope, SceneProxy root) { return (IList)Call(Collect, scope, root.Object); }
    private static ITxGeometry GeometryOf(object item)
    {
        return (ITxGeometry)Instance.GetField("Geometry", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(item);
    }
    private static string InstanceKey(object item)
    {
        return (string)Instance.GetField("InstanceKey", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(item);
    }
    private static void Fails(SceneProxy root, string message)
    {
        try { using (var scope = OpenScope()) Read(scope, root); }
        catch (InvalidOperationException) { checks++; return; }
        throw new Exception(message);
    }
    private static SceneProxy Source(string id) { return new SceneProxy(typeof(ISourceCollection), id); }
    private static SceneProxy Geometry(string id) { return new SceneProxy(typeof(IGeometry), id); }
    private static SceneProxy Appearance(string id, SceneProxy source, bool skid = false)
    {
        var appearance = new SceneProxy(skid ? typeof(ISkidCollection) : typeof(IAppearanceCollection), id);
        var planning = new SceneProxy(typeof(ITxPlanningObject), "planning-" + id);
        planning.Representation = source == null ? null : source.Object;
        appearance.Origin = (ITxPlanningObject)planning.Object;
        return appearance;
    }

    private static List<int[]> Triangles(string kind, int[] indices, int pointCount)
    {
        var constructor = typeof(TxPrimitiveData).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        var type = constructor.GetParameters()[1].ParameterType;
        var primitive = constructor.Invoke(new object[] { null, Enum.Parse(type, kind), indices });
        var method = Service.Assembly.GetType("TxTools.ExportByColor.ApproximationTriangles")
            .GetMethod("Indices", BindingFlags.Static | BindingFlags.NonPublic);
        return new List<int[]>((IEnumerable<int[]>)Invoke(method, primitive, pointCount));
    }

    private static void Main(string[] args)
    {
        Check(Triangles("Face", new[] { 0, 1, 2 }, 3).Count == 1, "A face triangle must be preserved");
        var strip = Triangles("Strip", new[] { 0, 1, 2, 3 }, 4);
        Check(strip.Count == 2 && string.Join(",", strip[0]) == "0,1,2" && string.Join(",", strip[1]) == "2,1,3", "Strip winding");
        var connectors = Triangles("Strip", new[] { 0, 1, 2, 2, 3, 4 }, 5);
        Check(connectors.Count == 2 && string.Join(",", connectors[1]) == "3,2,4", "Strip connector parity");
        Check(Triangles("Line", new[] { 0, 1, 2, 3 }, 4).Count == 0, "Lines must not become faces");
        try { Triangles("Face", new[] { 0, 1, 4 }, 3); throw new Exception("Invalid index accepted"); }
        catch (InvalidDataException) { checks++; }
        try { Triangles("Face", new[] { 0, 1, 2, 3 }, 4); throw new Exception("Polygon truncated"); }
        catch (InvalidDataException) { checks++; }

        var source = Source("source");
        var nested = Source("nested");
        var first = Geometry("geometry-1");
        var second = Geometry("geometry-2");
        source.Children.Add(nested.Object); source.Children.Add(first.Object);
        nested.Children.Add(first.Object); nested.Children.Add(second.Object); nested.Children.Add(source.Object);
        source.State = nested.State = first.State = second.State = TxDisplayableObjectVisibility.None;
        source.RequireVisible = true;
        var appearance = Appearance("appearance-1", source);
        appearance.Children.Add(Geometry("decoy").Object); // must read the linked part, not appearance children
        using (var scope = OpenScope())
        {
            var found = Read(scope, appearance);
            Check(found.Count == 2, "Linked part hierarchy must be deduplicated and cycle-safe");
            Check(ReferenceEquals(GeometryOf(found[0]), first.Object), "Wrong source geometry");
            Check(source.Visibility() == TxDisplayableObjectVisibility.All, "Hidden source not displayed");
            var approximation = GeometryOf(found[0]).Approximation;
            Check(source.BlankCalls == 0, "Source restored before approximation was read");
            var other = Appearance("appearance-2", source);
            var otherFound = Read(scope, other);
            Check(InstanceKey(found[0]) != InstanceKey(otherFound[0]), "Shared source instances were merged");
            Check(source.DisplayCalls == 1, "Shared hidden source displayed repeatedly");
        }
        Check(source.State == TxDisplayableObjectVisibility.None && first.State == TxDisplayableObjectVisibility.None,
            "Hidden source and children were not restored");
        Check(appearance.ChildReads == 0, "Appearance subtree incorrectly used as mesh source");

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Instance));
        var seen = new HashSet<string>();
        var collectChildren = Service.GetMethod("CollectGeometryChildren", BindingFlags.Static | BindingFlags.NonPublic);
        using (var scope = OpenScope())
        {
            Invoke(collectChildren, appearance.Object, list, seen, new HashSet<TxLibraryStorage>(), null, scope);
            Invoke(collectChildren, appearance.Object, list, seen, new HashSet<TxLibraryStorage>(), null, scope);
            Invoke(collectChildren, Appearance("appearance-3", source).Object, list, seen, new HashSet<TxLibraryStorage>(), null, scope);
            Check(list.Count == 4, "Production traversal lost an appearance sharing the same source");
        }
        Check(source.State == TxDisplayableObjectVisibility.None, "Production traversal did not restore source");
        var expand = Service.GetMethod("ExpandPicked", BindingFlags.Static | BindingFlags.NonPublic);
        var units = (List<ITxObject>)Invoke(expand, appearance.Object, null);
        Check(units.Count == 1 && ReferenceEquals(units[0], appearance.Object), "Appearance must stay one export unit");
        var deviceLike = Service.GetMethod("IsDeviceLike", BindingFlags.Static | BindingFlags.NonPublic);
        Check((bool)Invoke(deviceLike, appearance.Object), "Appearance selection rejected");
        Check(!(bool)Invoke(deviceLike, first.Object), "Geometry leaf became a resource");
        using (var scope = OpenScope()) Check(Read(scope, Appearance("skid", source, true)).Count == 2, "Skid source lookup failed");

        var visible = Source("visible"); visible.Children.Add(Geometry("visible-geometry").Object);
        using (var scope = OpenScope()) Read(scope, Appearance("visible-appearance", visible));
        Check(visible.DisplayCalls == 0 && visible.BlankCalls == 0, "Visible source must remain untouched");
        var partial = Source("partial"); partial.AggregateVisibility = true;
        var shown = Geometry("shown"); var hidden = Geometry("hidden"); hidden.State = TxDisplayableObjectVisibility.None;
        partial.Children.Add(shown.Object); partial.Children.Add(hidden.Object);
        using (var scope = OpenScope())
        {
            Read(scope, Appearance("partial-appearance", partial));
            Check(hidden.State == TxDisplayableObjectVisibility.All, "Hidden child in partial source was not displayed");
        }
        Check(shown.State == TxDisplayableObjectVisibility.All && hidden.State == TxDisplayableObjectVisibility.None &&
            partial.Visibility() == TxDisplayableObjectVisibility.Partial, "Mixed visibility was not restored");

        var fallback = Source("fallback"); fallback.FailDirect = true;
        fallback.Descendants = new TxObjectList(); fallback.Descendants.Add(first.Object); fallback.Descendants.Add(first.Object);
        using (var scope = OpenScope()) Check(Read(scope, Appearance("fallback-appearance", fallback)).Count == 1, "Fallback deduplication");
        Fails(Appearance("missing", null), "Missing engineering representation accepted");
        var empty = Source("empty"); empty.State = TxDisplayableObjectVisibility.None;
        Fails(Appearance("empty-appearance", empty), "Empty source accepted");
        Check(empty.State == TxDisplayableObjectVisibility.None, "Empty source not restored");
        var broken = Source("broken"); broken.State = TxDisplayableObjectVisibility.None; broken.FailDirect = broken.FailAll = true;
        Fails(Appearance("broken-appearance", broken), "Unreadable source accepted");
        Check(broken.State == TxDisplayableObjectVisibility.None, "Enumeration failure left source visible");
        var displayFail = Source("display-fail"); displayFail.State = TxDisplayableObjectVisibility.None; displayFail.FailDisplay = true;
        Fails(Appearance("display-fail-appearance", displayFail), "Display failure accepted");
        Check(displayFail.State == TxDisplayableObjectVisibility.None, "Partial display failure not rolled back");
        first.FailApproximation = true;
        try
        {
            using (var scope = OpenScope())
            {
                var items = Read(scope, appearance);
                var approximation = GeometryOf(items[0]).Approximation;
            }
            throw new Exception("Approximation failure accepted");
        }
        catch (InvalidOperationException) { checks++; }
        first.FailApproximation = false;
        Check(source.State == TxDisplayableObjectVisibility.None, "Approximation failure left source visible");
        var restoreFail = Source("restore-fail"); restoreFail.State = TxDisplayableObjectVisibility.None;
        restoreFail.Children.Add(Geometry("restore-fail-mesh").Object); restoreFail.FailBlank = true;
        try
        {
            using (var scope = OpenScope())
            {
                Read(scope, appearance); Read(scope, Appearance("restore-fail-appearance", restoreFail));
            }
            throw new Exception("Restore failure hidden");
        }
        catch (InvalidOperationException) { checks++; }
        Check(source.State == TxDisplayableObjectVisibility.None, "Restore failure stopped remaining cleanup");
        Check(source.UnexpectedCalls + appearance.UnexpectedCalls + first.UnexpectedCalls + second.UnexpectedCalls == 0,
            "Unexpected mutation, storage or scene access");

        // Detached appearance triangles use the shared production encoders.
        string directory = args[0];
        Directory.CreateDirectory(directory);
        var data = new DeviceData { Name = "appearance" };
        var color = new ColorGroup { R = 11, G = 123, B = 240, Surface = 1 };
        color.Tris.Add(new float[] { 10, 20, 30, 11, 20, 30, 10, 21, 30 });
        data.Colors.Add(color);
        foreach (string format in new[] { "STL", "OBJ", "PLY", "FBX_ASCII", "FBX_BINARY" })
        {
            string path = MeshExport.Write(data, format, directory, format, "world");
            Check(new FileInfo(path).Length > 0, "Missing " + format + " output");
        }
        Check(BitConverter.ToUInt32(File.ReadAllBytes(Path.Combine(directory, "STL.stl")), 80) == 1,
            "STL triangle count must match");
        Check(File.ReadAllText(Path.Combine(directory, "OBJ.mtl")).Contains("RGB_0"), "OBJ material is missing");
        Check(File.ReadAllText(Path.Combine(directory, "PLY.ply")).Contains("11 123 240"), "PLY lost RGB");
        var buildCgr = Service.GetMethod("BuildCgr", BindingFlags.Static | BindingFlags.NonPublic);
        string cgr = (string)Invoke(buildCgr, data.Colors, "appearance", directory, null, CgrBackend.Compact, false);
        Check(new FileInfo(cgr).Length > 0, "CGR encoder rejected detached appearance triangles");
        string rep = Path.Combine(directory, "appearance.3DRep");
        string packagePath = Path.Combine(directory, "appearance.3dxml");
        using (var package = new ThreeDXmlWriter(packagePath, "appearance"))
        {
            var prepare = typeof(ThreeDXmlWriter).GetMethod("PreparePart", BindingFlags.Instance | BindingFlags.NonPublic);
            object ticket = prepare.Invoke(package, new object[] { "appearance", new List<string> { "AppearanceArea" } });
            string identity = (string)ticket.GetType().GetField("Identity", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ticket);
            Cfv3Encoder.ConvertFile(cgr, rep, identity);
            typeof(ThreeDXmlWriter).GetMethod("AddEncodedPart", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(package, new object[] { ticket, rep });
            package.Complete();
            Check(package.PartCount == 1, "3DXML lost the appearance part");
        }
        using (var archive = ZipFile.OpenRead(packagePath))
        {
            int representations = 0;
            foreach (var entry in archive.Entries)
                if (entry.FullName.EndsWith(".3DRep", StringComparison.OrdinalIgnoreCase)) representations++;
            Check(representations == 1, "3DXML must contain one encoded representation");
            using (var reader = new StreamReader(archive.GetEntry("appearance.3dxml").Open()))
                Check(reader.ReadToEnd().Contains("AppearanceArea"), "3DXML lost the appearance hierarchy");
        }
        Console.WriteLine("PASS: " + checks + " appearance hierarchy and export checks");
    }
}
