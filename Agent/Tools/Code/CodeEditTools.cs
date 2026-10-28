// TxTools.Agent / Tools / Code / CodeEditTools.cs
//
// 源码【改】的三个工具。
//
// ── 为什么是"精确串替换"而不是"输出整个新文件" ──
//   让模型重写整文件有三个问题:
//     1. 几万 token 的输出,又慢又贵;
//     2. 模型会在无关处悄悄改动 —— 换个空行、调个顺序、"顺手优化"一下,
//        你 review 时根本看不出来动了什么;
//     3. 长输出容易被 max_tokens 截断,写出半个文件直接毁掉源码。
//
//   精确替换把改动限制在明确的一小段,diff 一眼看清,也没有截断风险。
//
// ── 唯一匹配是硬约束 ──
//   old_string 必须在文件里恰好出现一次。0 次说明模型记错了内容(常见于凭印象改),
//   多次说明定位不够具体 —— 两种情况都必须让它重来,而不是"取第一个"。
//   这条和 PsBridge 里同名对象的处理是同一个道理:静默猜测比报错危险得多。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    internal static class CodeBackup
    {
        private static readonly Dictionary<string, string> Snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly string FallbackSession = Guid.NewGuid().ToString("N");
        private static string Key(string fullPath) { return (AgentContext.CurrentConvId() ?? FallbackSession) + "|" + fullPath; }
        internal static string Find(string fullPath) { lock (Snapshots) { string target; return Snapshots.TryGetValue(Key(fullPath), out target) ? target : null; } }
        public static string Ensure(string fullPath)
        {
            lock (Snapshots) {
                var key = Key(fullPath); string target;
                if (Snapshots.TryGetValue(key, out target)) {
                    if (!File.Exists(target)) throw new IOException("当前会话原始备份已丢失，停止修改。");
                    return target;
                }
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxTools", "CodeBackups");
                var project = new DirectoryInfo(CodeWorkspace.Root);
                for (int i = 0; i < 3 && project != null; i++, project = project.Parent)
                    if (File.Exists(Path.Combine(project.FullName, "TxTools.csproj"))) {
                        dir = Path.GetFullPath(Path.Combine(project.FullName, "..", "TxTools.Local", "backups", "agent-code")); break;
                    }
                Directory.CreateDirectory(dir);
                target = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".bak");
                File.Copy(fullPath, target, false);
                Snapshots.Add(key, target);
                return target;
            }
        }
    }

    public sealed class CodeEditTool : TxAgentToolBase
    {
        public override string Name { get { return "code_edit"; } }

        public override string Description
        {
            get
            {
                return "修改源码:把 old_string 精确替换成 new_string。"
                     + "【old_string 必须在文件中恰好出现一次】—— 0 次或多次都会被拒绝，"
                     + "所以要带上足够的上下文行让它唯一(通常前后各带 1~3 行)。"
                     + "old_string 必须与文件内容逐字节一致，包括缩进和空白 —— "
                     + "先用 code_read 确认原文再改，不要凭记忆写。"
                     + "首次修改会保存本会话原始备份，备份放在源码目录之外。"
                     + "改完务必用 code_build 编译验证。";
            }
        }

        /// <summary>改源码是破坏性操作，走审批。</summary>
        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"file\": { \"type\":\"string\", \"description\":\"相对工作区根的路径\" }," +
                    " \"old_string\": { \"type\":\"string\", \"description\":\"要被替换的原文，须唯一\" }," +
                    " \"new_string\": { \"type\":\"string\", \"description\":\"替换成的新内容。传空串表示删除\" }" +
                    "}, \"required\":[\"file\",\"old_string\",\"new_string\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            string err;
            var full = CodeWorkspace.Resolve(GetString(input, "file"), out err);
            if (full == null) return "Error: " + err;
            if (!File.Exists(full)) return "Error: 文件不存在: " + CodeWorkspace.Relative(full);

            var oldStr = GetString(input, "old_string");
            if (input["new_string"] == null || input["new_string"].Type != JTokenType.String) return "Error: 必须显式提供字符串 new_string；删除请传空字符串。";
            var newStr = (string)input["new_string"];

            if (string.IsNullOrEmpty(oldStr))
                return "Error: old_string 不能为空。要插入新内容，请把插入点附近的原文一起带上。";

            string text;
            Encoding enc;
            try { text = ReadWithEncoding(full, out enc); }
            catch (Exception ex) { return "Error: 读取失败 - " + ex.Message; }

            // 换行符归一化后再匹配 —— 模型很难保证输出 \r\n
            var textN = text.Replace("\r\n", "\n");
            var oldN = oldStr.Replace("\r\n", "\n");

            int count = CountOccurrences(textN, oldN);

            if (count == 0)
            {
                var hint = NearestHint(textN, oldN);
                return "Error: old_string 在文件中找不到。\n"
                     + "常见原因:缩进或空白不一致、凭记忆写了不存在的代码、行首多了空格。\n"
                     + "请先 code_read 读出该段原文，逐字节复制过来再改。"
                     + hint;
            }

            if (count > 1)
                return "Error: old_string 在文件中出现 " + count + " 次，无法确定改哪一处。\n"
                     + "请往前后各多带 1~3 行上下文，使其唯一。";

            string backup;
            try { backup = CodeBackup.Ensure(full); }
            catch (Exception ex) { return "Error: 原始备份失败，未修改文件: " + ex.Message; }

            var updatedN = ReplaceFirst(textN, oldN, newStr.Replace("\r\n", "\n"));
            var updated = text.Contains("\r\n") ? updatedN.Replace("\n", "\r\n") : updatedN;

            try { File.WriteAllText(full, updated, enc); }
            catch (Exception ex) { return "Error: 写入失败 - " + ex.Message; }

            var sb = new StringBuilder();
            sb.AppendLine("已修改 " + CodeWorkspace.Relative(full));
            if (backup != null) sb.AppendLine("首次修改，已备份: " + Path.GetFileName(backup));

            int line = textN.Substring(0, textN.IndexOf(oldN, StringComparison.Ordinal))
                            .Count(c => c == '\n') + 1;
            sb.AppendLine("位置: 第 " + line + " 行附近");
            sb.AppendLine();
            sb.AppendLine(Diff(oldN, newStr.Replace("\r\n", "\n")));
            sb.AppendLine();
            sb.Append("改动已落盘。用 code_build 编译验证 —— 未经编译的改动不算完成。");
            return sb.ToString();
        }

        private static string Diff(string oldS, string newS)
        {
            var sb = new StringBuilder();
            foreach (var l in oldS.Split('\n')) sb.Append("- ").AppendLine(l);
            if (newS.Length == 0) { sb.Append("+ (删除)"); return sb.ToString(); }
            foreach (var l in newS.Split('\n')) sb.Append("+ ").AppendLine(l);
            return sb.ToString().TrimEnd();
        }

        /// <summary>找不到时给点线索:哪一行最像。省得模型盲目重试。</summary>
        private static string NearestHint(string text, string oldStr)
        {
            try
            {
                var firstLine = oldStr.Split('\n')[0].Trim();
                if (firstLine.Length < 6) return "";

                var probe = firstLine.Length > 30 ? firstLine.Substring(0, 30) : firstLine;
                var lines = text.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf(probe, StringComparison.Ordinal) < 0) continue;
                    return "\n\n文件第 " + (i + 1) + " 行有相似内容:\n"
                         + (i + 1) + "| " + lines[i].TrimEnd();
                }
            }
            catch { }
            return "";
        }

        private static int CountOccurrences(string hay, string needle)
        {
            int n = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        private static string ReplaceFirst(string text, string oldS, string newS)
        {
            int i = text.IndexOf(oldS, StringComparison.Ordinal);
            return i < 0 ? text : text.Substring(0, i) + newS + text.Substring(i + oldS.Length);
        }

        /// <summary>保留原文件编码与 BOM，避免改一行把整个文件编码换了。</summary>
        internal static string ReadWithEncoding(string path, out Encoding enc)
        {
            var bytes = File.ReadAllBytes(path);

            int offset = 0;
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) { enc = new UTF32Encoding(false, true, true); offset = 4; }
            else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF) { enc = new UTF32Encoding(true, true, true); offset = 4; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { enc = new UnicodeEncoding(false, true, true); offset = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { enc = new UnicodeEncoding(true, true, true); offset = 2; }
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { enc = new UTF8Encoding(true, true); offset = 3; }
            else enc = new UTF8Encoding(false, true);
            return enc.GetString(bytes, offset, bytes.Length - offset);
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class CodeCreateFileTool : TxAgentToolBase
    {
        public override string Name { get { return "code_create_file"; } }

        public override string Description
        {
            get
            {
                return "在工作区里新建源码文件。文件已存在时会拒绝 —— 改已有文件请用 code_edit。"
                     + "新建后记得把文件加进 .csproj(旧式项目格式需要显式 <Compile Include=.../>，"
                     + "SDK 风格项目会自动包含)。";
            }
        }

        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"file\": { \"type\":\"string\" }," +
                    " \"content\": { \"type\":\"string\" }" +
                    "}, \"required\":[\"file\",\"content\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            string err;
            var full = CodeWorkspace.Resolve(GetString(input, "file"), out err);
            if (full == null) return "Error: " + err;

            if (File.Exists(full))
                return "Error: 文件已存在: " + CodeWorkspace.Relative(full) + "。改已有文件请用 code_edit。";

            if (input["content"] == null || input["content"].Type != JTokenType.String) return "Error: 必须显式提供字符串 content。";
            var content = (string)input["content"];

            try
            {
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true))) writer.Write(content);
            }
            catch (Exception ex) { return "Error: 写入失败 - " + ex.Message; }

            return "已创建 " + CodeWorkspace.Relative(full)
                 + " (" + content.Split('\n').Length + " 行)。\n"
                 + "若是旧式 .csproj，记得加 <Compile Include=\"...\" />，否则编译不会包含它。";
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class CodeRevertTool : TxAgentToolBase
    {
        public override string Name { get { return "code_revert"; } }

        public override string Description
        {
            get
            {
                return "把某个文件回滚到本次会话首次修改前的状态(只使用本会话备份)。"
                     + "改坏了、或者方向不对想重来时用。";
            }
        }

        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"file\": { \"type\":\"string\" }" +
                    "}, \"required\":[\"file\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            string err;
            var full = CodeWorkspace.Resolve(GetString(input, "file"), out err);
            if (full == null) return "Error: " + err;

            try
            {
                var backup = CodeBackup.Find(full);
                if (backup == null || !File.Exists(backup)) return "Error: 当前会话没有该文件的原始备份。";
                File.Copy(backup, full, true);
                return "已恢复本会话首次修改前的版本: " + CodeWorkspace.Relative(full);
            }
            catch (Exception ex) { return "Error: 回滚失败 - " + ex.Message; }
        }
    }
}
