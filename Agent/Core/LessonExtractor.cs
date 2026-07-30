// TxTools.Agent / Core / LessonExtractor.cs
// 对话末的"经验萃取"：把 _fullHistory 交给专用 prompt,让 LLM 结构化输出
//   { facts: [...], gotchas: [{signature, correction}, ...] }
// 分别落入 FactsStore / GotchasStore。独立一次 API 调用,不占用主对话上下文。
//
// 触发时机(建议):
//   (1) UI 层"结束对话/切换对话"前调用 AgentLoop.ExtractLessonsAsync
//   (2) 对话消息数超过阈值(如 20 条)时后台触发一次
//   (3) 用户显式点 "记住我们这次的结论" 按钮
//
// 用便宜模型(deepseek-v4-flash)即可,萃取任务对推理能力要求不高。
//
// 【前缀续写】末尾预填一条 prefix=true 的 assistant 消息("{"),模型只能接着补 JSON,
// 不会再包 ```json 围栏或写解释,"萃取输出非合法 JSON" 这类失败基本消失。
// 该特性需要 beta 端点:构造 DeepSeekClient 时 baseUrl 传 https://api.deepseek.com/beta。
// 端点不对时 API 会忽略或拒绝 prefix 字段 —— 此时 StripCodeFence + RestorePrefix
// 仍能兜住普通输出,不会退化,只是少了这层保障。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class LessonExtractor
    {
        private readonly DeepSeekClient _client;
        private readonly string _model;

        public LessonExtractor(DeepSeekClient client, string model = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _model = string.IsNullOrEmpty(model) ? "deepseek-v4-flash" : model;
        }

        public sealed class ExtractResult
        {
            public int FactsAdded { get; set; }
            public int GotchaCorrectionsAdded { get; set; }
            public string RawJson { get; set; }
            public string Error { get; set; }
        }

        public async Task<ExtractResult> ExtractAsync(
            string convId, IEnumerable<ChatMessage> fullHistory, CancellationToken ct)
        {
            var result = new ExtractResult();
            var history = fullHistory != null ? fullHistory.ToList() : new List<ChatMessage>();
            if (history.Count < 3)
            {
                result.Error = "对话太短，无需萃取。";
                return result;
            }

            var transcript = BuildTranscript(history);
            if (transcript.Length < 100)
            {
                result.Error = "对话内容不足。";
                return result;
            }

            var extractPrompt = BuildExtractPrompt(transcript);

            try
            {
                var req = new ChatRequest
                {
                    Model = _model,
                    MaxTokens = 2048,
                    Temperature = 0.1,
                    Stream = false,
                    Messages = new List<ChatMessage>
                    {
                        new ChatMessage("system",
                            "你是对话经验萃取器。仅输出严格 JSON，不要 markdown 代码块围栏，不要额外说明。"),
                        new ChatMessage("user", extractPrompt),
                        // 前缀续写(Prefix Completion):预填一个 "{" 作为 assistant 开头,
                        // 模型只能接着往下补 JSON,不会再自作主张包 ```json 围栏或写解释。
                        // 需要 API 支持该特性;不支持时该消息会被当成普通 assistant 轮,
                        // 输出仍能被 StripCodeFence + RepairJson 兜住,不会退化。
                        new ChatMessage("assistant", "{") { Prefix = true }
                    }
                };

                var resp = await _client.SendAsync(req, ct);
                var content = resp != null && resp.Choices != null && resp.Choices.Count > 0
                    ? (resp.Choices[0].Message != null ? resp.Choices[0].Message.Content : "")
                    : "";
                result.RawJson = content;

                // 前缀续写生效时模型的输出不含开头的 "{",要补回来
                var json = RestorePrefix(StripCodeFence(content));
                JObject obj;
                try { obj = JObject.Parse(json); }
                catch (Exception parseEx)
                {
                    result.Error = "萃取输出非合法 JSON: " + parseEx.Message;
                    return result;
                }

                // facts
                var facts = obj["facts"] as JArray;
                if (facts != null)
                    foreach (var f in facts)
                    {
                        var text = (string)f["content"];
                        var cat = (string)f["category"] ?? "misc";
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            FactsStore.Add(text, cat, convId);
                            result.FactsAdded++;
                        }
                    }

                // gotchas 正解补充
                var gotchas = obj["gotchas"] as JArray;
                if (gotchas != null)
                    foreach (var g in gotchas)
                    {
                        var sig = (string)g["signature"];
                        var correction = (string)g["correction"];
                        if (!string.IsNullOrWhiteSpace(sig) && !string.IsNullOrWhiteSpace(correction))
                        {
                            if (GotchasStore.AddCorrection(sig, correction))
                                result.GotchaCorrectionsAdded++;
                        }
                    }

                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        // ── 提示词 & 对话摘录构造 ──

        private static string BuildExtractPrompt(string transcript)
        {
            var sb = new StringBuilder();
            sb.AppendLine("以下是我(Claude)与用户在 Process Simulate 内的一段对话。");
            sb.AppendLine("请从中提取可跨对话复用的知识,严格按下面的 JSON schema 输出(仅 JSON,不加任何解释):");
            sb.AppendLine();
            sb.AppendLine("{");
            sb.AppendLine("  \"facts\": [");
            sb.AppendLine("    { \"content\": \"简明陈述,20-80字\", \"category\": \"preference|scene_constant|api_fact|workflow\" }");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"gotchas\": [");
            sb.AppendLine("    { \"signature\": \"CS0117:类型.成员 或 异常类型:类型.成员\", \"correction\": \"正确写法,附最小示例代码\" }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("提取原则:");
            sb.AppendLine("- 宁缺毋滥,只提取“确定成立、值得下次复用”的信息;拿不准就不写。");
            sb.AppendLine("- facts 类别说明:");
            sb.AppendLine("  * preference: 用户偏好(例: 用户偏好复用现有工具而非 run_csharp)");
            sb.AppendLine("  * scene_constant: 明确说出的场景常量(例: 当前场景机器人 8 台均为 KR210_L150)");
            sb.AppendLine("  * api_fact: 已通过实际调用验证的 PS SDK 事实(例: TxCollisionRoot.Pairs 是集合入口)");
            sb.AppendLine("  * workflow: 完整走通过一次的稳定多步流程");
            sb.AppendLine("- gotchas 只写“对话中已经确认了正确写法”的项,没有正确写法就不放。");
            sb.AppendLine("  signature 要与错误消息里的 CS 号或异常类型对齐,让下次能匹配到。");
            sb.AppendLine("- 每个数组最多 10 条。没有可提取的对应数组为 []。");
            sb.AppendLine();
            sb.AppendLine("=== 对话开始 ===");
            sb.AppendLine(transcript);
            sb.AppendLine("=== 对话结束 ===");
            return sb.ToString();
        }

        private static string BuildTranscript(List<ChatMessage> history)
        {
            var sb = new StringBuilder();
            const int limit = 60000;
            foreach (var m in history)
            {
                if (m == null) continue;
                if (m.Role == "system") continue;

                string line;
                if (m.Role == "tool")
                {
                    line = "[tool 返回] " + Truncate(m.Content, 400);
                }
                else if (m.Role == "assistant")
                {
                    var body = m.Content ?? "";
                    if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                    {
                        var names = string.Join(", ",
                            m.ToolCalls.Select(tc => tc.Function != null ? tc.Function.Name : "?"));
                        body = "[调用: " + names + "] " + body;
                    }
                    line = "assistant: " + Truncate(body, 800);
                }
                else
                {
                    line = "user: " + Truncate(m.Content, 800);
                }

                if (sb.Length + line.Length > limit) break;
                sb.AppendLine(line);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 前缀续写下模型接着 "{" 往下写,返回内容不含开头的花括号 —— 这里补回来。
        /// 若 API 不支持该特性、模型仍返回了完整 JSON,则原样放行。
        /// </summary>
        private static string RestorePrefix(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "{}";
            var t = json.TrimStart();
            if (t.StartsWith("{")) return t;
            return "{" + json;
        }

        /// <summary>去掉模型有时会包上的 ```json ... ``` 围栏。</summary>
        private static string StripCodeFence(string content)
        {
            if (string.IsNullOrEmpty(content)) return "{}";
            content = content.Trim();
            if (content.StartsWith("```"))
            {
                var firstNl = content.IndexOf('\n');
                if (firstNl > 0) content = content.Substring(firstNl + 1);
                if (content.EndsWith("```"))
                    content = content.Substring(0, content.Length - 3);
                content = content.Trim();
            }
            // 去掉可能残留的 json 语言标签
            if (content.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                content = content.Substring(4).TrimStart();
            return content;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
