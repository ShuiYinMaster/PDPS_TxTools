using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TxTools.Agent.Core;

namespace TxTools.WeldPointImporter
{
    internal static class PointTableFile
    {
        internal static PointReadResult Read(string path)
        {
            var result = new PointReadResult();
            var rows = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? XlsxReader.Read(path).Primary?.Rows
                : ReadDelimited(path);
            if (rows == null) return result;
            int heading = rows.FindIndex(row => row.Any(value => !string.IsNullOrWhiteSpace(value)));
            if (heading < 0) return result;
            string[] headers = rows[heading].Select(value => (value ?? "").Trim().TrimStart('\ufeff')).ToArray();
            int[] axes = new[] { "x", "y", "z" }.Select(axis => Column(headers, axis, axis + "_mm", "center_" + axis + "_mm")).ToArray();
            if (axes.Any(index => index < 0)) throw new InvalidDataException("文件需要 X、Y、Z 列，也支持 X_mm 或 center_x_mm 等列名。");
            bool mmColumns = axes.Any(index => headers[index].EndsWith("_mm", StringComparison.OrdinalIgnoreCase));
            int nameColumn = Column(headers, "name", "名称", "点名称", "proxy_id", "id", "externalid");
            int unitColumn = Column(headers, "unit", "单位");
            int confidenceColumn = Column(headers, "confidence");
            int methodColumn = Column(headers, "extraction_method");
            int[] shapeColumns = Enumerable.Range(0, headers.Length).Where(i => WeldLayers.ShapeFields.Contains(headers[i])).ToArray();
            int[] countColumns = Enumerable.Range(0, headers.Length).Where(i => WeldLayers.CountFields.Contains(headers[i])).ToArray();
            int[] layerColumns = countColumns.Concat(Enumerable.Range(0, headers.Length)
                .Where(i => headers[i].Equals("type", StringComparison.OrdinalIgnoreCase))).ToArray();
            for (int index = heading + 1; index < rows.Count; index++)
            {
                var row = rows[index];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                try
                {
                    string unit = At(row, unitColumn).ToLowerInvariant();
                    if (unit.Length == 0) unit = "mm";
                    if (mmColumns && unit != "mm") throw new InvalidDataException("坐标列的 mm 单位与 Unit 列冲突。");
                    double scale;
                    switch (unit)
                    {
                        case "mm": scale = 1; break;
                        case "cm": scale = 10; break;
                        case "m": scale = 1000; break;
                        case "in": scale = 25.4; break;
                        default: throw new InvalidDataException("不支持坐标单位 " + unit);
                    }
                    string name = At(row, nameColumn);
                    if (name.Length == 0) name = "Point_" + (index - heading);
                    string confidence = At(row, confidenceColumn).ToLowerInvariant();
                    string method = At(row, methodColumn).ToLowerInvariant();
                    int? layerCount = WeldLayers.FromMetadata(shapeColumns.Select(i => At(row, i)),
                        layerColumns.Select(i => At(row, i)), countColumns.Select(i => At(row, i)));
                    result.Points.Add(new ImportPoint(name, axes.Select(axis => Parse(At(row, axis)) * scale).ToArray(),
                        confidence: confidence == "high" ? 3 : confidence == "medium" ? 2 : confidence == "low" ? 1 : 0,
                        methodRank: method == "xml_mesh_feature_matching" ? 2 : method == "binary_tessellation_signature_fallback" ? 0 : 1,
                        layerCount: layerCount));
                }
                catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is InvalidDataException)
                { result.Warn("第 " + (index + 1) + " 行：" + ex.Message); }
            }
            return result;
        }

        private static List<List<string>> ReadDelimited(string path)
        {
            string data = FileParserService.ReadTextAuto(path).TrimStart('\ufeff');
            foreach (char delimiter in new[] { ',', '\t', ';' })
            {
                var rows = FileParserService.ParseCsvText(data, delimiter);
                if (rows.FirstOrDefault(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))?.Count >= 3) return rows;
            }
            return FileParserService.ParseCsvText(data, ',');
        }

        private static double Parse(string value)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return number;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number)) return number;
            throw new FormatException("无法读取坐标值 “" + value + "”。");
        }

        internal static string At(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? (row[index] ?? "").Trim() : "";

        internal static int Column(string[] headers, params string[] names)
        {
            foreach (string name in names)
            {
                int index = Array.FindIndex(headers, header => name.Equals(header, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) return index;
            }
            return -1;
        }
    }

    internal static class BiWPointFile
    {
        private static readonly Regex JointName = new Regex(@"Joint Element\.([0-9]+)", RegexOptions.CultureInvariant);
        private static readonly Regex NeutralKey = new Regex(@"([A-Za-z][A-Za-z0-9 _-]*)\s*[=:]\s*([^;\t,]+)", RegexOptions.CultureInvariant);
        private static readonly Regex Number = new Regex(@"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][-+]?\d+)?", RegexOptions.CultureInvariant);

        internal static PointReadResult Read(string path)
        {
            var result = new PointReadResult();
            if (Path.GetExtension(path).Equals(".CATProduct", StringComparison.OrdinalIgnoreCase))
                result.Points.AddRange(ReadProduct(path));
            else ReadNeutral(FileParserService.ReadTextAuto(path), result);
            return result;
        }

        internal static List<ImportPoint> ReadProduct(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (!StartsWith(data, 0, Encoding.ASCII.GetBytes("V5_CFV2"))) throw new InvalidDataException("CATProduct 文件头不是 V5_CFV2。");
            var names = JointName.Matches(Encoding.ASCII.GetString(data)).Cast<Match>()
                .Where(match => match.Groups[1].Value != "00000")
                .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length == 0) return new List<ImportPoint>();

            var groups = new Dictionary<(int PrefixLength, byte Kind), List<(double X, double Y, double Z)>>();
            for (int offset = 0; offset < data.Length - 2; offset++)
            {
                if (data[offset] != 0xfe) continue;
                int prefix = data[offset + 1] == 0xfe && data[offset + 2] == 0x32 ? 3
                    : data[offset + 1] == 0x32 && (offset == 0 || data[offset - 1] != 0xfe) ? 2 : 0;
                if (prefix == 0 || offset + prefix >= data.Length) continue;
                int kind = offset + prefix;
                for (int first = kind + 1; first <= offset + 32 && first <= data.Length - 27; first++)
                {
                    if (data[first] != 0xe6 || data[first + 9] != 0xe6 || data[first + 18] != 0xe6) continue;
                    var point = (X: BitConverter.ToDouble(data, first + 1), Y: BitConverter.ToDouble(data, first + 10), Z: BitConverter.ToDouble(data, first + 19));
                    if (Math.Abs(point.X) < 1e7 && Math.Abs(point.Y) < 1e7 && Math.Abs(point.Z) < 1e7)
                    {
                        var key = (prefix, data[kind]);
                        if (!groups.TryGetValue(key, out var coordinates)) groups.Add(key, coordinates = new List<(double, double, double)>());
                        coordinates.Add(point);
                    }
                    break;
                }
            }
            var matching = groups.Values.Where(points => points.Count == names.Length).ToArray();
            if (matching.Length == 0) throw new InvalidDataException("没有与 " + names.Length + " 个 Joint Element 匹配的坐标记录组。");
            var repeated = matching.Where(points => matching.Any(other => !ReferenceEquals(other, points) && points.SequenceEqual(other))).ToArray();
            var candidates = repeated.Length != 0 ? repeated : matching;
            var chosen = candidates[0];
            if (candidates.Skip(1).Any(other => !chosen.SequenceEqual(other)))
                throw new InvalidDataException("存在多个互相冲突的 BiW 坐标记录组，无法确认 Joint Element 坐标。");
            return names.Select((name, index) => new ImportPoint(name, new[] { chosen[index].X, chosen[index].Y, chosen[index].Z })).ToList();
        }

        private static void ReadNeutral(string text, PointReadResult result)
        {
            int[] header = null;
            int lineNumber = 0;
            using (var reader = new StringReader(text.TrimStart('\ufeff')))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line)) { header = null; continue; }
                    var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (Match match in NeutralKey.Matches(line)) pairs[Normalize(match.Groups[1].Value)] = match.Groups[2].Value.Trim();
                    if (pairs.TryGetValue("LOCX", out string x) && pairs.TryGetValue("LOCY", out string y) && pairs.TryGetValue("LOCZ", out string z))
                    {
                        if (!pairs.TryGetValue("ID", out string name)) pairs.TryGetValue("JEID", out name);
                        AddNeutral(result, lineNumber, name, new[] { x, y, z });
                        continue;
                    }
                    var values = line.Split('\t');
                    if (values.Length < 3) continue;
                    var labels = values.Select(Normalize).ToArray();
                    var columns = new[] { PointTableFile.Column(labels, "LOCX", "X", "LOCATIONX"),
                        PointTableFile.Column(labels, "LOCY", "Y", "LOCATIONY"), PointTableFile.Column(labels, "LOCZ", "Z", "LOCATIONZ"),
                        PointTableFile.Column(labels, "ID", "JEID", "JOINTELEMENT") };
                    if (columns.Take(3).All(index => index >= 0)) { header = columns; continue; }
                    if (header != null) AddNeutral(result, lineNumber, PointTableFile.At(values, header[3]),
                        header.Take(3).Select(index => PointTableFile.At(values, index)).ToArray());
                }
            }
        }

        private static void AddNeutral(PointReadResult result, int line, string name, string[] xyz)
        {
            try
            {
                var point = xyz.Select(value => double.Parse(Number.Match(value).Value, CultureInfo.InvariantCulture)).ToArray();
                name = (name ?? "").Trim();
                if (name.Length == 0) name = "Joint Element";
                else if (!Regex.IsMatch(name, @"joint\s*element", RegexOptions.IgnoreCase)) name = "Joint Element." + name;
                result.Points.Add(new ImportPoint(name, point));
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is InvalidDataException)
            { result.Warn("第 " + line + " 行：" + ex.Message); }
        }

        private static string Normalize(string value) => Regex.Replace(value.ToUpperInvariant(), "[^A-Z0-9]", "");

        internal static bool StartsWith(byte[] data, int offset, byte[] signature)
        {
            if (offset < 0 || offset > data.Length - signature.Length) return false;
            for (int index = 0; index < signature.Length; index++)
                if (data[offset + index] != signature[index]) return false;
            return true;
        }
    }
}
