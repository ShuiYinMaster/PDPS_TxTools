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

        /// <summary>是否正被【另一个 TxAgent 进程】打开着。为 true 时不应自动载入。</summary>
        public bool HeldByOther { get; set; }
    }

    public sealed class Conversation
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        /// <summary>
        /// 本会话累计 token 用量。持久化的原因:AgentLoop 的计数器随实例存活,
        /// 重开对话就归零 —— 但用户看到的应该是"这个会话到目前为止一共花了多少",
        /// 而不是"本次打开之后花了多少"。
        /// </summary>
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }

        public List<ChatMessage> Messages { get; set; }

        public Conversation() { Messages = new List<ChatMessage>(); }
    }

    public static class ConversationStore
    {
        private const string FolderName = "conversations";
        private const string LegacyFile = "conversation.json";

        public static string NewId()
        {
            // 【必须带进程标识】只精确到毫秒的话，两个 PDPS 同时新建对话就是同一个 id，
            // 两边各写各的，后保存的整份覆盖先保存的。
            return "conv_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
                 + "_" + ProcessSync.InstanceId;
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
                                metas.Add(new ConversationMeta
                                {
                                    Id = c.Id,
                                    Title = c.Title,
                                    UpdatedUtc = c.UpdatedUtc,
                                    HeldByOther = ProcessSync.IsHeldByOther(dir, c.Id)
                                });
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

                // 同一个 conv 理论上只被一个进程持有，但锁失效时仍可能并发写，
                // 加一道互斥保证文件不会写到一半被另一个进程截断
                ProcessSync.WithFileLock("conv_" + Safe(conv.Id), delegate
                {
                    File.WriteAllText(Path.Combine(dir, Safe(conv.Id) + ".json"),
                        JsonConvert.SerializeObject(conv, Formatting.Indented), Encoding.UTF8);
                });

                ProcessSync.RenewConversation(dir, conv.Id);   // 保存即心跳
            }
            catch { /* 持久化失败不影响对话本身 */ }

            // JSON 是给 API 无损重放用的;MD 摘要是给检索用的。两者一起更新。
            // 索引失败不抛 —— 它只是加速层。
            ConversationIndex.Rebuild(conv);
        }

        /// <summary>占用某会话。已被别的活进程占着时返回 false。</summary>
        public static bool Acquire(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            return ProcessSync.AcquireConversation(FolderPath(), id);
        }

        public static void Release(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            ProcessSync.ReleaseConversation(FolderPath(), id);
        }

        /// <summary>启动时挑一个可用会话:最近的、且没被别的进程占着的。</summary>
        public static ConversationMeta PickAvailable()
        {
            foreach (var m in List())
                if (!m.HeldByOther) return m;
            return null;
        }

        /// <summary>监听目录变化，对方新建/更新对话时回调。</summary>
        public static ProcessSync.Watcher Watch(Action onChanged)
        {
            return new ProcessSync.Watcher(FolderPath(), "*.json", onChanged);
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
            ConversationIndex.Delete(id);
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

        /// <summary>供 ConversationIndex 定位 index 子目录。</summary>
        public static string FolderPathPublic() { return FolderPath(); }

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
