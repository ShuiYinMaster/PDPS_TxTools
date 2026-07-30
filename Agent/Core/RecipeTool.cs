// TxTools.Agent / Core / RecipeTool.cs
// 把一条配方(代码 + 参数声明)包装成可调用工具：
//  - InputSchema 由 RecipeParam 生成(object → 传 ITxObject.Id 字符串, 与侧边栏同源)
//  - Execute 时用 RecipeRunner.BuildCode 拼出"前置参数声明 + 配方原文"，再执行
//  - 代码配方会执行任意代码 → IsReadOnly = false(走审批)，审批框展示的就是完整代码
//  - 执行结果记入 RecipeStore.RecordRun(failCount 会决定这条配方还值不值得留着)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class RecipeTool : ITxAgentTool
    {
        private readonly Recipe _recipe;

        public RecipeTool(Recipe recipe, ToolRegistry registry)
        {
            _recipe = recipe;
        }

        // 工具名必须匹配 ^[a-zA-Z0-9_-]+$ (API function.name 要求)。
        // 配方 Name 存原文(可含中文), 此处用 ToApiSafeName 转成 API 安全名。
        public string Name { get { return Recipe.ToApiSafeName(_recipe.Name); } }

        public string Description
        {
            get
            {
                var lang = SnippetStore.NormalizeLang(_recipe.Lang);
                var baseDesc = "(配方/" + lang + ") " + (_recipe.Description ?? "")
                             + " — 已固化的可执行代码，参数由调用方提供。";
                // 若净化后的 API 名与原文不同，在描述里带上原文名方便 LLM 识别
                var apiName = Recipe.ToApiSafeName(_recipe.Name);
                if (!string.Equals(apiName, _recipe.Name, StringComparison.Ordinal))
                    baseDesc = "(配方: " + _recipe.Name + "/" + lang + ") " + (_recipe.Description ?? "")
                             + " — 已固化的可执行代码。";
                return baseDesc;
            }
        }

        /// <summary>配方代码可能改场景，一律走审批(与 run_csharp 同级)。</summary>
        public bool IsReadOnly { get { return false; } }

        public JObject InputSchema
        {
            get
            {
                var props = new JObject();
                var required = new JArray();
                if (_recipe.Params != null)
                {
                    foreach (var p in _recipe.Params)
                    {
                        if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;

                        var pd = new JObject();
                        pd["type"] = MapType(p.Kind);
                        var help = new StringBuilder();
                        if (!string.IsNullOrEmpty(p.Label) && !string.Equals(p.Label, p.Name, StringComparison.Ordinal))
                            help.Append("(").Append(p.Label).Append(") ");
                        if (p.Kind == "object" || p.Kind == "objects")
                            help.Append("传 ITxObject.Id 字符串");
                        if (!string.IsNullOrEmpty(p.TypeHint))
                            help.Append("，期望类型 ").Append(p.TypeHint);
                        if (!string.IsNullOrEmpty(p.Help))
                            help.Append("。").Append(p.Help);
                        if (!string.IsNullOrEmpty(p.Default))
                            help.Append("。默认 ").Append(p.Default);
                        if (help.Length > 0) pd["description"] = help.ToString();

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
            if (_recipe == null) return "配方不存在。";

            // 与侧边栏同源:参数值都是字符串(object 传 Id),由 RecipeRunner 生成前置声明
            var args = new Dictionary<string, string>();
            if (input != null)
            {
                foreach (var kv in input)
                {
                    var v = kv.Value;
                    args[kv.Key] = v == null || v.Type == JTokenType.Null
                        ? null : v.ToString();
                }
            }

            string err;
            var full = RecipeRunner.BuildCode(_recipe, args, out err);
            if (full == null) return "Error: " + err;

            bool ok;
            string text;
            if (SnippetStore.NormalizeLang(_recipe.Lang) == "python")
            {
                var res = Scripting.PythonHostProvider.Instance.Run(
                    full, Scripting.PythonRunMode.Execute, "配方: " + _recipe.Name);
                ok = res.Success;
                text = res.ToAgentText();
            }
            else
            {
                text = Ps.PsBridge.RunCSharp(full, out ok, "配方: " + _recipe.Name);
            }

            RecipeStore.RecordRun(_recipe.Id, ok);
            try
            {
                AuditLog.Write((ok ? "[info]" : "[warn]") + " [Recipe] " + _recipe.Name
                    + " 执行" + (ok ? "成功" : "失败"));
            }
            catch { }

            return ok ? text : "Error: " + (text ?? "");
        }

        private static string MapType(string kind)
        {
            if (kind == "number") return "number";
            if (kind == "bool") return "boolean";
            // object / objects / text 一律按字符串传(Id 或原文)
            return "string";
        }
    }
}
