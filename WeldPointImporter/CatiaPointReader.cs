using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using TxTools.CatiaPartTree.Conversion;
using Catia = TxTools.Agent.Core.Catia.CatiaBridge;

namespace TxTools.WeldPointImporter
{
    internal enum CatiaPointMode { Points, GeometricalSpheres, SolidBodies, BiW, Studs }

    internal static class CatiaPointReader
    {
        private const int CatMeasurablePoint = 11;

        private sealed class Occurrence
        {
            internal object Product, Document;
            internal string Name, PartNumber, Path;
            internal Lazy<Frame12> Frame;
        }

        [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDispatchTypeInfo
        {
            void GetTypeInfoCount(out uint count);
            void GetTypeInfo(uint index, uint locale, out ITypeInfo info);
        }

        internal static PointReadResult Read(CatiaPointMode mode, string query, bool skipHidden = false)
        {
            var terms = mode == CatiaPointMode.Studs ? Regex.Split(query ?? "", @"[,;，；、|\r\n]+")
                .Select(term => term.Trim()).Where(term => term.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            if (mode == CatiaPointMode.Studs && terms.Length == 0) throw new ArgumentException("请输入螺柱实例名称或零件号。");
            object app = ActiveApplication();
            var document = TryGet(app, "ActiveDocument");
            if (document == null) throw new InvalidOperationException("请在 CATIA 中激活源文档。");
            string name = GetText(document, "Name");
            var result = new PointReadResult();
            using var visibility = skipHidden ? SelectionVisibility.TryCreate(document, result) : null;

            if (name.EndsWith(".CATPart", StringComparison.OrdinalIgnoreCase) && mode != CatiaPointMode.BiW && mode != CatiaPointMode.Studs)
            {
                ReadPart(app, document, name, Frame12.Identity, mode, result, visibility);
                return result;
            }
            if (!name.EndsWith(".CATProduct", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请在 CATIA 中激活" + (mode == CatiaPointMode.BiW || mode == CatiaPointMode.Studs
                    ? " CATProduct 装配文档。" : " CATPart 或 CATProduct 文档。"));
            var root = Catia.InvokeGet(document, "Product");
            var rootFrame = new Lazy<Frame12>(() => Frame12.Identity);
            var occurrences = Enumerate(root, name, rootFrame, mode == CatiaPointMode.Studs, result, visibility, 0);
            if (mode == CatiaPointMode.Studs)
            {
                ReadStuds(occurrences, terms, result);
                return result;
            }
            if (mode == CatiaPointMode.BiW)
            {
                var cache = new Dictionary<string, List<ImportPoint>>(StringComparer.OrdinalIgnoreCase);
                ReadBiW(GetText(document, "FullName"), name, rootFrame, cache, result);
                foreach (var occurrence in occurrences)
                {
                    string file = GetText(occurrence.Document, "FullName");
                    if (file.EndsWith(".CATProduct", StringComparison.OrdinalIgnoreCase))
                        ReadBiW(file, occurrence.Path, occurrence.Frame, cache, result);
                }
                if (cache.Count == 0 && result.WarningCount == 0) result.Warn("装配中没有可读的已保存 CATProduct 文件。");
                return result;
            }

            foreach (var occurrence in occurrences)
            {
                if (!GetText(occurrence.Document, "Name").EndsWith(".CATPart", StringComparison.OrdinalIgnoreCase)) continue;
                try { ReadPart(app, occurrence.Document, occurrence.Path, occurrence.Frame.Value, mode, result, visibility); }
                catch (Exception ex) { result.Warn(occurrence.Path + "：" + Message(ex)); }
            }
            return result;
        }

        internal static string ActiveThreeDXmlPath()
        {
            var app = ActiveApplication();
            var window = TryGet(app, "ActiveWindow");
            var document = TryGet(app, "ActiveDocument");
            string name = new[] { GetText(window, "Name"), GetText(document, "Name") }
                .FirstOrDefault(value => value.EndsWith(".3dxml", StringComparison.OrdinalIgnoreCase));
            if (name == null) throw new InvalidOperationException("请在 CATIA 中激活 3DXML，或改用选择文件。");
            foreach (var item in new[] { window, document, TryGet(window, "Parent") })
            foreach (string field in new[] { "FullName", "FileName", "Path" })
            {
                string file = GetText(item, field);
                if (file.EndsWith(".3dxml", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(file) && File.Exists(file)) return file;
            }

            string fileName = Path.GetFileName(name);
            var possible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items(app, "Documents"))
            {
                string file = GetText(item, "FullName");
                if (!Path.IsPathRooted(file)) continue;
                string folder = Path.GetDirectoryName(file);
                for (int depth = 0; depth < 3 && !string.IsNullOrEmpty(folder); depth++, folder = Path.GetDirectoryName(folder))
                {
                    string candidate = Path.Combine(folder, fileName);
                    if (File.Exists(candidate)) possible.Add(candidate);
                }
            }
            if (possible.Count == 1) return possible.First();
            throw new InvalidOperationException("无法唯一定位 CATIA 当前 3DXML 对应的文件，请改用选择文件。");
        }

        private static object ActiveApplication()
        {
            try { return Marshal.GetActiveObject("CATIA.Application"); }
            catch (COMException ex) { throw new InvalidOperationException("请先启动 CATIA V5 并激活源文档。", ex); }
        }

        private static IEnumerable<Occurrence> Enumerate(object parent, string path, Lazy<Frame12> parentFrame,
            bool skipGenerated, PointReadResult result, SelectionVisibility visibility, int depth)
        {
            if (depth >= 100) throw new InvalidDataException("CATIA 装配层级超过 100。");
            foreach (var product in Items(parent, "Products"))
            {
                string name = GetText(product, "Name"), partNumber = GetText(product, "PartNumber");
                if (skipGenerated && partNumber.EndsWith("_OriginPoints", StringComparison.OrdinalIgnoreCase)) continue;
                string instancePath = path + "/" + (name.Length == 0 ? partNumber : name);
                if (visibility?.IsHidden(product) == true) continue;
                var reference = TryGet(product, "ReferenceProduct");
                var occurrence = new Occurrence
                {
                    Product = product, Document = TryGet(reference, "Parent"), Path = instancePath, Name = name,
                    PartNumber = partNumber, Frame = new Lazy<Frame12>(() => parentFrame.Value.ThenChild(Position(product)))
                };
                if (occurrence.Document == null && !skipGenerated)
                    result.Warn(instancePath + "：引用文档无法读取，请检查装配是否已加载到设计模式。");
                yield return occurrence;
                foreach (var child in Enumerate(occurrence.Product, instancePath, occurrence.Frame, skipGenerated, result, visibility, depth + 1))
                    yield return child;
            }
        }

        private static void ReadStuds(IEnumerable<Occurrence> occurrences, string[] terms, PointReadResult result)
        {
            var matches = new List<(string Name, double[] Position)>();
            foreach (var occurrence in occurrences)
            {
                if (!terms.Any(term => occurrence.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                    || occurrence.PartNumber.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                try
                {
                    string name = occurrence.Name.Length == 0 ? occurrence.PartNumber : occurrence.Name;
                    matches.Add((name, occurrence.Frame.Value.Point(new double[3])));
                }
                catch (Exception ex) { result.Warn(occurrence.Path + "：" + Message(ex)); }
            }
            var counts = matches.GroupBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < matches.Count; index++)
            {
                var match = matches[index];
                string name = match.Name + "_Origin";
                if (counts[match.Name] > 1 || used.Contains(name)) name = match.Name + "_" + (index + 1) + "_Origin";
                while (!used.Add(name)) name += "_";
                result.Points.Add(new ImportPoint(name, match.Position));
            }
        }

        private static void ReadBiW(string path, string instance, Lazy<Frame12> frame,
            Dictionary<string, List<ImportPoint>> cache, PointReadResult result)
        {
            try
            {
                if (!Path.IsPathRooted(path) || !File.Exists(path)) throw new IOException("请先将 CATProduct 保存到可访问的文件。");
                path = Path.GetFullPath(path);
                if (!cache.TryGetValue(path, out var points)) cache.Add(path, points = BiWPointFile.ReadProduct(path));
                foreach (var point in points)
                    result.Points.Add(new ImportPoint(point.Name, frame.Value.Point(new[] { point.X, point.Y, point.Z }),
                        layerCount: point.LayerCount));
            }
            catch (Exception ex) { result.Warn(instance + "：" + Message(ex)); }
        }

        private static void ReadPart(object app, object document, string path, Frame12 frame, CatiaPointMode mode,
            PointReadResult result, SelectionVisibility visibility)
        {
            var part = Catia.InvokeGet(document, "Part");
            var spa = Catia.InvokeMethod(document, "GetWorkbench", "SPAWorkbench");
            var service = new Lazy<object>(() => Catia.InvokeGet(app, "SystemService"));
            if (mode == CatiaPointMode.SolidBodies)
            {
                foreach (var body in Items(part, "Bodies"))
                    if (visibility?.IsHidden(body) != true) Measure(body, part, spa, service, frame, path, "", mode, result);
                return;
            }
            var seen = new HashSet<object>();
            ReadGeometrySets(part, part, spa, service, frame, path, mode, result, visibility, seen, 0);
            foreach (var body in Items(part, "Bodies"))
                if (visibility?.IsHidden(body) != true)
                    ReadGeometrySets(body, part, spa, service, frame, path + "/" + GetText(body, "Name"), mode, result, visibility, seen, 0);
        }

        private static void ReadGeometrySets(object owner, object part, object spa, Lazy<object> service, Frame12 frame, string path,
            CatiaPointMode mode, PointReadResult result, SelectionVisibility visibility, HashSet<object> seen, int depth)
        {
            if (depth >= 100) throw new InvalidDataException("CATIA 图形集层级超过 100。");
            string set = GetText(owner, "Name");
            foreach (var shape in Items(owner, "HybridShapes"))
            {
                if (seen.Contains(shape)) continue;
                string type = TypeName(shape);
                bool relevant = mode == CatiaPointMode.GeometricalSpheres
                    ? type.IndexOf("HybridShapeSphere", StringComparison.OrdinalIgnoreCase) >= 0
                        || (IsGenericShape(type) && TryGet(shape, "Radius") != null && TryGet(shape, "Center") != null)
                    : type.StartsWith("HybridShapePoint", StringComparison.OrdinalIgnoreCase) || IsGenericShape(type)
                        || type.Equals("HybridShapeIntersection", StringComparison.OrdinalIgnoreCase);
                if (relevant && visibility?.IsHidden(shape) != true && seen.Add(shape))
                    Measure(shape, part, spa, service, frame, path, set, mode, result, IsGenericShape(type));
            }
            foreach (string collection in new[] { "HybridBodies", "OrderedGeometricalSets" })
            foreach (var child in Items(owner, collection))
                if (visibility?.IsHidden(child) != true)
                    ReadGeometrySets(child, part, spa, service, frame, path + "/" + GetText(child, "Name"), mode, result, visibility, seen, depth + 1);
        }

        private sealed class SelectionVisibility : IDisposable
        {
            private readonly INFITF.Selection _selection;
            private readonly INFITF.AnyObject[] _selected;
            private readonly PointReadResult _result;
            private bool _used, _warned;

            private SelectionVisibility(INFITF.Selection selection, PointReadResult result)
            {
                _selection = selection;
                _result = result;
                _selected = Enumerable.Range(1, selection.Count2)
                    .Select(index => selection.Item2(index).Value as INFITF.AnyObject).ToArray();
                if (_selected.Any(item => item == null))
                    throw new InvalidOperationException("当前 CATIA 选区含无法恢复的元素。");
            }

            internal static SelectionVisibility TryCreate(object document, PointReadResult result)
            {
                try { return new SelectionVisibility((INFITF.Selection)Catia.InvokeGet(document, "Selection"), result); }
                catch (Exception error)
                {
                    result.Warn("无法读取 CATIA 可见性，保留来源对象。" + Message(error));
                    return null;
                }
            }

            internal bool IsHidden(object target)
            {
                try
                {
                    var value = (INFITF.AnyObject)target;
                    _used = true;
                    _selection.Clear();
                    _selection.Add(value);
                    if (_selection.Count2 == 1 &&
                        _selection.VisProperties.GetShow(out var state) == INFITF.CatVisPropertyStatus.catVisPropertyDefined)
                        return state == INFITF.CatVisPropertyShow.catVisPropertyNoShowAttr;
                }
                catch (Exception error)
                {
                    WarnUnknown(Message(error));
                    return false;
                }
                WarnUnknown("");
                return false;
            }

            private void WarnUnknown(string detail)
            {
                if (_warned) return;
                _warned = true;
                _result.Warn("部分 CATIA 对象未能确认可见性，已保留这些对象。" + detail);
            }

            public void Dispose()
            {
                if (!_used) return;
                try
                {
                    _selection.Clear();
                    foreach (var item in _selected) _selection.Add(item);
                }
                catch (Exception error) { _result.Warn("CATIA 选区未能完整恢复。" + Message(error)); }
            }
        }

        private static void Measure(object shape, object part, object spa, Lazy<object> service, Frame12 frame, string path, string geometrySet,
            CatiaPointMode mode, PointReadResult result, bool generic = false)
        {
            string name = GetText(shape, "Name");
            if (name.Length == 0) name = "Point_" + (result.Points.Count + 1);
            try
            {
                var reference = Catia.InvokeMethod(part, "CreateReferenceFromObject", shape);
                object measurable = Catia.InvokeMethod(spa, "GetMeasurable", reference);
                bool isPoint = mode == CatiaPointMode.Points;
                var geometryType = isPoint ? TryGet(measurable, "GeometryName") : null;
                if (geometryType != null && Convert.ToInt32(geometryType, CultureInfo.InvariantCulture) != CatMeasurablePoint) return;
                double[] xyz = ReadMeasurement(measurable, service, isPoint ? "GetPoint" : "GetCOG");
                if (mode != CatiaPointMode.Points) name = path + "/" + name;
                result.Points.Add(new ImportPoint(name, frame.Point(xyz), isPoint ? geometrySet : "",
                    layerCount: mode == CatiaPointMode.GeometricalSpheres ? (int?)2 : null));
            }
            catch (Exception ex)
            {
                // Generic HybridShape interfaces can also represent curves and surfaces.
                if (!generic || mode != CatiaPointMode.Points) result.Warn(path + "/" + name + "：" + Message(ex));
            }
        }

        private static bool IsGenericShape(string type) => type.Length == 0 || type == "HybridShape" || type == "AnyObject" || type == "IDispatch";

        private static string TypeName(object shape)
        {
            if (!(shape is IDispatchTypeInfo dispatch)) return shape.GetType().Name;
            try
            {
                dispatch.GetTypeInfo(0, 0, out var type);
                type.GetDocumentation(-1, out string name, out _, out _, out _);
                return name ?? "";
            }
            catch (COMException) { return ""; }
        }

        private static Frame12 Position(object product)
        {
            var frame = new Frame12(ReadArray(Catia.InvokeGet(product, "Position"), "GetComponents", 12));
            if (Math.Abs(frame.Determinant) < 1e-12) throw new InvalidDataException("CATIA 实例变换不可逆。");
            return frame;
        }

        private static double[] ReadMeasurement(object measurable, Lazy<object> service, string method)
        {
            try
            {
                var direct = ReadArray(measurable, method, 3);
                if (direct.Any(value => value != 0)) return direct;
            }
            catch (Exception ex) when (ex is COMException || ex is TargetInvocationException || ex is InvalidDataException || ex is MissingMemberException)
            { }
            // Some CATIA versions leave the SAFEARRAY unchanged on late-bound SPA calls.
            // A local CATVBA output array also distinguishes a real origin from that failure.
            const string function = "TxToolsMeasure";
            string code = "Public Function " + function + "(m)\r\nDim p(2)\r\nm." + method + " p\r\n" + function + " = p\r\nEnd Function\r\n";
            object returned = Catia.InvokeMethod(service.Value, "Evaluate", code, 0, function, new object[] { measurable });
            return ValidateArray(returned as Array, method, 3);
        }

        private static double[] ReadArray(object target, string method, int length)
        {
            var args = new object[] { new object[length] };
            var byRef = new ParameterModifier(1);
            byRef[0] = true;
            object returned = target.GetType().InvokeMember(method, BindingFlags.InvokeMethod,
                null, target, args, new[] { byRef }, CultureInfo.InvariantCulture, null);
            return ValidateArray((returned as Array) ?? (args[0] as Array), method, length);
        }

        private static double[] ValidateArray(Array array, string method, int length)
        {
            if (array == null || array.Rank != 1 || array.Length != length || array.Cast<object>().Any(v => v == null || v == DBNull.Value))
                throw new InvalidDataException("CATIA 未返回有效数组：" + method);
            var values = array.Cast<object>().Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
            if (!values.All(ImportPoint.IsFinite)) throw new InvalidDataException("CATIA 返回了非有限数值：" + method);
            return values;
        }

        private static IEnumerable<object> Items(object owner, string property)
        {
            var collection = TryGet(owner, property);
            if (collection == null) yield break;
            int count = Convert.ToInt32(Catia.InvokeGet(collection, "Count"), CultureInfo.InvariantCulture);
            for (int index = 1; index <= count; index++) yield return Catia.InvokeMethod(collection, "Item", index);
        }

        private static object TryGet(object owner, string property)
        {
            if (owner == null) return null;
            try { return Catia.InvokeGet(owner, property); }
            catch (Exception ex) when (ex is COMException || ex is MissingMemberException || ex is TargetInvocationException) { return null; }
        }

        private static string GetText(object owner, string property) => Convert.ToString(TryGet(owner, property), CultureInfo.InvariantCulture)?.Trim() ?? "";

        private static string Message(Exception error) => (error is TargetInvocationException ? error.InnerException ?? error : error).Message;
    }
}
