// TxTools.Agent / Core / ConversationStore.cs
// 多对话持久化：每个对话存成 conversations/{id}.json，含标题/时间/消息。
// 像常见 AI 工具那样——“新对话”不再清空旧对话，而是开一条新的，旧对话保留可回看。
// 兼容：若检测到旧版单文件 conversation.json，首次访问时迁移成一条对话。
// 路径策略与 KeyStore/RecipeStore 一致(优先插件目录，回退 LocalAppData)。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    public sealed class ConversationMeta
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public sealed class Conversation
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<ChatMessage> Messages { get; set; }

        public Conversation() { Messages = new List<ChatMessage>(); }
    }

    public static class ConversationStore
    {
        private const string FolderName = "conversations";
        private const string LegacyFile = "conversation.json";

        public static string NewId()
        {
            return "conv_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        }

        /// <summary>列出全部对话(按更新时间倒序)。</summary>
        public static List<ConversationMeta> List()
        {
            MigrateLegacyIfNeeded();
            var metas = new List<ConversationMeta>();
            try
            {
                var dir = FolderPath();
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.json"))
                    {
                        try
                        {
                            var c = JsonConvert.DeserializeObject<Conversation>(File.ReadAllText(f, Encoding.UTF8));
                            if (c != null && !string.IsNullOrEmpty(c.Id))
                                metas.Add(new ConversationMeta { Id = c.Id, Title = c.Title, UpdatedUtc = c.UpdatedUtc });
                        }
                        catch { }
                    }
            }
            catch { }
            return metas.OrderByDescending(m => m.UpdatedUtc).ToList();
        }

        public static Conversation Load(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try
            {
                var path = Path.Combine(FolderPath(), Safe(id) + ".json");
                if (File.Exists(path))
                    return JsonConvert.DeserializeObject<Conversation>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { }
            return null;
        }

        public static void Save(Conversation conv)
        {
            if (conv == null || string.IsNullOrEmpty(conv.Id)) return;
            if (conv.CreatedUtc == default(DateTime)) conv.CreatedUtc = DateTime.UtcNow;
            conv.UpdatedUtc = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(conv.Title)) conv.Title = DeriveTitle(conv.Messages);
            try
            {
                var dir = FolderPath();
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, Safe(conv.Id) + ".json"),
                    JsonConvert.SerializeObject(conv, Formatting.Indented), Encoding.UTF8);
            }
            catch { /* 持久化失败不影响对话本身 */ }
        }

        public static void Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                var p = Path.Combine(FolderPath(), Safe(id) + ".json");
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        /// <summary>是否含有至少一条用户消息(用于判断空对话不必落盘)。</summary>
        public static bool HasUserContent(IEnumerable<ChatMessage> messages)
        {
            if (messages == null) return false;
            foreach (var m in messages)
                if (m != null && m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content)) return true;
            return false;
        }

        private static string DeriveTitle(List<ChatMessage> msgs)
        {
            if (msgs != null)
                foreach (var m in msgs)
                    if (m != null && m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content))
                    {
                        var t = m.Content.Trim().Replace("\r", " ").Replace("\n", " ");
                        return t.Length <= 30 ? t : t.Substring(0, 30) + "…";
                    }
            return "新对话 " + DateTime.Now.ToString("MM-dd HH:mm");
        }

        private static void MigrateLegacyIfNeeded()
        {
            try
            {
                var dir = FolderPath();
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.json").Length > 0) return;

                foreach (var lp in LegacyPaths())
                {
                    if (!File.Exists(lp)) continue;
                    try
                    {
                        var msgs = JsonConvert.DeserializeObject<List<ChatMessage>>(File.ReadAllText(lp, Encoding.UTF8));
                        if (msgs != null && msgs.Count > 0)
                            Save(new Conversation { Id = "conv_legacy", CreatedUtc = DateTime.UtcNow, Messages = msgs });
                    }
                    catch { }
                    break;
                }
            }
            catch { }
        }

        private static string FolderPath()
        {
            string pluginDir = null;
            try { pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { }

            if (!string.IsNullOrEmpty(pluginDir))
            {
                var d = Path.Combine(pluginDir, FolderName);
                try { Directory.CreateDirectory(d); return d; } catch { }
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxTools.Agent", FolderName);
        }

        private static string[] LegacyPaths()
        {
            var list = new List<string>();
            try
            {
                var pd = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(pd)) list.Add(Path.Combine(pd, LegacyFile));
            }
            catch { }
            list.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxTools.Agent", LegacyFile));
            return list.ToArray();
        }

        private static string Safe(string id)
        {
            var sb = new StringBuilder();
            foreach (var c in id) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }
    }
}
