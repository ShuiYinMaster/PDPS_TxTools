// TxTools.Agent / Core / RecipeTool.cs
// 把一条 Recipe 包装成可调用工具：
//  - InputSchema 由配方参数生成
//  - IsReadOnly 按步骤继承：所有步骤都只读 -> 配方只读(免审批)；任一步会改场景 -> 配方需审批
//  - Execute 时把 {{param}} 替换进各步骤模板，再依次调用对应工具
//  - 审批在配方层一次性完成(由 AgentLoop)，内部步骤直接 Execute，不二次弹窗

using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class RecipeTool : ITxAgentTool
    {
        private static readonly Regex Token = new Regex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}");
        [System.ThreadStatic] private static int _depth; // 防配方相互递归

        private readonly Recipe _recipe;
        private readonly ToolRegistry _registry;

        public RecipeTool(Recipe recipe, ToolRegistry registry)
        {
            _recipe = recipe;
            _registry = registry;
        }

        public string Name { get { return _recipe.Name; } }

        public string Description
        {
            get { return "(配方) " + (_recipe.Description ?? "") + " — 由现有工具组合而成。"; }
        }

        public bool IsReadOnly
        {
            get
            {
                if (_recipe.Steps == null) return true;
                foreach (var step in _recipe.Steps)
                {
                    ITxAgentTool t;
                    if (!_registry.TryGet(step.Tool, out t)) return false; // 未知步骤当作变更，安全侧
                    if (!t.IsReadOnly) return false;
                }
                return true;
            }
        }

        public JObject InputSchema
        {
            get
            {
                var props = new JObject();
                var required = new JArray();
                if (_recipe.Parameters != null)
                {
                    foreach (var p in _recipe.Parameters)
                    {
                        var pd = new JObject();
                        pd["type"] = MapType(p.Type);
                        if (!string.IsNullOrEmpty(p.Description)) pd["description"] = p.Description;
                        props[p.Name] = pd;
                        if (p.Required) required.Add(p.Name);
                    }
                }
                var schema = new JObject();
                schema["type"] = "object";
                schema["properties"] = props;
                if (required.Count > 0) schema["required"] = required;
                return schema;
            }
        }

        public string Execute(JObject input)
        {
            if (_depth > 8) return "配方嵌套过深，已中止。";
            if (_recipe.Steps == null || _recipe.Steps.Count == 0) return "该配方没有步骤。";

            _depth++;
            try
            {
                var args = input ?? new JObject();
                var sb = new StringBuilder();
                int i = 0;
                foreach (var step in _recipe.Steps)
                {
                    i++;
                    ITxAgentTool tool;
                    if (!_registry.TryGet(step.Tool, out tool))
                    {
                        sb.AppendLine("步骤" + i + " (" + step.Tool + "): 工具不存在，已跳过。");
                        continue;
                    }
                    var concrete = step.Input != null
                        ? (JObject)Substitute(step.Input, args)
                        : new JObject();

                    string result;
                    try { result = tool.Execute(concrete) ?? ""; }
                    catch (System.Exception ex) { result = "异常: " + ex.Message; }

                    sb.AppendLine("步骤" + i + " (" + step.Tool + "): " + result);
                }
                return sb.ToString().TrimEnd();
            }
            finally { _depth--; }
        }

        // ---- 参数替换 ----

        private static JToken Substitute(JToken node, JObject args)
        {
            switch (node.Type)
            {
                case JTokenType.Object:
                    var o = new JObject();
                    foreach (var pr in (JObject)node) o[pr.Key] = Substitute(pr.Value, args);
                    return o;
                case JTokenType.Array:
                    var a = new JArray();
                    foreach (var it in (JArray)node) a.Add(Substitute(it, args));
                    return a;
                case JTokenType.String:
                    return SubstituteString((string)node, args);
                default:
                    return node.DeepClone();
            }
        }

        private static JToken SubstituteString(string s, JObject args)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var m = Token.Match(s);

            // 整个值就是单个 {{name}} —— 保留原始类型 (数字/布尔等)
            if (m.Success && m.Index == 0 && m.Length == s.Length)
            {
                var v = args[m.Groups[1].Value];
                return v != null ? v.DeepClone() : (JToken)"";
            }

            // 内嵌替换 —— 一律转文本 (避开会崩的 JToken.ToString(Formatting) 重载)
            return Token.Replace(s, delegate(Match mm)
            {
                var v = args[mm.Groups[1].Value];
                if (v == null) return "";
                return v.Type == JTokenType.String ? (string)v : JsonConvert.SerializeObject(v);
            });
        }

        private static string MapType(string t)
        {
            if (t == "number") return "number";
            if (t == "boolean") return "boolean";
            return "string";
        }
    }
}
