using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TxTools.CatiaPartTree.Conversion;
using TxTools.Common.TreeWorkflow;

namespace TxTools.CatiaPartTree.Core
{
    internal sealed class CatiaSourcePart
    {
        internal string File;
        internal double[] Frame;
        internal string ReferenceId, InstancePath, ConversionIssue;
        internal string[] GeometryEntries;
    }

    internal sealed class CatiaImportSource
    {
        internal StructureNode Root;
        internal string DocumentPath;
        internal readonly Dictionary<string, CatiaSourcePart> Parts = new Dictionary<string, CatiaSourcePart>();
        internal readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> GeometryIssues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal static string Hash(string path)
        {
            using (var file = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }

        // Called in a dedicated STA. Only DTOs leave this apartment; no shared CATIA RCWs.
        internal static CatiaImportSource ReadActive()
        {
            using (var reader = new Reader()) return reader.Read();
        }

        internal static CatiaImportSource ReadProductFile(string path)
        {
            using (var reader = new Reader()) return reader.ReadProductFile(path);
        }

        private sealed class Reader : IDisposable
        {
            private readonly List<object> _com = new List<object>();
            private readonly CatiaImportSource _result = new CatiaImportSource();
            private object Track(object value)
            {
                if (value != null && Marshal.IsComObject(value) && !_com.Any(v => ReferenceEquals(v, value))) _com.Add(value);
                return value;
            }
            private object Get(object value, string member)
            { return Track(value.GetType().InvokeMember(member, BindingFlags.GetProperty, null, value, null)); }
            private object Call(object value, string member, params object[] args)
            { return Track(value.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, value, args)); }
            private string Text(object value, string member) { return Convert.ToString(Get(value, member), CultureInfo.InvariantCulture); }
            private void RememberPart(string file)
            {
                // Assembly placement comes from the captured CATIA session. Only
                // CATPart geometry is read from disk by the conversion worker.
                if (_result.Files.ContainsKey(file) || _result.GeometryIssues.ContainsKey(file)) return;
                try { _result.Files.Add(file, Hash(file)); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { _result.GeometryIssues[file] = "无法读取 CATPart 几何文件：" + file + "；" + error.Message; }
            }
            private object Connect(object application)
            {
                try { return Track(application ?? Marshal.GetActiveObject("CATIA.Application")); }
                catch (COMException) { throw new InvalidOperationException("请先启动 CATIA。"); }
            }
            internal CatiaImportSource Read(object application = null)
            {
                var document = Get(Connect(application), "ActiveDocument");
                if (document == null) throw new InvalidOperationException("请先在 CATIA 中打开要读取的文档。");
                return ReadDocument(document);
            }
            internal CatiaImportSource ReadProductFile(string path, object application = null)
            {
                string file = Path.GetFullPath(path);
                if (!Path.GetExtension(file).Equals(".CATProduct", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                    throw new IOException("请选择存在的 CATProduct 装配文件。");

                var documents = Get(Connect(application), "Documents");
                int count = Convert.ToInt32(Get(documents, "Count"), CultureInfo.InvariantCulture);
                for (int i = 1; i <= count; i++)
                {
                    var document = Call(documents, "Item", i);
                    string fullName;
                    try { fullName = Text(document, "FullName"); }
                    catch (Exception error) when (MissingProperty(error) || error.GetBaseException() is COMException) { continue; }
                    if (!string.IsNullOrWhiteSpace(fullName) && Path.IsPathRooted(fullName) &&
                        string.Equals(Path.GetFullPath(fullName), file, StringComparison.OrdinalIgnoreCase))
                        return ReadDocument(document);
                }

                var opened = Call(documents, "Open", file);
                try { return ReadDocument(opened); }
                finally { Call(opened, "Close"); }
            }
            private CatiaImportSource ReadDocument(object document)
            {
                string documentName = null;
                try { documentName = Text(document, "Name"); } catch (Exception ex) when (MissingProperty(ex)) { }
                bool is3dXml = string.Equals(Path.GetExtension(documentName), ".3dxml", StringComparison.OrdinalIgnoreCase);
                string documentFile;
                try { documentFile = Text(document, "FullName"); }
                catch (Exception) when (is3dXml) { throw new ThreeDXmlFileRequiredException(documentName); }
                if (is3dXml && (string.IsNullOrWhiteSpace(documentFile) || !File.Exists(documentFile) ||
                    !string.Equals(Path.GetExtension(documentFile), ".3dxml", StringComparison.OrdinalIgnoreCase)))
                    throw new ThreeDXmlFileRequiredException(documentName);
                string file = Path.GetFullPath(documentFile);
                if (Path.GetExtension(file).Equals(".3dxml", StringComparison.OrdinalIgnoreCase))
                    return ThreeDXmlSource.Read(file);
                _result.DocumentPath = file;
                if (Path.GetExtension(file).Equals(".CATPart", StringComparison.OrdinalIgnoreCase))
                {
                    RememberPart(file);
                    _result.Root = new StructureNode { SourceName = Path.GetFileNameWithoutExtension(file), PartNumber = Path.GetFileNameWithoutExtension(file) };
                    _result.Parts.Add(_result.Root.Key, new CatiaSourcePart { File = file, Frame = Frame12.Identity.Components });
                }
                else if (Path.GetExtension(file).Equals(".CATProduct", StringComparison.OrdinalIgnoreCase))
                    _result.Root = Product(Get(document, "Product"), Frame12.Identity, "root", 0);
                else throw new NotSupportedException("活动文档必须是 CATProduct 或 CATPart。");
                return _result;
            }
            private Frame12 ReadPosition(object product, string name)
            {
                var position = Get(product, "Position");
                var args = new object[] { new object[12] };
                // IDispatch needs an explicit by-reference VARIANT. A managed test
                // double can infer ref from its signature, unlike CATIA late binding.
                var byRef = new ParameterModifier(1);
                byRef[0] = true;
                position.GetType().InvokeMember("GetComponents", BindingFlags.InvokeMethod,
                    null, position, args, new[] { byRef }, CultureInfo.InvariantCulture, null);
                var array = args[0] as Array;
                if (array == null || array.Rank != 1 || array.Length != 12)
                    throw new InvalidDataException("CATIA 未返回完整的实例位姿：" + name);
                var components = array.Cast<object>().ToArray();
                if (components.Any(value => value == null || value == DBNull.Value))
                    throw new InvalidDataException("CATIA 实例位姿存在空分量：" + name);
                return new Frame12(components.Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray());
            }
            private StructureNode Product(object product, Frame12 parent, string instancePath, int depth)
            {
                if (depth > 100) throw new IOException("CATIA 装配层级超过 100，已停止读取。");
                string name = Text(product, "Name");
                var frame = depth == 0 ? Frame12.Identity : parent.ThenChild(ReadPosition(product, name));
                var reference = Get(product, "ReferenceProduct");
                var refDoc = ReferenceDocument(reference);
                string file = Path.GetFullPath(Text(refDoc, "FullName"));
                var node = new StructureNode { SourceName = name, PartNumber = Text(reference, "PartNumber") };
                if (Path.GetExtension(file).Equals(".CATPart", StringComparison.OrdinalIgnoreCase))
                {
                    RememberPart(file);
                    _result.Parts.Add(node.Key, new CatiaSourcePart { File = file, Frame = frame.Components, InstancePath = instancePath });
                }
                else
                {
                    if (!Path.GetExtension(file).Equals(".CATProduct", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("引用未解析为 CATProduct/CATPart：" + node.SourceName);
                    node.IsContainer = true;
                    var children = Get(product, "Products"); int count = Convert.ToInt32(Get(children, "Count"));
                    for (int i = 1; i <= count; i++) node.Add(Product(Call(children, "Item", i), frame, instancePath + "/" + i, depth + 1));
                }
                return node;
            }
            private object ReferenceDocument(object reference)
            {
                var owner = Get(reference, "Parent");
                for (int i = 0; i < 100 && owner != null; i++)
                {
                    try { if (!string.IsNullOrWhiteSpace(Text(owner, "FullName"))) return owner; }
                    catch (Exception ex) when (MissingProperty(ex)) { }
                    owner = Get(owner, "Parent");
                }
                throw new IOException("无法解析 CATIA 引用所属的已保存文档。");
            }
            private static bool MissingProperty(Exception error)
            {
                if (error is TargetInvocationException && error.InnerException != null) return MissingProperty(error.InnerException);
                var com = error as COMException;
                return error is MissingMemberException || (com != null && (com.ErrorCode == unchecked((int)0x80020003) || com.ErrorCode == unchecked((int)0x80020006)));
            }
            public void Dispose()
            {
                for (int i = _com.Count - 1; i >= 0; i--)
                    try { Marshal.ReleaseComObject(_com[i]); } catch (InvalidComObjectException) { }
                _com.Clear();
            }
        }
    }
}
