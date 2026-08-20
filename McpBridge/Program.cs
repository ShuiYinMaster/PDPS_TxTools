// TxToolsMcpBridge / Program.cs
//
// MCP (Model Context Protocol) stdio bridge 入口。
//
// 用法:
//   TxToolsMcpBridge.exe [--instance <环境名|pid>] [--readonly]
//
// 让通用智能体(opencode / claude code / codex 等)通过 MCP 协议控制 Process Simulate:
//
//   智能体 --MCP stdio--> 本桥 --命名管道--> PS 进程内 PsRpcServer --> ToolRegistry --> Tecnomatix API
//
// 本桥只做协议翻译:stdio 上的 MCP JSON-RPC <-> 命名管道上的 TxAgent RPC 帧。
// 它不引用 Tecnomatix 任何程序集,纯 .NET Framework 4.8 + Newtonsoft.Json。

using System;
using System.Text;

namespace TxToolsMcpBridge
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // MCP stdio 要求 UTF-8;Windows 控制台默认可能是本地代码页,必须显式设。
            try { Console.InputEncoding = Encoding.UTF8; } catch { }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            string instanceArg = null;
            bool readOnlyOnly = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--instance":
                        if (i + 1 < args.Length) { instanceArg = args[++i]; }
                        break;
                    case "--readonly":
                        readOnlyOnly = true;
                        break;
                    case "--help":
                    case "-h":
                        PrintHelp();
                        return 0;
                    default:
                        // 未识别的参数忽略,保持向后兼容
                        break;
                }
            }

            try
            {
                var target = InstanceDiscovery.Select(instanceArg);
                if (target == null)
                {
                    Console.Error.WriteLine("[TxToolsMcpBridge] 未找到可用的 PDPS 实例。");
                    Console.Error.WriteLine("[TxToolsMcpBridge] 请确认 Process Simulate 已启动且 TxAgent 插件已加载。");
                    Console.Error.WriteLine("[TxToolsMcpBridge] 指定实例: --instance <环境名|pid>,或用 list 查看当前实例。");
                    return 2;
                }

                Console.Error.WriteLine("[TxToolsMcpBridge] 目标实例: " + target.Name
                    + " [pid " + target.Pid + "] study=" + (target.Study ?? "(未打开)"));

                var server = new McpServer(target.Pid, readOnlyOnly);
                server.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[TxToolsMcpBridge] 致命错误: " + ex.Message);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.Error.WriteLine("TxToolsMcpBridge - Process Simulate MCP 桥");
            Console.Error.WriteLine();
            Console.Error.WriteLine("用法: TxToolsMcpBridge [--instance <环境名|pid>] [--readonly]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  --instance  指定要控制的 PDPS 实例(环境名或 pid)。");
            Console.Error.WriteLine("              默认:主控实例,无主控时选唯一存活实例。");
            Console.Error.WriteLine("  --readonly  只暴露只读工具,禁止任何写操作。");
            Console.Error.WriteLine();
            Console.Error.WriteLine("这是一个 MCP stdio server:请从智能体(opencode / claude code / codex)");
            Console.Error.WriteLine("的 MCP 配置里以 command 方式启动,不要直接当普通程序运行。");
        }
    }
}