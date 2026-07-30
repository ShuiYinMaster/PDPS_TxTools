// TxTools.Agent / Core / Embedding / DashScopeEmbedder.cs
//
// 百炼(DashScope)文本嵌入。走 OpenAI 兼容端点 /compatible-mode/v1/embeddings,
// 和聊天用的是同一把 key。
//
// 成本参考(2026-08):文本 0.0007元/千 token,新开通有 100 万 token 免费额度(90 天)。
// 一份十几万字的知识库全量嵌入一次不到一毛钱,而且只有文档改动时才重算;
// 查询侧每次一个短句,基本可以忽略。
//
// 【维度选大不一定更好】dimensions 可选 1024/768/512 等。
// 768 对中文技术文档已经够用,存储和点积开销比 2560 小一大截。
// 改了维度要重建索引 —— Id 里带了维度,KnowledgeIndex 会自动识别并提示重建。

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class DashScopeEmbedder : IEmbedder
    {
        private static readonly HttpClient Http = CreateHttp();

        private readonly string _apiKey;
        private readonly string _endpoint;
        private readonly string _model;
        private readonly int _dim;

        public string Id { get { return "dashscope:" + _model + ":" + _dim; } }
        public int Dimension { get { return _dim; } }

        /// <summary>百炼文本嵌入单次上限 10 条。超了会 400。</summary>
        public int BatchSize { get { return 10; } }

        /// <param name="model">
        /// 文本嵌入模型名。默认 text-embedding-v4。
        /// 【上线前用 ListModelsAsync 核对一次】模型名迭代很快,写死容易过期。
        /// </param>
        /// <param name="dimension">向量维度，需为该模型支持的值。</param>
        public DashScopeEmbedder(string apiKey, string baseUrl = null,
                                 string model = "text-embedding-v4", int dimension = 768)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("百炼 API key 不能为空。", "apiKey");

            _apiKey = apiKey.Trim();
            _model = string.IsNullOrWhiteSpace(model) ? "text-embedding-v4" : model.Trim();
            _dim = dimension > 0 ? dimension : 768;

            var b = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://dashscope.aliyuncs.com/compatible-mode"
                : baseUrl.Trim().TrimEnd('/');
            _endpoint = b + "/v1/embeddings";
        }

        /// <summary>用当前配置的 qwen key 构造。没配 key 返回 null。</summary>
        public static DashScopeEmbedder TryCreate(string model = null, int dimension = 768)
        {
            try
            {
                var key = ModelRouter.GetKey("qwen");
                if (string.IsNullOrEmpty(key)) return null;

                string baseUrl = null;
                try
                {
                    var p = LlmProviders.ById("qwen");
                    if (p != null) baseUrl = p.BaseUrl;
                }
                catch { }

                return new DashScopeEmbedder(key, baseUrl,
                    string.IsNullOrWhiteSpace(model) ? "text-embedding-v4" : model, dimension);
            }
            catch { return null; }
        }

        public async Task<List<float[]>> EmbedAsync(IList<string> texts, CancellationToken ct)
        {
            var result = new List<float[]>();
            if (texts == null || texts.Count == 0) return result;

            for (int i = 0; i < texts.Count; i += BatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var slice = new List<string>();
                for (int j = i; j < Math.Min(i + BatchSize, texts.Count); j++)
                    slice.Add(Clip(texts[j]));

                List<float[]> part;
                try
                {
                    part = await EmbedBatchAsync(slice, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 单批失败不中断整次重建 —— 补 null 占位,上层会跳过这些块
                    try { AuditLog.Write("[warn] [Embed] 批次失败(" + slice.Count + " 条): " + ex.Message); }
                    catch { }

                    // 授权类错误重试多少次都是同样结果,直接中止整次嵌入,
                    // 不要拿几百个批次去反复撞同一堵墙
                    var apiEx = ex as LlmApiException;
                    if (apiEx != null && (apiEx.StatusCode == 401 || apiEx.StatusCode == 403))
                        throw;
                    part = new List<float[]>();
                    for (int k = 0; k < slice.Count; k++) part.Add(null);
                }

                result.AddRange(part);
            }

            return result;
        }

        private async Task<List<float[]>> EmbedBatchAsync(List<string> slice, CancellationToken ct)
        {
            var body = new JObject
            {
                ["model"] = _model,
                ["input"] = new JArray(slice.ToArray()),
                ["encoding_format"] = "float"
            };
            // 部分模型不认 dimensions,失败时下面会去掉重试
            body["dimensions"] = _dim;

            string json;
            try { json = await PostAsync(body, ct).ConfigureAwait(false); }
            catch (LlmApiException ex) when (ex.StatusCode == 400 &&
                (ex.Message ?? "").IndexOf("dimension", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                body.Remove("dimensions");
                json = await PostAsync(body, ct).ConfigureAwait(false);
            }

            var root = JObject.Parse(json);
            var data = root["data"] as JArray;
            if (data == null) throw new Exception("响应里没有 data 字段。");

            // 结果不保证按 index 有序,按 index 归位
            var byIndex = new Dictionary<int, float[]>();
            foreach (var item in data)
            {
                var arr = item["embedding"] as JArray;
                if (arr == null) continue;

                var v = new float[arr.Count];
                for (int i = 0; i < arr.Count; i++) v[i] = (float)arr[i];

                int idx = item["index"] != null ? (int)item["index"] : byIndex.Count;
                byIndex[idx] = VectorMath.Normalize(v);
            }

            var outList = new List<float[]>(slice.Count);
            for (int i = 0; i < slice.Count; i++)
            {
                float[] v;
                outList.Add(byIndex.TryGetValue(i, out v) ? v : null);
            }
            return outList;
        }

        private async Task<string> PostAsync(JObject body, CancellationToken ct)
        {
            using (var msg = new HttpRequestMessage(HttpMethod.Post, _endpoint))
            {
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                msg.Content = new StringContent(
                    JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

                using (var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ct)
                                            .ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new LlmApiException((int)resp.StatusCode, ExtractError(text, resp.StatusCode), text);
                    return text;
                }
            }
        }

        /// <summary>超长文本截断。文本嵌入上限通常 8k token,中文按 1.5 字/token 保守估。</summary>
        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return " ";   // 空串会被 API 拒绝
            const int maxChars = 6000;
            return s.Length <= maxChars ? s : s.Substring(0, maxChars);
        }

        private string ExtractError(string body, HttpStatusCode code)
        {
            string msg = null, errCode = null;
            try
            {
                var root = JObject.Parse(body);
                var err = root["error"];
                if (err != null)
                {
                    msg = (string)err["message"];
                    errCode = (string)err["code"];
                }
                if (msg == null) msg = (string)root["message"];
                if (errCode == null) errCode = (string)root["code"];
            }
            catch { }

            if (string.IsNullOrEmpty(msg)) msg = body;

            var head = "[" + (int)code + (string.IsNullOrEmpty(errCode) ? "" : " " + errCode) + "] " + msg;

            // 403 在百炼上几乎总是"模型没授权",而不是 key 无效 ——
            // key 无效会是 401。直接把排查步骤写出来,省得回控制台猜。
            if ((int)code == 403)
                return head
                     + "\n\n模型 \"" + _model + "\" 未获授权。请检查两处(它们是独立的两层):"
                     + "\n  1) 业务空间 → 模型授权:嵌入模型与对话模型分开授权，"
                     + "只授权了对话模型不会自动包含 text-embedding-*"
                     + "\n  2) API-KEY 管理 → 该 key 的可调模型限制:百炼支持给单把 key 单独设白名单"
                     + "\n授权后无需改代码，重试即可。";

            if ((int)code == 401)
                return head + "\n\nAPI key 无效或已过期，请到百炼控制台重新生成。";

            if ((int)code == 404)
                return head + "\n\n模型名 \"" + _model + "\" 可能已过期。"
                            + "用 ListModelsAsync 拉一次当前可用列表核对。";

            return head;
        }

        private static HttpClient CreateHttp()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var h = new HttpClient();
            h.Timeout = TimeSpan.FromMinutes(3);
            return h;
        }
    }
}