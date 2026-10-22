using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.CrossEnvIO;

internal static class Program
{
    private static string _root;
    private static int _passed;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static void Run(string name, Action test)
    { test(); _passed++; Console.WriteLine("PASS " + name); }
    private static string Folder(string name)
    { string path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
    private static TxComponent Part(TxContainer parent, string name, string cojt = null)
    {
        var part = new TxComponent(name);
        if (cojt != null) part.StorageObject = new Storage { FullPath = cojt };
        parent.AddObject(part); return part;
    }
    private static TxWeldLocationOperation Weld(TxContainer group, string name, params TxComponent[] parts)
    {
        var op = new TxWeldLocationOperation(name) { WeldPoint = new TxWeldPoint("point-" + name) };
        group.AddObject(op);
        var assigned = new TxObjectList(); assigned.AddRange(parts);
        op.WeldPoint.AssignParts(assigned);
        op.WeldPoint.LeadingPart = parts.FirstOrDefault(); return op;
    }
    private static void NewScene(string name)
    {
        TxApplication.ActiveDocument = new TxDocument();
        TxApplication.ActiveDocument.PhysicalRoot.Name = name;
        TxApplication.ActiveDocument.CurrentStudy.Name = name;
        TxApplication.SystemRootDirectory = Folder("library");
    }

    private static int Main()
    {
        _root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            Run("rotation round-trip at both poles and nearby poses", Rotation);
            Run("copy identity never selects old objects", CloneIdentity);
            Run("junction target validation rejects wrong targets", JunctionTarget);
            Run("GBK / UTF-8 / UTF-16 encoding and BOM retention", Encodings);
            Run("XML entities preserve decoded filenames", Entities);
            Run("ambiguous cojt is not guessed", AmbiguousCojt);
            Run("numbered directories and component versions stay distinct", ExactCopy);
            Run("copy cleanup protects scene dependencies and changed files", Cleanup);
            Run("selected subtree retains root and child hierarchy", SelectedSubtree);
            Run("full-tree roots map across different scene names", PhysicalRoots);
            Run("component mapping disambiguates same-name library versions", ComponentMapping);
            Run("weld selection uses IDs and binds multiple parts", WeldSelectionAndBinding);
            Run("same-name parts bind by full path and failed binding preserves assignments", SameNameParts);
            Run("same-name resource and part containers remain distinct for binding", SameNameTrees);
            Run("bind-only uses parent path and counts once", BindOnly);
            Run("projection failures are reported", ProjectionFailure);
            Run("remote preflight refuses wrong study, document or library", RemotePreflight);
            Run("failed export preserves the previous file", FailedExport);
            Run("each remote transfer has its own immutable snapshot file", TransferSnapshots);
            Console.WriteLine("ALL PASS: " + _passed);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            // 本进程创建的唯一目录；不扫描或删除用户工程、库和历史测试文件。
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private static void Rotation()
    {
        var poses = new List<double[]>();
        foreach (double ry in new[] { -90.0, 90.0, -89.9999, 89.9999, 0.0 })
            foreach (double rx in new[] { 0.0, 17.0, -51.0 })
                foreach (double rz in new[] { 0.0, 29.0, -123.0 }) poses.Add(new[] { rx, ry, rz });
        var random = new Random(17);
        for (int i = 0; i < 200; i++) poses.Add(new[] { random.NextDouble() * 360 - 180,
            random.NextDouble() * 179 - 89.5, random.NextDouble() * 360 - 180 });
        foreach (var pose in poses)
        {
            var original = TxMath.BuildTransform(3, 7, 11, pose[0], pose[1], pose[2]);
            double rx, ry, rz;
            TxMath.MatrixToEulerDeg(TxMath.TxToArr(original), out rx, out ry, out rz);
            var result = TxMath.BuildTransform(3, 7, 11, rx, ry, rz);
            for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++)
                Check(Math.Abs(original[row, col] - result[row, col]) < 1e-8,
                    "rotation mismatch: " + string.Join(",", pose));
        }
    }

    private sealed class Item { public string Id, Name; }
    private static void CloneIdentity()
    {
        var source = new Item { Id = "source", Name = "Robot" };
        var old = new Item { Id = "old", Name = "Other" };
        var before = new[] { source.Id, old.Id };
        Check(CopyIdentity.FindNew(source, before, new[] { old }, x => x.Id, x => x.Name, x => x.GetType()) == null,
            "failed paste selected existing object");
        var created = new Item { Id = "new", Name = "Robot_2" };
        Check(CopyIdentity.FindNew(source, before, new[] { old, created }, x => x.Id, x => x.Name, x => x.GetType()) == created,
            "new clone not identified");
        var other = new Item { Id = "new2", Name = "Robot_3" };
        Check(CopyIdentity.FindNew(source, before, new[] { created, other }, x => x.Id, x => x.Name, x => x.GetType()) == null,
            "ambiguous clones were guessed");
    }

    private static void WriteZip(string file, byte[] state)
    {
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var output = zip.CreateEntry("StandaloneStudy_PsState.xml").Open()) output.Write(state, 0, state.Length);
            using (var output = zip.CreateEntry("untouched.bin").Open()) output.WriteByte(37);
        }
    }

    private static void JunctionTarget()
    {
        string first = Folder("junction-a"), second = Folder("junction-b");
        string link = Path.Combine(_root, "owned-junction");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + first + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        try
        {
            using (var process = System.Diagnostics.Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Check(process.ExitCode == 0, "junction creation failed: " + output + error);
            }
            Check(DirectoryLinks.PointsTo(link, first), "correct junction target not recognized");
            Check(!DirectoryLinks.PointsTo(link, second), "wrong junction target accepted");
            Check(!DirectoryLinks.PointsTo(first, first), "ordinary directory treated as owned link");
            Check(CojtTransfer.CollectCojtPaths(link, s => { }).Count == 0, "scanner followed a junction");
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }
    private static byte[] ReadState(string file)
    {
        using (var zip = ZipFile.OpenRead(file))
        using (var input = zip.GetEntry("StandaloneStudy_PsState.xml").Open())
        using (var output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); }
    }
    private static void Encodings()
    {
        string library = Folder("encoding-lib");
        string real = Path.Combine(library, "产品", "目标.cojt"); Directory.CreateDirectory(real);
        var encodings = new Encoding[] { Encoding.GetEncoding(936), new UTF8Encoding(false),
            new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true),
            new UTF32Encoding(false, true), new UTF32Encoding(true, true) };
        int index = 0;
        foreach (var encoding in encodings)
        {
            string header = "<?xml version=\"1.0\" encoding=\"" + encoding.WebName + "\"?>";
            string input = header + "<root><note>中文保持</note><fileName>#\\ANV_Link\\目标.cojt</fileName></root>";
            string expected = input.Replace("#\\ANV_Link\\目标.cojt", "#\\产品\\目标.cojt");
            string file = Path.Combine(_root, "encoding-" + index++ + ".psz");
            WriteZip(file, encoding.GetPreamble().Concat(encoding.GetBytes(input)).ToArray());
            Check(PszPathFixer.FixPszPaths(file, library, s => { }, new[] { real }) == 1, "path repair failed");
            Check(ReadState(file).SequenceEqual(encoding.GetPreamble().Concat(encoding.GetBytes(expected))),
                "encoding/BOM changed: " + encoding.WebName);
            using (var zip = ZipFile.OpenRead(file)) using (var stream = zip.GetEntry("untouched.bin").Open())
                Check(stream.ReadByte() == 37, "unrelated ZIP entry changed");
        }
        string fallback = Path.Combine(_root, "gbk-no-declaration.psz");
        string gbkXml = "<root><note>中文</note><fileName>#\\ANV_Link\\目标.cojt</fileName></root>";
        WriteZip(fallback, Encoding.GetEncoding(936).GetBytes(gbkXml));
        Check(PszPathFixer.FixPszPaths(fallback, library, s => { }, new[] { real }) == 1, "GBK fallback failed");
        Check(ReadState(fallback).SequenceEqual(Encoding.GetEncoding(936).GetBytes(gbkXml.Replace("ANV_Link", "产品"))), "GBK fallback changed encoding");

        string ascii = Path.Combine(_root, "ambiguous-ascii.psz");
        string asciiName = Path.Combine(library, "产品", "A.cojt"); Directory.CreateDirectory(asciiName);
        byte[] original = Encoding.ASCII.GetBytes("<root><fileName>#\\ANV_Link\\A.cojt</fileName></root>");
        WriteZip(ascii, original);
        Check(PszPathFixer.FixPszPaths(ascii, library, s => { }, new[] { asciiName }) < 0, "ambiguous ASCII was converted");
        Check(ReadState(ascii).SequenceEqual(original), "failed encoding changed original archive");
    }

    private static void Entities()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var escape = typeof(PszPathFixer).GetMethod("EscapeXmlInFileNameElements", flags);
        foreach (string inner in new[] { "A&#38;B", "A&#x26;B", "A&amp;lt;B", "A&amp;B" })
        {
            string xml = "<fileName>#\\" + inner + ".cojt</fileName>";
            string result = (string)escape.Invoke(null, new object[] { xml, (Action<string>)(s => { }) });
            Check(result == xml, "legal entity changed: " + inner);
        }
        string library = Folder("entities-lib");
        string component = Path.Combine(library, "Real", "A&B.cojt"); Directory.CreateDirectory(component);
        string psz = Path.Combine(_root, "entities.psz");
        string source = "<?xml version=\"1.0\" encoding=\"utf-8\"?><root><fileName>#\\ANV_Link\\A&#38;B.cojt</fileName></root>";
        WriteZip(psz, Encoding.UTF8.GetBytes(source));
        Check(PszPathFixer.FixPszPaths(psz, library, s => { }, new[] { component }) == 1, "entity filename not resolved");
        var document = new XmlDocument(); document.LoadXml(Encoding.UTF8.GetString(ReadState(psz)));
        Check(document.DocumentElement.FirstChild.InnerText == "#\\Real\\A&B.cojt", "decoded filename changed");
    }

    private static void AmbiguousCojt()
    {
        string library = Folder("ambiguous-lib");
        Directory.CreateDirectory(Path.Combine(library, "A", "Robot.cojt"));
        Directory.CreateDirectory(Path.Combine(library, "B", "deeper", "Robot.cojt"));
        Check(PszPathFixer.FindRealCojtInRoot(library, "Robot.cojt", s => { }) == null, "ambiguous version selected");
        Check(CojtTransfer.LinkNameForPath(Path.Combine(library, "A", "Robot.cojt"))
            != CojtTransfer.LinkNameForPath(Path.Combine(library, "B", "Robot.cojt")), "same-name link collision");
    }

    private static void ExactCopy()
    {
        string source = Folder("copy-source"), target = Folder("copy-target");
        string a = Path.Combine(source, "Station1", "Robot.cojt"), b = Path.Combine(source, "Station2", "Robot.cojt");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "model.jt"), "v1"); File.WriteAllText(Path.Combine(b, "model.jt"), "v2");
        Directory.CreateDirectory(Path.Combine(target, "Station9"));
        var result = CojtTransfer.CopyCojtList(new List<string> { a, b }, source, target, s => { });
        Check(result.Ok == 2 && result.Fail == 0, "copy failed");
        Check(File.ReadAllText(Path.Combine(target, "Station1", "Robot.cojt", "model.jt")) == "v1", "Station1 mis-mapped");
        Check(File.ReadAllText(Path.Combine(target, "Station2", "Robot.cojt", "model.jt")) == "v2", "Station2 mis-mapped");
        Check(result.TargetPaths[a] == Path.Combine(target, "Station1", "Robot.cojt"), "exact map missing");
        Check(CojtTransfer.CopyCojtList(new List<string> { a }, source, target, s => { }).Skipped == 1, "identical version not reused");
        File.WriteAllText(Path.Combine(a, "model.jt"), "new version");
        Check(CojtTransfer.CopyCojtList(new List<string> { a }, source, target, s => { }).Fail == 1, "different version silently reused");
        Check(!CojtTransfer.CopyCojtDirectory(a, Path.Combine(a, "nested"), s => { }), "recursive self-copy accepted");
    }

    private static void Cleanup()
    {
        string source = Folder("cleanup-src"), target = Folder("cleanup-dst");
        string cojt = Path.Combine(source, "A.cojt"); Directory.CreateDirectory(cojt);
        File.WriteAllText(Path.Combine(cojt, "model.jt"), "data");
        var copy = CojtTransfer.CopyCojtList(new List<string> { cojt }, source, target, s => { });
        copy.TargetPids.Add(41); copy.SceneMayReferenceFiles = true;
        copy.Undo(s => { }, directory => true);
        Check(Directory.Exists(Path.Combine(target, "A.cojt")), "scene dependency deleted");
        var merged = new CopyResult(); merged.TargetPids.Add(42); merged.Merge(copy);
        Check(merged.SceneMayReferenceFiles && merged.TargetPids.SetEquals(new[] { 41, 42 }), "dependency metadata lost on merge");
        copy.SceneMayReferenceFiles = false;
        File.WriteAllText(Path.Combine(target, "A.cojt", "model.jt"), "changed");
        copy.Undo(s => { }, directory => true);
        Check(Directory.Exists(Path.Combine(target, "A.cojt")), "changed files deleted");
    }

    private static void SelectedSubtree()
    {
        NewScene("Source");
        var fixture = new TxCompoundResource("Fixture"); TxApplication.ActiveDocument.PhysicalRoot.AddObject(fixture);
        var child = new TxCompoundResource("Station"); fixture.AddObject(child);
        string file = Path.Combine(_root, "selected.txt");
        StructureIO.ExportStructure(fixture, file, true, s => { });
        NewScene("Target");
        var result = StructureIO.RebuildStructure(file, TxApplication.ActiveDocument.PhysicalRoot, true, s => { });
        var targetFixture = TxApplication.ActiveDocument.PhysicalRoot.Children.Single();
        Check(targetFixture.Name == "Fixture", "root lost");
        Check(((TxContainer)targetFixture).Children.Single().Name == "Station", "child moved to physical root");
        Check(result.Failed == 0 && result.Created == 2, "invalid subtree result");
        var again = StructureIO.RebuildStructure(file, TxApplication.ActiveDocument.PhysicalRoot, true, s => { });
        Check(again.Created == 0 && again.Failed == 0, "existing containers counted as created");
    }

    private static void PhysicalRoots()
    {
        NewScene("SourceScene");
        TxApplication.ActiveDocument.PhysicalRoot.AddObject(new TxCompoundResource("Resource"));
        TxApplication.ActiveDocument.PhysicalRoot.AddObject(new TxCompoundPart("Part"));
        string file = Path.Combine(_root, "whole.txt");
        StructureIO.ExportStructure(TxApplication.ActiveDocument.PhysicalRoot, file, true, s => { });
        NewScene("TargetScene");
        var resources = StructureIO.RebuildStructure(file, TxApplication.ActiveDocument.PhysicalRoot, true, s => { });
        var parts = StructureIO.RebuildStructure(file, TxApplication.ActiveDocument.PhysicalRoot, false, s => { });
        Check(resources.Failed == 0 && parts.Failed == 0, "cross-scene physical roots not mapped");
        Check(TxApplication.ActiveDocument.PhysicalRoot.Children.Count == 2, "tree mode filtering failed");
    }

    private static void ComponentMapping()
    {
        NewScene("Source"); string sourceLib = TxApplication.SystemRootDirectory;
        string src = Path.Combine(sourceLib, "Station2", "Robot.cojt"); Directory.CreateDirectory(src);
        var fixture = new TxCompoundResource("Fixture"); TxApplication.ActiveDocument.PhysicalRoot.AddObject(fixture);
        Part(fixture, "Robot", src);
        string file = Path.Combine(_root, "mapped.txt"); StructureIO.ExportStructure(fixture, file, true, s => { });
        NewScene("Target"); string targetLib = Folder("mapped-target"); TxApplication.SystemRootDirectory = targetLib;
        Directory.CreateDirectory(Path.Combine(targetLib, "Station1", "Robot.cojt"));
        string desired = Path.Combine(targetLib, "Station2", "Robot.cojt"); Directory.CreateDirectory(desired);
        var result = StructureIO.RebuildStructure(file, TxApplication.ActiveDocument.PhysicalRoot, true, s => { });
        var created = ((TxContainer)TxApplication.ActiveDocument.PhysicalRoot.Children.Single()).Children.Single();
        Check(((Storage)((TxObject)created).StorageObject).FullPath == desired && result.Failed == 0, "wrong version inserted");
    }

    private static void WeldSelectionAndBinding()
    {
        NewScene("Source");
        var a = Part(TxApplication.ActiveDocument.PhysicalRoot, "A+sheet");
        var b = Part(TxApplication.ActiveDocument.PhysicalRoot, "B");
        TxWeldOperation selected = new TxWeldOperation("Selected"), other = new TxWeldOperation("Other");
        TxApplication.ActiveDocument.OperationRoot.AddObject(selected); TxApplication.ActiveDocument.OperationRoot.AddObject(other);
        var wop = Weld(selected, "Spot", a, b); Weld(other, "Spot", a);
        string file = Path.Combine(_root, "weld.txt");
        Check(WeldPointIO.ExportWeldPoints(file, s => { }, new HashSet<string> { wop.Id }) == 1, "same-name weld exported unexpectedly");
        NewScene("Target");
        TxComponent ta = Part(TxApplication.ActiveDocument.PhysicalRoot, "A+sheet"), tb = Part(TxApplication.ActiveDocument.PhysicalRoot, "B");
        var result = WeldPointIO.RebuildWeldPoints(file, false, false, s => { });
        var group = (TxContainer)TxApplication.ActiveDocument.OperationRoot.Children.Single();
        var created = (TxWeldLocationOperation)group.Children.Single();
        Check(group.Name == "Selected", "top operation name omitted");
        Check(created.WeldPoint.AssignedParts.Select(p => p.Id).SequenceEqual(new[] { ta.Id, tb.Id }), "multiple assignments not preserved");
        Check(created.WeldPoint.Name == "point-Spot", "weld point name lost");
        Check(result.Created == 1 && result.Bound == 1 && result.Failed == 0, "invalid creation/binding counts");
    }

    private static void BindOnly()
    {
        NewScene("Source"); var part = Part(TxApplication.ActiveDocument.PhysicalRoot, "Panel");
        var group = new TxWeldOperation("TargetGroup"); TxApplication.ActiveDocument.OperationRoot.AddObject(group);
        var source = Weld(group, "Spot", part);
        string file = Path.Combine(_root, "bind-only.txt"); WeldPointIO.ExportWeldPoints(file, s => { });
        NewScene("Target"); var desired = Part(TxApplication.ActiveDocument.PhysicalRoot, "Panel");
        TxWeldOperation wrongGroup = new TxWeldOperation("OtherGroup"), rightGroup = new TxWeldOperation("TargetGroup");
        TxApplication.ActiveDocument.OperationRoot.AddObject(wrongGroup); TxApplication.ActiveDocument.OperationRoot.AddObject(rightGroup);
        TxWeldLocationOperation wrong = Weld(wrongGroup, "Spot"), right = Weld(rightGroup, "Spot");
        var result = WeldPointIO.RebuildWeldPoints(file, false, true, s => { });
        Check(wrong.WeldPoint.AssignedParts.Count == 0 && right.WeldPoint.AssignedParts.Single().Id == desired.Id,
            "bind-only modified wrong same-name weld");
        Check(result.Bound == 1 && result.Created == 0 && result.Updated == 1, "bind-only double counted");
    }

    private static void SameNameParts()
    {
        NewScene("Source");
        var left = new TxCompoundPart("Left"); var right = new TxCompoundPart("Right");
        TxApplication.ActiveDocument.PhysicalRoot.AddObject(left); TxApplication.ActiveDocument.PhysicalRoot.AddObject(right);
        Part(left, "Panel"); var sourcePart = Part(right, "Panel");
        var sourceGroup = new TxWeldOperation("Group"); TxApplication.ActiveDocument.OperationRoot.AddObject(sourceGroup);
        Weld(sourceGroup, "Spot", sourcePart);
        string file = Path.Combine(_root, "same-name-parts.txt"); WeldPointIO.ExportWeldPoints(file, s => { });
        NewScene("Target");
        var targetLeft = new TxCompoundPart("Left"); var targetRight = new TxCompoundPart("Right");
        TxApplication.ActiveDocument.PhysicalRoot.AddObject(targetLeft); TxApplication.ActiveDocument.PhysicalRoot.AddObject(targetRight);
        var wrongPart = Part(targetLeft, "Panel"); var wantedPart = Part(targetRight, "Panel");
        var targetGroup = new TxWeldOperation("Group"); TxApplication.ActiveDocument.OperationRoot.AddObject(targetGroup);
        var spot = Weld(targetGroup, "Spot", wrongPart);
        var result = WeldPointIO.RebuildWeldPoints(file, false, true, s => { });
        Check(result.Failed == 0 && spot.WeldPoint.AssignedParts.Single().Id == wantedPart.Id, "same-name part guessed by traversal order");
        targetRight.Name = "Renamed";
        var failed = WeldPointIO.RebuildWeldPoints(file, false, true, s => { });
        Check(failed.Failed == 1 && spot.WeldPoint.AssignedParts.Single().Id == wantedPart.Id,
            "failed binding cleared previous assignments");
    }

    private static void ProjectionFailure()
    {
        NewScene("Source"); var part = Part(TxApplication.ActiveDocument.PhysicalRoot, "Panel");
        var group = new TxWeldOperation("Group"); TxApplication.ActiveDocument.OperationRoot.AddObject(group);
        var weld = Weld(group, "Spot", part); weld.FailProject = true;
        string file = Path.Combine(_root, "projection.txt"); WeldPointIO.ExportWeldPoints(file, s => { });
        var result = WeldPointIO.RebuildWeldPoints(file, true, true, s => { });
        Check(result.Projected == 0 && result.Failed == 1 && result.Errors.Any(e => e.Contains("投影失败")), "projection failure hidden");
    }

    private static void SameNameTrees()
    {
        NewScene("Source");
        var resource = new TxCompoundResource("Same"); var part = new TxCompoundPart("Same");
        TxApplication.ActiveDocument.PhysicalRoot.AddObject(resource); TxApplication.ActiveDocument.PhysicalRoot.AddObject(part);
        TxComponent wrong = Part(resource, "Panel"), right = Part(part, "Panel");
        var group = new TxWeldOperation("Group"); TxApplication.ActiveDocument.OperationRoot.AddObject(group);
        var spot = Weld(group, "Spot", right);
        string file = Path.Combine(_root, "same-trees.txt"); WeldPointIO.ExportWeldPoints(file, s => { });
        var assigned = new TxObjectList { wrong }; spot.WeldPoint.AssignParts(assigned);
        var result = WeldPointIO.RebuildWeldPoints(file, false, true, s => { });
        Check(result.Failed == 0 && spot.WeldPoint.AssignedParts.Single().Id == right.Id,
            "resource and part containers with same name collided");
    }

    private static void RemotePreflight()
    {
        NewScene("Source");
        TxApplication.ActiveDocument.PhysicalRoot.AddObject(new TxCompoundResource("Fixture"));
        string file = Path.Combine(_root, "remote.txt");
        StructureIO.ExportStructure(TxApplication.ActiveDocument.PhysicalRoot, file, true, s => { });
        NewScene("Target");
        var tool = new CrossEnvRebuildTool();
        var input = new JObject { ["kind"] = "resource", ["file"] = file,
            ["expected_study"] = "Wrong", ["expected_document"] = "",
            ["expected_library_root"] = TxApplication.SystemRootDirectory };
        Check(tool.Execute(input).StartsWith("Error:") && TxApplication.ActiveDocument.PhysicalRoot.Children.Count == 0,
            "wrong study modified scene");
        input["expected_study"] = "Target"; input["expected_document"] = "another.psz";
        Check(tool.Execute(input).StartsWith("Error:") && TxApplication.ActiveDocument.PhysicalRoot.Children.Count == 0,
            "wrong document modified scene");
        input["expected_document"] = ""; input["expected_library_root"] = Folder("other-library");
        Check(tool.Execute(input).StartsWith("Error:") && TxApplication.ActiveDocument.PhysicalRoot.Children.Count == 0,
            "wrong library modified scene");
        input["expected_library_root"] = TxApplication.SystemRootDirectory;
        Check(!tool.Execute(input).StartsWith("Error:") && TxApplication.ActiveDocument.PhysicalRoot.Children.Count == 1,
            "matching target failed");
        input["file"] = Path.Combine(_root, "does-not-exist.txt");
        Check(tool.Execute(input).StartsWith("Error:"), "missing file reported success");
    }

    private static void FailedExport()
    {
        NewScene("Source");
        string file = Path.Combine(_root, "previous-export.txt");
        File.WriteAllText(file, "previous data");
        bool failed = false;
        using (var locked = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { StructureIO.ExportStructure(TxApplication.ActiveDocument.PhysicalRoot, file, true, s => { }); }
            catch (IOException) { failed = true; }
        }
        Check(failed && File.ReadAllText(file) == "previous data", "export failure did not propagate or changed old data");
    }

    private static void TransferSnapshots()
    {
        string configured = Path.Combine(_root, "structure.txt");
        string first = TransferFile.SnapshotPath(configured), second = TransferFile.SnapshotPath(configured);
        Check(first != second && first != configured && second != configured, "snapshot file collision");
        TransferFile.Write(first, "first task"); TransferFile.Write(second, "second task");
        Check(File.ReadAllText(first) == "first task", "second transfer overwrote first snapshot");
        Check(Directory.GetFiles(_root, "*.tmp").Length == 0, "export staging file left behind");
    }
}
