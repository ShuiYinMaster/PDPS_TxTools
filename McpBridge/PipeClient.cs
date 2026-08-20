// TxToolsMcpBridge / PipeClient.cs
//
// 命名管道客户端,与 Agent/Core/Multi/PsRpcServer.cs 的帧协议完全对齐:
//   4 字节小端长度前缀 + UTF-8 JSON 正文。
// 管道名: TxAgent_PS_<pid> (见 PsInstanceRegistry.PipeNameFor)。

using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxToolsMcpBridge
{
    public static class PipeClient
    {
        /// <summary>调用超时。PS 主线程可能正忙(仿真、大批量遍历),给宽松些。</summary>
        public static int TimeoutMs = 120000;

        public const string PipePrefix = "TxAgent_PS_";

        public sealed class Result
        {
            public bool Ok;
            public JObject Data;
            public string Error;
        }

        public static Result Call(int pid, JObject request)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(
                    ".", PipePrefix + pid, PipeDirection.InOut, PipeOptions.None))
                {
                    try { pipe.Connect(3000); }
                    catch (TimeoutException)
                    {
                        return Fail("连不上 PDPS 实例(pid " + pid + ")。该实例可能未加载 TxAgent 插件。");
                    }

                    pipe.ReadMode = PipeTransmissionMode.Message;
                    WriteMessage(pipe, JsonConvert.SerializeObject(request));

                    var raw = ReadMessageTimed(pipe, TimeoutMs);
                    if (raw == null)
                        return Fail("实例(pid " + pid + ")无返回(超时或断连)。该 PDPS 可能正忙于阻塞操作。");

                    var resp = JObject.Parse(raw);
                    if (resp["ok"] != null && (bool)resp["ok"])
                        return new Result { Ok = true, Data = resp };

                    return Fail((string)resp["error"] ?? "未知错误");
                }
            }
            catch (Exception ex)
            {
                return Fail("与 PDPS(pid " + pid + ")通信失败 - " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Result Fail(string msg)
        {
            return new Result { Ok = false, Error = msg };
        }

        internal static void WriteMessage(PipeStream pipe, string text)
        {
            var body = Encoding.UTF8.GetBytes(text ?? "");
            pipe.Write(BitConverter.GetBytes(body.Length), 0, 4);
            pipe.Write(body, 0, body.Length);
            pipe.Flush();
        }

        private static string ReadMessageTimed(PipeStream pipe, int timeoutMs)
        {
            var lenBuf = new byte[4];
            if (!ReadExactTimed(pipe, lenBuf, 4, timeoutMs)) return null;
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0 || len > 64 * 1024 * 1024) return null;
            var buf = new byte[len];
            if (!ReadExactTimed(pipe, buf, len, timeoutMs)) return null;
            return Encoding.UTF8.GetString(buf);
        }

        private static bool ReadExactTimed(PipeStream pipe, byte[] buf, int count, int timeoutMs)
        {
            int read = 0;
            while (read < count)
            {
                int n;
                var done = new ManualResetEvent(false);
                IAsyncResult ar = null;
                try
                {
                    ar = pipe.BeginRead(buf, read, count - read, delegate { try { done.Set(); } catch { } }, null);
                }
                catch { return false; }

                bool completed = ar != null && done.WaitOne(timeoutMs);
                if (!completed)
                {
                    try { pipe.EndRead(ar); } catch { }
                    return false;
                }
                try { n = pipe.EndRead(ar); }
                catch { return false; }
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
    }
}