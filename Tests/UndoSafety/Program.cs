using System;
using System.IO;
using System.Threading;
using Tecnomatix.Engineering;
using TxTools.Common;
using TxTools.CrossEnvIO;

internal static class Program
{
    private static int _passed;
    private static readonly string TestRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox-" + Guid.NewGuid().ToString("N"));
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action)
    { bool thrown = false; try { action(); } catch (Exception) { thrown = true; } Assert(thrown, "expected failure"); }
    private static TxDocument Document() { return TxApplication.ActiveDocument = new TxDocument(); }
    private static void Test(string name, Action action)
    { action(); _passed++; Console.WriteLine("PASS: " + name); }
    private static void Log(string message) { }
    private static string DirectoryFor(string name)
    { string path = Path.Combine(TestRoot, name); Directory.CreateDirectory(path); return path; }
    private static CopyResult Copy(string name, out string target)
    {
        string source = DirectoryFor(name + "-source");
        string cojt = Path.Combine(source, "model.cojt"); Directory.CreateDirectory(cojt);
        File.WriteAllText(Path.Combine(cojt, "model.jt"), "original");
        string destination = DirectoryFor(name + "-destination");
        var report = CojtTransfer.CopyCojtList(new System.Collections.Generic.List<string> { cojt }, source, destination, Log);
        target = Path.Combine(destination, "model.cojt");
        Assert(report.Ok == 1 && report.CopiedDirs.Count == 1, "copy tracking");
        return report;
    }

    private static void Main()
    {
        Directory.CreateDirectory(TestRoot);
        try
        {
            Test("One native transaction for nested calls; double disposal is safe", () =>
            {
                var doc = Document(); var outer = SceneUndoScope.Begin("outer");
                using (SceneUndoScope.Begin("inner")) Assert(doc.UndoManager.Starts == 1, "nested start");
                Assert(doc.UndoManager.Ends == 0 && SceneUndoScope.HasActiveTransaction, "inner ended outer");
                outer.Dispose(); outer.Dispose();
                Assert(doc.UndoManager.Ends == 1 && !SceneUndoScope.HasActiveTransaction, "outer end");
            });
            Test("Start failure stops mutation and next start can recover", () =>
            {
                var doc = Document(); doc.UndoManager.FailStart = true; bool mutated = false;
                Throws(() => { using (SceneUndoScope.Begin("bad")) mutated = true; });
                Assert(!mutated && doc.UndoManager.Ends == 0, "mutation after failed start");
                doc.UndoManager.FailStart = false;
                using (SceneUndoScope.Begin("good")) { }
            });
            Test("Body failure always closes its transaction", () =>
            {
                var doc = Document(); Throws(() => { using (SceneUndoScope.Begin("body")) throw new IOException(); });
                Assert(doc.UndoManager.Ends == 1 && !doc.UndoManager.Open, "leaked transaction");
            });
            Test("End failure blocks subsequent writes to same document", () =>
            {
                var doc = Document(); var scope = SceneUndoScope.Begin("bad close"); doc.UndoManager.FailEnd = true;
                Throws(scope.Dispose); Throws(() => SceneUndoScope.Begin("unsafe next"));
                Assert(doc.UndoManager.Starts == 1, "reopened failed document");
                Document(); using (SceneUndoScope.Begin("new document")) { }
                TxApplication.ActiveDocument = doc;
                Throws(() => SceneUndoScope.Begin("switched back to failed document"));
                Assert(doc.UndoManager.Starts == 1, "document switch cleared failed-close guard");
            });
            Test("Document switch refuses mutation and closes captured manager", () =>
            {
                var old = Document(); var scope = SceneUndoScope.Begin("old"); var current = Document();
                Throws(scope.EnsureDocument); Throws(() => SceneUndoScope.Begin("new while old open"));
                Throws(SceneUndoScope.EnsureActiveDocument);
                scope.Dispose(); Assert(old.UndoManager.Ends == 1 && current.UndoManager.Ends == 0, "wrong manager closed");
            });
            Test("Wrong thread and out-of-order disposal do not close another scope", () =>
            {
                var doc = Document(); var outer = SceneUndoScope.Begin("outer"); var inner = SceneUndoScope.Begin("inner");
                Throws(outer.Dispose); bool rejected = false;
                var thread = new Thread(() => { try { inner.Dispose(); } catch { rejected = true; } });
                thread.Start(); thread.Join(); Assert(rejected && doc.UndoManager.Ends == 0, "wrong owner");
                inner.Dispose(); outer.Dispose(); Assert(doc.UndoManager.Ends == 1, "failed recovery");
            });
            Test("Existing modeling container and unrelated geometry survive fence cleanup", () =>
            {
                var doc = Document(); var existing = new SceneObject(doc, "existing");
                var original = new SceneObject(doc, "original", existing); var generated = new SceneObject(doc, "fence", existing);
                var batch = new SceneCreationBatch(); batch.Add(generated);
                Assert(batch.RemoveCreated(Log) && generated.Deleted && !existing.Deleted && !original.Deleted, "deleted original model");
            });
            Test("Multiple new containers are cleaned as one transaction", () =>
            {
                var doc = Document(); var batch = new SceneCreationBatch();
                for (int i = 0; i < 3; i++)
                {
                    var container = new SceneObject(doc, "container" + i); batch.AddNewContainer(container);
                    var group = new SceneObject(doc, "group" + i, container); batch.AddNewContainer(group);
                    batch.Add(new SceneObject(doc, "solid" + i, group));
                }
                Assert(batch.RemoveCreated(Log) && doc.Objects.Count == 0, "left earlier baselines");
                Assert(doc.UndoManager.Starts == 1 && doc.UndoManager.Ends == 1, "cleanup split into steps");
            });
            Test("Failed deletion remains retryable; objects already undone are harmless", () =>
            {
                var doc = Document(); var obj = new SceneObject(doc, "locked") { FailDelete = true };
                var batch = new SceneCreationBatch(); batch.Add(obj);
                Assert(!batch.RemoveCreated(Log) && batch.Count == 1, "lost failed cleanup record");
                obj.FailDelete = false; obj.Delete(); Assert(batch.RemoveCreated(Log), "undone object rejected");
            });
            Test("New user content protects generated container and document switches are rejected", () =>
            {
                var doc = Document(); var container = new SceneObject(doc, "new"); var batch = new SceneCreationBatch(); batch.AddNewContainer(container);
                var group = new SceneObject(doc, "group", container); batch.AddNewContainer(group);
                var userContent = new SceneObject(doc, "added-later", group);
                var owned = new SceneObject(doc, "generated", group); batch.Add(owned);
                Assert(!batch.RemoveCreated(Log) && !container.Deleted && !group.Deleted && !userContent.Deleted && owned.Deleted,
                    "deleted later user content in generated group");
                Document(); Throws(() => batch.RemoveCreated(Log));
            });
            Test("Snapshot export verifies file, uses unique paths and rejects transaction overlap", () =>
            {
                var doc = Document(); string directory = DirectoryFor("snapshots");
                string first = StudySnapshot.Export(doc, directory), second = StudySnapshot.Export(doc, directory);
                Assert(first != second && File.ReadAllText(first) == "study snapshot", "snapshot invalid");
                using (SceneUndoScope.Begin("writing")) Throws(() => StudySnapshot.Export(doc, directory));
                Assert(doc.PlatformGlobalServicesProvider.Calls == 2, "export overlapped transaction");
            });
            Test("Missing, empty and failed exports never report a restore point", () =>
            {
                var doc = Document(); string directory = DirectoryFor("failed-snapshots");
                doc.PlatformGlobalServicesProvider.Missing = true; Throws(() => StudySnapshot.Export(doc, directory));
                doc.PlatformGlobalServicesProvider.Missing = false; doc.PlatformGlobalServicesProvider.Empty = true; Throws(() => StudySnapshot.Export(doc, directory));
                doc.PlatformGlobalServicesProvider.Fail = true; Throws(() => StudySnapshot.Export(doc, directory));
            });
            Test("Successful copied files can be cleaned without touching source", () =>
            {
                string target; var report = Copy("clean", out target); report.Undo(Log, path => true);
                Assert(!Directory.Exists(target) && report.CopiedDirs.Count == 0, "cleanup failed");
                Assert(File.Exists(Path.Combine(TestRoot, "clean-source", "model.cojt", "model.jt")), "deleted source");
            });
            Test("Changed copied content is protected and failed cleanup can retry", () =>
            {
                string target; var report = Copy("changed", out target); string file = Path.Combine(target, "model.jt");
                File.WriteAllText(file, "edited"); report.Undo(Log, path => true);
                Assert(Directory.Exists(target) && report.CopiedDirs.Count == 1, "deleted modified files");
                File.WriteAllText(file, "original"); report.Undo(Log, path => true); Assert(!Directory.Exists(target), "retry failed");
            });
            Test("Referenced files and missing reference checks prevent cleanup", () =>
            {
                string target; var report = Copy("referenced", out target);
                report.Undo(Log); report.Undo(Log, path => false); Assert(Directory.Exists(target), "unchecked cleanup");
                report.SceneMayReferenceFiles = true; report.Undo(Log, path => true); Assert(report.CopiedDirs.Count == 1, "lost reference record");
            });
            Test("Locked source leaves tracked staging directory; retry cleans half-copy", () =>
            {
                string source = DirectoryFor("locked-source"), destination = DirectoryFor("locked-destination");
                string cojt = Path.Combine(source, "model.cojt"); Directory.CreateDirectory(cojt);
                string file = Path.Combine(cojt, "model.jt"); File.WriteAllText(file, "locked");
                CopyResult report;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    report = CojtTransfer.CopyCojtList(new System.Collections.Generic.List<string> { cojt }, source, destination, Log);
                Assert(report.Fail == 1 && report.CopiedDirs.Count == 1 && !Directory.Exists(Path.Combine(destination, "model.cojt")), "failed copy untracked/published");
                report.Undo(Log, path => true); Assert(report.CopiedDirs.Count == 0, "half-copy cannot clean");
            });
            Test("Existing destination is not overwritten; merging preserves previous records", () =>
            {
                string target; var first = Copy("merge-first", out target); string secondTarget; var second = Copy("merge-second", out secondTarget);
                second.Merge(first); Assert(second.CopiedDirs.Count == 2, "lost previous copy");
                string source = Path.Combine(TestRoot, "merge-first-source", "model.cojt");
                Assert(!CojtTransfer.CopyCojtDirectory(source, Path.GetDirectoryName(target), Log), "overwrote existing directory");
                second.Undo(Log, path => true); Assert(!Directory.Exists(target) && !Directory.Exists(secondTarget), "merged cleanup incomplete");
            });
            Console.WriteLine("PASS: " + _passed + " undo safety scenarios");
        }
        finally
        {
            string resolved = Path.GetFullPath(TestRoot);
            string allowed = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe test cleanup path");
            Directory.Delete(resolved, true);
        }
    }
}
