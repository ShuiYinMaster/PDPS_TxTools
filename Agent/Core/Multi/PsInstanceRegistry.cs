// TxTools.Agent / Core / Multi / PsInstanceRegistry.cs
//
// 多 PDPS 实例的发现与角色仲裁。
//
// ── 为什么必须"脑出去、执行器留下" ──
//   Tecnomatix API 只能在进程内主线程调用，没有任何办法从外部驱动。
//   所以插件必须留在每个 PDPS 里；能搬出去的只是 agent 的决策部分。
//
//        TxAgent 主控(脑) —— 全局只有一份
//           ├─ 命名管道 ─→ PDPS #1 插件(执行器)
//           └─ 命名管道 ─→ PDPS #2 插件(执行器)
//
//   主控自己也宿在某个 PDPS 里，不需要单独做个应用。
//
// ── 角色仲裁 ──
//   先启动的那个抢到主控，后启动的检测到已有主控就退化成纯执行器。
//   这顺带解决了"两个窗口各开一个 agent 各写各的"那个问题:
//   全局只有一个脑，对话数据自然只有一份。
//
// ── 为什么用文件注册而不是服务发现 ──
//   同机、少量实例、无网络。文件注册零依赖、可肉眼排查，
//   进程崩了留下的残留项靠"进程是否存活"判掉即可。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    public sealed class PsInstanceInfo
    {
        /// <summary>PDPS 进程号。同时也是管道名的后缀。</summary>
        public int Pid { get; set; }

        /// <summary>用户可读的环境名。默认取 study 名，重名时补序号。</summary>
        public string Name { get; set; }

        /// <summary>当前打开的 study。</summary>
        public string Study { get; set; }

        /// <summary>是否为主控(脑)。全局应只有一个 true。</summary>
        public bool IsBrain { get; set; }

        /// <summary>本实例是否已打开 Agent 窗口。全局应至多一个 true —— 避免两窗口各自写会话互相覆盖。</summary>
        public bool HasWindow { get; set; }

        public DateTime HeartbeatUtc { get; set; }

        [JsonIgnore]
        public string PipeName { get { return PsInstanceRegistry.PipeNameFor(Pid); } }

        [JsonIgnore]
        public bool IsSelf { get { return Pid == PsInstanceRegistry.SelfPid; } }

        /// <summary>进程还在 且 心跳没过期。两条都要查:强杀会留下未过期的死记录。</summary>
        [JsonIgnore]
        public bool IsAlive
        {
            get
            {
                if (DateTime.UtcNow - HeartbeatUtc > PsInstanceRegistry.Ttl) return false;
                try { Process.GetProcessById(Pid); return true; }
                catch { return false; }
            }
        }

        public override string ToString()
        {
            return Name + " [pid " + Pid + "]" + (IsBrain ? " (主控)" : "");
        }
    }

    public static class PsInstanceRegistry
    {
        public static readonly int SelfPid = Process.GetCurrentProcess().Id;

        /// <summary>心跳过期时间。超过即认为该实例已不可用。</summary>
        public static TimeSpan Ttl = TimeSpan.FromSeconds(45);

        public static string PipeNameFor(int pid) { return "TxAgent_PS_" + pid; }

        private static string Dir()
        {
            var d = Path.Combine(Path.GetTempPath(), "TxAgent.Instances");
            Directory.CreateDirectory(d);
            return d;
        }

        private static string PathFor(int pid)
        {
            return Path.Combine(Dir(), pid + ".json");
        }

        // ── 注册与心跳 ──

        /// <summary>
        /// 跨进程互斥:保护"读全部 + 判定主控 + 写自己"这段 ——
        /// 两个进程同时首次注册时有极小概率都判自己是主控(检查与写入非原子)。
        /// 用 Local\ 会话级命名互斥体(PDPS 同机同会话);不用 Global\ 避免权限问题。
        /// </summary>
        private static readonly System.Threading.Mutex RegMutex =
            new System.Threading.Mutex(false, @"Local\TxAgent_Registry_Mutex");

        /// <summary>
        /// 注册本实例。返回本实例最终的角色 —— 已有活着的主控时自动退为执行器。
        /// 【每次心跳都要重新判定】主控进程崩掉后，剩下的执行器应该有人顶上。
        /// </summary>
        public static PsInstanceInfo Register(string study, bool wantBrain)
        {
            PsInstanceInfo me = null;
            bool gotLock = false;
            try
            {
                // 心跳(30s 一次)也会走这里,5 秒拿不到锁说明另一个进程正卡在文件 I/O,
                // 放弃本次重判即可 —— 下次心跳再来。
                try { gotLock = RegMutex.WaitOne(5000); }
                catch (System.Threading.AbandonedMutexException) { gotLock = true; }   // 上一个持有者崩溃,锁已释放
                if (!gotLock) return FallbackSelf(study, wantBrain);

                try
                {
                    var all = All();
                    var existingBrain = all.FirstOrDefault(x => x.IsBrain && x.IsAlive && !x.IsSelf);

                    me = all.FirstOrDefault(x => x.IsSelf) ?? new PsInstanceInfo { Pid = SelfPid };
                    me.Study = study;
                    me.HeartbeatUtc = DateTime.UtcNow;
                    me.IsBrain = wantBrain && existingBrain == null;

                    if (string.IsNullOrWhiteSpace(me.Name))
                        me.Name = MakeName(study, all);

                    File.WriteAllText(PathFor(SelfPid),
                        JsonConvert.SerializeObject(me, Formatting.Indented), Encoding.UTF8);
                }
                finally { RegMutex.ReleaseMutex(); }
            }
            catch { /* 锁或 I/O 异常时走兜底:尽量把本实例写出去 */ }

            if (me == null) me = FallbackSelf(study, wantBrain);
            return me;
        }

        /// <summary>拿不到互斥锁时的兜底:不重判角色,仅刷新心跳,沿用上次判定。</summary>
        private static PsInstanceInfo FallbackSelf(string study, bool wantBrain)
        {
            var me = All().FirstOrDefault(x => x.IsSelf) ?? new PsInstanceInfo { Pid = SelfPid };
            me.Study = study;
            me.HeartbeatUtc = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(me.Name))
                me.Name = MakeName(study, All());
            try
            {
                File.WriteAllText(PathFor(SelfPid),
                    JsonConvert.SerializeObject(me, Formatting.Indented), Encoding.UTF8);
            }
            catch { }
            return me;
        }

        // ── Agent 窗口独占 ──
        // 目的:同时开两个 PDPS 时，Agent 窗口全局至多开一个。
        // 晚打开的那个显示提示信息，不进入对话界面 —— 避免两个窗口
        // 各自加载同一会话、SaveCurrent 整份覆盖互相抹掉。
        //
        // 用与 RegMutex 同款的跨进程命名互斥体串行化"查-置"，
        // 避免两个进程同时点开都各自抢到窗口。

        private static readonly System.Threading.Mutex WindowMutex =
            new System.Threading.Mutex(false, @"Local\TxAgent_Window_Mutex");

        /// <summary>
        /// 尝试独占 Agent 窗口。成功返回 true（本进程是唯一窗口）；
        /// 失败返回 false（已有其它活进程开着窗口）。
        /// </summary>
        public static bool TryAcquireWindow()
        {
            bool gotLock = false;
            try
            {
                try { gotLock = WindowMutex.WaitOne(5000); }
                catch (System.Threading.AbandonedMutexException) { gotLock = true; }
                if (!gotLock) return true;   // 拿不到锁：不阻断，允许打开（宁可不误伤）

                try
                {
                    var live = Live();
                    foreach (var i in live)
                    {
                        if (i.HasWindow && i.IsAlive && !i.IsSelf)
                            return false;
                    }

                    var me = Self() ?? new PsInstanceInfo { Pid = SelfPid };
                    me.HasWindow = true;
                    me.HeartbeatUtc = DateTime.UtcNow;
                    if (string.IsNullOrWhiteSpace(me.Name))
                        me.Name = MakeName(me.Study, All());
                    File.WriteAllText(PathFor(SelfPid),
                        JsonConvert.SerializeObject(me, Formatting.Indented), Encoding.UTF8);
                    return true;
                }
                finally { WindowMutex.ReleaseMutex(); }
            }
            catch { return true; }   // 锁机制失效时不阻断正常使用
        }

        /// <summary>释放窗口独占（窗口关闭时调用）。</summary>
        public static void ReleaseWindow()
        {
            try
            {
                var me = Self();
                if (me == null) return;
                me.HasWindow = false;
                File.WriteAllText(PathFor(SelfPid),
                    JsonConvert.SerializeObject(me, Formatting.Indented), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>心跳。定期调用，否则别的实例会认为本进程已死。</summary>
        public static void Heartbeat(string study)
        {
            Register(study, IsSelfBrain());
        }

        public static void Unregister()
        {
            try { var p = PathFor(SelfPid); if (File.Exists(p)) File.Delete(p); }
            catch { }
        }

        // ── 查询 ──

        /// <summary>全部注册项(含已死的，调用方按需过滤)。</summary>
        public static List<PsInstanceInfo> All()
        {
            var list = new List<PsInstanceInfo>();
            try
            {
                foreach (var f in Directory.GetFiles(Dir(), "*.json"))
                {
                    try
                    {
                        var info = JsonConvert.DeserializeObject<PsInstanceInfo>(
                            File.ReadAllText(f, Encoding.UTF8));
                        if (info != null && info.Pid > 0) list.Add(info);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        /// <summary>活着的实例，按名称排序。顺手清掉死记录。</summary>
        public static List<PsInstanceInfo> Live()
        {
            var all = All();
            var live = new List<PsInstanceInfo>();

            foreach (var i in all)
            {
                if (i.IsAlive) { live.Add(i); continue; }
                try { File.Delete(PathFor(i.Pid)); } catch { }   // 顺手打扫
            }

            return live.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static PsInstanceInfo Self()
        {
            return All().FirstOrDefault(x => x.IsSelf);
        }

        public static bool IsSelfBrain()
        {
            var me = Self();
            return me != null && me.IsBrain;
        }

        public static PsInstanceInfo Brain()
        {
            return Live().FirstOrDefault(x => x.IsBrain);
        }

        /// <summary>按名称或 pid 找一个实例。</summary>
        public static PsInstanceInfo Find(string nameOrPid)
        {
            if (string.IsNullOrWhiteSpace(nameOrPid)) return null;
            var live = Live();

            int pid;
            if (int.TryParse(nameOrPid, out pid))
            {
                var byPid = live.FirstOrDefault(x => x.Pid == pid);
                if (byPid != null) return byPid;
            }

            return live.FirstOrDefault(x =>
                       string.Equals(x.Name, nameOrPid, StringComparison.OrdinalIgnoreCase))
                ?? live.FirstOrDefault(x => x.Name != null &&
                       x.Name.IndexOf(nameOrPid, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>重命名本实例。study 同名时靠它区分。</summary>
        public static void Rename(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return;
            var me = Self();
            if (me == null) return;
            me.Name = newName.Trim();
            try
            {
                File.WriteAllText(PathFor(SelfPid),
                    JsonConvert.SerializeObject(me, Formatting.Indented), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>取 study 名作环境名;重名时补序号，否则模型没法区分两个环境。</summary>
        private static string MakeName(string study, List<PsInstanceInfo> others)
        {
            var b = string.IsNullOrWhiteSpace(study) ? "PS" : study.Trim();
            var used = new HashSet<string>(
                others.Where(x => !x.IsSelf && x.IsAlive).Select(x => x.Name ?? ""),
                StringComparer.OrdinalIgnoreCase);

            if (!used.Contains(b)) return b;
            for (int i = 2; i < 20; i++)
                if (!used.Contains(b + "#" + i)) return b + "#" + i;

            return b + "#" + SelfPid;
        }
    }
}
