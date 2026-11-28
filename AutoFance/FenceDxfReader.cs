using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TxTools.FenceBuilder
{
    /// <summary>
    /// DXF 基线读取选项。
    /// </summary>
    public class FenceDxfOptions
    {
        /// <summary>孤立短线阈值(mm)：单独成链且长度小于该值的线段视为符号线丢弃；0=保留全部。</summary>
        public double IsolatedSegmentThreshold = 1100.0;

        /// <summary>图层过滤，逗号分号分隔，空=全部图层。</summary>
        public string LayerFilter = "";

        /// <summary>端点连接容差(mm)。</summary>
        public double JoinTolerance = 0.5;

        /// <summary>基线 Z(mm)。围栏生成侧默认忽略 Z，这里只作记录。</summary>
        public double EntityZ = 0.0;

        /// <summary>圆弧离散：每段弦相对真弧的最大偏差(mm)，按半径自动决定分段数。</summary>
        public double MaxChordDeviation = 5.0;

        /// <summary>圆弧离散兜底：每 90° 的段数上限（半径未知或阈值无效时使用）。</summary>
        public int ArcStepsPerQuarter = 8;
    }

    /// <summary>
    /// 一条 DXF 折线链（纯几何，不依赖 PS SDK，便于独立测试）。
    /// </summary>
    public class FenceDxfPolyline
    {
        public List<double> Xs = new List<double>();
        public List<double> Ys = new List<double>();
        public string Layer;
        public double Length;

        public int SegmentCount { get { return Xs.Count > 0 ? Xs.Count - 1 : 0; } }
    }

    /// <summary>
    /// DXF 读取结果。
    /// </summary>
    public class FenceDxfResult
    {
        public string FilePath;
        public List<FenceDxfPolyline> Polylines = new List<FenceDxfPolyline>();

        public int EntityCount;
        public int LineCount;
        public int PolylineCount;
        public int ArcCount;
        public int UnsupportedCount;

        public int SegmentCount;
        public int DuplicateCount;
        public int IsolatedDroppedCount;
        public int ZeroLengthCount;

        public double TotalLength;
        public double MinX, MinY, MaxX, MaxY;
        public bool HasBounds;

        public List<string> Layers = new List<string>();
        public List<string> Warnings = new List<string>();

        public double Width { get { return HasBounds ? MaxX - MinX : 0.0; } }
        public double Depth { get { return HasBounds ? MaxY - MinY : 0.0; } }
    }

    /// <summary>
    /// DXF 基线读取器。
    ///
    /// 只做几何提取，不依赖 PS SDK：
    ///   1. 读 ASCII DXF 的 ENTITIES 段（按 $DWGCODEPAGE 判编码，回退 GBK / UTF-8）
    ///   2. 支持 LINE、LWPOLYLINE、POLYLINE(+VERTEX)、ARC；圆弧和 bulge 按角度离散成折线
    ///   3. 线段去重（正反向完全重合的视为同一条）
    ///   4. 按端点连通性串成折线链，拐角顺序保持
    ///   5. 丢弃孤立短线（图纸里常见的柱位/符号短线）
    ///
    /// 输出 List&lt;BaseSegmentChain&gt;，与 FenceBaselineReader 的产物同构，
    /// 可直接喂给 FenceLayoutPlanner。
    /// </summary>
    public static class FenceDxfReader
    {
        private const int MaxEntities = 500000;
        private const long MaxFileBytes = 400L * 1024 * 1024;

        private struct DxfPair
        {
            public int Code;
            public string Value;
        }

        private class DxfEntity
        {
            public string Type;
            public List<DxfPair> Pairs = new List<DxfPair>();
            public string Layer = "0";
        }

        private class DxfSeg
        {
            public double X1, Y1, X2, Y2;
            public string Layer;
        }

        private struct PKey : IEquatable<PKey>
        {
            public readonly long X;
            public readonly long Y;

            public PKey(double x, double y, double tol)
            {
                double t = tol > 1e-9 ? tol : 1e-3;
                X = (long)Math.Round(x / t);
                Y = (long)Math.Round(y / t);
            }

            public bool Equals(PKey other) { return X == other.X && Y == other.Y; }
            public override bool Equals(object obj) { return obj is PKey && Equals((PKey)obj); }
            public override int GetHashCode() { return (X.GetHashCode() * 397) ^ Y.GetHashCode(); }
        }

        // ==================== 入口 ====================

        public static FenceDxfResult Read(string path, FenceDxfOptions options, Action<string> log)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("DXF 路径为空");
            if (!File.Exists(path)) throw new FileNotFoundException("找不到 DXF 文件", path);

            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
                throw new InvalidDataException("DXF 文件超过 " + (MaxFileBytes / 1024 / 1024) + " MB，已拒绝解析");

            if (options == null) options = new FenceDxfOptions();

            var result = new FenceDxfResult { FilePath = path };
            log?.Invoke("[DXF] 读取 " + Path.GetFileName(path) + " (" + (info.Length / 1024) + " KB)");

            Encoding enc;
            string text = LoadText(path, out enc, log);
            List<DxfPair> pairs = SplitPairs(text);

            List<DxfEntity> entities = ReadEntities(pairs, log);
            result.EntityCount = entities.Count;
            if (entities.Count == 0)
            {
                result.Warnings.Add("ENTITIES 段为空");
                log?.Invoke("[DXF] WARN ENTITIES 段没有实体");
                return result;
            }

            HashSet<string> layerFilter = BuildLayerFilter(options.LayerFilter);
            List<DxfSeg> segs = ExtractSegments(entities, options, result, layerFilter, log);

            result.SegmentCount = segs.Count;
            log?.Invoke("[DXF] 实体 " + result.EntityCount +
                        " (LINE " + result.LineCount + " / POLYLINE " + result.PolylineCount +
                        " / ARC " + result.ArcCount + " / 跳过 " + result.UnsupportedCount + ")" +
                        " → 线段 " + segs.Count);

            if (segs.Count == 0)
            {
                result.Warnings.Add("没有可用的线/多段线/圆弧");
                log?.Invoke("[DXF] WARN 没有可用的基线线段");
                return result;
            }

            segs = Dedupe(segs, result);
            List<List<DxfSeg>> chains = BuildChains(segs, options.JoinTolerance);

            foreach (List<DxfSeg> chain in chains)
            {
                double len = ChainLength(chain);
                if (options.IsolatedSegmentThreshold > 0 && chain.Count == 1 && len < options.IsolatedSegmentThreshold)
                {
                    result.IsolatedDroppedCount++;
                    continue;
                }

                var poly = new FenceDxfPolyline
                {
                    Layer = chain[0].Layer,
                    Length = len
                };
                poly.Xs.Add(chain[0].X1);
                poly.Ys.Add(chain[0].Y1);
                foreach (DxfSeg s in chain)
                {
                    poly.Xs.Add(s.X2);
                    poly.Ys.Add(s.Y2);
                    ExpandBounds(result, s);
                }
                result.Polylines.Add(poly);
                result.TotalLength += len;
            }

            LogSummary(result, log);
            return result;
        }

        /// <summary>
        /// 把纯几何结果转成 FenceLayoutPlanner 需要的链结构（此方法会用到 PS SDK 的 TxVector）。
        /// </summary>
        public static List<BaseSegmentChain> ToBaseChains(FenceDxfResult result, double groundZ, string namePrefix)
        {
            var list = new List<BaseSegmentChain>();
            if (result == null) return list;

            string prefix = string.IsNullOrEmpty(namePrefix) ? "DXF" : namePrefix;
            for (int ci = 0; ci < result.Polylines.Count; ci++)
            {
                FenceDxfPolyline poly = result.Polylines[ci];
                var bc = new BaseSegmentChain
                {
                    FeatureName = prefix + "#" + ci,
                    GroundZ = groundZ
                };
                for (int i = 0; i + 1 < poly.Xs.Count; i++)
                {
                    bc.Segments.Add(new BaseSegment
                    {
                        Start = new Tecnomatix.Engineering.TxVector(poly.Xs[i], poly.Ys[i], groundZ),
                        End = new Tecnomatix.Engineering.TxVector(poly.Xs[i + 1], poly.Ys[i + 1], groundZ),
                        SourceFeature = prefix,
                        SegmentIndexInFeature = i
                    });
                }
                if (bc.Segments.Count > 0) list.Add(bc);
            }
            return list;
        }

        /// <summary>把链整体平移，用于把图纸坐标拉回原点附近。</summary>
        public static List<BaseSegmentChain> OffsetChains(List<BaseSegmentChain> chains,
            double dx, double dy, string suffix)
        {
            var list = new List<BaseSegmentChain>();
            if (chains == null) return list;
            foreach (BaseSegmentChain src in chains)
            {
                var dst = new BaseSegmentChain
                {
                    FeatureName = string.IsNullOrEmpty(suffix) ? src.FeatureName : src.FeatureName + suffix,
                    GroundZ = src.GroundZ
                };
                foreach (BaseSegment s in src.Segments)
                {
                    dst.Segments.Add(new BaseSegment
                    {
                        Start = new Tecnomatix.Engineering.TxVector(s.Start.X + dx, s.Start.Y + dy, s.Start.Z),
                        End = new Tecnomatix.Engineering.TxVector(s.End.X + dx, s.End.Y + dy, s.End.Z),
                        SourceFeature = s.SourceFeature,
                        SegmentIndexInFeature = s.SegmentIndexInFeature
                    });
                }
                list.Add(dst);
            }
            return list;
        }

        // ==================== 文本与分组码 ====================

        private static string LoadText(string path, out Encoding enc, Action<string> log)
        {
            byte[] bytes = File.ReadAllBytes(path);
            enc = DetectEncoding(bytes);
            log?.Invoke("[DXF] 编码 " + enc.WebName + " (CodePage " + enc.CodePage + ")");
            return enc.GetString(bytes);
        }

        private static Encoding DetectEncoding(byte[] bytes)
        {
            if (bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false);

            int probe = Math.Min(bytes.Length, 16384);
            string head = Encoding.ASCII.GetString(bytes, 0, probe);
            int i = head.IndexOf("$DWGCODEPAGE", StringComparison.Ordinal);
            if (i >= 0)
            {
                int nl = head.IndexOf('\n', i);
                if (nl > 0)
                {
                    string cp = head.Substring(nl + 1).Trim();
                    if (cp.StartsWith("ANSI_", StringComparison.Ordinal))
                    {
                        int code;
                        if (int.TryParse(cp.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                        {
                            try { return Encoding.GetEncoding(code); } catch { }
                        }
                    }
                    else if (cp.IndexOf("UTF", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return new UTF8Encoding(false);
                    }
                }
            }

            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                return new UTF8Encoding(false);
            }
            catch (DecoderFallbackException) { }

            try { return Encoding.GetEncoding(936); } catch { return Encoding.Default; }
        }

        private static List<DxfPair> SplitPairs(string text)
        {
            var list = new List<DxfPair>();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i + 1 < lines.Length; i += 2)
            {
                int code;
                string raw = lines[i].Trim();
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) continue;
                list.Add(new DxfPair { Code = code, Value = lines[i + 1].Trim() });
            }
            return list;
        }

        private static List<DxfEntity> ReadEntities(List<DxfPair> pairs, Action<string> log)
        {
            // 定位 ENTITIES 段
            int start = -1, end = -1;
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Code != 2) continue;
                if (string.Equals(pairs[i].Value, "ENTITIES", StringComparison.OrdinalIgnoreCase) &&
                    i > 0 && pairs[i - 1].Code == 0 &&
                    string.Equals(pairs[i - 1].Value, "SECTION", StringComparison.OrdinalIgnoreCase))
                {
                    start = i + 1;
                    break;
                }
            }
            if (start < 0)
            {
                log?.Invoke("[DXF] WARN 未找到 ENTITIES 段");
                return new List<DxfEntity>();
            }
            for (int i = start; i < pairs.Count; i++)
            {
                if (pairs[i].Code == 0 && string.Equals(pairs[i].Value, "ENDSEC", StringComparison.OrdinalIgnoreCase))
                {
                    end = i;
                    break;
                }
            }
            if (end < 0) end = pairs.Count;

            var entities = new List<DxfEntity>();
            DxfEntity cur = null;
            for (int i = start; i < end && entities.Count < MaxEntities; i++)
            {
                DxfPair p = pairs[i];
                if (p.Code == 0)
                {
                    cur = new DxfEntity { Type = p.Value };
                    entities.Add(cur);
                    continue;
                }
                if (cur == null) continue;
                cur.Pairs.Add(p);
                if (p.Code == 8) cur.Layer = p.Value;
            }
            return entities;
        }

        private static HashSet<string> BuildLayerFilter(string raw)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw)) return set;
            foreach (string part in raw.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim();
                if (t.Length > 0) set.Add(t);
            }
            return set;
        }

        // ==================== 实体 → 线段 ====================

        private static List<DxfSeg> ExtractSegments(List<DxfEntity> entities, FenceDxfOptions opt,
            FenceDxfResult result, HashSet<string> layerFilter, Action<string> log)
        {
            var segs = new List<DxfSeg>();
            var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int ei = 0; ei < entities.Count; ei++)
            {
                DxfEntity e = entities[ei];
                string type = (e.Type ?? "").ToUpperInvariant();

                if (type == "VERTEX" || type == "SEQEND") continue;   // POLYLINE 的子实体，已单独处理

                bool layerOk = layerFilter.Count == 0 || layerFilter.Contains(e.Layer ?? "0");
                if (!string.IsNullOrEmpty(e.Layer)) layers.Add(e.Layer);
                if (!layerOk) continue;

                switch (type)
                {
                    case "LINE":
                        result.LineCount++;
                        EmitLine(e, segs, result);
                        break;

                    case "LWPOLYLINE":
                        result.PolylineCount++;
                        EmitLwPolyline(e, segs, opt, result);
                        break;

                    case "POLYLINE":
                        result.PolylineCount++;
                        EmitPolyline(entities, ref ei, e, segs, opt, result);
                        break;

                    case "ARC":
                        result.ArcCount++;
                        EmitArc(e, segs, opt, result);
                        break;

                    case "CIRCLE":
                    case "ELLIPSE":
                    case "SPLINE":
                    case "HATCH":
                    case "INSERT":
                        result.UnsupportedCount++;
                        AddWarning(result, "跳过不支持的实体 " + type + " (图层 " + e.Layer + ")");
                        break;

                    default:
                        break;   // TEXT/MTEXT/DIMENSION 等与基线无关，静默忽略
                }
            }

            result.Layers.AddRange(layers);
            return segs;
        }

        private static void EmitLine(DxfEntity e, List<DxfSeg> segs, FenceDxfResult result)
        {
            double x1, y1, x2, y2;
            if (!TryGet(e, 10, out x1) || !TryGet(e, 20, out y1) ||
                !TryGet(e, 11, out x2) || !TryGet(e, 21, out y2))
                return;
            AddSeg(segs, x1, y1, x2, y2, e.Layer, result);
        }

        private static void EmitLwPolyline(DxfEntity e, List<DxfSeg> segs, FenceDxfOptions opt, FenceDxfResult result)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            var bulges = new List<double>();

            foreach (DxfPair p in e.Pairs)
            {
                double v;
                if (p.Code == 10)
                {
                    if (!TryParse(p.Value, out v)) continue;
                    xs.Add(v);
                    ys.Add(0.0);
                    bulges.Add(0.0);
                }
                else if (p.Code == 20)
                {
                    if (xs.Count == 0 || !TryParse(p.Value, out v)) continue;
                    ys[ys.Count - 1] = v;
                }
                else if (p.Code == 42)
                {
                    if (bulges.Count == 0 || !TryParse(p.Value, out v)) continue;
                    bulges[bulges.Count - 1] = v;
                }
            }
            if (xs.Count < 2) return;

            bool closed = (GetInt(e, 70) & 1) != 0;
            EmitVertexChain(xs, ys, bulges, closed, e.Layer, segs, opt, result);
        }

        private static void EmitPolyline(List<DxfEntity> entities, ref int ei, DxfEntity poly,
            List<DxfSeg> segs, FenceDxfOptions opt, FenceDxfResult result)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            var bulges = new List<double>();
            string layer = poly.Layer;

            for (int j = ei + 1; j < entities.Count; j++)
            {
                string t = (entities[j].Type ?? "").ToUpperInvariant();
                if (t == "SEQEND") { ei = j; break; }
                if (t != "VERTEX") { ei = j - 1; break; }

                DxfEntity v = entities[j];
                double x, y, b = 0.0;
                if (!TryGet(v, 10, out x) || !TryGet(v, 20, out y)) continue;
                TryGet(v, 42, out b);
                xs.Add(x); ys.Add(y); bulges.Add(b);
                if (string.IsNullOrEmpty(layer)) layer = v.Layer;
            }
            if (xs.Count < 2) return;

            bool closed = (GetInt(poly, 70) & 1) != 0;
            EmitVertexChain(xs, ys, bulges, closed, layer, segs, opt, result);
        }

        /// <summary>顶点序列 → 线段（含 bulge 圆弧离散）。</summary>
        private static void EmitVertexChain(List<double> xs, List<double> ys, List<double> bulges,
            bool closed, string layer, List<DxfSeg> segs, FenceDxfOptions opt, FenceDxfResult result)
        {
            int n = xs.Count;
            int segCount = closed ? n : n - 1;
            for (int i = 0; i < segCount; i++)
            {
                int j = (i + 1) % n;
                double x1 = xs[i], y1 = ys[i], x2 = xs[j], y2 = ys[j];
                double b = (bulges != null && i < bulges.Count) ? bulges[i] : 0.0;

                if (Math.Abs(b) < 1e-9)
                {
                    AddSeg(segs, x1, y1, x2, y2, layer, result);
                }
                else
                {
                    EmitBulgeArc(segs, x1, y1, x2, y2, b, layer, opt, result);
                }
            }
        }

        private static void EmitBulgeArc(List<DxfSeg> segs, double x1, double y1, double x2, double y2,
            double bulge, string layer, FenceDxfOptions opt, FenceDxfResult result)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9) { result.ZeroLengthCount++; return; }

            double theta = 4.0 * Math.Atan(bulge);          // 含角(带符号)
            double sinHalf = Math.Sin(theta / 2.0);
            if (Math.Abs(sinHalf) < 1e-9) { AddSeg(segs, x1, y1, x2, y2, layer, result); return; }

            double r = d / (2.0 * sinHalf);
            double c = r * Math.Cos(theta / 2.0);
            double nx = -dy / d, ny = dx / d;
            double cx = (x1 + x2) / 2.0 + nx * c;
            double cy = (y1 + y2) / 2.0 + ny * c;

            double a0 = Math.Atan2(y1 - cy, x1 - cx);
            int steps = ArcSteps(theta, Math.Abs(r), opt);
            double px = x1, py = y1;
            for (int k = 1; k <= steps; k++)
            {
                double a = a0 + theta * ((double)k / steps);
                double qx = cx + Math.Abs(r) * Math.Cos(a);
                double qy = cy + Math.Abs(r) * Math.Sin(a);
                if (k == steps) { qx = x2; qy = y2; }
                AddSeg(segs, px, py, qx, qy, layer, result);
                px = qx; py = qy;
            }
        }

        private static void EmitArc(DxfEntity e, List<DxfSeg> segs, FenceDxfOptions opt, FenceDxfResult result)
        {
            double cx, cy, r, a0, a1;
            if (!TryGet(e, 10, out cx) || !TryGet(e, 20, out cy) || !TryGet(e, 40, out r)) return;
            if (!TryGet(e, 50, out a0)) a0 = 0.0;
            if (!TryGet(e, 51, out a1)) a1 = 360.0;
            if (r < 1e-6) { result.ZeroLengthCount++; return; }

            double sweep = a1 - a0;
            while (sweep <= 0.0) sweep += 360.0;
            double rad0 = a0 * Math.PI / 180.0;
            double radSweep = sweep * Math.PI / 180.0;

            int steps = ArcSteps(radSweep, r, opt);
            double px = cx + r * Math.Cos(rad0);
            double py = cy + r * Math.Sin(rad0);
            for (int k = 1; k <= steps; k++)
            {
                double a = rad0 + radSweep * ((double)k / steps);
                double qx = cx + r * Math.Cos(a);
                double qy = cy + r * Math.Sin(a);
                AddSeg(segs, px, py, qx, qy, e.Layer, result);
                px = qx; py = qy;
            }
        }

        /// <summary>
        /// 按"最大弦高偏差"决定离散段数；半径过小时退化为每 90° 的固定段数。
        /// 弦高 = R(1-cos(Δ/2))，因此 Δ = 2·acos(1 - tol/R)。
        /// </summary>
        private static int ArcSteps(double sweep, double radius, FenceDxfOptions opt)
        {
            double tol = opt.MaxChordDeviation > 1e-6 ? opt.MaxChordDeviation : 5.0;
            int perQuarter = opt.ArcStepsPerQuarter > 0 ? opt.ArcStepsPerQuarter : 8;
            double absSweep = Math.Abs(sweep);
            double r = Math.Abs(radius);

            double delta;
            if (r <= tol) delta = Math.PI / 2.0 / perQuarter;
            else delta = 2.0 * Math.Acos(Math.Max(-1.0, Math.Min(1.0, 1.0 - tol / r)));

            int steps = delta > 1e-9 ? (int)Math.Ceiling(absSweep / delta) : perQuarter;
            int minSteps = (int)Math.Ceiling(absSweep / (Math.PI / 2.0) * perQuarter);
            if (steps < minSteps) steps = minSteps;
            if (steps < 2) steps = 2;
            if (steps > 2048) steps = 2048;
            return steps;
        }

        private static void AddSeg(List<DxfSeg> segs, double x1, double y1, double x2, double y2,
            string layer, FenceDxfResult result)
        {
            if (Math.Abs(x2 - x1) < 1e-6 && Math.Abs(y2 - y1) < 1e-6)
            {
                result.ZeroLengthCount++;
                return;
            }
            segs.Add(new DxfSeg { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Layer = layer });
        }

        // ==================== 去重 / 成链 ====================

        private static List<DxfSeg> Dedupe(List<DxfSeg> segs, FenceDxfResult result)
        {
            const double tol = 0.001;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<DxfSeg>(segs.Count);

            foreach (DxfSeg s in segs)
            {
                long ax = (long)Math.Round(s.X1 / tol), ay = (long)Math.Round(s.Y1 / tol);
                long bx = (long)Math.Round(s.X2 / tol), by = (long)Math.Round(s.Y2 / tol);
                string k1 = ax + "|" + ay + "|" + bx + "|" + by;
                string k2 = bx + "|" + by + "|" + ax + "|" + ay;
                if (seen.Contains(k1) || seen.Contains(k2))
                {
                    result.DuplicateCount++;
                    continue;
                }
                seen.Add(k1);
                list.Add(s);
            }
            return list;
        }

        private static List<List<DxfSeg>> BuildChains(List<DxfSeg> segs, double joinTol)
        {
            double tol = joinTol > 1e-6 ? joinTol : 0.5;
            var adj = new Dictionary<PKey, List<int>>();
            for (int i = 0; i < segs.Count; i++)
            {
                PKey k1 = new PKey(segs[i].X1, segs[i].Y1, tol);
                PKey k2 = new PKey(segs[i].X2, segs[i].Y2, tol);
                if (!adj.ContainsKey(k1)) adj[k1] = new List<int>();
                if (!adj.ContainsKey(k2)) adj[k2] = new List<int>();
                adj[k1].Add(i);
                adj[k2].Add(i);
            }

            bool[] used = new bool[segs.Count];
            var chains = new List<List<DxfSeg>>();

            // 先走开放链（端点度 != 2 的段），再处理闭环
            var order = new List<int>(segs.Count * 2);
            for (int i = 0; i < segs.Count; i++)
            {
                PKey k1 = new PKey(segs[i].X1, segs[i].Y1, tol);
                PKey k2 = new PKey(segs[i].X2, segs[i].Y2, tol);
                if (adj[k1].Count != 2 || adj[k2].Count != 2) order.Add(i);
            }
            for (int i = 0; i < segs.Count; i++) order.Add(i);

            foreach (int seed in order)
            {
                if (used[seed]) continue;
                chains.Add(WalkChain(segs, adj, used, seed, tol));
            }
            return chains;
        }

        private static List<DxfSeg> WalkChain(List<DxfSeg> segs, Dictionary<PKey, List<int>> adj,
            bool[] used, int seed, double tol)
        {
            used[seed] = true;
            DxfSeg first = segs[seed];

            var fwd = new List<DxfSeg> { first };
            PKey cur = new PKey(first.X2, first.Y2, tol);
            while (true)
            {
                int next = NextUnused(adj, used, cur);
                if (next < 0) break;
                used[next] = true;
                DxfSeg o = Orient(segs[next], cur, tol);
                fwd.Add(o);
                cur = new PKey(o.X2, o.Y2, tol);
            }

            var bwd = new List<DxfSeg>();
            cur = new PKey(first.X1, first.Y1, tol);
            while (true)
            {
                int next = NextUnused(adj, used, cur);
                if (next < 0) break;
                used[next] = true;
                DxfSeg o = Orient(segs[next], cur, tol);
                bwd.Add(new DxfSeg { X1 = o.X2, Y1 = o.Y2, X2 = o.X1, Y2 = o.Y1, Layer = o.Layer });
                cur = new PKey(o.X2, o.Y2, tol);
            }
            bwd.Reverse();
            bwd.AddRange(fwd);
            return bwd;
        }

        private static int NextUnused(Dictionary<PKey, List<int>> adj, bool[] used, PKey key)
        {
            List<int> list;
            if (!adj.TryGetValue(key, out list)) return -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (!used[list[i]]) return list[i];
            }
            return -1;
        }

        private static DxfSeg Orient(DxfSeg seg, PKey from, double tol)
        {
            if (new PKey(seg.X1, seg.Y1, tol).Equals(from)) return seg;
            return new DxfSeg { X1 = seg.X2, Y1 = seg.Y2, X2 = seg.X1, Y2 = seg.Y1, Layer = seg.Layer };
        }

        // ==================== 杂项 ====================

        private static double ChainLength(List<DxfSeg> chain)
        {
            double total = 0.0;
            foreach (DxfSeg s in chain)
            {
                double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1;
                total += Math.Sqrt(dx * dx + dy * dy);
            }
            return total;
        }

        private static void ExpandBounds(FenceDxfResult r, DxfSeg s)
        {
            if (!r.HasBounds)
            {
                r.MinX = Math.Min(s.X1, s.X2); r.MaxX = Math.Max(s.X1, s.X2);
                r.MinY = Math.Min(s.Y1, s.Y2); r.MaxY = Math.Max(s.Y1, s.Y2);
                r.HasBounds = true;
                return;
            }
            r.MinX = Math.Min(r.MinX, Math.Min(s.X1, s.X2));
            r.MaxX = Math.Max(r.MaxX, Math.Max(s.X1, s.X2));
            r.MinY = Math.Min(r.MinY, Math.Min(s.Y1, s.Y2));
            r.MaxY = Math.Max(r.MaxY, Math.Max(s.Y1, s.Y2));
        }

        private static void AddWarning(FenceDxfResult r, string msg)
        {
            if (r.Warnings.Count < 50 && !r.Warnings.Contains(msg)) r.Warnings.Add(msg);
        }

        private static void LogSummary(FenceDxfResult r, Action<string> log)
        {
            if (log == null) return;
            if (r.DuplicateCount > 0) log("[DXF] 去重 " + r.DuplicateCount + " 条重复线段");
            if (r.IsolatedDroppedCount > 0) log("[DXF] 丢弃孤立短线 " + r.IsolatedDroppedCount + " 条");
            if (r.ZeroLengthCount > 0) log("[DXF] 跳过零长度段 " + r.ZeroLengthCount + " 条");
            if (r.Layers.Count > 0) log("[DXF] 图层: " + string.Join(", ", r.Layers.ToArray()));
            if (r.Warnings.Count > 0)
            {
                for (int i = 0; i < r.Warnings.Count && i < 10; i++) log("[DXF] WARN " + r.Warnings[i]);
                if (r.Warnings.Count > 10) log("[DXF] WARN 其余 " + (r.Warnings.Count - 10) + " 条同类提示已省略");
            }

            log("[DXF] 结果: 链 " + r.Polylines.Count + " 条 / 总长 " + (r.TotalLength / 1000.0).ToString("F1") + " m" +
                (r.HasBounds
                    ? "  范围 X " + r.MinX.ToString("F0") + ".." + r.MaxX.ToString("F0") +
                      "  Y " + r.MinY.ToString("F0") + ".." + r.MaxY.ToString("F0")
                    : ""));
            for (int i = 0; i < r.Polylines.Count && i < 20; i++)
            {
                FenceDxfPolyline c = r.Polylines[i];
                log("[DXF]   #" + i + " (图层 " + (string.IsNullOrEmpty(c.Layer) ? "0" : c.Layer) + "): 段 " +
                    c.SegmentCount + " / " + (c.Length / 1000.0).ToString("F2") + " m");
            }
            if (r.Polylines.Count > 20) log("[DXF]   ... 其余 " + (r.Polylines.Count - 20) + " 条链略");
        }

        private static bool TryParse(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static bool TryGet(DxfEntity e, int code, out double v)
        {
            foreach (DxfPair p in e.Pairs)
            {
                if (p.Code == code) return TryParse(p.Value, out v);
            }
            v = 0.0;
            return false;
        }

        private static int GetInt(DxfEntity e, int code)
        {
            foreach (DxfPair p in e.Pairs)
            {
                if (p.Code != code) continue;
                int v;
                if (int.TryParse(p.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            }
            return 0;
        }
    }
}
