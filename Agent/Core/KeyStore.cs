// TxTools.Agent / Core / KeyStore.cs
// API key 的本地持久化。默认写入插件文件夹 (随程序集)，用 DPAPI 按当前用户加密，不存明文。
// 若插件目录不可写 (例如部署在 Program Files)，自动回退到 %LOCALAPPDATA%\TxTools.Agent。
//
// v2 (多提供商): Load/Save/Clear 接受 providerId 参数,keyfile 名 = {providerId}.key
//   deepseek.key / kimi.key / qwen.key / openai.key / ollama.key
// 老代码调用 Load()/Save(key) 无参数版本时,自动路由到默认 provider (deepseek),
// 保持向后兼容。

using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TxTools.Agent.Core
{
    public static class KeyStore
    {
        private const string DefaultProviderId = "deepseek";

        /// <summary>读取已保存的 key;没有或解密失败返回 null。</summary>
        public static string Load(string providerId = null)
        {
            var id = NormalizeProviderId(providerId);
            foreach (var path in CandidatePaths(id))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var blob = Convert.FromBase64String(File.ReadAllText(path, Encoding.ASCII).Trim());
                    var bytes = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
                    var key = Encoding.UTF8.GetString(bytes);
                    if (!string.IsNullOrWhiteSpace(key)) return key;
                }
                catch
                {
                    // 换下一个候选路径
                }
            }
            return null;
        }

        /// <summary>保存 key。返回实际写入的完整路径;全部失败抛异常。</summary>
        public static string Save(string key, string providerId = null)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("API key 不能为空。", nameof(key));

            var id = NormalizeProviderId(providerId);
            var blob = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
            var text = Convert.ToBase64String(blob);

            Exception last = null;
            foreach (var path in CandidatePaths(id))
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, text, Encoding.ASCII);
                    return path;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw new IOException("无法写入 key 文件。", last);
        }

        public static void Clear(string providerId = null)
        {
            var id = NormalizeProviderId(providerId);
            foreach (var path in CandidatePaths(id))
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        // ── 内部 ──

        private static string NormalizeProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return DefaultProviderId;
            var id = providerId.Trim().ToLowerInvariant();
            // 只允许安全字符,防止路径穿越
            var sb = new StringBuilder(id.Length);
            foreach (var c in id)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
            }
            var safe = sb.ToString();
            return string.IsNullOrEmpty(safe) ? DefaultProviderId : safe;
        }

        private static string FileNameFor(string providerId)
        {
            return providerId + ".key";
        }

        // 候选路径,按优先级:插件文件夹 -> 用户本地目录。
        private static string[] CandidatePaths(string providerId)
        {
            var fileName = FileNameFor(providerId);
            string pluginDir = null;
            try { pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { }

            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxTools.Agent");

            if (string.IsNullOrEmpty(pluginDir))
                return new[] { Path.Combine(localDir, fileName) };

            return new[]
            {
                Path.Combine(pluginDir, fileName),
                Path.Combine(localDir, fileName)
            };
        }
    }
}
