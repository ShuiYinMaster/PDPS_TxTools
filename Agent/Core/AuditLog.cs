// TxTools.Agent / Core / AuditLog.cs
// 变更类工具的审计日志：每次变更操作(审批通过/拒绝/执行结果)追加一行到插件文件夹 audit.log。
// 尽力而为，失败静默(不影响对话)。路径策略与其他 Store 一致。

using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace TxTools.Agent.Core
{
    public static class AuditLog
    {
        private const string FileName = "audit.log";

        // 日志可能被多个后台线程(LLM 流式、工具线程)同时写,不加锁会互相撞文件,
        // 抛 "file in use" IOException 导致条目静默丢失,或写入交叉错乱。
        private static readonly object _sync = new object();

        public static void Write(string line)
        {
            try
            {
                var entry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine;
                lock (_sync)
                {
                    foreach (var path in CandidatePaths())
                    {
                        try
                        {
                            var dir = Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            File.AppendAllText(path, entry, Encoding.UTF8);
                            return;
                        }
                        catch { /* 下一个候选 */ }
                    }
                }
            }
            catch { /* 审计失败不影响主流程 */ }
        }

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
