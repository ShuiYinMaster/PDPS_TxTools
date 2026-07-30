// TxTools.Agent / Core / Embedding / KnowledgeIndex.cs
//
// 知识库的向量索引:持久化 + 混合检索。
//
// ── 为什么是"混合"而不是纯向量 ──
//   向量擅长语义("焊枪装在哪个法兰" ↔ "TCP 与 Toolframe 的挂接关系"),
//   但对精确串很弱 —— 搜 TxWeldOperation、CS1061、KR210 这类型号/API 名,
//   关键字匹配反而更准。技术文档里这两类查询各占一半,所以两路都跑,再融合排名。
//
//   融合用 RRF(Reciprocal Rank Fusion):只看名次不看分数。
//   两路的分值量纲完全不同(余弦相似度 0~1 vs 关键字计数),直接加权相加是错的,
//   归一化又会被离群值带偏。RRF 规避了这个问题,而且没有需要调的权重。
//
// ── 增量 ──
//   每节按内容 hash 记账,只有内容变了才重新嵌入。改一节不会触发全量重算。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    public sealed class IndexedVector
    {
        public string Ref { get; set; }
        public string Hash { get; set; }
        /// <summary>base64(float32)。存数字数组的话文件会大 6~8 倍,读写都慢。</summary>
        public string V { get; set; }
    }

    public sealed class IndexFile
    {
        public string EmbedderId { get; set; }
        public int Dimension { get; set; }
        public DateTime BuiltUtc { get; set; }
        public List<IndexedVector> Vectors { get; set; }

        public IndexFile() { Vectors = new List<IndexedVector>(); }
    }

    public static class KnowledgeIndex
    {
        private const string FileName = "vectors.json";

        private static Dictionary<string, float[]> _mem;
        private static string _memEmbedderId;
        private static readonly object _sync = new object();

        /// <summary>
        /// 当前使用的嵌入器。启动时设一次;为 null 则检索自动退回纯关键字。
        /// 优先本地 ONNX(离线、零成本),没有模型文件再用云端。
        /// </summary>
        public static IEmbedder Embedder { get; set; }

        /// <summary>按可用性挑一个嵌入器。两个都不可用返回 null。</summary>
        public static IEmbedder AutoSelect()
        {
            var local = OnnxEmbedder.TryCreate();
            if (local != null) return local;
            return DashScopeEmbedder.TryCreate();
        }

        public static bool Ready
        {
            get
            {
                if (Embedder == null) return false;
                lock (_sync) { return _mem != null && _mem.Count > 0; }
            }
        }

        private static string IndexPath()
        {
            return Path.Combine(KnowledgeStore.FolderPath(), FileName);
        }

        // ── 构建 ──

        /// <summary>
        /// 增量重建索引。返回新嵌入的节数;-1 表示没有可用的嵌入器。
        /// 换了嵌入器或维度会整份重建 —— 不同模型的向量空间不通用,混着算相似度是错的。
        /// </summary>
        public static async Task<int> BuildAsync(CancellationToken ct, Action<string> progress = null)
        {
            var emb = Embedder;
            if (emb == null) return -1;

            var docs = KnowledgeStore.All();
            var sections = docs.SelectMany(d => d.Sections).ToList();
            if (sections.Count == 0) return 0;

            var old = Load();
            bool sameEmbedder = old != null
                && string.Equals(old.EmbedderId, emb.Id, StringComparison.Ordinal);

            var existing = new Dictionary<string, IndexedVector>(StringComparer.Ordinal);
            if (sameEmbedder)
                foreach (var v in old.Vectors)
                    if (!string.IsNullOrEmpty(v.Ref)) existing[v.Ref] = v;

            var keep = new List<IndexedVector>();
            var todo = new List<KnowledgeSection>();
            var todoText = new List<string>();

            foreach (var sec in sections)
            {
                var text = EmbedText(sec);
                var hash = Hash(text);

                IndexedVector hit;
                if (existing.TryGetValue(sec.Ref, out hit)
                    && string.Equals(hit.Hash, hash, StringComparison.Ordinal))
                {
                    keep.Add(hit);          // 内容没变,复用旧向量
                    continue;
                }

                todo.Add(sec);
                todoText.Add(text);
            }

            if (todo.Count > 0)
            {
                if (progress != null)
                    progress("正在嵌入 " + todo.Count + " 节(复用 " + keep.Count + " 节)…");

                var vecs = await emb.EmbedAsync(todoText, ct).ConfigureAwait(false);

                for (int i = 0; i < todo.Count && i < vecs.Count; i++)
                {
                    if (vecs[i] == null) continue;   // 该节失败,下次重建再试
                    keep.Add(new IndexedVector
                    {
                        Ref = todo[i].Ref,
                        Hash = Hash(todoText[i]),
                        V = VectorMath.ToBase64(vecs[i])
                    });
                }
            }

            var file = new IndexFile
            {
                EmbedderId = emb.Id,
                Dimension = emb.Dimension,
                BuiltUtc = DateTime.UtcNow,
                Vectors = keep
            };

            Save(file);
            LoadIntoMemory(file);

            if (progress != null)
                progress("索引就绪:" + keep.Count + " 节,新增 " + todo.Count);

            return todo.Count;
        }

        /// <summary>
        /// 嵌入用的文本 = 文档名 + 小节标题 + 正文。
        /// 【标题必须带上】只嵌正文的话,"坐标系约定"这个主题信息就丢了,
        /// 而它往往正是用户问题里出现的词。
        /// </summary>
        private static string EmbedText(KnowledgeSection sec)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(sec.Doc)) sb.Append(sec.Doc).Append(" / ");
            // 面包屑必须进嵌入文本:「已知问题」这种标题本身没有语义,
            // 带上「工艺设计器接口」之后,向量才落在正确的主题区域
            if (!string.IsNullOrEmpty(sec.Path)) sb.Append(sec.Path).Append(" / ");
            if (!string.IsNullOrEmpty(sec.Heading)) sb.Append(sec.Heading).Append('\n');
            sb.Append(sec.Body);
            return sb.ToString();
        }

        // ── 检索 ──

        public sealed class Hit
        {
            public KnowledgeSection Section;
            public double Score;
            public string How;      // vector / keyword / both
            public List<string> Lines = new List<string>();
        }

        /// <summary>
        /// 混合检索。向量不可用时自动退回纯关键字,不报错。
        /// </summary>
        public static async Task<List<Hit>> SearchAsync(
            string query, string[] keywords, int max, CancellationToken ct)
        {
            var kw = KnowledgeStore.Search(keywords, max * 3);

            List<KeyValuePair<string, double>> vec = null;
            if (Embedder != null)
            {
                try { vec = await VectorSearchAsync(query, max * 3, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    try { AuditLog.Write("[warn] [Embed] 向量检索失败，退回关键字: " + ex.Message); }
                    catch { }
                }
            }

            if (vec == null || vec.Count == 0)
            {
                return kw.Take(max).Select(h => new Hit
                {
                    Section = h.Section,
                    Score = h.Score,
                    How = "keyword",
                    Lines = h.Lines
                }).ToList();
            }

            // ── RRF 融合 ──
            // 名次倒数相加。k=60 是常用取值:压低头部差距,让两路都有话语权。
            const double k = 60.0;
            var fused = new Dictionary<string, Hit>(StringComparer.Ordinal);
            // 【不要用 ToDictionary】Ref 理论上唯一,但一旦分节逻辑有疏漏就会撞车,
            // ToDictionary 会直接抛 ArgumentException 让整个检索崩掉 ——
            // 索引撞车最多是少召回一节,不该升级成功能不可用。
            var byRef = new Dictionary<string, KnowledgeSection>(StringComparer.Ordinal);
            int dupes = 0;
            foreach (var sec in AllSections())
            {
                if (sec == null || string.IsNullOrEmpty(sec.Ref)) continue;
                if (byRef.ContainsKey(sec.Ref)) { dupes++; continue; }
                byRef[sec.Ref] = sec;
            }
            if (dupes > 0)
            {
                try { AuditLog.Write("[warn] [Knowledge] 有 " + dupes + " 个小节 Ref 重复，已跳过"); }
                catch { }
            }

            for (int i = 0; i < kw.Count; i++)
            {
                var h = Get(fused, byRef, kw[i].Section.Ref);
                if (h == null) continue;
                h.Score += 1.0 / (k + i + 1);
                h.How = "keyword";
                h.Lines = kw[i].Lines;
            }

            for (int i = 0; i < vec.Count; i++)
            {
                var h = Get(fused, byRef, vec[i].Key);
                if (h == null) continue;
                h.Score += 1.0 / (k + i + 1);
                h.How = h.How == "keyword" ? "both" : "vector";
            }

            return fused.Values.OrderByDescending(x => x.Score).Take(max).ToList();
        }

        private static Hit Get(Dictionary<string, Hit> map,
                               Dictionary<string, KnowledgeSection> byRef, string reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;

            Hit h;
            if (map.TryGetValue(reference, out h)) return h;

            KnowledgeSection sec;
            if (!byRef.TryGetValue(reference, out sec)) return null;

            h = new Hit { Section = sec, Score = 0 };
            map[reference] = h;
            return h;
        }

        private static async Task<List<KeyValuePair<string, double>>> VectorSearchAsync(
            string query, int max, CancellationToken ct)
        {
            EnsureLoaded();

            Dictionary<string, float[]> snapshot;
            lock (_sync)
            {
                if (_mem == null || _mem.Count == 0) return null;
                if (!string.Equals(_memEmbedderId, Embedder.Id, StringComparison.Ordinal))
                {
                    // 索引是别的模型建的,向量空间不通用 —— 宁可不用也不能混着算
                    try { AuditLog.Write("[warn] [Embed] 索引与当前嵌入器不匹配，需重建"); } catch { }
                    return null;
                }
                snapshot = _mem;
            }

            var qv = await Embedder.EmbedAsync(new List<string> { query }, ct).ConfigureAwait(false);
            if (qv.Count == 0 || qv[0] == null) return null;

            var q = qv[0];
            var scored = new List<KeyValuePair<string, double>>(snapshot.Count);
            foreach (var kv in snapshot)
                scored.Add(new KeyValuePair<string, double>(kv.Key, VectorMath.Dot(q, kv.Value)));

            return scored.OrderByDescending(x => x.Value).Take(max).ToList();
        }

        private static IEnumerable<KnowledgeSection> AllSections()
        {
            return KnowledgeStore.All().SelectMany(d => d.Sections);
        }

        // ── 持久化 ──

        private static IndexFile Load()
        {
            try
            {
                var p = IndexPath();
                if (!File.Exists(p)) return null;
                return JsonConvert.DeserializeObject<IndexFile>(File.ReadAllText(p, Encoding.UTF8));
            }
            catch { return null; }
        }

        private static void Save(IndexFile f)
        {
            try
            {
                var p = IndexPath();
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(p, JsonConvert.SerializeObject(f), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try { AuditLog.Write("[warn] [Embed] 索引保存失败: " + ex.Message); } catch { }
            }
        }

        private static void EnsureLoaded()
        {
            lock (_sync) { if (_mem != null) return; }
            var f = Load();
            if (f != null) LoadIntoMemory(f);
        }

        private static void LoadIntoMemory(IndexFile f)
        {
            var map = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var v in f.Vectors)
            {
                var arr = VectorMath.FromBase64(v.V);
                if (arr != null && !string.IsNullOrEmpty(v.Ref)) map[v.Ref] = arr;
            }
            lock (_sync)
            {
                _mem = map;
                _memEmbedderId = f.EmbedderId;
            }
        }

        public static void Invalidate()
        {
            lock (_sync) { _mem = null; _memEmbedderId = null; }
        }

        private static string Hash(string text)
        {
            using (var md5 = MD5.Create())
            {
                var b = md5.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                return BitConverter.ToString(b).Replace("-", "").Substring(0, 16);
            }
        }
    }
}
