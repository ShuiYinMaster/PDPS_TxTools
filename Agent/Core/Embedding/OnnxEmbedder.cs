// TxTools.Agent / Core / Embedding / OnnxEmbedder.cs
//
// 本地 ONNX 嵌入。依赖 NuGet 包 Microsoft.ML.OnnxRuntime(CPU 版即可)。
//
// ── 模型放哪 ──
//   {插件目录}\models\embedding\
//       model.onnx      导出好的模型
//       vocab.txt       配套词表
//
// ── 推荐模型 ──
//   bge-small-zh-v1.5   24M 参数 / 512 维 / onnx 约 90MB(fp32)、25MB(int8)
//   中文技术文档检索够用,内存占用 100~200MB,单条推理几毫秒。
//   别用 7B 那种生成模型来做嵌入 —— 那是另一回事,内存和速度都不可接受。
//
// ── 池化 ──
//   bge 系列官方用 CLS 池化(取第 0 个 token 的隐状态)再 L2 归一化。
//   m3e / text2vec 系列多用 mean 池化。选错会让相似度整体失真但不报错,
//   所以这里做成可配置,默认 CLS。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TxTools.Agent.Core
{
    public enum PoolingMode
    {
        /// <summary>取 [CLS] 位置的隐状态。bge 系列用这个。</summary>
        Cls = 0,
        /// <summary>按 attention_mask 加权平均。m3e / text2vec 系列用这个。</summary>
        Mean = 1
    }

    public sealed class OnnxEmbedder : IEmbedder, IDisposable
    {
        private readonly InferenceSession _session;
        private readonly WordPieceTokenizer _tok;
        private readonly PoolingMode _pooling;
        private readonly string _name;
        private readonly bool _needsTokenType;
        private int _dim;

        // ONNX InferenceSession 与 tokenizer 均非线程安全。KnowledgeIndex 的 BuildAsync 与
        // SearchAsync 可能并发调用 Embed,必须在推理入口串行化,否则会崩溃或返回损坏结果。
        private readonly object _gate = new object();

        public string Id { get { return "onnx:" + _name + ":" + _dim; } }
        public int Dimension { get { return _dim; } }

        /// <summary>本地推理没有网络往返,批大小主要受内存约束。</summary>
        public int BatchSize { get { return 16; } }

        public OnnxEmbedder(string modelPath, string vocabPath,
                            PoolingMode pooling = PoolingMode.Cls, int maxLength = 512)
        {
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("找不到 ONNX 模型: " + modelPath);

            var opts = new SessionOptions();
            // PS 主线程本来就吃 CPU,别让推理把核占满
            opts.IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            _session = new InferenceSession(modelPath, opts);
            _tok = new WordPieceTokenizer(vocabPath) { MaxLength = maxLength };
            _pooling = pooling;
            _name = Path.GetFileNameWithoutExtension(modelPath);

            // 有的导出带 token_type_ids,有的不带 —— 按实际输入签名决定传不传
            _needsTokenType = _session.InputMetadata.ContainsKey("token_type_ids");

            _dim = InferDimension();
        }

        /// <summary>按约定路径查找模型。缺文件返回 null,不抛异常。</summary>
        public static OnnxEmbedder TryCreate(PoolingMode pooling = PoolingMode.Cls)
        {
            try
            {
                var dir = DefaultModelDir();
                var model = Path.Combine(dir, "model.onnx");
                var vocab = Path.Combine(dir, "vocab.txt");
                if (!File.Exists(model) || !File.Exists(vocab)) return null;

                return new OnnxEmbedder(model, vocab, pooling);
            }
            catch (Exception ex)
            {
                try { AuditLog.Write("[warn] [Embed] 本地 ONNX 加载失败: " + ex.Message); } catch { }
                return null;
            }
        }

        public static string DefaultModelDir()
        {
            string pluginDir = null;
            try { pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { }

            if (!string.IsNullOrEmpty(pluginDir))
            {
                var d = Path.Combine(pluginDir, "models", "embedding");
                if (Directory.Exists(d)) return d;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TxTools.Agent", "models", "embedding");
        }

        public Task<List<float[]>> EmbedAsync(IList<string> texts, CancellationToken ct)
        {
            // ONNX 推理是同步的,包一层 Task 满足接口即可 —— 不要用 Task.Run 丢进线程池,
            // 调用方本来就在后台线程,再切一次没有意义。
            return Task.FromResult(Embed(texts, ct));
        }

        public List<float[]> Embed(IList<string> texts, CancellationToken ct)
        {
            var result = new List<float[]>();
            if (texts == null || texts.Count == 0) return result;

            for (int i = 0; i < texts.Count; i += BatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var slice = new List<string>();
                for (int j = i; j < Math.Min(i + BatchSize, texts.Count); j++)
                    slice.Add(texts[j] ?? " ");

                try { result.AddRange(RunBatch(slice)); }
                catch (Exception ex)
                {
                    try { AuditLog.Write("[warn] [Embed] ONNX 批次失败: " + ex.Message); } catch { }
                    for (int k = 0; k < slice.Count; k++) result.Add(null);
                }
            }

            return result;
        }

        private List<float[]> RunBatch(List<string> texts)
        {
            // 串行化共享的非线程安全资源(InferenceSession + tokenizer + _dim 写入)
            lock (_gate)
            {
                long[,] ids, mask;
                int seq;
                _tok.EncodeBatch(texts, out ids, out mask, out seq);

                int batch = texts.Count;

                var idsT = new DenseTensor<long>(Flatten(ids, batch, seq), new[] { batch, seq });
                var maskT = new DenseTensor<long>(Flatten(mask, batch, seq), new[] { batch, seq });

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", idsT),
                    NamedOnnxValue.CreateFromTensor("attention_mask", maskT)
                };

                if (_needsTokenType)
                {
                    var zeros = new long[batch * seq];
                    inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
                        new DenseTensor<long>(zeros, new[] { batch, seq })));
                }

                using (var results = _session.Run(inputs))
                {
                    // 输出名各家不一(last_hidden_state / sentence_embedding / output_0),取第一个
                    var first = results.First();
                    var tensor = first.AsTensor<float>();
                    var dims = tensor.Dimensions.ToArray();

                    // 已经是句向量 [batch, dim]
                    if (dims.Length == 2)
                        return Slice2D(tensor, dims[0], dims[1]);

                    // token 级 [batch, seq, hidden] → 池化
                    if (dims.Length == 3)
                        return Pool(tensor, dims[0], dims[1], dims[2], mask);

                    throw new Exception("无法识别的输出形状:" + string.Join("x", dims));
                }
            }
        }

        private List<float[]> Slice2D(Tensor<float> t, int batch, int dim)
        {
            var outList = new List<float[]>(batch);
            for (int b = 0; b < batch; b++)
            {
                var v = new float[dim];
                for (int d = 0; d < dim; d++) v[d] = t[b, d];
                outList.Add(VectorMath.Normalize(v));
            }
            _dim = dim;
            return outList;
        }

        private List<float[]> Pool(Tensor<float> t, int batch, int seq, int hidden, long[,] mask)
        {
            var outList = new List<float[]>(batch);

            for (int b = 0; b < batch; b++)
            {
                var v = new float[hidden];

                if (_pooling == PoolingMode.Cls)
                {
                    for (int d = 0; d < hidden; d++) v[d] = t[b, 0, d];
                }
                else
                {
                    // 按 mask 加权平均:padding 位置不参与,否则短文本会被稀释
                    int count = 0;
                    for (int s = 0; s < seq; s++)
                    {
                        if (mask[b, s] == 0) continue;
                        count++;
                        for (int d = 0; d < hidden; d++) v[d] += t[b, s, d];
                    }
                    if (count > 0)
                        for (int d = 0; d < hidden; d++) v[d] /= count;
                }

                outList.Add(VectorMath.Normalize(v));
            }

            _dim = hidden;
            return outList;
        }

        private static long[] Flatten(long[,] src, int rows, int cols)
        {
            var flat = new long[rows * cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    flat[r * cols + c] = src[r, c];
            return flat;
        }

        /// <summary>拿一条短文本跑一次,推出实际维度。</summary>
        private int InferDimension()
        {
            try
            {
                var probe = RunBatch(new List<string> { "维度探测" });
                if (probe.Count > 0 && probe[0] != null) return probe[0].Length;
            }
            catch { }
            return 512;
        }

        public void Dispose()
        {
            try { if (_session != null) _session.Dispose(); } catch { }
        }
    }
}
