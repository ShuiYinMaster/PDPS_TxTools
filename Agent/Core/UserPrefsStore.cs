// TxTools.Agent / Core / UserPrefsStore.cs
// 用户偏好 + 模型列表缓存的持久化 —— 一个 prefs.json 文件搞定。
// 存放位置策略与 KeyStore 一致(插件目录优先, LocalAppData 回退)。
//
// 存的内容:
//   ProviderId    — 上次用的 provider (下次开窗默认恢复)
//   Model         — 上次用的具体模型
//   ApprovalMode  — 上次的审批模式(ask/auto_safe/auto_all)
//   Models        — 各 provider /v1/models 拉取的真实模型列表 + 时间戳
//                   下次开窗立即用缓存,不再显示硬编码默认;同时后台刷新最新。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    public sealed class ProviderModelsCache
    {
        public DateTime FetchedUtc { get; set; }
        public string[] List { get; set; }
    }

    public sealed class UserPrefs
    {
        public string ProviderId { get; set; }
        public string Model { get; set; }
        public string ApprovalMode { get; set; }
        /// <summary>启用的工具组(ToolGate)。null/空 = 用代码默认值。</summary>
        public List<string> EnabledToolGroups { get; set; }
        public Dictionary<string, ProviderModelsCache> Models { get; set; }
            = new Dictionary<string, ProviderModelsCache>(StringComparer.Ordinal);
    }

    public static class UserPrefsStore
    {
        private const string FileName = "prefs.json";

        /// <summary>读取偏好文件;不存在或解析失败返回空 UserPrefs 对象(非 null)。</summary>
        public static UserPrefs Load()
        {
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    var p = JsonConvert.DeserializeObject<UserPrefs>(json);
                    if (p != null)
                    {
                        if (p.Models == null)
                            p.Models = new Dictionary<string, ProviderModelsCache>(StringComparer.Ordinal);
                        return p;
                    }
                }
                catch { /* 换下一个候选路径 */ }
            }
            return new UserPrefs();
        }

        public static void Save(UserPrefs prefs)
        {
            if (prefs == null) return;
            var json = JsonConvert.SerializeObject(prefs, Formatting.Indented);
            foreach (var path in CandidatePaths())
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, json, Encoding.UTF8);
                    return;
                }
                catch { /* 换下一个候选路径 */ }
            }
        }

        // ── 高频便捷方法 (自动 load → mutate → save,不必构造整个对象) ──

        public static void UpdateChoice(string providerId, string model)
        {
            var p = Load();
            if (!string.IsNullOrWhiteSpace(providerId)) p.ProviderId = providerId;
            if (!string.IsNullOrWhiteSpace(model)) p.Model = model;
            Save(p);
        }

        public static void UpdateApprovalMode(string mode)
        {
            var p = Load();
            p.ApprovalMode = mode;
            Save(p);
        }

        /// <summary>保存启用的工具组清单(空清单 = 回退代码默认)。</summary>
        public static void UpdateToolGroups(IEnumerable<string> enabledGroups)
        {
            var p = Load();
            p.EnabledToolGroups = enabledGroups == null
                ? null
                : new List<string>(enabledGroups);
            Save(p);
        }

        public static void UpdateModels(string providerId, string[] models)
        {
            if (string.IsNullOrWhiteSpace(providerId) || models == null || models.Length == 0) return;
            var p = Load();
            if (p.Models == null)
                p.Models = new Dictionary<string, ProviderModelsCache>(StringComparer.Ordinal);
            p.Models[providerId] = new ProviderModelsCache
            {
                FetchedUtc = DateTime.UtcNow,
                List = models
            };
            Save(p);
        }

        // ── 路径 ──

        private static string[] CandidatePaths()
        {
            string pluginDir = null;
            try { pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { }

            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxTools.Agent");

            if (string.IsNullOrEmpty(pluginDir))
                return new[] { Path.Combine(localDir, FileName) };

            return new[]
            {
                Path.Combine(pluginDir, FileName),
                Path.Combine(localDir, FileName)
            };
        }
    }
}
