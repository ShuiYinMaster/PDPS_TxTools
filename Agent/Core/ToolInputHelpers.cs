// TxTools.Agent / Core / ToolInputHelpers.cs
// LLM 传参经常不严格遵守 JSON Schema:
//   声明 tags 是 array<string>, 结果给你 ["a","b"] 或 "a,b" 或 "[\"a\"]" 都有可能。
// 强转 (string)jToken 遇上 JArray 就抛 "Can not convert Array to String"。
// 这里给一组"永不抛异常"的弹性解析工具,工具实现里都走这些方法就不会因 LLM 出格而崩。

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public static class ToolInputHelpers
    {
        /// <summary>
        /// 弹性拿字符串:任何 JToken 类型都不抛异常。
        ///   String  → 原文
        ///   Number/Boolean/Date → ToString()
        ///   Array/Object → JSON 序列化字符串(方便工具体拿到原始形态自己再解析)
        ///   Null/Undefined/null → fallback
        /// 注意:用 JsonConvert.SerializeObject 而非 JToken.ToString(Formatting),
        /// 后者是 Newtonsoft.Json 5.0+ 才有的重载,PS 环境如果加载了老版会崩。
        /// </summary>
        public static string String(JToken tok, string fallback = null)
        {
            if (tok == null || tok.Type == JTokenType.Null || tok.Type == JTokenType.Undefined)
                return fallback;
            if (tok.Type == JTokenType.String) return (string)tok;
            if (tok.Type == JTokenType.Array || tok.Type == JTokenType.Object)
                return JsonConvert.SerializeObject(tok, Formatting.None);
            return tok.ToString();
        }

        /// <summary>
        /// 弹性拿字符串列表 —— 覆盖 LLM 三种常见传法:
        ///   ["a","b","c"]              → ["a","b","c"]
        ///   "a,b,c"                    → ["a","b","c"]
        ///   "a;b|c"                    → 按 ; 或 | 也能切
        ///   数字/其他标量               → 转字符串单元素列表
        ///   null/空                    → 空列表
        /// </summary>
        public static List<string> StringList(JToken tok)
        {
            var list = new List<string>();
            if (tok == null || tok.Type == JTokenType.Null || tok.Type == JTokenType.Undefined)
                return list;

            if (tok is JArray arr)
            {
                foreach (var it in arr)
                {
                    var s = String(it, null);
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                }
                return list;
            }

            if (tok.Type == JTokenType.String)
            {
                var s = (string)tok;
                if (string.IsNullOrWhiteSpace(s)) return list;
                // 支持 , ; | 三种分隔符,同时容忍空白
                var parts = s.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                    if (!string.IsNullOrWhiteSpace(p)) list.Add(p.Trim());
                return list;
            }

            // 其他标量类型 —— 转字符串放一个元素
            var one = String(tok, null);
            if (!string.IsNullOrWhiteSpace(one)) list.Add(one);
            return list;
        }

        /// <summary>弹性拿 bool:接受 true/false / "true"/"false" / 1/0。fallback 为 null 表示未提供。</summary>
        public static bool? BoolOpt(JToken tok)
        {
            if (tok == null || tok.Type == JTokenType.Null || tok.Type == JTokenType.Undefined) return null;
            if (tok.Type == JTokenType.Boolean) return (bool)tok;
            if (tok.Type == JTokenType.Integer) return ((int)tok) != 0;
            if (tok.Type == JTokenType.String)
            {
                var s = ((string)tok).Trim().ToLowerInvariant();
                if (s == "true" || s == "1" || s == "yes" || s == "y") return true;
                if (s == "false" || s == "0" || s == "no" || s == "n") return false;
            }
            return null;
        }

        public static bool Bool(JToken tok, bool fallback = false)
        {
            var v = BoolOpt(tok);
            return v.HasValue ? v.Value : fallback;
        }

        /// <summary>弹性拿 int:支持 "12" / 12 / 12.0(截断)。</summary>
        public static int Int(JToken tok, int fallback = 0)
        {
            if (tok == null || tok.Type == JTokenType.Null) return fallback;
            if (tok.Type == JTokenType.Integer) return (int)tok;
            if (tok.Type == JTokenType.Float) return (int)(double)tok;
            if (tok.Type == JTokenType.String)
            {
                int n;
                if (int.TryParse((string)tok, out n)) return n;
            }
            return fallback;
        }
    }
}