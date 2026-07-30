// TxTools.Agent / Core / MarkdownDoc.cs
// 极简的 frontmatter + 正文 文档模型。
//
// 为什么自己写而不是引 YamlDotNet:
//   多一个第三方程序集就多一处和 PS 自带 DLL 撞版本的风险 —— Newtonsoft 那次
//   MissingMethodException 已经证明过代价。这里只需要 key: value、数字和简单数组,
//   五十行足够,零依赖。
//
// 格式:
//   ---
//   id: conv_20260727095802719
//   title: 测试askuser
//   tools: [ask_user, save_recipe, api_lookup]
//   turns: 6
//   ---
//   正文(自由 Markdown)
//
// 约定:
//   • 值一律当字符串存,取用时按需转换。
//   • 数组用 [a, b, c],元素含逗号的场景本项目用不到,不做转义。
//   • 值里出现换行的场景不支持 —— 需要多行内容请放正文。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TxTools.Agent.Core
{
    public sealed class MarkdownDoc
    {
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, string> _meta =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Body { get; set; }

        public MarkdownDoc() { Body = ""; }

        public IEnumerable<string> Keys { get { return _order; } }

        // ── 读 ──

        public string Get(string key, string def = null)
        {
            string v;
            return _meta.TryGetValue(key ?? "", out v) ? v : def;
        }

        public int GetInt(string key, int def = 0)
        {
            int n;
            return int.TryParse(Get(key, ""), out n) ? n : def;
        }

        public DateTime GetDate(string key)
        {
            DateTime d;
            return DateTime.TryParse(Get(key, ""), out d) ? d : default(DateTime);
        }

        public List<string> GetList(string key)
        {
            var raw = Get(key, "");
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return list;

            raw = raw.Trim();
            if (raw.StartsWith("[") && raw.EndsWith("]"))
                raw = raw.Substring(1, raw.Length - 2);

            foreach (var part in raw.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        // ── 写 ──

        public void Set(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!_meta.ContainsKey(key)) _order.Add(key);
            _meta[key] = value ?? "";
        }

        public void Set(string key, int value) { Set(key, value.ToString()); }

        public void Set(string key, DateTime value)
        {
            Set(key, value.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        public void SetList(string key, IEnumerable<string> values)
        {
            if (values == null) { Set(key, "[]"); return; }
            var cleaned = values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Replace(",", " ").Replace("\r", " ").Replace("\n", " ").Trim());
            Set(key, "[" + string.Join(", ", cleaned) + "]");
        }

        public void Increment(string key)
        {
            Set(key, GetInt(key, 0) + 1);
        }

        // ── 序列化 ──

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            foreach (var k in _order)
            {
                var v = _meta[k] ?? "";
                // 值里有换行会破坏 frontmatter,压成空格
                v = v.Replace("\r", " ").Replace("\n", " ");
                sb.Append(k).Append(": ").AppendLine(v);
            }
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(Body ?? "");
            return sb.ToString();
        }

        public static MarkdownDoc Parse(string text)
        {
            var doc = new MarkdownDoc();
            if (string.IsNullOrEmpty(text)) return doc;

            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int i = 0;

            // 没有 frontmatter 的话整篇都是正文
            if (i < lines.Length && lines[i].Trim() == "---")
            {
                i++;
                while (i < lines.Length && lines[i].Trim() != "---")
                {
                    var line = lines[i];
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        var k = line.Substring(0, colon).Trim();
                        var v = line.Substring(colon + 1).Trim();
                        if (k.Length > 0) doc.Set(k, v);
                    }
                    i++;
                }
                if (i < lines.Length) i++;   // 跳过收尾的 ---
            }

            var body = new StringBuilder();
            for (; i < lines.Length; i++) body.AppendLine(lines[i]);
            doc.Body = body.ToString().TrimStart('\n');

            return doc;
        }

        // ── 文件读写 ──

        public static MarkdownDoc Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { return null; }
        }

        public bool SaveTo(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, ToString(), Encoding.UTF8);
                return true;
            }
            catch { return false; }
        }

        /// <summary>把任意名称转成安全的文件名(用于一物一文件的存储布局)。</summary>
        public static string Slug(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unnamed";
            var sb = new StringBuilder();
            foreach (var c in name.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                else if (c == ' ' || c == '.' || c == '/' || c == '\\') sb.Append('_');
                // 其余字符(含中文标点)丢弃;中文本身是 IsLetterOrDigit,会保留
            }
            var s = sb.ToString().Trim('_');
            if (s.Length == 0) s = "unnamed";
            if (s.Length > 80) s = s.Substring(0, 80);
            return s;
        }
    }
}
