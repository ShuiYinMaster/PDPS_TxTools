using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    internal sealed class DesktopPetSnapshot
    {
        public int OwnerPid;
        public long OwnerStart;
        public int HelperPid;
        public long HelperStart;
        public int ForegroundPid;
        public bool Visible = true;
        public string State;
        public string Task;
    }

    // One session-wide lease, independent of which PS process contains the Agent window.
    // All mutations are short transactions. No mutex is held by a UI thread for its lifetime.
    internal static class DesktopPetSession
    {
        static DesktopPetSession() { }
        internal static readonly string SessionKey = Environment.GetEnvironmentVariable("TXAGENT_PET_TEST_SESSION")
            ?? Process.GetCurrentProcess().SessionId.ToString();
        private static readonly Mutex Gate = new Mutex(false, @"Local\TxAgent_DesktopPet_" + SessionKey);
        internal static string DirectoryPath => Path.Combine(Path.GetTempPath(), "TxAgentDesktopPet", SessionKey);
        internal static string ProfilePath => Path.Combine(DirectoryPath, "profile");
        private static string FilePath => Path.Combine(DirectoryPath, "session.json");
        internal static Func<int> ForegroundPidGetter = NativeForegroundPid;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        private static int NativeForegroundPid()
        {
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return (int)pid;
        }

        // Observe activation events as well as polling: a user can switch PS and click
        // the focusable pet menu before the next timer tick. Only the elected host watches.
        internal static IDisposable WatchForeground()
        {
            if (Environment.GetEnvironmentVariable("TXAGENT_PET_TEST_SESSION") != null) return null;
            return new ForegroundWatch();
        }

        internal static void ObserveForeground(int eventPid)
        {
            var instances = PsInstanceRegistry.All();
            Transaction(value =>
            {
                if (value.OwnerPid != PsInstanceRegistry.SelfPid) return;
                int current = ForegroundPidGetter();
                // Delayed activation callbacks must not overwrite a newer foreground app.
                int observed = current == eventPid || current == value.HelperPid ? eventPid : current;
                value.ForegroundPid = ResolveForeground(observed, instances, value);
            });
        }

        private sealed class ForegroundWatch : IDisposable
        {
            private delegate void WinEventProc(IntPtr hook, uint kind, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);
            [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
            [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
            private readonly WinEventProc _callback;
            private IntPtr _hook;
            internal ForegroundWatch()
            {
                _callback = (hook, kind, hwnd, objectId, childId, threadId, time) =>
                {
                    try { uint pid; GetWindowThreadProcessId(hwnd, out pid); ObserveForeground((int)pid); }
                    catch (Exception ex) { Debug.WriteLine("[TxAgent pet foreground] " + ex.Message); }
                };
                _hook = SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out of context
                if (_hook == IntPtr.Zero) Debug.WriteLine("[TxAgent pet foreground] Activation hook unavailable; using polling.");
            }
            public void Dispose() { if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; } }
        }

        private static long StartTime(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return process.HasExited ? 0 : process.StartTime.ToUniversalTime().Ticks; }
            catch { return 0; }
        }

        private static DesktopPetSnapshot Transaction(Action<DesktopPetSnapshot> change, bool initialVisible = true)
        {
            bool acquired = false;
            try
            {
                try { acquired = Gate.WaitOne(1000); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("桌宠共享状态暂时忙碌，请稍后重试。");
                Directory.CreateDirectory(DirectoryPath);
                DesktopPetSnapshot value;
                if (File.Exists(FilePath))
                    value = JsonConvert.DeserializeObject<DesktopPetSnapshot>(File.ReadAllText(FilePath, Encoding.UTF8))
                        ?? throw new IOException("桌宠共享状态无效。");
                else value = new DesktopPetSnapshot { Visible = initialVisible };
                string before = JsonConvert.SerializeObject(value);
                change(value);
                string after = JsonConvert.SerializeObject(value);
                if (before != after || !File.Exists(FilePath))
                {
                    string temporary = FilePath + "." + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.WriteAllText(temporary, after, new UTF8Encoding(false));
                        if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                        else File.Move(temporary, FilePath);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                return value;
            }
            finally { if (acquired) Gate.ReleaseMutex(); }
        }

        internal static int ResolveForeground(int foregroundPid, IList<PsInstanceInfo> instances, DesktopPetSnapshot snapshot)
        {
            if (instances.Any(i => i.Pid == foregroundPid && i.IsAlive)) return foregroundPid;
            // Focusing the pet menu does not replace the PS selected immediately before it.
            if (foregroundPid == snapshot.HelperPid && snapshot.HelperStart != 0
                && StartTime(snapshot.HelperPid) == snapshot.HelperStart
                && instances.Any(i => i.Pid == snapshot.ForegroundPid && i.IsAlive)) return snapshot.ForegroundPid;
            return 0;
        }

        internal static DesktopPetSnapshot Poll(bool initialVisible)
        {
            var instances = PsInstanceRegistry.All();
            return Transaction(value =>
            {
                value.ForegroundPid = ResolveForeground(ForegroundPidGetter(), instances, value);
                if (value.OwnerStart == 0 || StartTime(value.OwnerPid) != value.OwnerStart)
                {
                    value.OwnerPid = PsInstanceRegistry.SelfPid;
                    value.OwnerStart = StartTime(value.OwnerPid);
                    value.State = null; value.Task = null;
                }
            }, initialVisible);
        }

        internal static PsInstanceInfo ForegroundTarget()
        {
            var instances = PsInstanceRegistry.All();
            var value = Transaction(s => s.ForegroundPid = ResolveForeground(ForegroundPidGetter(), instances, s));
            return instances.FirstOrDefault(i => i.Pid == value.ForegroundPid && i.IsAlive);
        }

        internal static string ValidateSelf()
        {
            try
            {
                return ForegroundTarget()?.Pid == PsInstanceRegistry.SelfPid ? null
                    : "前台程序已变化，请切回目标 Process Simulate，再重新选取对象后执行。";
            }
            catch (Exception ex) { return "无法确认前台程序：" + ex.Message; }
        }

        internal static void SetVisible(bool visible) { Transaction(s => s.Visible = visible); }
        internal static void SetState(string state, string task) { Transaction(s => { s.State = state; s.Task = task; }); }
        internal static void SetHelper(int pid)
        {
            Transaction(s =>
            {
                if (s.OwnerPid != PsInstanceRegistry.SelfPid) return;
                s.HelperPid = pid; s.HelperStart = StartTime(pid);
            });
        }
        internal static void Release()
        {
            Transaction(s =>
            {
                if (s.OwnerPid == PsInstanceRegistry.SelfPid) { s.OwnerPid = 0; s.OwnerStart = 0; }
            });
        }
    }
}
