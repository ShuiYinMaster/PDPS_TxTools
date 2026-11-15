using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Tecnomatix.Engineering;
using TxTools.Common;

namespace TxTools.CrossEnvIO
{
    internal static class TransferCapture
    {
        internal static string StudyId()
        {
            var study = TxApplication.ActiveDocument?.CurrentStudy;
            if (study == null) return "";
            string id = Convert.ToString(study.GetType().GetProperty("Id")?.GetValue(study, null));
            return string.IsNullOrEmpty(id) ? "pid:" + System.Diagnostics.Process.GetCurrentProcess().Id
                + "/root:" + TxApplication.ActiveDocument.PhysicalRoot.Id : id;
        }

        internal static TransferPlan Capture(IEnumerable<ITxObject> selection, bool wholeScene, bool weld,
            string output, Action<string> log,bool includeWeldParts=false)
        {
            var doc = TxApplication.ActiveDocument ?? throw new InvalidOperationException("请先打开工程");
            if (weld)
            {
                var list = (selection ?? Enumerable.Empty<ITxObject>()).ToList();
                if (!wholeScene && list.Count == 0) throw new InvalidOperationException("请使用原生多选框选取焊点操作，或明确选择整个场景");
                var ids = wholeScene ? null : ResolveWeldSelection(list);
                if (WeldPointIO.ExportWeldPoints(output, log, ids) == 0)
                    throw new InvalidOperationException("没有可导出的焊点");
            }
            else
            {
                var roots = wholeScene ? new List<ITxObject> { doc.PhysicalRoot } : NormalizeRoots(selection);
                if (roots.Count == 0) throw new InvalidOperationException("请使用原生多选框选取对象，空列表不会自动使用整个场景");
                var reader = new SceneObjectTypeReader();
                if (StructureIO.ExportStructureMulti(roots, output, true, log, reader.Read) == 0)
                    throw new InvalidOperationException("没有可导出的模型结构");
            }
            var plan = FromFile(output, weld ? "weld" : "model");
            plan.SourceStudy = doc.CurrentStudy.Name; plan.SourceDocument = doc.FinalDestination ?? "";
            plan.SourceStudyId = StudyId();
            if(weld&&includeWeldParts)
            {
                var selected=wholeScene?null:ResolveWeldSelection(selection??Enumerable.Empty<ITxObject>());
                var parts=new Dictionary<string,ITxObject>(StringComparer.Ordinal);
                foreach(var op in TransferRuntime.SceneObjects(true).OfType<TxWeldLocationOperation>().Where(o=>selected==null||selected.Contains(o.Id)))
                {
                    if(op.WeldPoint==null)continue;
                    foreach(ITxObject part in op.WeldPoint.AssignedParts)if(part!=null)parts[part.Id]=part;
                    var leading=op.WeldPoint.LeadingPart as ITxObject;if(leading!=null)parts[leading.Id]=leading;
                }
                if(parts.Count>0)
                {
                    string modelsFile=output+".models.tsv";var reader=new SceneObjectTypeReader();
                    StructureIO.ExportStructureMulti(NormalizeRoots(parts.Values),modelsFile,true,log,reader.Read);
                    var models=FromFile(modelsFile,"model");
                    plan.Packages=models.Packages;plan.WeldModelsFile=models.DataFile;plan.WeldModelsHash=models.DataHash;
                }
                else
                {
                    // No source bindings: the option still permits transferring the welds alone.
                    log("[焊点] 选取范围没有绑定零件，将仅传输焊点。");
                }
                plan.IncludeWeldParts=parts.Count>0;
            }
            return plan;
        }

        internal static HashSet<string> ResolveWeldSelection(IEnumerable<ITxObject> selection)
        {
            var all = TransferRuntime.SceneObjects(true).OfType<TxWeldLocationOperation>().ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var invalid = new List<string>();
            foreach (var item in selection)
            {
                if (item == null) { invalid.Add("失效对象，请重新选取"); continue; }
                if (item is TxWeldLocationOperation weld) { ids.Add(weld.Id); continue; }
                if (item is TxWeldPoint point)
                {
                    var matches = all.Where(o => o.WeldPoint?.Id == point.Id).ToArray();
                    if (matches.Length > 0) { foreach (var op in matches) ids.Add(op.Id); continue; }
                }
                if (item is TxCompoundOperation || item is TxWeldOperation || item is TxOperationRoot)
                {
                    var pending = new Stack<ITxObject>(); pending.Push(item);
                    var seen = new HashSet<string>();
                    bool found = false;
                    while (pending.Count > 0)
                    {
                        var current = pending.Pop(); if (!seen.Add(current.Id)) continue;
                        if (current is TxWeldLocationOperation op) { ids.Add(op.Id); found = true; }
                        foreach (var child in SceneAccess.Children(current)) pending.Push(child);
                    }
                    if (found) continue;
                }
                invalid.Add(item.Name + "（" + item.GetType().Name + "）");
            }
            if (invalid.Count > 0) throw new InvalidOperationException("以下选取项没有可传输的焊点位置操作：\n"
                + string.Join("\n", invalid) + "\n请选取焊点、焊点位置操作或包含焊点的焊接操作组；模型请在“模型结构”页传输。");
            if (ids.Count == 0) throw new InvalidOperationException("所选范围没有焊点位置操作，请检查操作树。");
            return ids;
        }

        internal static TransferPlan FromFile(string file, string kind)
        {
            var table = TransferTable.Read(file);
            bool isWeld = table.Header.Contains("WeldOpName");
            if (isWeld != (kind == "weld")) throw new InvalidDataException("文件类型与当前页不一致");
            var plan = new TransferPlan { Kind = kind, DataFile = Path.GetFullPath(file),
                DataHash = PackageFiles.HashFile(file), ObjectRows = table.Rows.Count };
            plan.SourceStudy = table.Value(table.Rows[0], "SourceStudy");
            plan.SourceDocument = table.Value(table.Rows[0], "SourceDocument");
            plan.SourceStudyId = table.Value(table.Rows[0], "SourceStudyId");
            foreach (string field in new[] { "SourceStudy", "SourceDocument", "SourceStudyId" })
                if (table.Rows.Select(r => table.Value(r, field)).Distinct(StringComparer.Ordinal).Count() != 1)
                    throw new InvalidDataException("数据混入不同源工程: " + field);
            if (isWeld) return plan;
            var packages = new Dictionary<string, TransferPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in table.Rows)
            {
                string source = table.Value(row, "COJT");
                if (string.IsNullOrWhiteSpace(source)) continue;
                source = Path.GetFullPath(source);
                TransferPackage item;
                if (!packages.TryGetValue(source, out item))
                {
                    item = new TransferPackage { Key = PackageFiles.HashBytes(Encoding.UTF8.GetBytes(source.ToUpperInvariant())),
                        OriginalSource = source, Source = source, SourceRoot = table.Value(row, "SourceLibraryRoot"),
                        Name = Path.GetFileName(source.TrimEnd('\\', '/')) };
                    packages.Add(source, item);
                }
                string descriptor = table.Value(row, "PrototypeData");
                var prototype = string.IsNullOrWhiteSpace(descriptor) ? null : JsonConvert.DeserializeObject<DevicePrototypeData>(descriptor);
                if (item.Instances == 0) item.Prototype = prototype;
                else if (JsonConvert.SerializeObject(item.Prototype) != JsonConvert.SerializeObject(prototype))
                    throw new InvalidDataException("同一组件包的原型信息不一致，请分别保存模型后重新捕获: " + item.Name);
                item.Instances++;
                string json = table.Value(row, "TypeKeys");
                var types = string.IsNullOrWhiteSpace(json)
                    ? new Dictionary<string, string> { ["Tecnomatix.Engineering." + table.Value(row, "Type")] = table.Value(row, "Type") }
                    : JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (types != null) foreach (var type in types) item.Types[type.Key] = type.Value;
            }
            plan.Packages = packages.Values.OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList();
            return plan;
        }

        private static List<ITxObject> NormalizeRoots(IEnumerable<ITxObject> selection)
        {
            var roots = new Dictionary<string, ITxObject>();
            foreach (var picked in selection ?? Enumerable.Empty<ITxObject>())
            {
                ITxObject item = picked;
                if (item == null) throw new InvalidOperationException("选取对象已失效，请重新选取");
                var visited = new HashSet<string>();
                while (item != null && !(item is ITxComponent) && !(item is TxCompoundPart)
                    && !(item is TxCompoundResource) && !(item is TxPhysicalRoot))
                {
                    if (!visited.Add(item.Id)) throw new InvalidOperationException("对象父集合存在循环: " + picked.Name);
                    item = SceneAccess.Parent(item);
                }
                if (item == null || item is TxPhysicalRoot && !(picked is TxPhysicalRoot))
                    throw new InvalidOperationException("无法为所选对象找到可传输组件: " + picked.Name);
                roots[item.Id] = item;
            }
            return roots.Values.Where(item =>
            {
                var visited = new HashSet<string>();
                for (var parent = SceneAccess.Parent(item); parent != null; parent = SceneAccess.Parent(parent))
                {
                    if (!visited.Add(parent.Id)) throw new InvalidOperationException("对象父集合存在循环: " + item.Name);
                    if (roots.ContainsKey(parent.Id)) return false;
                }
                return true;
            }).ToList();
        }
    }

    internal sealed class TransferTable
    {
        public string[] Header;
        public List<string[]> Rows = new List<string[]>();
        public string Value(string[] row, string name)
        { int i = Array.IndexOf(Header, name); return i >= 0 && i < row.Length ? row[i] : ""; }
        public static TransferTable Read(string file)
        {
            var lines = File.ReadAllLines(file, new UTF8Encoding(false, true)).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (lines.Count < 2) throw new InvalidDataException("文件没有有效数据行");
            var table = new TransferTable { Header = lines[0].TrimStart('\uFEFF').Split('\t') };
            if (!table.Header.Contains("ParentPath") && !table.Header.Contains("ParentOpPath"))
                throw new InvalidDataException("无法识别数据文件表头");
            int min = table.Header.Contains("WeldOpName")
                ? Math.Max(Array.IndexOf(table.Header, "X") + 6, Array.IndexOf(table.Header, "ParentOpPath") + 1) : 9;
            foreach (string line in lines.Skip(1))
            { var cells = line.Split('\t'); if (cells.Length < min) throw new InvalidDataException("数据行列数不足"); table.Rows.Add(cells); }
            return table;
        }
    }
}
