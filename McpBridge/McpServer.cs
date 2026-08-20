// TxToolsMcpBridge / McpServer.cs
//
// MCP (Model Context Protocol) stdio server。
//
// 传输:stdin/stdout 上按行分隔的 JSON-RPC 2.0 消息(UTF-8)。
//   - 请求(id 存在)必须回 response
//   - 通知(id 不存在)不回
//   - 日志一律走 stderr,stdout 只能有协议帧
//
// 实现的方法:
//   initialize                        -> 握手
//   notifications/initialized        -> 忽略
//   tools/list                       -> 从 PDPS 拉取工具清单
//   tools/call                       -> 转发到 PDPS 执行
//   ping                              -> {}
//   shutdown                          -> {} 然后退出
//   其它                              -> JSON-RPC 错误 -32601

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxToolsMcpBridge
{
    public sealed class McpServer
    {
        private readonly int _pid;
        private readonly bool _readOnlyOnly;

        /// <summary>MCP 安全名 -> 原始工具名。tools/list 时建立,tools/call 时反向查。</summary>
        private readonly Dictionary<string, string> _nameMap = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>上次成功拉取的工具清单。实例短暂不可达时退化使用。</summary>
        private JArray _cachedTools;

        public McpServer(int pid, bool readOnlyOnly)
        {
            _pid = pid;
            _readOnlyOnly = readOnlyOnly;
        }

        public void Run()
        {
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JObject msg;
                try { msg = JObject.Parse(line); }
                catch { continue; }

                var id = msg["id"];
                var method = (string)msg["method"];
                var isRequest = id != null && id.Type != JTokenType.Null;

                // 通知:不回
                if (!isRequest)
                {
                    // notifications/initialized 等,直接忽略
                    continue;
                }

                JObject response;
                try { response = Dispatch(id, method, msg["params"] as JObject); }
                catch (Exception ex)
                {
                    response = Error(id, -32603, "内部错误: " + ex.Message);
                }

                Console.Out.WriteLine(JsonConvert.SerializeObject(response));
                Console.Out.Flush();

                if (method == "shutdown") break;
            }
        }

        private JObject Dispatch(JToken id, string method, JObject prm)
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, new JObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject
                        {
                            ["name"] = "tx-tools-mcp-bridge",
                            ["version"] = "1.0.0"
                        }
                    });

                case "ping":
                    return Result(id, new JObject());

                case "tools/list":
                    return Result(id, new JObject { ["tools"] = ListTools() });

                case "tools/call":
                    return CallTool(id, prm);

                case "shutdown":
                    return Result(id, new JObject());

                case "resources/list":
                    return Result(id, new JObject { ["resources"] = new JArray() });

                default:
                    return Error(id, -32601, "方法不存在: " + method);
            }
        }

        // ── tools/list ──

        private JArray ListTools()
        {
            var resp = PipeClient.Call(_pid, new JObject { ["op"] = "list_tools" });
            JArray arr = null;

            if (resp.Ok && resp.Data != null)
            {
                var tools = resp.Data["tools"] as JArray;
                if (tools != null) arr = tools;
            }

            if (arr == null && _cachedTools != null) arr = _cachedTools;

            var outArr = new JArray();
            if (arr == null)
            {
                // 拉不到工具清单,给一个可诊断的占位工具,避免客户端以为服务器空
                outArr.Add(new JObject
                {
                    ["name"] = "ps_connection_status",
                    ["description"] = "检查与 PDPS 实例的连接状态。连接失败时返回错误详情,帮助排查。",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject()
                    }
                });
                return outArr;
            }

            _nameMap.Clear();
            var result = new JArray();

            foreach (var t in arr)
            {
                if (t == null || t.Type != JTokenType.Object) continue;
                var name = (string)t["name"];
                var readOnly = t["read_only"] != null && (bool)t["read_only"];
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (_readOnlyOnly && !readOnly) continue;

                var safeName = ToMcpName(name);
                if (string.IsNullOrWhiteSpace(safeName)) continue;

                _nameMap[safeName] = name;
                var description = (string)t["description"] ?? "";
                if (_readOnlyOnly && !string.IsNullOrEmpty(description))
                    description = "[只读模式] " + description;

                var schema = t["input_schema"] as JObject ?? new JObject { ["type"] = "object" };

                result.Add(new JObject
                {
                    ["name"] = safeName,
                    ["description"] = description,
                    ["inputSchema"] = schema,
                    ["readOnlyHint"] = readOnly
                });
            }

            _cachedTools = result;
            return result;
        }

        // ── tools/call ──

        private JObject CallTool(JToken id, JObject prm)
        {
            if (prm == null)
                return Error(id, -32602, "tools/call 缺少 params");

            var name = (string)prm["name"];
            var arguments = prm["arguments"] as JObject ?? new JObject();

            if (string.IsNullOrWhiteSpace(name))
                return Error(id, -32602, "缺少工具名");

            // 占位诊断工具
            if (name == "ps_connection_status")
            {
                var ping = PipeClient.Call(_pid, new JObject { ["op"] = "ping" });
                return TextResult(id, ping.Ok
                    ? "连接正常。实例信息: " + (ping.Data != null ? ping.Data.ToString() : "")
                    : "连接失败: " + ping.Error);
            }

            // 反向映射: MCP 安全名 -> 原始工具名
            string original;
            if (!_nameMap.TryGetValue(name, out original))
                original = name;

            var req = new JObject
            {
                ["op"] = "invoke",
                ["tool"] = original,
                ["input"] = arguments
            };

            var resp = PipeClient.Call(_pid, req);
            if (!resp.Ok)
                return TextResult(id, "错误: " + resp.Error, isError: true);

            var output = (string)resp.Data["output"] ?? "";
            return TextResult(id, output, isError: false);
        }

        // ── JSON-RPC 构造 ──

        private static JObject Result(JToken id, JObject result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result
            };
        }

        private static JObject Error(JToken id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message ?? ""
                }
            };
        }

        private static JObject TextResult(JToken id, string text, bool isError = false)
        {
            return Result(id, new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = text ?? ""
                    }
                },
                ["isError"] = isError
            });
        }

        /// <summary>MCP 工具名要求 ^[a-zA-Z0-9_-]{1,64}$。与 Agent/Core/RecipeStore.ToApiSafeName 思路一致。</summary>
        private static string ToMcpName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            if (Regex.IsMatch(name, @"^[a-zA-Z0-9_-]+$"))
            {
                return name.Length <= 64 ? name : name.Substring(0, 64);
            }

            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
                else if (c == ' ')
                    sb.Append('_');
            }

            if (sb.Length == 0) return null;
            var s = sb.ToString();
            if (s.Length > 64) s = s.Substring(0, 64);
            // 全 ASCII 安全名(如 run_in_environment)直接返回;含下划线截断的仍可能以 _ 结尾,可接受
            return s;
        }
    }
}