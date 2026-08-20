// TxToolsMcpBridge / InstanceDiscovery.cs
//
// 读取 PS 进程内 PsInstanceRegistry 写入的实例注册文件(%TEMP%\TxAgent.Instances\<pid>.json),
// 枚举存活实例并选择一个目标。与 Agent/Core/Multi/PsInstanceRegistry.cs 的格式保持一致。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace TxToolsMcpBridge
{
    public sealed class InstanceInfo
    {
        public int Pid { get; set; }
        public string Name { get; set; }
        public string Study { get; set; }
        public bool IsBrain { get; set; }
        public DateTime HeartbeatUtc { get; set; }

        [JsonIgnore]
        public bool IsAlive
        {
            get
            {
                if (DateTime.UtcNow - HeartbeatUtc > InstanceDiscovery.Ttl) return false;
                try { Process.GetProcessById(Pid); return true; }
                catch { return false; }
            }
        }
    }

    public static class InstanceDiscovery
    {
        /// <summary>心跳过期时间。与 PsInstanceRegistry.Ttl 保持一致。</summary>
        public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);

        private static string Dir()
        {
            var d = Path.Combine(Path.GetTempPath(), "TxAgent.Instances");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        public static List<InstanceInfo> All()
        {
            var list = new List<InstanceInfo>();
            try
            {
                foreach (var f in Directory.GetFiles(Dir(), "*.json"))
                {
                    try
                    {
                        var info = JsonConvert.DeserializeObject<InstanceInfo>(File.ReadAllText(f, Encoding.UTF8));
                        if (info != null && info.Pid > 0) list.Add(info);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        public static List<InstanceInfo> Live()
        {
            return All().Where(x => x.IsAlive)
                        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
        }

        /// <summary>
        /// 选择目标实例。nameOrPid 为空时:
        ///   优先主控实例;无主控时若只有一个存活实例则选它,否则返回 null(提示用 list)。
        /// nameOrPid 非空时按环境名或 pid 匹配(名称支持前缀模糊)。
        /// </summary>
        public static InstanceInfo Select(string nameOrPid)
        {
            var live = Live();
            if (live.Count == 0) return null;

            if (string.IsNullOrWhiteSpace(nameOrPid))
            {
                var brain = live.FirstOrDefault(x => x.IsBrain);
                if (brain != null) return brain;
                return live.Count == 1 ? live[0] : null;
            }

            nameOrPid = nameOrPid.Trim();
            int pid;
            if (int.TryParse(nameOrPid, out pid))
            {
                var byPid = live.FirstOrDefault(x => x.Pid == pid);
                if (byPid != null) return byPid;
            }

            var byName = live.FirstOrDefault(x =>
                string.Equals(x.Name, nameOrPid, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;

            var byPrefix = live.FirstOrDefault(x =>
                x.Name != null && x.Name.IndexOf(nameOrPid, StringComparison.OrdinalIgnoreCase) >= 0);
            return byPrefix;
        }

        public static string Describe()
        {
            var live = Live();
            if (live.Count == 0) return "当前没有可用 PDPS 实例。";
            return string.Join("; ", live.Select(x =>
                x.Name + " (pid " + x.Pid + ", study=" + (x.Study ?? "未打开") + (x.IsBrain ? ", 主控" : "") + ")"));
        }
    }
}