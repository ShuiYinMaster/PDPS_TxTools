// EnvLibrary.cs  --  C# 8.0
// 跨环境库根识别：当前环境 + 其它 PDPS 实例。
//
// 库根事实（TxAgent 记忆 + Tecnomatix.Engineering.dll 反射确认）:
//   TxApplication.SystemRootDirectory = 当前环境的 Library 根目录
//   （如 H:\...\T18FL4_Station\Library\）。cojt 目录都在它之下。
//
// 另一环境的库根通过 PsRpcServer.ping 获取 —— 每个 PDPS 进程都在跑 RPC 执行器，
// ping 响应现含 systemRoot 字段。这样两端的库根都能自动识别，无需手动填。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;
using Newtonsoft.Json.Linq;

namespace TxTools.CrossEnvIO
{
    public sealed class EnvInfo
    {
        public string Name;
        public int Pid;
        public string Study;
        public string SystemRoot;
        public bool IsSelf;
    }

    public static class EnvLibrary
    {
        /// <summary>当前环境的库根（TxApplication.SystemRootDirectory，去尾反斜杠，目录存在校验）。</summary>
        public static string CurrentRoot()
        {
            try { return Normalize(TxApplication.SystemRootDirectory); }
            catch { return null; }
        }

        /// <summary>把路径规范化：去引号、去尾反斜杠；空则返回 null。</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string p = path.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(p)) return null;
            return p.TrimEnd('\\', '/');
        }

        /// <summary>
        /// 列出所有 PDPS 实例（含自身）。每个实例的 SystemRoot 来自:
        ///   自身 → TxApplication.SystemRootDirectory（本进程直接读）；
        ///   其它 → PsRpcClient.Ping 实时取 systemRoot。
        /// </summary>
        public static List<EnvInfo> ListEnvironments(Action<string> log)
        {
            log = log ?? (s => { });
            var result = new List<EnvInfo>();
            var live = PsInstanceRegistry.Live();
            foreach (var inst in live)
            {
                var info = new EnvInfo
                {
                    Name = inst.Name,
                    Pid = inst.Pid,
                    Study = inst.Study,
                    IsSelf = inst.IsSelf
                };
                if (inst.IsSelf)
                {
                    info.SystemRoot = CurrentRoot();
                }
                else
                {
                    var r = PsRpcClient.PingFast(inst);
                    if (r.Ok && !string.IsNullOrEmpty(r.Output))
                    {
                        try
                        {
                            var jo = JObject.Parse(r.Output);
                            info.SystemRoot = Normalize((string)jo["systemRoot"]);
                            if (string.IsNullOrEmpty(info.Study))
                                info.Study = (string)jo["study"];
                        }
                        catch { }
                    }
                    if (string.IsNullOrEmpty(info.SystemRoot))
                        log("[环境] " + inst.Name + " ping 无库根（其 TxAgent 可能未启动）");
                }
                result.Add(info);
            }
            return result;
        }

        /// <summary>取当前环境信息（自身）。</summary>
        public static EnvInfo SelfInfo()
        {
            var me = PsInstanceRegistry.Self();
            return new EnvInfo
            {
                Name = me != null ? me.Name : "本环境",
                Pid = PsInstanceRegistry.SelfPid,
                Study = me != null ? me.Study : null,
                SystemRoot = CurrentRoot(),
                IsSelf = true
            };
        }

        /// <summary>
        /// 识别「另一环境」的库根：优先取唯一的其它实例；多个时按名字取第一个可 ping 通且库根存在的。
        /// 找不到返回 null。
        /// </summary>
        public static string ResolveRemoteRoot(List<EnvInfo> envs, Action<string> log)
        {
            log = log ?? (s => { });
            if (envs == null) return null;
            var remotes = envs.Where(e => !e.IsSelf).ToList();
            foreach (var r in remotes)
            {
                if (string.IsNullOrEmpty(r.SystemRoot)) continue;
                if (Directory.Exists(r.SystemRoot)) return r.SystemRoot;
            }
            return null;
        }
    }
}
