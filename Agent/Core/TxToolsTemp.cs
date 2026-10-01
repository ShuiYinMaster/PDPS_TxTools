// TxTools.Agent / Core / TxToolsTemp.cs
// 所有插件的临时文件统一入口。

using System;
using System.IO;
using System.Text;

namespace TxTools.Agent.Core
{
    /// <summary>
    /// TxTools 运行期临时目录策略。
    /// 默认使用 %TEMP%\TxTools；如果该路径含非 ASCII 字符，则回退到纯 ASCII 路径，
    /// 以兼容 Process Simulate/CATIA 某些仍使用窄字符接口的调用。
    /// </summary>
    public static class TxToolsTemp
    {
        public const string RootEnvironmentVariable = "TXTOOLS_TEMP_ROOT";
        private const string DefaultRootFolder = "TxTools";
        private static readonly object Sync = new object();
        private static string _rootPath;

        /// <summary>统一临时文件根目录。可通过 TXTOOLS_TEMP_ROOT 覆盖，但必须是绝对 ASCII 路径。</summary>
        public static string RootPath
        {
            get
            {
                if (_rootPath != null) return _rootPath;
                lock (Sync)
                {
                    if (_rootPath == null) _rootPath = ResolveRootPath();
                    return _rootPath;
                }
            }
        }

        /// <summary>返回并创建某个插件的临时目录，可继续传入子目录名。</summary>
        public static string DirectoryFor(string pluginName, params string[] parts)
        {
            var path = Path.Combine(RootPath, SafeSegment(pluginName, "Common"));
            if (parts != null)
            {
                foreach (var part in parts)
                    path = Path.Combine(path, SafeSegment(part, "data"));
            }

            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>返回某个插件临时目录下的文件路径，并确保目录存在。</summary>
        public static string FileFor(string pluginName, string fileName)
        {
            return Path.Combine(DirectoryFor(pluginName), SafeFileName(fileName));
        }

        /// <summary>返回某个插件子目录下的文件路径，并确保目录存在。</summary>
        public static string FileFor(string pluginName, string subDirectory, string fileName)
        {
            return Path.Combine(DirectoryFor(pluginName, subDirectory), SafeFileName(fileName));
        }

        /// <summary>创建带时间和随机后缀的独立会话目录。</summary>
        public static string SessionDirectory(string pluginName, string prefix)
        {
            var name = SafeSegment(prefix, "session") + "_"
                     + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_"
                     + Guid.NewGuid().ToString("N").Substring(0, 8);
            return DirectoryFor(pluginName, name);
        }

        private static string ResolveRootPath()
        {
            var configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
            {
                try
                {
                    var full = Path.GetFullPath(configured.Trim());
                    if (IsAscii(full)) return full;
                }
                catch
                {
                    // 非法环境变量不应阻断插件加载，继续走默认策略。
                }
            }

            var systemTemp = Path.Combine(Path.GetTempPath(), DefaultRootFolder);
            if (IsAscii(systemTemp)) return systemTemp;

            var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(commonData))
            {
                var commonTemp = Path.Combine(commonData, DefaultRootFolder, "Temp");
                if (IsAscii(commonTemp)) return commonTemp;
            }

            // 最后的兼容兜底：保证导入组件时路径仍然是纯 ASCII。
            return @"C:\TxToolsTemp";
        }

        private static bool IsAscii(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (var c in value)
                if (c > 127) return false;
            return true;
        }

        private static string SafeSegment(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var sb = new StringBuilder(value.Length);
            foreach (var c in value.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') sb.Append(c);
                else sb.Append('_');
            }

            var result = sb.ToString().Trim('.', ' ');
            return result == "." || result == ".." || result.Length == 0 ? fallback : result;
        }

        private static string SafeFileName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "unnamed.tmp" : Path.GetFileName(value.Trim());
            if (string.IsNullOrEmpty(name)) name = "unnamed.tmp";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.Length == 0 ? "unnamed.tmp" : sb.ToString();
        }
    }
}
