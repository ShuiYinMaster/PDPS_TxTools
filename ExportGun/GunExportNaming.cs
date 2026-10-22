using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TxTools.ExportGun
{
    internal static class GunExportNaming
    {
        internal static string NextOperationName(string operationName, DateTime exportedAt, ISet<string> usedNames)
        {
            string stem = string.IsNullOrWhiteSpace(operationName) ? "Operation" : operationName.Trim();
            stem = Regex.Replace(stem, @"[/\\:*?""<>|\x00-\x1f]", "_");
            string date = exportedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string marker = "-" + date + "-";
            int largest = 0;
            foreach (string name in usedNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                int at = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                int sequence;
                if (at < 0 || !int.TryParse(name.Substring(at + marker.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out sequence) || sequence < 1) continue;
                string suffix = marker + sequence.ToString("D3", CultureInfo.InvariantCulture);
                string prefix = stem.Substring(0, Math.Min(stem.Length, 80 - suffix.Length));
                if (string.Equals(name.Substring(0, at), prefix, StringComparison.OrdinalIgnoreCase))
                    largest = Math.Max(largest, sequence);
            }
            while (largest < int.MaxValue)
            {
                string suffix = marker + (++largest).ToString("D3", CultureInfo.InvariantCulture);
                string candidate = stem.Substring(0, Math.Min(stem.Length, 80 - suffix.Length)) + suffix;
                if (usedNames.Add(candidate)) return candidate;
            }
            throw new InvalidOperationException("操作导出序号已超出范围。");
        }
    }
}
