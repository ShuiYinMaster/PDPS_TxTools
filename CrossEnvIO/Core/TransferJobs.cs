using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.CrossEnvIO
{
    public sealed class TransferJob
    {
        public string Id, Fingerprint, State, Message, Study, StudyId, Document, TargetRoot;
        public bool ReloadAttempted,SceneReloaded;
        public string TargetParentId,PszBackup;
        public DateTime UpdatedUtc;
        public Dictionary<string, string> ObjectMap = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>先持久化运行意图再写场景；任务标识重复时只返回状态，不重放场景变更。</summary>
    internal sealed class TransferJobs
    {
        private readonly string _directory;
        private static readonly object Gate = new object();
        public TransferJobs(string directory) { _directory = Path.GetFullPath(directory); Directory.CreateDirectory(_directory); }
        private string FileFor(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 80) throw new ArgumentException("无效任务标识");
            foreach (char c in id) if (!char.IsLetterOrDigit(c) && c != '-') throw new ArgumentException("无效任务标识");
            return Path.Combine(_directory, id + ".json");
        }
        public TransferJob Read(string id)
        {
            lock (Gate)
            {
                string file = FileFor(id);
                return File.Exists(file) ? JsonConvert.DeserializeObject<TransferJob>(File.ReadAllText(file, Encoding.UTF8)) : null;
            }
        }
        public bool Begin(TransferPlan plan, out TransferJob record)
        {
            lock (Gate)
            {
                string stamp = plan.Stamp();
                record = Read(plan.JobId);
                if (record != null)
                {
                    if (record.Fingerprint != stamp) throw new InvalidOperationException("任务标识已用于另一份传输计划，拒绝重复写入");
                    return false;
                }
                record = new TransferJob { Id = plan.JobId, Fingerprint = stamp, State = "running", Message = "已登记，开始场景重建",
                    Study = plan.TargetStudy, StudyId = plan.TargetStudyId, Document = plan.TargetDocument, TargetRoot = plan.TargetRoot };
                Save(record);
                return true;
            }
        }
        public void Save(TransferJob record)
        {
            lock (Gate)
            {
                record.UpdatedUtc = DateTime.UtcNow;
                TransferFile.Write(FileFor(record.Id), JsonConvert.SerializeObject(record, Formatting.Indented));
            }
        }
    }
}
