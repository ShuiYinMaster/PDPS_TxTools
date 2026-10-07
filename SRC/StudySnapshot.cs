using System;
using System.IO;
using Tecnomatix.Engineering;

namespace TxTools.Common
{
    /// <summary>导出独立工程快照，只有实际生成有效文件才返回成功路径。</summary>
    public static class StudySnapshot
    {
        /// <summary>使用经核验的 SDK 导出接口，不调用不存在的 TxDocument.Save。</summary>
        public static string Export(TxDocument document, string directory)
        {
            if (document == null || !Equals(document, TxApplication.ActiveDocument))
                throw new InvalidOperationException("没有有效的活动工程。");
            if (SceneUndoScope.HasActiveTransaction)
                throw new InvalidOperationException("场景事务尚未结束，不能导出恢复快照。");
            var provider = document.PlatformGlobalServicesProvider;
            if (provider == null) throw new InvalidOperationException("无法取得工程导出服务。");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory,
                "study_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N") + ".psz");
            provider.SaveDataToFile(path, TxExportStudyAttributesMode.AllAttributes);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
                throw new IOException("导出未生成有效快照，未建立回滚点。");
            return path;
        }
    }
}
