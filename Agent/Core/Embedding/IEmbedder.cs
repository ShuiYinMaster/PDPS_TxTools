// TxTools.Agent / Core / Embedding / IEmbedder.cs
//
// 嵌入(向量化)的统一契约。两套实现:
//   DashScopeEmbedder —— 云端,零部署,按量计费(百炼文本嵌入 0.0007元/千 token)
//   OnnxEmbedder      —— 本地 ONNX Runtime,离线可用,首次需放模型文件
//
// 检索逻辑只依赖本接口,换实现不用动 KnowledgeIndex。
//
// ── 约定:向量一律 L2 归一化后返回 ──
// 归一化之后余弦相似度就等于点积,检索时省掉每次算模长,几万条向量的差别很明显。
// 各实现自己负责归一化,不要把这件事推给调用方。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TxTools.Agent.Core
{
    public interface IEmbedder
    {
        /// <summary>标识,写进索引文件。换了模型或维度时用它判断缓存是否失效。</summary>
        string Id { get; }

        /// <summary>向量维度。</summary>
        int Dimension { get; }

        /// <summary>单次请求允许的最大条数。</summary>
        int BatchSize { get; }

        /// <summary>
        /// 批量嵌入。返回顺序与入参一致;某条失败时对应位置为 null,不要整批丢弃 ——
        /// 知识库嵌入是长任务,一条超长文本不该让整次重建白跑。
        /// </summary>
        Task<List<float[]>> EmbedAsync(IList<string> texts, CancellationToken ct);
    }

    public static class VectorMath
    {
        /// <summary>就地 L2 归一化。零向量原样返回。</summary>
        public static float[] Normalize(float[] v)
        {
            if (v == null || v.Length == 0) return v;

            double sum = 0;
            for (int i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];

            var norm = Math.Sqrt(sum);
            if (norm < 1e-12) return v;

            for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
            return v;
        }

        /// <summary>点积。两侧都已归一化时即余弦相似度,范围约 [-1, 1]。</summary>
        public static double Dot(float[] a, float[] b)
        {
            if (a == null || b == null) return 0;
            int n = Math.Min(a.Length, b.Length);
            double s = 0;
            for (int i = 0; i < n; i++) s += (double)a[i] * b[i];
            return s;
        }

        // ── 序列化:base64(float32 小端) ──
        //
        // 存 JSON 数字数组的话,768 维一条要 6~8KB,几百条就几 MB,读写都慢。
        // base64 后是 4KB 出头,而且解析是纯字节拷贝,没有文本转数字的开销。

        public static string ToBase64(float[] v)
        {
            if (v == null || v.Length == 0) return "";
            var bytes = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        public static float[] FromBase64(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                var bytes = Convert.FromBase64String(s);
                var v = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, v, 0, v.Length * 4);
                return v;
            }
            catch { return null; }
        }
    }
}
