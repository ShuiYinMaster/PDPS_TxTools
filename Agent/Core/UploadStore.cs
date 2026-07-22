// TxTools.Agent / Core / UploadStore.cs
// 用户上传文件的存储与元数据管理。
// 存储策略:
//   - 每个文件写到 %TEMP%\TxTools.Agent\uploads\{convId}\{fileId}_{safeName}
//   - 内存维护 fileId → UploadedFile 的字典(所有元数据)
//   - 切对话时不删旧对话的文件(可能用户还想切回来引用),关窗时统一清理
//   - 同一对话内文件累积,可以在多轮对话中重复引用
//
// 生命周期:
//   Store(convId, filename, bytes)              上传时调用,返回 UploadedFile(含 id、path、Size)
//   Get(fileId)                                  按 id 查
//   ByConv(convId)                               列出某对话所有已上传文件
//   ClearConversation(convId)                    清某对话的所有文件
//   ClearAll()                                   关窗时清全部(删物理目录 + 清字典)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TxTools.Agent.Core
{
    public sealed class UploadedFile
    {
        public string Id { get; set; }
        public string ConvId { get; set; }
        public string OriginalName { get; set; }
        public string LocalPath { get; set; }
        /// <summary>小写扩展名带点,例:.xlsx / .csv / .txt / .md / .json / .xml / (空)</summary>
        public string Extension { get; set; }
        public long Size { get; set; }
        public DateTime UploadedUtc { get; set; }

        /// <summary>解析后的简短摘要(注入到用户消息前缀 + 展示在附件卡片)。</summary>
        public string ParsedSummary { get; set; }
        /// <summary>解析失败时的错误说明。ParsedSummary 会同时置为一个安全的占位。</summary>
        public string ParseError { get; set; }

        /// <summary>xlsx 才有:表格 sheet 数。其他类型 = 0。</summary>
        public int SheetCount { get; set; }
        /// <summary>表格类型(xlsx/csv)才有:主 sheet 或全表的行数。其他 = 0。</summary>
        public int RowCount { get; set; }
        /// <summary>表格类型才有:主 sheet 列数。</summary>
        public int ColCount { get; set; }
    }

    public static class UploadStore
    {
        private const string RootFolderName = "TxTools.Agent";
        private const string UploadsSubfolder = "uploads";

        private static readonly Dictionary<string, UploadedFile> _byId
            = new Dictionary<string, UploadedFile>(StringComparer.Ordinal);

        // 值都为 List<string>(fileId),分组便于按 convId 快速枚举/清理
        private static readonly Dictionary<string, List<string>> _byConv
            = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>
        /// 保存上传的二进制内容,返回 UploadedFile。不做解析(由 FileParserService.Parse 补充)。
        /// 若 convId 为 null/空,存到 "_default" 桶,关窗时一并清理。
        /// </summary>
        public static UploadedFile Store(string convId, string originalFileName, byte[] content)
        {
            if (content == null) content = new byte[0];
            if (string.IsNullOrWhiteSpace(originalFileName)) originalFileName = "unnamed";
            if (string.IsNullOrWhiteSpace(convId)) convId = "_default";

            var safeName = SanitizeFileName(originalFileName);
            var ext = (Path.GetExtension(safeName) ?? "").ToLowerInvariant();
            var id = "file_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);

            var dir = ConversationDir(convId);
            Directory.CreateDirectory(dir);
            var localPath = Path.Combine(dir, id + "_" + safeName);

            File.WriteAllBytes(localPath, content);

            var uf = new UploadedFile
            {
                Id = id,
                ConvId = convId,
                OriginalName = originalFileName,
                LocalPath = localPath,
                Extension = ext,
                Size = content.Length,
                UploadedUtc = DateTime.UtcNow
            };

            _byId[id] = uf;
            List<string> bucket;
            if (!_byConv.TryGetValue(convId, out bucket))
            {
                bucket = new List<string>();
                _byConv[convId] = bucket;
            }
            bucket.Add(id);

            return uf;
        }

        public static UploadedFile Get(string fileId)
        {
            if (string.IsNullOrEmpty(fileId)) return null;
            UploadedFile uf;
            return _byId.TryGetValue(fileId, out uf) ? uf : null;
        }

        public static List<UploadedFile> ByConv(string convId)
        {
            if (string.IsNullOrEmpty(convId)) convId = "_default";
            List<string> bucket;
            if (!_byConv.TryGetValue(convId, out bucket)) return new List<UploadedFile>();
            var list = new List<UploadedFile>(bucket.Count);
            foreach (var id in bucket)
            {
                UploadedFile uf;
                if (_byId.TryGetValue(id, out uf)) list.Add(uf);
            }
            return list;
        }

        public static bool Remove(string fileId)
        {
            if (string.IsNullOrEmpty(fileId)) return false;
            UploadedFile uf;
            if (!_byId.TryGetValue(fileId, out uf)) return false;
            _byId.Remove(fileId);

            List<string> bucket;
            if (_byConv.TryGetValue(uf.ConvId ?? "_default", out bucket))
                bucket.Remove(fileId);

            try { if (File.Exists(uf.LocalPath)) File.Delete(uf.LocalPath); }
            catch { }
            return true;
        }

        public static void ClearConversation(string convId)
        {
            if (string.IsNullOrEmpty(convId)) return;
            List<string> bucket;
            if (!_byConv.TryGetValue(convId, out bucket)) return;
            foreach (var id in bucket.ToList())
            {
                UploadedFile uf;
                if (_byId.TryGetValue(id, out uf))
                {
                    try { if (File.Exists(uf.LocalPath)) File.Delete(uf.LocalPath); } catch { }
                    _byId.Remove(id);
                }
            }
            _byConv.Remove(convId);

            try
            {
                var dir = ConversationDir(convId);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }

        /// <summary>关窗时清理:删所有上传物理文件 + 清空字典。</summary>
        public static void ClearAll()
        {
            _byId.Clear();
            _byConv.Clear();
            try
            {
                var root = UploadsRoot();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch { }
        }

        // ── 路径与文件名清理 ──

        private static string UploadsRoot()
        {
            return Path.Combine(Path.GetTempPath(), RootFolderName, UploadsSubfolder);
        }

        private static string ConversationDir(string convId)
        {
            return Path.Combine(UploadsRoot(), SanitizeFileName(convId));
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            var s = sb.ToString();
            // 避免过长文件名
            if (s.Length > 120) s = s.Substring(0, 120);
            return s;
        }
    }
}
