using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TxTools.CatiaPartTree.Conversion;

namespace TxTools.WeldPointImporter
{
    internal enum ThreeDXmlPointMode { Auto, NameOnly, GeometryOnly, EnvelopeCenters }

    internal static class ThreeDXmlPoints
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("V5_CFV3");
        private static readonly byte[] LegacyMagic = Encoding.ASCII.GetBytes("V5_CFV");
        private static readonly byte[] Node = { 0x04, 0xfd, 0xff };
        private static readonly byte[] Mesh = { 0xff, 0xff, 0x02, 0, 1 };
        private static readonly byte[] Cube = { 0x14, 0x45, 0x51, 0x14, 0x45, 0x51, 0x18 };
        private static readonly Regex Number = new Regex(@"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][-+]?\d+)?", RegexOptions.CultureInvariant);

        private sealed class Symbol
        {
            internal double[] Center;
            internal int Count;
            internal bool IsCube;
            internal double Radius;
            internal int Offset;
        }

        internal static PointReadResult Read(string path, ThreeDXmlPointMode mode, string hint, double radius)
        {
            if (!ImportPoint.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
            bool envelopes = mode == ThreeDXmlPointMode.EnvelopeCenters;
            hint = mode == ThreeDXmlPointMode.GeometryOnly || envelopes ? "" : (hint ?? "").Trim();
            if (mode == ThreeDXmlPointMode.NameOnly && hint.Length == 0) throw new ArgumentException("仅名称筛选需要输入零件名称线索。");
            var result = new PointReadResult();
            using (var archive = ZipFile.OpenRead(path))
            {
                var entries = archive.Entries.ToLookup(entry => entry.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
                var byFile = archive.Entries.ToLookup(entry => Path.GetFileName(entry.FullName.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
                ZipArchiveEntry Resolve(string uri)
                {
                    const string prefix = "urn:3dxml:";
                    string key = uri.Trim().Replace('\\', '/');
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) key = key.Substring(prefix.Length);
                    var exact = entries[key];
                    var matches = (exact.Any() ? exact : byFile[Path.GetFileName(key)]).Take(2).ToArray();
                    if (key.Length == 0 || matches.Length != 1) throw new InvalidDataException("无法唯一定位 3DXML 关联文件：“" + uri + "”。");
                    return matches[0];
                }

                var manifest = ReadXml(Resolve("Manifest.xml"));
                var rootLink = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "Root");
                var model = ReadXml(Resolve(rootLink?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase))?.Value
                    ?? rootLink?.Value ?? ""));
                var structure = model.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProductStructure");
                if (structure == null) throw new InvalidDataException("3DXML 缺少 ProductStructure，无法定位装配坐标。");
                var references = Index(structure, "Reference3D");
                var representations = Index(structure, "ReferenceRep");
                var children = structure.Elements().Where(e => e.Name.LocalName == "Instance3D").ToLookup(e => Child(e, "IsAggregatedBy"));
                var attachments = structure.Elements().Where(e => e.Name.LocalName == "InstanceRep").ToLookup(e => Child(e, "IsAggregatedBy"));
                var cache = new Dictionary<ZipArchiveEntry, List<Symbol>>();
                var ancestry = new HashSet<string>(StringComparer.Ordinal);
                int occurrences = 0;

                void Visit(string id, string instanceName, Frame12 frame, bool inheritedHint)
                {
                    if (ancestry.Count >= 100 || !ancestry.Add(id)) throw new InvalidDataException("3DXML 存在循环装配引用或层级超过 100。");
                    if (++occurrences > 100000) throw new InvalidDataException("3DXML 展开实例超过 100000。");
                    if (!references.TryGetValue(id, out var reference)) throw new InvalidDataException("3DXML 缺少内部 Reference3D：" + id);
                    string name = (string)reference.Attribute("name") ?? id;
                    bool matchesHint = inheritedHint || (hint.Length != 0 && (Contains(name, hint) || Contains(instanceName, hint)
                        || Contains(Child(reference, "PLM_ExternalID"), hint)));
                    if (mode != ThreeDXmlPointMode.NameOnly || matchesHint)
                    foreach (var attachment in attachments[id])
                    {
                        string repId = Child(attachment, "IsInstanceOf");
                        if (!representations.TryGetValue(repId, out var rep)) throw new InvalidDataException("3DXML 缺少内部 ReferenceRep：" + repId);
                        var entry = Resolve((string)rep.Attribute("associatedFile") ?? "");
                        if (!cache.TryGetValue(entry, out var symbols)) cache.Add(entry, symbols = LoadRepresentation(entry, radius, envelopes, result));
                        var family = envelopes ? null : symbols.GroupBy(symbol => symbol.Count).ToDictionary(group => group.Key, group => group.Count());
                        var world = frame.ThenChild(LocalFrame(attachment));
                        int sequence = 0;
                        foreach (var symbol in symbols)
                        {
                            if (envelopes)
                            {
                                result.Points.Add(new ImportPoint(name + (symbol.Offset >= 0 ? ":3DRep@0x" + symbol.Offset.ToString("X", CultureInfo.InvariantCulture)
                                    : ":3DRep 包络中心"), world.Point(symbol.Center), confidence: 1));
                                continue;
                            }
                            bool repeated = family[symbol.Count] >= 3;
                            if (!symbol.IsCube && !matchesHint && !repeated) continue;
                            int confidence = symbol.IsCube || (matchesHint && repeated) ? 3 : 2;
                            result.Points.Add(new ImportPoint(name + ":WeldCandidate_" + (++sequence).ToString("D4", CultureInfo.InvariantCulture),
                                world.Point(symbol.Center), confidence: confidence, layerCount: symbol.IsCube ? (int?)3 : null));
                        }
                    }
                    foreach (var child in children[id])
                        Visit(Child(child, "IsInstanceOf"), (string)child.Attribute("name") ?? "", frame.ThenChild(LocalFrame(child)), matchesHint);
                    ancestry.Remove(id);
                }

                Visit((string)structure.Attribute("root") ?? "", "", Frame12.Identity, false);
            }
            return result;
        }

        private static bool Contains(string text, string hint) => text.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0;

        private static Dictionary<string, XElement> Index(XElement structure, string tag) => structure.Elements()
            .Where(e => e.Name.LocalName == tag).ToDictionary(e => (string)e.Attribute("id") ?? "", StringComparer.Ordinal);

        private static string Child(XElement element, string name) => element.Elements()
            .FirstOrDefault(child => child.Name.LocalName == name)?.Value.Trim() ?? "";

        private static XDocument ReadXml(ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 }))
                return XDocument.Load(reader);
        }

        private static Frame12 LocalFrame(XElement instance)
        {
            string text = Child(instance, "RelativeMatrix");
            if (text.Length == 0) return Frame12.Identity;
            var values = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            var frame = new Frame12(values);
            for (int i = 0; i < 3; i++)
            for (int j = i; j < 3; j++)
            {
                double dot = values[3 * i] * values[3 * j] + values[3 * i + 1] * values[3 * j + 1] + values[3 * i + 2] * values[3 * j + 2];
                if (Math.Abs(dot - (i == j ? 1 : 0)) > 0.001) throw new InvalidDataException("3DXML RelativeMatrix 含缩放或剪切，无法确认毫米坐标。");
            }
            return frame;
        }

        private static List<Symbol> LoadRepresentation(ZipArchiveEntry entry, double maxRadius, bool envelopes, PointReadResult result)
        {
            byte[] data;
            using (var stream = entry.Open())
            {
                var header = new byte[Magic.Length];
                int read = 0, size;
                while (read < header.Length && (size = stream.Read(header, read, header.Length - read)) != 0) read += size;
                if (!BiWPointFile.StartsWith(header, 0, envelopes ? LegacyMagic : Magic))
                {
                    if (envelopes)
                    {
                        try { return ReadXmlEnvelope(entry, maxRadius); }
                        catch (XmlException) { }
                    }
                    result.Warn(entry.FullName + "：该几何表示编码不支持焊点符号识别。");
                    return new List<Symbol>();
                }
                if (entry.Length > int.MaxValue) throw new InvalidDataException("3DXML 几何表示过大：" + entry.FullName);
                data = new byte[(int)entry.Length];
                Buffer.BlockCopy(header, 0, data, 0, read);
                while (read < data.Length && (size = stream.Read(data, read, data.Length - read)) != 0) read += size;
                if (read != data.Length) throw new InvalidDataException("3DXML 几何表示长度不完整：" + entry.FullName);
            }
            return envelopes ? ReadBinaryEnvelopes(data, maxRadius) : ReadSymbols(data, maxRadius);
        }

        private static List<Symbol> ReadSymbols(byte[] data, double maxRadius)
        {
            var symbols = new List<Symbol>();
            for (int offset = Magic.Length; offset <= data.Length - 47; offset++)
            {
                if (data[offset] != Node[0] || !BiWPointFile.StartsWith(data, offset, Node) || data[offset + 7] != 0
                    || BitConverter.ToUInt32(data, offset + 12) != 0 || !BiWPointFile.StartsWith(data, offset + 32, Mesh)) continue;
                double radius = BitConverter.ToSingle(data, offset + 8);
                if (!(radius >= 0.1 && radius <= maxRadius)) continue;
                int cursor = offset + 37;
                uint count = CompactCount(data, ref cursor), repeated = CompactCount(data, ref cursor);
                if (count < 4 || count > 5000 || count != repeated || cursor > data.Length - 3
                    || data[cursor] != 0 || data[cursor + 1] != 0 || data[cursor + 2] != 0) continue;
                var center = ReadPoint(data, offset + 16);
                if (!center.All(ImportPoint.IsFinite)) continue;
                var cube = count == 24 ? FitCube(data, cursor + 3, radius, center) : null;
                symbols.Add(new Symbol { Center = cube ?? center, Count = (int)count, IsCube = cube != null });
            }
            return symbols;
        }

        private static List<Symbol> ReadBinaryEnvelopes(byte[] data, double maxRadius)
        {
            var groups = new Dictionary<(double R, double X, double Y, double Z), List<Symbol>>();
            for (int offset = Magic.Length; offset <= data.Length - 47; offset++)
            {
                if (data[offset] != Node[0] || !BiWPointFile.StartsWith(data, offset, Node)) continue;
                double radius = BitConverter.ToSingle(data, offset + 8);
                var center = ReadPoint(data, offset + 16);
                if (!(radius > 0 && radius <= 1e9) || !center.All(ImportPoint.IsFinite)
                    || (radius <= 1.0001 && center.All(value => Math.Abs(value) <= 0.0001))) continue;
                int cursor = offset + 37;
                uint count = BiWPointFile.StartsWith(data, offset + 32, Mesh) ? CompactCount(data, ref cursor) : 0;
                var key = (Math.Round(radius, 4), Math.Round(center[0], 4), Math.Round(center[1], 4), Math.Round(center[2], 4));
                if (!groups.TryGetValue(key, out var records)) groups.Add(key, records = new List<Symbol>());
                records.Add(new Symbol { Center = center, Radius = radius, Count = (int)Math.Min(count, (uint)int.MaxValue), Offset = offset });
            }
            var all = groups.Values.Select(records => new { Symbol = records.FirstOrDefault(record => record.Count > 0) ?? records[0], Hits = records.Count }).ToArray();
            double RadiusLimit(Symbol symbol) => Math.Min(maxRadius, symbol.Count >= 24 ? 10 : symbol.Count >= 4 ? 20 : maxRadius);
            var withinLimit = all.Where(group => group.Symbol.Radius <= RadiusLimit(group.Symbol)).ToArray();
            var repeated = withinLimit.Where(group => group.Hits >= 2).Select(group => group.Symbol).ToList();
            if (repeated.Count > 0) return repeated;

            var radii = all.Select(group => Math.Round(group.Symbol.Radius, 4)).Where(radius => radius > 1).Distinct().OrderBy(radius => radius).ToArray();
            double minimum = 0;
            if (radii.Length >= 4)
            for (int index = 1; index < radii.Length; index++)
            {
                double previous = radii[index - 1], current = radii[index];
                if (previous > radii[radii.Length - 1] * 0.1 || current / previous < 1.5) continue;
                minimum = Math.Sqrt(previous * current);
                break;
            }
            var candidates = withinLimit.Select(group => group.Symbol).Where(symbol => symbol.Radius >= minimum).ToList();
            if (candidates.Count > 1)
            {
                double largest = candidates.Max(symbol => symbol.Radius);
                var smaller = candidates.Where(symbol => symbol.Radius < largest).ToList();
                if (smaller.Count > 0) return smaller;
            }
            return candidates;
        }

        private static List<Symbol> ReadXmlEnvelope(ZipArchiveEntry entry, double maxRadius)
        {
            var document = ReadXml(entry);
            var low = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var high = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            var positions = new HashSet<string>(new[] { "positions", "position", "coordinates", "values" }, StringComparer.OrdinalIgnoreCase);
            foreach (var buffer in document.Descendants().Where(e => e.Name.LocalName.Equals("VertexBuffer", StringComparison.OrdinalIgnoreCase)))
            {
                var values = buffer.Attributes().Where(a => positions.Contains(a.Name.LocalName)).Select(a => a.Value)
                    .Concat(buffer.Elements().Where(e => positions.Contains(e.Name.LocalName)).Select(e => e.Value)).ToArray();
                string text = values.Length == 0 ? string.Join(" ", buffer.Nodes().OfType<XText>().Select(node => node.Value)) : string.Join(" ", values);
                var numbers = Number.Matches(text).Cast<Match>().Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
                for (int index = 0; index + 2 < numbers.Length; index += 3)
                {
                    for (int axis = 0; axis < 3; axis++)
                    {
                        low[axis] = Math.Min(low[axis], numbers[index + axis]);
                        high[axis] = Math.Max(high[axis], numbers[index + axis]);
                    }
                }
            }
            var result = new List<Symbol>();
            if (low.All(ImportPoint.IsFinite) && Distance(low, high) / 2 <= maxRadius)
                result.Add(new Symbol { Center = Enumerable.Range(0, 3).Select(axis => (low[axis] + high[axis]) / 2).ToArray(), Offset = -1 });
            return result;
        }

        private static uint CompactCount(byte[] data, ref int offset)
        {
            if (offset >= data.Length) return 0;
            uint value = data[offset++];
            if (value != 255) return value;
            if (offset > data.Length - 4) return 0;
            value = BitConverter.ToUInt32(data, offset);
            offset += 4;
            return value;
        }

        private static double[] FitCube(byte[] data, int cursor, double radius, double[] boundCenter)
        {
            for (int offset = cursor; offset <= Math.Min(cursor + 24, data.Length) - Cube.Length; offset++)
            {
                if (!BiWPointFile.StartsWith(data, offset, Cube)) continue;
                int verticesStart = offset + Cube.Length;
                if (verticesStart > data.Length - 96) return null;
                var vertices = Enumerable.Range(0, 8).Select(index => ReadPoint(data, verticesStart + 12 * index)).ToArray();
                if (vertices.Any(point => !point.All(ImportPoint.IsFinite) || Distance(point, boundCenter) > radius + 0.02)) return null;
                var distances = new List<double>(28);
                for (int i = 0; i < vertices.Length; i++)
                for (int j = i + 1; j < vertices.Length; j++) distances.Add(Distance(vertices[i], vertices[j]));
                distances.Sort();
                double edge = distances.Take(12).Average();
                if (edge <= 0.05) return null;
                for (int index = 0; index < distances.Count; index++)
                {
                    double ideal = edge * Math.Sqrt(index < 12 ? 1 : index < 24 ? 2 : 3);
                    if (Math.Abs(distances[index] - ideal) / edge > 0.005) return null;
                }
                var center = Enumerable.Range(0, 3).Select(axis => vertices.Average(point => point[axis])).ToArray();
                return Distance(center, boundCenter) <= 0.02 ? center : null;
            }
            return null;
        }

        private static double[] ReadPoint(byte[] bytes, int offset) => new[]
            { (double)BitConverter.ToSingle(bytes, offset), BitConverter.ToSingle(bytes, offset + 4), BitConverter.ToSingle(bytes, offset + 8) };

        private static double Distance(double[] a, double[] b)
        {
            double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
            return Math.Sqrt(x * x + y * y + z * z);
        }
    }
}
