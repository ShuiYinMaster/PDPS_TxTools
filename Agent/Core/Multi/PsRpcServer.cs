// TxTools.Agent / Core / Multi / PsRpcServer.cs
//
// 执行器:跑在【每个】PDPS 进程里，通过命名管道接收工具调用。
//
// 主控进程自己也跑一份 —— 这样"本地环境"和"远程环境"走的是同一条代码路径，
// 不用为本地单独开一个分支。少一套分支就少一半 bug。
//
// ── 线程 ──
//   管道在后台线程收请求 → 工具执行必须封送回 PS 主线程(PsContext.Run)。
//   这一步不能省:Tecnomatix API 非线程安全，从管道线程直接调必崩。
//
// ── 为什么用命名管道而不是 HTTP ──
//   同机通信，管道更快、不占端口、不会被防火墙拦、天然带 Windows 身份校验。
//   代价是只能同机 —— 但 PDPS 本来就是本机重型应用，没有跨机需求。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class PsRpcServer : IDisposable
    {
        private readonly ToolRegistry _tools;
        private readonly Func<string> _studyGetter;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>并发接受的连接数。多主控场景用不到，1~2 够。</summary>
        private const int MaxServerInstances = 4;

        public PsRpcServer(ToolRegistry tools, Func<string> studyGetter)
        {
            _tools = tools;
            _studyGetter = studyGetter;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "TxAgent.PsRpcServer"
            };
            _thread.Start();
        }

        private void Loop()
        {
            var pipeName = PsInstanceRegistry.PipeNameFor(PsInstanceRegistry.SelfPid);

            while (_running)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName, PipeDirection.InOut, MaxServerInstances,
                        PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                    server.WaitForConnection();
                    if (!_running)
                    {
                        server.Dispose();
                        return;
                    }

                    // 每个连接丢到工作线程处理 —— 否则一个慢请求(遍历场景、仿真阻塞)
                    // 会把后面的所有调用都堵住，主控侧的 parallel_tool_calls 全被串行化。
                    // 管道所有权随工作线程走:这里置 null,不再由 Loop 的 using 释放。
                    var conn = server;
                    server = null;
                    ThreadPool.QueueUserWorkItem(delegate { HandleConnectionInWorker(conn); });
                }
                catch (Exception ex)
                {
                    if (server != null)
                    {
                        try { server.Dispose(); } catch { }
                    }
                    if (!_running) return;
                    try { AuditLog.Write("[warn] [PsRpc] 服务端循环异常: " + ex.Message); } catch { }
                    Thread.Sleep(500);
                }
            }
        }

        /// <summary>并发处理槽位。超出就排队 —— 既不无限开线程，也不让慢请求饿死其它请求太久。</summary>
        private static readonly System.Threading.SemaphoreSlim ConnSlots =
            new System.Threading.SemaphoreSlim(MaxServerInstances);

        private void HandleConnectionInWorker(NamedPipeServerStream pipe)
        {
            try
            {
                if (!ConnSlots.Wait(TimeSpan.FromSeconds(30)))
                {
                    try { AuditLog.Write("[warn] [PsRpc] 并发连接过多，丢弃一个请求"); } catch { }
                    // 必须释放管道句柄 —— 否则 native 句柄只能等 GC 终结器回收,
                    // 客户端也会一直等一个永远不会来的响应。
                    try { pipe.Dispose(); } catch { }
                    return;
                }
                try
                {
                    using (pipe)
                    {
                        HandleConnection(pipe);
                    }
                }
                finally { ConnSlots.Release(); }
            }
            catch { }
        }

        private void HandleConnection(NamedPipeServerStream pipe)
        {
            try
            {
                var request = ReadMessage(pipe);
                if (string.IsNullOrEmpty(request)) return;

                var response = Handle(request);
                WriteMessage(pipe, response);

                try { pipe.WaitForPipeDrain(); } catch { }
            }
            catch (Exception ex)
            {
                try { AuditLog.Write("[warn] [PsRpc] 处理请求失败: " + ex.Message); } catch { }
            }
        }

        private string Handle(string requestJson)
        {
            JObject req;
            try { req = JObject.Parse(requestJson); }
            catch (Exception ex) { return Err("请求不是合法 JSON: " + ex.Message); }

            var op = (string)req["op"] ?? "";

            try
            {
                switch (op)
                {
                    case "ping":
                        return Ping();

                    case "list_tools":
                        {
                            var arr = new JArray();
                            if (_tools != null)
                                foreach (var t in _tools.Tools)
                                    if (t != null)
                                        arr.Add(new JObject
                                        {
                                            ["name"] = t.Name,
                                            ["read_only"] = t.IsReadOnly,
                                            ["description"] = t.Description ?? "",
                                            ["input_schema"] = t.InputSchema ?? new JObject()
                                        });
                            return Ok(new JObject { ["tools"] = arr });
                        }

                    case "invoke":
                        return Invoke(req);

                    default:
                        return Err("未知操作: " + op);
                }
            }
            catch (Exception ex)
            {
                return Err(ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// ping：管道在后台线程，study / systemRoot 都要回主线程取 ——
        /// 直接读 Tecnomatix API 会跨线程抛异常/返回空，导致对端识别不到库根。
        /// </summary>
        private string Ping()
        {
            string study = null, systemRoot = null;
            try
            {
                PsContext.Current.Run(delegate
                {
                    study = SafeStudy();
                    systemRoot = SafeSystemRoot();
                });
            }
            catch { /* 取不到就留 null */ }

            return Ok(new JObject
            {
                ["pid"] = PsInstanceRegistry.SelfPid,
                ["study"] = study,
                ["systemRoot"] = systemRoot,
                ["tools"] = _tools != null ? _tools.Count : 0
            });
        }

        private string Invoke(JObject req)
        {
            var name = (string)req["tool"];
            if (string.IsNullOrWhiteSpace(name)) return Err("缺少 tool 参数。");

            ITxAgentTool tool;
            if (_tools == null || !_tools.TryGet(name, out tool) || tool == null)
                return Err("本环境没有名为 \"" + name + "\" 的工具。");

            var input = req["input"] as JObject ?? new JObject();

            // 【主被控互访】允许远程调用全部工具(含写操作)。
            // 写工具在目标进程内执行，仍受其自身 undo 保护；场景安全靠本窗口可见 + Ctrl+Z。
            string output;
            try
            {
                // 管道在后台线程，PS API 必须回主线程
                output = PsContext.Current.Run<string>(delegate { return tool.Execute(input); });
            }
            catch (Exception ex)
            {
                return Err("工具执行异常 - " + ex.GetType().Name + ": " + ex.Message);
            }

            return Ok(new JObject { ["output"] = output ?? "" });
        }

        /// <summary>
        /// 是否允许远程调用写操作。默认开启 —— 主被控互访，写工具也能跨环境执行。
        /// 若确需收紧(如仅限只读)，由宿主在 UI 中显式关闭。
        /// </summary>
        public static bool AllowRemoteWrite = true;

        /// <summary>
        /// 当前环境的 SystemRootDirectory（库根）。由宿主注入 —— Core 层不直接依赖 Tecnomatix。
        /// ping 时随响应返回，供其它环境识别本环境的库根路径。
        /// </summary>
        public static Func<string> SystemRootGetter;

        private static string SafeSystemRoot()
        {
            try { return SystemRootGetter != null ? SystemRootGetter() : null; }
            catch { return null; }
        }

        private string SafeStudy()
        {
            try { return _studyGetter != null ? _studyGetter() : null; }
            catch { return null; }
        }

        private static string Ok(JObject data)
        {
            data["ok"] = true;
            return JsonConvert.SerializeObject(data);
        }

        private static string Err(string message)
        {
            return JsonConvert.SerializeObject(new JObject
            {
                ["ok"] = false,
                ["error"] = message
            });
        }

        // ── 消息帧 ──
        //
        // 管道用 Message 模式，但 .NET 的实现在大消息上仍可能分多次读到，
        // 所以自己加 4 字节长度前缀，别依赖 IsMessageComplete。

        internal static string ReadMessage(PipeStream pipe)
        {
            var lenBuf = new byte[4];
            if (!ReadExact(pipe, lenBuf, 4)) return null;

            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0 || len > 64 * 1024 * 1024) return null;

            var buf = new byte[len];
            if (!ReadExact(pipe, buf, len)) return null;

            return Encoding.UTF8.GetString(buf);
        }

        internal static void WriteMessage(PipeStream pipe, string text)
        {
            var body = Encoding.UTF8.GetBytes(text ?? "");
            pipe.Write(BitConverter.GetBytes(body.Length), 0, 4);
            pipe.Write(body, 0, body.Length);
            pipe.Flush();
        }

        private static bool ReadExact(PipeStream pipe, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = pipe.Read(buf, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        public void Dispose()
        {
            _running = false;
            try
            {
                // 自连一次把 WaitForConnection 唤醒，否则线程会一直挂着
                using (var c = new NamedPipeClientStream(".",
                    PsInstanceRegistry.PipeNameFor(PsInstanceRegistry.SelfPid), PipeDirection.InOut))
                {
                    c.Connect(200);
                }
            }
            catch { }
        }
    }
}
