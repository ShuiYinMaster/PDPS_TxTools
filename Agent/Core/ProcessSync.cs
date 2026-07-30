// TxTools.Agent / Core / ProcessSync.cs
//
// 多进程协同。
//
// 场景:同时开两个 PDPS，各自跑一份 TxAgent，共享同一份磁盘数据
// (conversations/、memory/、recipes.json …)。
//
// ── 先解决"互相覆盖"，再谈"同步" ──
//   两个进程当前会在两处必然打架:
//     1. 启动都取最新对话打开 → 各自往 _fullHistory 追加 → SaveCurrent 整份覆盖，
//        后保存的把先保存的消息全抹掉。这不是概率问题，是每次都发生。
//     2. NewId() 只精确到毫秒，同时新建就是同一个 id。
//   数据丢了再同步没有意义，所以隔离优先。
//
// ── 三个机制 ──
//   InstanceId    每个进程一个稳定标识，写进锁文件和新建的 id 里
//   会话占用锁    {id}.lock 记录占用者，另一个进程不会自动打开它
//   变更监听      FileSystemWatcher，对方新建/更新时刷新本地列表与缓存
//
// 都基于文件系统，不引入任何 IPC 依赖 —— 两个进程本来就共享目录，
// 用文件做协调是最省事也最不容易出错的方式。

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace TxTools.Agent.Core
{
    public static class ProcessSync
    {
        /// <summary>本进程的稳定标识:进程号 + 启动时刻，重启后必然不同。</summary>
        public static readonly string InstanceId =
            Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
            + "-" + DateTime.UtcNow.ToString("HHmmssfff");

        /// <summary>
        /// 锁文件多久算过期。进程被强杀时锁文件会残留，
        /// 光看"文件存在"会导致对话永远打不开，所以要有过期时间 + 进程存活校验。
        /// </summary>
        public static TimeSpan LockTtl = TimeSpan.FromMinutes(3);

        // ── 跨进程互斥 ──

        /// <summary>
        /// 用具名 Mutex 串行化整份重写类的写操作(vectors.json / recipes.json 这种)。
        /// 一物一文件的存储(memory/snippets/*.md 等)不需要 —— 它们本来就不会整份重写。
        /// </summary>
        public static T WithFileLock<T>(string name, Func<T> action, int timeoutMs = 5000)
        {
            var mutexName = @"Global\TxAgent_" + Sanitize(name);
            Mutex mutex = null;
            bool held = false;

            try
            {
                mutex = new Mutex(false, mutexName);
                try { held = mutex.WaitOne(timeoutMs); }
                catch (AbandonedMutexException) { held = true; }   // 上一个持有者挂了，锁归我们

                return action();
            }
            finally
            {
                if (mutex != null)
                {
                    if (held) { try { mutex.ReleaseMutex(); } catch { } }
                    try { mutex.Dispose(); } catch { }
                }
            }
        }

        public static void WithFileLock(string name, Action action, int timeoutMs = 5000)
        {
            WithFileLock<object>(name, delegate { action(); return null; }, timeoutMs);
        }

        // ── 会话占用锁 ──

        /// <summary>标记本进程正在使用某会话。返回是否成功占用。</summary>
        public static bool AcquireConversation(string dir, string convId)
        {
            try
            {
                var p = LockPath(dir, convId);
                var owner = ReadOwner(p);

                // 已被别的活进程占着
                if (owner != null && !owner.IsMine && owner.IsAlive) return false;

                File.WriteAllText(p,
                    InstanceId + "|" + DateTime.UtcNow.ToString("o"), Encoding.ASCII);
                return true;
            }
            catch { return true; }   // 锁机制失效时不阻断正常使用
        }

        /// <summary>心跳:定期调用，让锁不过期。切换/关闭时调 Release。</summary>
        public static void RenewConversation(string dir, string convId)
        {
            AcquireConversation(dir, convId);
        }

        public static void ReleaseConversation(string dir, string convId)
        {
            try
            {
                var p = LockPath(dir, convId);
                var owner = ReadOwner(p);
                if (owner != null && !owner.IsMine) return;   // 不是自己的锁别删
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        /// <summary>该会话是否被【别的活进程】占用。</summary>
        public static bool IsHeldByOther(string dir, string convId)
        {
            try
            {
                var owner = ReadOwner(LockPath(dir, convId));
                return owner != null && !owner.IsMine && owner.IsAlive;
            }
            catch { return false; }
        }

        private sealed class LockOwner
        {
            public string Instance;
            public DateTime StampUtc;
            public bool IsMine { get { return string.Equals(Instance, InstanceId, StringComparison.Ordinal); } }

            /// <summary>未过期 且 进程还在。两条都要查:强杀会留下未过期的死锁文件。</summary>
            public bool IsAlive
            {
                get
                {
                    if (DateTime.UtcNow - StampUtc > LockTtl) return false;

                    var dash = Instance.IndexOf('-');
                    if (dash <= 0) return true;

                    int pid;
                    if (!int.TryParse(Instance.Substring(0, dash), out pid)) return true;

                    try { Process.GetProcessById(pid); return true; }
                    catch { return false; }   // 进程没了
                }
            }
        }

        private static LockOwner ReadOwner(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var parts = File.ReadAllText(path, Encoding.ASCII).Split('|');
                if (parts.Length < 2) return null;

                DateTime t;
                if (!DateTime.TryParse(parts[1], null,
                        DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out t))
                    return null;

                return new LockOwner { Instance = parts[0], StampUtc = t };
            }
            catch { return null; }
        }

        private static string LockPath(string dir, string convId)
        {
            var locks = Path.Combine(dir, ".locks");
            Directory.CreateDirectory(locks);
            return Path.Combine(locks, Sanitize(convId) + ".lock");
        }

        // ── 变更监听 ──

        /// <summary>
        /// 监听目录变化。对方新建对话、改了记忆文件时触发。
        ///
        /// 【务必去抖】一次保存会连续触发 Changed 好几下(写内容、写属性、改时间戳)，
        /// 不去抖的话 UI 会被刷屏。
        /// </summary>
        public sealed class Watcher : IDisposable
        {
            private readonly FileSystemWatcher _fsw;
            private readonly Timer _debounce;
            private readonly Action _onChanged;
            private const int DebounceMs = 800;

            public Watcher(string dir, string filter, Action onChanged)
            {
                _onChanged = onChanged;
                _debounce = new Timer(delegate { Fire(); }, null, Timeout.Infinite, Timeout.Infinite);

                Directory.CreateDirectory(dir);
                _fsw = new FileSystemWatcher(dir, filter)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    IncludeSubdirectories = false
                };

                FileSystemEventHandler h = delegate { Schedule(); };
                _fsw.Created += h;
                _fsw.Changed += h;
                _fsw.Deleted += h;
                _fsw.Renamed += delegate { Schedule(); };
                _fsw.EnableRaisingEvents = true;
            }

            private void Schedule()
            {
                try { _debounce.Change(DebounceMs, Timeout.Infinite); } catch { }
            }

            private void Fire()
            {
                try { if (_onChanged != null) _onChanged(); } catch { }
            }

            public void Dispose()
            {
                try { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); } catch { }
                try { _debounce.Dispose(); } catch { }
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "x";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }
    }
}
