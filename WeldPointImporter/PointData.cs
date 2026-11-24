using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TxTools.WeldPointImporter
{
    internal sealed class ImportPoint
    {
        internal readonly string OriginalName, GeometrySet;
        internal readonly double X, Y, Z;
        internal readonly int Confidence, MethodRank;
        internal readonly int? LayerCount;
        internal string Name;

        internal ImportPoint(string name, double[] xyz, string geometrySet = "", int confidence = 0, int methodRank = 1, int? layerCount = null)
        {
            if (xyz.Length != 3 || !xyz.All(IsFinite)) throw new InvalidDataException("坐标必须是三个有限数值。");
            Name = OriginalName = (name ?? "").Trim();
            GeometrySet = geometrySet;
            X = xyz[0]; Y = xyz[1]; Z = xyz[2];
            Confidence = confidence;
            MethodRank = methodRank;
            LayerCount = layerCount ?? WeldLayers.FromGeometrySet(geometrySet);
        }

        internal double Axis(int axis) => axis == 0 ? X : axis == 1 ? Y : Z;
        internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal string StandardName()
        {
            if (string.IsNullOrWhiteSpace(GeometrySet)) return OriginalName;
            string[] point = OriginalName.Split('-');
            string[] parts = GeometrySet.Split('&')[0].Trim().Split('-').Select(segment => segment.Split('_')[0]).ToArray();
            string part = parts.FirstOrDefault(segment => Regex.IsMatch(segment, @"^[0-9]+$"))
                ?? parts[Math.Min(1, parts.Length - 1)];
            if (string.IsNullOrEmpty(part))
            {
                part = Regex.Replace(GeometrySet, "[^A-Za-z0-9]", "");
                part = part.Length == 0 ? "PART" : part.Substring(0, Math.Min(12, part.Length));
            }
            string id = point.Length > 1 ? point[1].Trim().TrimStart('0') : "";
            return point[0].Trim() + "_" + part + "_" + (id.Length == 0 ? "0" : id);
        }
    }

    internal enum WeldLayerGroup { Two, Three, FourOrMore }

    internal static class WeldLayers
    {
        internal static readonly HashSet<string> CountFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "weldlayers", "weld_layers", "layercount", "layer_count", "layers", "焊点层数", "板层数" };
        internal static readonly HashSet<string> ShapeFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "geometrytype", "geometry_type", "shape", "shape_candidate", "type" };
        private static readonly Regex LayerCode = new Regex(@"(?<![A-Z0-9.])([0-9]+)\s*T(?![A-Z0-9])", RegexOptions.CultureInvariant);
        private static readonly Regex LayerText = new Regex(@"(?<![0-9.零一二两三四五六七八九十百千万])([0-9]+|[一二两三四五六七八九十]+)\s*层", RegexOptions.CultureInvariant);
        private const string ChineseDigits = "零一二三四五六七八九";

        internal static WeldLayerGroup Group(int? count) => count >= 4 ? WeldLayerGroup.FourOrMore
            : count == 3 ? WeldLayerGroup.Three : WeldLayerGroup.Two;

        internal static int? FromGeometrySet(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            int count = name.Normalize(NormalizationForm.FormKC).Split('&').Count(part => !string.IsNullOrWhiteSpace(part));
            return count >= 2 ? (int?)count : null;
        }

        internal static int? FromMetadata(IEnumerable<string> shapes, IEnumerable<string> labels, IEnumerable<string> counts)
        {
            var geometry = new HashSet<int>();
            foreach (string shape in shapes)
                switch (Normalize(shape))
                {
                    case "球体": case "球形": case "SPHERE": case "SPHERICAL": geometry.Add(2); break;
                    case "正方体": case "立方体": case "CUBE": geometry.Add(3); break;
                    case "圆锥": case "圆锥体": case "CONE": case "CONICAL": geometry.Add(4); break;
                }
            if (geometry.Count > 0) return geometry.Count == 1 ? (int?)geometry.First() : null;

            var layers = new HashSet<int>();
            foreach (string label in labels)
            {
                string value = Normalize(label);
                foreach (Match match in LayerCode.Matches(value)) AddNumeric(layers, match.Groups[1].Value);
                foreach (Match match in LayerText.Matches(value))
                {
                    string text = match.Groups[1].Value;
                    if (text[0] >= '0' && text[0] <= '9') AddNumeric(layers, text);
                    else
                    {
                        int count = ParseChinese(text);
                        if (count >= 2) layers.Add(count);
                    }
                }
                if (value.Contains("双层")) layers.Add(2);
            }
            foreach (string count in counts) AddNumeric(layers, Normalize(count));
            return layers.Count == 1 ? (int?)layers.First() : null;
        }

        private static string Normalize(string text) => (text ?? "").Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();

        private static void AddNumeric(HashSet<int> layers, string value)
        {
            if (decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal count)
                && count >= 2 && count <= int.MaxValue && count == decimal.Truncate(count)) layers.Add((int)count);
        }

        private static int ParseChinese(string text)
        {
            text = text.Replace('两', '二');
            if (text.Length == 1 && text != "十") return ChineseDigits.IndexOf(text[0]);
            int tens = text.IndexOf('十');
            if (tens < 0 || tens > 1 || text.Length - tens > 2 || text.LastIndexOf('十') != tens) return 0;
            int tensCount = tens == 0 ? 1 : ChineseDigits.IndexOf(text[0]);
            int units = tens == text.Length - 1 ? 0 : ChineseDigits.IndexOf(text[tens + 1]);
            return 10 * tensCount + units;
        }
    }

    internal sealed class PointReadResult
    {
        internal readonly List<ImportPoint> Points = new List<ImportPoint>();
        internal readonly List<string> Warnings = new List<string>();
        internal int WarningCount { get; private set; }

        internal void Warn(string message)
        {
            WarningCount++;
            if (Warnings.Count < 5) Warnings.Add(message);
        }
    }

    internal sealed class DuplicateMatch
    {
        internal readonly int Kept;
        internal readonly double Distance;
        internal DuplicateMatch(int kept, double distance) { Kept = kept; Distance = distance; }
    }

    internal static class DuplicatePoints
    {
        internal static Dictionary<int, DuplicateMatch> Find(IReadOnlyList<ImportPoint> points, double threshold)
        {
            if (!ImportPoint.IsFinite(threshold) || threshold <= 0)
                throw new ArgumentOutOfRangeException(nameof(threshold));
            var cells = new Dictionary<(double X, double Y, double Z), List<int>>();
            var duplicates = new Dictionary<int, DuplicateMatch>();
            double limit = threshold * threshold;
            var order = Enumerable.Range(0, points.Count)
                .OrderByDescending(index => points[index].Confidence).ThenByDescending(index => points[index].MethodRank);
            foreach (int index in order)
            {
                ImportPoint point = points[index];
                var cell = (X: Math.Floor(point.X / threshold), Y: Math.Floor(point.Y / threshold), Z: Math.Floor(point.Z / threshold));
                int nearest = -1;
                double best = limit;
                for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                {
                    if (!cells.TryGetValue((cell.X + x, cell.Y + y, cell.Z + z), out var neighbors)) continue;
                    foreach (int candidate in neighbors)
                    {
                        var kept = points[candidate];
                        double dx = point.X - kept.X, dy = point.Y - kept.Y, dz = point.Z - kept.Z;
                        double distance = dx * dx + dy * dy + dz * dz;
                        if (distance > best || (distance == best && nearest >= 0 && candidate >= nearest)) continue;
                        best = distance;
                        nearest = candidate;
                    }
                }
                if (nearest >= 0) duplicates.Add(index, new DuplicateMatch(nearest, Math.Sqrt(best)));
                else
                {
                    if (!cells.TryGetValue(cell, out var kept)) cells.Add(cell, kept = new List<int>());
                    kept.Add(index);
                }
            }
            return duplicates;
        }
    }

    internal sealed class PointNaming
    {
        internal string Prefix = "", Main = "WeldPoint", Separator = "_", Suffix = "";
        internal int Start = 1, Digits = 4;

        internal string At(int index)
        {
            string number = ((long)Start + index).ToString("D" + Digits, CultureInfo.InvariantCulture);
            return Prefix + Main + Separator + number + Suffix;
        }
    }
}
