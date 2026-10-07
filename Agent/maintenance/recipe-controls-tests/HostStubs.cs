// Only the engineering/model boundaries are replaced. Store, parser, tools, runner,
// preferences and UI execution gate are production sources. No engineering code runs.
using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Tecnomatix.Engineering
{
    public interface ITxObject { string Id { get; } string Name { get; } }
    public interface ITxDisplayableObject : ITxObject { }
    public interface ITxObjectCollection { List<ITxObject> GetAllDescendants(TxTypeFilter filter); }
    public sealed class TxTypeFilter { public TxTypeFilter(Type type) { } }
    public class TestObject : ITxObject { public string Id { get; set; } public string Name { get; set; } }
    public class TxComponent : TestObject, ITxDisplayableObject { }
    public sealed class TxFixtureCustom : TxComponent { }
    public sealed class TxRobot : TestObject, ITxDisplayableObject { }
    public sealed class TxTool : TestObject, ITxDisplayableObject { }
    public sealed class TxFrame : TestObject, ITxDisplayableObject { }
    public sealed class TxSolid : TestObject, ITxDisplayableObject { }
    public sealed class TxSurface : TestObject, ITxDisplayableObject { }
    public sealed class TxWeldPoint : TestObject, ITxDisplayableObject { }
    public sealed class TxGroup : TestObject, ITxDisplayableObject { }
    public sealed class Tx1DimensionalGeometry : TestObject, ITxDisplayableObject { }
    public sealed class Tx2Or3DimensionalGeometry : TestObject, ITxDisplayableObject { }
    public sealed class TxCompoundPart : TestObject, ITxDisplayableObject { }
    public sealed class TxCompoundResource : TestObject, ITxDisplayableObject { }
    public sealed class TxDevice : TestObject, ITxDisplayableObject { }
    public sealed class TxGenericRoboticLocationOperation : TestObject, ITxDisplayableObject { }
    public sealed class TxGeometry : TestObject, ITxDisplayableObject { }
    public sealed class TxGripper : TestObject, ITxDisplayableObject { }
    public sealed class TxGun : TestObject, ITxDisplayableObject { }
    public sealed class TxKinematicLink : TestObject, ITxDisplayableObject { }
    public sealed class TxNote : TestObject, ITxDisplayableObject { }
    public sealed class TxPartAppearance : TestObject, ITxDisplayableObject { }
    public sealed class TxRoboticSeamLocationOperation : TestObject, ITxDisplayableObject { }
    public sealed class TxRoboticViaLocationOperation : TestObject, ITxDisplayableObject { }
    public sealed class TxSeamMfgFeature : TestObject, ITxDisplayableObject { }
    public sealed class TxServoGun : TestObject, ITxDisplayableObject { }
    public sealed class TxSweptVolume : TestObject, ITxDisplayableObject { }
    public sealed class TxWeldLocationOperation : TestObject, ITxDisplayableObject { }
    public sealed class TestCollection : ITxObjectCollection { public List<ITxObject> Items = new List<ITxObject>(); public int Reads; public List<ITxObject> GetAllDescendants(TxTypeFilter filter) { Reads++; return Items; } }
    public sealed class TestSelection { public List<ITxObject> Items = new List<ITxObject>(); public List<ITxObject> GetItems() => Items; }
    public sealed class TestDocument { public TestCollection PhysicalRoot { get; set; } = new TestCollection(); public TestCollection ComponentRoot { get; set; } = new TestCollection(); public TestCollection ResourceRoot { get; set; } = new TestCollection(); public TestStudy CurrentStudy { get; set; } = new TestStudy(); }
    public sealed class TestStudy { public string Name { get; set; } = "test-study"; }
    public static class TxApplication
    {
        public static TestDocument ActiveDocument = new TestDocument();
        public static TestSelection ActiveSelection = new TestSelection();
    }
}
namespace TxTools.Agent
{
    public static class TxAgentCommand { public static void SetPetState(string state, string text = null) { } }
}
namespace TxTools.Agent.Core
{
    public static class AuditLog { public static void Write(string text) { Console.WriteLine(text); } }
    public sealed class LlmProvider { }
    public sealed class ToolDef { public string Type; public FunctionDef Function; }
    public sealed class FunctionDef { public string Name; public string Description; public JObject Parameters; }
    public sealed class Snippet
    {
        public string Name; public string Origin; public string Description; public string Code;
        public int SuccessCount; public int FailureCount;
    }
    public static class SnippetStore
    {
        public static string NormalizeLang(string lang) => lang == "python" ? "python" : "csharp";
        public static List<Snippet> All() => new List<Snippet>();
        public static bool IsUnreliable(Snippet s) => false;
    }
    public sealed class PsContext
    {
        public static PsContext Current = new PsContext();
        public void Run(Action action) => action();
    }
}
namespace TxTools.Agent.Ps
{
    public static class PsBridge
    {
        public static string Code;
        public static ManualResetEventSlim Gate = new ManualResetEventSlim(true);
        public static string RunCSharp(string code, out bool ok, string label, Func<string> validate = null)
        {
            Code = code;
            if (!Gate.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Test gate timed out");
            var error = validate?.Invoke(); ok = error == null; return error ?? "test host accepted generated code";
        }
    }
}
namespace TxTools.Agent.Scripting
{
    public enum PythonRunMode { Execute }
    public sealed class TestPythonResult { public bool Success => true; public string ToAgentText() => "test host"; }
    public sealed class PythonHostProvider
    {
        public static PythonHostProvider Instance = new PythonHostProvider();
        public TestPythonResult Run(string code, PythonRunMode mode, string label) => new TestPythonResult();
    }
}

namespace OtherSceneLibrary { public sealed class TxRobot : Tecnomatix.Engineering.TestObject, Tecnomatix.Engineering.ITxDisplayableObject { } }
