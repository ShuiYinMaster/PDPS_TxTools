using System;
using System.IO;
using System.Text;

namespace TxTools.CrossEnvIO
{
    internal static class TransferFile
    {
        internal static string SnapshotPath(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath)) throw new ArgumentException("数据文件路径为空");
            string path = Path.GetFullPath(configuredPath);
            if (string.IsNullOrEmpty(Path.GetFileName(path))) throw new ArgumentException("请选择数据文件路径");
            return Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path)
                + "-" + Guid.NewGuid().ToString("N") + ".txt");
        }

        internal static void Write(string file, string content)
        {
            string full = Path.GetFullPath(file);
            string staging = full + ".export-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(staging, content, new UTF8Encoding(true));
                if (File.Exists(full)) File.Replace(staging, full, null);
                else File.Move(staging, full);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        internal static string Field(string value)
        {
            if (value != null && value.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0)
                throw new InvalidOperationException("名称或路径包含 TSV 分隔符，无法安全导出: " + value);
            return value ?? "";
        }
    }
}
