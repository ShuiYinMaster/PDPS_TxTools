// TxTools.Agent / Core / DeepSeekClient.cs
// 直连 OpenAI 兼容 /v1/chat/completions 的薄客户端 (baseUrl 可配置)。
// 类名沿用历史命名,内部完全 provider 中立 —— 换用 DeepSeek / Kimi / Qwen / OpenAI / Ollama 
// 只需构造时传对应 baseUrl。网络要求: 目标 host 出站 HTTPS(Ollama 是本地 HTTP)可达。

using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class DeepSeekClient
    {
        public const string DefaultBaseUrl = "https://api.deepseek.com";

        private static readonly HttpClient Http = CreateHttp();
        private static readonly JsonSerializerSettings JsonSettings =
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };

        private readonly string _apiKey;
        /// <summary>完整 endpoint,如 https://api.deepseek.com/v1/chat/completions</summary>
        private readonly string _endpoint;
        /// <summary>模型列表 endpoint,如 https://api.deepseek.com/v1/models</summary>
        private readonly string _modelsEndpoint;

        static DeepSeekClient()
        {
            // .NET Framework 默认可能不启用 TLS1.2,否则握手直接失败。
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public DeepSeekClient(string apiKey) : this(apiKey, DefaultBaseUrl) { }

        public DeepSeekClient(string apiKey, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key 不能为空。", nameof(apiKey));
            _apiKey = apiKey.Trim();
            var b = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
            _endpoint = b + "/v1/chat/completions";
            _modelsEndpoint = b + "/v1/models";
        }

        /// <summary>
        /// 拉取当前 provider 真实的模型列表(OpenAI 兼容: GET /v1/models → {data: [{id: "..."}, ...]})。
        /// 五家 DeepSeek/Kimi/Qwen/OpenAI/Ollama 都支持。抛异常表示 API 不响应或返回格式不合规。
        /// </summary>
        public async Task<List<string>> ListModelsAsync(CancellationToken ct)
        {
            using (var msg = new HttpRequestMessage(HttpMethod.Get, _modelsEndpoint))
            {
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using (var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ct))
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                        throw new LlmApiException((int)resp.StatusCode, ExtractError(body, resp.StatusCode), body);

                    var root = JObject.Parse(body);
                    var data = root["data"] as JArray;
                    if (data == null) return new List<string>();

                    var list = new List<string>();
                    foreach (var item in data)
                    {
                        var id = (string)item["id"];
                        if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
                    }
                    return list;
                }
            }
        }

        public async Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            try
            {
                return await SendOnceAsync(request, ct);
            }
            catch (LlmApiException ex) when (ShouldRetryWithoutTemperature(ex, request))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[DeepSeekClient] 400 temperature 不支持,自动去掉 temperature 重试: " + ex.Message);
                request.Temperature = null;
                return await SendOnceAsync(request, ct);
            }
        }

        private async Task<ChatResponse> SendOnceAsync(ChatRequest request, CancellationToken ct)
        {
            var json = JsonConvert.SerializeObject(request, JsonSettings);

            using (var msg = new HttpRequestMessage(HttpMethod.Post, _endpoint))
            {
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                msg.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ct))
                {
                    var body = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                        throw new LlmApiException((int)resp.StatusCode, ExtractError(body, resp.StatusCode), body);

                    var parsed = JsonConvert.DeserializeObject<ChatResponse>(body);
                    if (parsed == null || parsed.Choices == null || parsed.Choices.Count == 0)
                        throw new LlmApiException((int)resp.StatusCode, "API 响应为空或无 choices。", body);
                    return parsed;
                }
            }
        }

        /// <summary>流式发送：边收边回调文本分片，结束后返回拼装好的 assistant 消息(含 tool_calls)。
        /// 最后一个 SSE 包中的 usage 字段会写入 outUsage（如不为 null）。</summary>
        public async Task<ChatMessage> SendStreamAsync(ChatRequest request, Action<string> onTextDelta,
            CancellationToken ct, Action<TokenUsage> onUsage = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            try
            {
                return await SendStreamOnceAsync(request, onTextDelta, ct, onUsage);
            }
            catch (LlmApiException ex) when (ShouldRetryWithoutTemperature(ex, request))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[DeepSeekClient] 400 temperature 不支持,自动去掉 temperature 重试: " + ex.Message);
                request.Temperature = null;
                return await SendStreamOnceAsync(request, onTextDelta, ct, onUsage);
            }
        }

        private async Task<ChatMessage> SendStreamOnceAsync(ChatRequest request, Action<string> onTextDelta,
            CancellationToken ct, Action<TokenUsage> onUsage)
        {
            request.Stream = true;
            var json = JsonConvert.SerializeObject(request, JsonSettings);

            using (var msg = new HttpRequestMessage(HttpMethod.Post, _endpoint))
            {
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                msg.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        throw new LlmApiException((int)resp.StatusCode, ExtractError(body, resp.StatusCode), body);
                    }

                    var content = new StringBuilder();
                    var toolAcc = new SortedDictionary<int, ToolCallAcc>();
                    TokenUsage lastUsage = null;

                    using (var stream = await resp.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (line.Length == 0 || !line.StartsWith("data:")) continue;

                            var data = line.Substring(5).Trim();
                            if (data == "[DONE]") break;

                            JObject chunk;
                            try { chunk = JObject.Parse(data); } catch { continue; }

                            // 顶层 usage（DeepSeek 在末尾 chunk 带回）
                            var usageTok = chunk["usage"];
                            if (usageTok != null && usageTok.Type == JTokenType.Object)
                            {
                                try { lastUsage = usageTok.ToObject<TokenUsage>(); } catch { }
                            }

                            var choices = chunk["choices"] as JArray;
                            if (choices == null || choices.Count == 0) continue;
                            var delta = choices[0]["delta"] as JObject;
                            if (delta == null) continue;

                            var ctok = delta["content"];
                            if (ctok != null && ctok.Type == JTokenType.String)
                            {
                                var frag = (string)ctok;
                                if (frag.Length > 0) { content.Append(frag); if (onTextDelta != null) onTextDelta(frag); }
                            }

                            var tcs = delta["tool_calls"] as JArray;
                            if (tcs != null)
                                foreach (var tc in tcs)
                                {
                                    int idx = tc["index"] != null ? (int)tc["index"] : 0;
                                    ToolCallAcc acc;
                                    if (!toolAcc.TryGetValue(idx, out acc)) { acc = new ToolCallAcc(); toolAcc[idx] = acc; }
                                    if (tc["id"] != null && tc["id"].Type == JTokenType.String) acc.Id = (string)tc["id"];
                                    var fn = tc["function"] as JObject;
                                    if (fn != null)
                                    {
                                        if (fn["name"] != null && fn["name"].Type == JTokenType.String) acc.Name = (string)fn["name"];
                                        if (fn["arguments"] != null && fn["arguments"].Type == JTokenType.String) acc.Args.Append((string)fn["arguments"]);
                                    }
                                }
                        }
                    }

                    // 回调 usage
                    if (lastUsage != null && onUsage != null) onUsage(lastUsage);

                    var message = new ChatMessage("assistant", content.Length > 0 ? content.ToString() : null);
                    if (toolAcc.Count > 0)
                    {
                        message.ToolCalls = new List<ToolCall>();
                        foreach (var kv in toolAcc)
                            message.ToolCalls.Add(new ToolCall
                            {
                                Id = kv.Value.Id,
                                Type = "function",
                                Function = new FunctionCall { Name = kv.Value.Name, Arguments = kv.Value.Args.ToString() }
                            });
                    }
                    return message;
                }
            }
        }

        private sealed class ToolCallAcc
        {
            public string Id;
            public string Name;
            public readonly StringBuilder Args = new StringBuilder();
        }

        /// <summary>
        /// 判断是否应该"去掉 temperature 参数后重试":
        ///   - HTTP 400 (invalid_request_error)
        ///   - 错误消息含 "temperature"
        ///   - 当前 request 确实带了 temperature (未曾发生过重试)
        /// 已知触发场景:
        ///   - Kimi(Moonshot) k2 系列: "invalid temperature: only 1 is allowed for this model"
        ///   - OpenAI o1/o3 系列:      "'temperature' does not support 0.3 with this model. Only ..."
        /// 宽松匹配 —— 只要 400 + msg 含 temperature 就重试,不追具体表述,避免 provider 措辞变化。
        /// </summary>
        private static bool ShouldRetryWithoutTemperature(LlmApiException ex, ChatRequest request)
        {
            if (ex.StatusCode != 400) return false;
            if (request == null || !request.Temperature.HasValue) return false;
            var msg = ex.Message ?? "";
            return msg.IndexOf("temperature", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractError(string body, HttpStatusCode code)
        {
            try
            {
                var root = JObject.Parse(body);
                var err = root["error"];
                if (err != null)
                {
                    var message = (string)err["message"];
                    var type = (string)err["type"];
                    return $"[{(int)code} {type}] {message}";
                }
            }
            catch { /* 落到通用信息 */ }
            return $"[{(int)code}] {body}";
        }

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient();
            h.Timeout = TimeSpan.FromMinutes(5);
            return h;
        }
    }

    public sealed class LlmApiException : Exception
    {
        public int StatusCode { get; }
        public string RawBody { get; }

        public LlmApiException(int statusCode, string message, string rawBody) : base(message)
        {
            StatusCode = statusCode;
            RawBody = rawBody;
        }
    }
}