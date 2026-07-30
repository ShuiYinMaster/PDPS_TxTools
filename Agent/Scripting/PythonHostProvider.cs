// =============================================================================
//  PythonHostProvider.cs  —  PythonHost 的进程内单例与配置推导
// -----------------------------------------------------------------------------
//  放置位置: TxAgent/Scripting/PythonHostProvider.cs
//
//  职责:
//    1. 懒加载、线程安全的 PythonHost 单例（一个 PS 会话共用一个引擎与 scope）。
//    2. 路径自动推导 —— 不需要任何硬编码配置：
//         eMPower 目录  <- 进程内已加载的 Tecnomatix.Engineering 程序集 Location
//         引用 DLL      <- 同目录的 Tecnomatix.Engineering.dll / TxEuOlpUtil.dll
//                          + 插件目录的 TxTools.Common.dll（若存在）
//         Lib 目录      <- 插件目录下的 IronPythonLib / Lib / python-lib（若存在）
//    3. Configure() 供 UserPrefsStore 覆盖默认值（在首次 Instance 之前调用）。
//
//  注意: 本类不做主线程调度。调用方（RunPythonTool）请用现有的 PsContext 包住
//        整个 host.Run(...)，避免与 PythonHostOptions.MainThreadContext 双重 marshal。
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TxTools.Agent.Scripting
{
    public static class PythonHostProvider
    {
        private static readonly object _gate = new object();
        private static PythonHost _host;
        private static PythonHostOptions _override;
        private static Action<string> _log;

        /// <summary>诊断日志通道。建议在插件启动时接到 TxAgent 现有的日志。</summary>
        public static Action<string> Log
        {
            get { return _log; }
            set { _log = value; }
        }

        /// <summary>是否已经创建过引擎（用于 UI 显示状态，不会触发初始化）。</summary>
        public static bool IsCreated { get { lock (_gate) return _host != null; } }

        /// <summary>
        /// 覆盖自动推导的配置。必须在第一次访问 Instance 之前调用，
        /// 否则先 Shutdown() 再 Configure()。传入 null 的字段沿用自动推导值。
        /// </summary>
        public static void Configure(PythonHostOptions options)
        {
            lock (_gate)
            {
                _override = options;
                if (_host != null)
                {
                    Write("配置已变更，正在重建引擎。");
                    SafeDispose();
                }
            }
        }

        public static PythonHost Instance
        {
            get
            {
                lock (_gate)
                {
                    if (_host == null)
                    {
                        var opt = BuildOptions();
                        var h = new PythonHost(opt);
                        try
                        {
                            h.Initialize();
                        }
                        catch
                        {
                            // 初始化失败时不缓存半死的实例，否则下次调用会走 RunCore 里的
                            // 补初始化分支，错误信息变形（HostInitFailed vs 通道异常），更难排查。
                            try { h.Dispose(); } catch { }
                            throw;
                        }
                        _host = h;
                    }
                    return _host;
                }
            }
        }

        /// <summary>清空 Python scope 变量并重新 bootstrap。切换对话时调用。</summary>
        public static void ResetScope()
        {
            lock (_gate)
            {
                if (_host == null) return;
                try { _host.ResetScope(); }
                catch (Exception ex) { Write("ResetScope 失败: " + ex.Message); }
            }
        }

        /// <summary>插件卸载时调用。</summary>
        public static void Shutdown()
        {
            lock (_gate) { SafeDispose(); }
        }

        private static void SafeDispose()
        {
            try { if (_host != null) _host.Dispose(); }
            catch (Exception ex) { Write("Dispose 失败: " + ex.Message); }
            finally { _host = null; }
        }

        // ---------------------------------------------------------------- 配置推导

        private static PythonHostOptions BuildOptions()
        {
            string ePowerDir = FindEmpowerDirectory();
            string pluginDir = GetPluginDirectory();

            var opt = new PythonHostOptions
            {
                TecnomatixRoot = DeriveTecnomatixRoot(ePowerDir),
                Log = Write
            };

            // --- 引用程序集 ---
            AddIfExists(opt.ReferenceDlls, ePowerDir, "Tecnomatix.Engineering.dll");
            AddIfExists(opt.ReferenceDlls, ePowerDir, "TxEuOlpUtil.dll");
            AddIfExists(opt.ReferenceDlls, ePowerDir, "Tecnomatix.Engineering.Ui.dll");
            AddIfExists(opt.ReferenceDlls, pluginDir, "TxTools.Common.dll");

            // --- 标准库 Lib 目录（PDPS 未部署，需自带）---
            foreach (var name in new[] { "IronPythonLib", "python-lib", "Lib" })
            {
                string p = SafeCombine(pluginDir, name);
                if (p != null && Directory.Exists(p)) { opt.LibPaths.Add(p); break; }
            }

            // --- 覆盖 ---
            var ov = _override;
            if (ov != null)
            {
                if (!string.IsNullOrEmpty(ov.TecnomatixRoot)) opt.TecnomatixRoot = ov.TecnomatixRoot;
                if (ov.LibPaths != null && ov.LibPaths.Count > 0)
                {
                    opt.LibPaths.Clear();
                    opt.LibPaths.AddRange(ov.LibPaths.Where(p => !string.IsNullOrEmpty(p)));
                }
                if (ov.ReferenceDlls != null && ov.ReferenceDlls.Count > 0)
                {
                    foreach (var d in ov.ReferenceDlls.Where(d => !string.IsNullOrEmpty(d)))
                        if (!opt.ReferenceDlls.Contains(d, StringComparer.OrdinalIgnoreCase))
                            opt.ReferenceDlls.Add(d);
                }
                if (ov.StarImports != null && ov.StarImports.Count > 0)
                    opt.StarImports = new List<string>(ov.StarImports);
                if (ov.TimeoutSeconds >= 0) opt.TimeoutSeconds = ov.TimeoutSeconds;
                if (ov.WatchdogCheckInterval > 0) opt.WatchdogCheckInterval = ov.WatchdogCheckInterval;
                if (!string.IsNullOrEmpty(ov.UndoContextName)) opt.UndoContextName = ov.UndoContextName;
                opt.EnableFrames = ov.EnableFrames;
                if (ov.Log != null) opt.Log = ov.Log;
            }

            Write("eMPower 目录: " + (ePowerDir ?? "(未找到)"));
            Write("引用程序集: " + (opt.ReferenceDlls.Count == 0 ? "(无)" :
                  string.Join(", ", opt.ReferenceDlls.Select(Path.GetFileName).ToArray())));
            Write("Lib 目录: " + (opt.LibPaths.Count == 0
                  ? "(未配置 —— json/os/collections 等模块将不可用，预检会拦截相关 import)"
                  : string.Join(", ", opt.LibPaths.ToArray())));

            return opt;
        }

        /// <summary>从进程内已加载的 Tecnomatix.Engineering 反推 eMPower 目录。</summary>
        private static string FindEmpowerDirectory()
        {
            foreach (var name in new[] { "Tecnomatix.Engineering", "TxEuOlpUtil", "Tecnomatix.Engineering.Ui" })
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(SafeName(a), name, StringComparison.OrdinalIgnoreCase));
                if (asm == null) continue;
                try
                {
                    if (asm.IsDynamic) continue;
                    string loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
                }
                catch { }
            }

            // 退路：宿主进程所在目录
            try
            {
                var entry = System.Diagnostics.Process.GetCurrentProcess().MainModule;
                if (entry != null && !string.IsNullOrEmpty(entry.FileName))
                    return Path.GetDirectoryName(entry.FileName);
            }
            catch { }

            return null;
        }

        /// <summary>eMPower 的上一级通常就是 Tecnomatix 安装根。</summary>
        private static string DeriveTecnomatixRoot(string empowerDir)
        {
            if (string.IsNullOrEmpty(empowerDir)) return null;
            try
            {
                var parent = Directory.GetParent(empowerDir);
                return parent != null ? parent.FullName : empowerDir;
            }
            catch { return empowerDir; }
        }

        private static string GetPluginDirectory()
        {
            try
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
            }
            catch { }
            try { return AppDomain.CurrentDomain.BaseDirectory; } catch { }
            return null;
        }

        private static void AddIfExists(List<string> list, string dir, string fileName)
        {
            string p = SafeCombine(dir, fileName);
            if (p != null && File.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
                list.Add(p);
        }

        private static string SafeCombine(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
            try { return Path.Combine(dir, name); } catch { return null; }
        }

        private static string SafeName(Assembly a)
        {
            try { return a.GetName().Name; } catch { return null; }
        }

        private static void Write(string msg)
        {
            var f = _log;
            if (f == null) return;
            try { f("[PythonHostProvider] " + msg); } catch { }
        }
    }
}