// TxTools.Agent / Tools / Code / CodeBuildTool.cs
//
// 编译验证。
//
// ── 这是整套代码工具里最重要的一个 ──
//   没有编译反馈,AI 改完只能"看着像对的",你也只能自己去 VS 里点一下才知道。
//   接上之后就形成闭环:改 → 编译 → 错误回灌 → 自修,和 harness 的错误回灌是同一个机制,
//   只是把"运行时异常"换成了"编译器诊断"。
//
//   实测经验:编译反馈接上之前,模型改 C# 的一次成功率大概五成;接上之后,
//   两三轮内收敛到编译通过是常态 —— 因为 CS 错误码 + 行号 + 消息是极强的信号。
//
// ── 只回错误,不回整个构建日志 ──
//   msbuild 一次输出几千行,整段回灌会瞬间吃掉上下文。这里只提取
//   error/warning 行并去重,通常十几行就够模型定位。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class CodeBuildTool : TxAgentToolBase, ITxOffUiThreadTool
    {
        /// <summary>构建超时。大工程首次编译可能要几分钟。</summary>
        public static int TimeoutMs = 5 * 60 * 1000;

        /// <summary>最多回灌多少条诊断。同一个错误刷屏没有意义。</summary>
        public static int MaxDiagnostics = 25;

        public override string Name { get { return "code_build"; } }

        public override string Description
        {
            get
            {
                return "编译当前工作区的项目，返回编译器错误和警告(带文件、行号、CS 错误码)。"
                     + "【每次 code_edit 之后都要调用本工具】—— 未经编译验证的改动不算完成，"
                     + "看着对的代码经常编译不过。"
                     + "有错误时按返回的行号 code_read 看上下文再修，不要凭错误消息猜。"
                     + "不传 project 时自动找工作区里的 .sln 或 .csproj。";
            }
        }

        /// <summary>只读取源码产出程序集，不改源码本身。</summary>
        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"project\": { \"type\":\"string\", \"description\":\"可选，.sln 或 .csproj 相对路径。留空自动查找\" }," +
                    " \"configuration\": { \"type\":\"string\", \"description\":\"Debug(默认) 或 Release\" }," +
                    " \"warnings\": { \"type\":\"boolean\", \"description\":\"是否一并返回警告，默认 false\" }" +
                    "}, \"required\":[] }");
            }
        }

        public override string Execute(JObject input)
        {
            if (!CodeWorkspace.IsOpen)
                return "Error: 尚未打开工作区。先调用 open_workspace(path=\"项目目录\")。";

            var projectArg = GetString(input, "project");
            var config = GetString(input, "configuration", "Debug");
            bool withWarnings = input["warnings"] != null && input["warnings"].Type == JTokenType.Boolean
                                && (bool)input["warnings"];

            string project;
            if (!string.IsNullOrWhiteSpace(projectArg))
            {
                string err;
                project = CodeWorkspace.Resolve(projectArg, out err);
                if (project == null) return "Error: " + err;
                if (!File.Exists(project)) return "Error: 项目文件不存在: " + projectArg;
            }
            else
            {
                project = FindProject();
                if (project == null)
                    return "Error: 工作区里找不到 .sln 或 .csproj。请用 project 参数显式指定。";
            }

            var msbuild = FindMsBuild();
            if (msbuild == null)
                return "Error: 找不到可用的 MSBuild.exe。\n"
                     + "已尝试:vswhere 查询、VS 2022/2019/2017 常见安装路径、PATH。\n"
                     + "请安装 Visual Studio 或 Build Tools for Visual Studio。\n"
                     + "注意:.NET Framework 自带的 v4.0.30319\\MSBuild.exe 被【故意排除】——"
                     + "它只支持 C# 5，编 C# 6+ 代码会报出误导性的语法错误。";

            var sb = new StringBuilder();
            sb.AppendLine("编译 " + CodeWorkspace.Relative(project) + "  [" + config + "]");
            sb.AppendLine("MSBuild: " + msbuild);
            sb.AppendLine("工具集: " + ToolsetOf(msbuild));
            sb.AppendLine();

            string stdout;
            int exitCode;
            try { exitCode = Run(msbuild, project, config, out stdout); }
            catch (Exception ex) { return sb + "Error: 启动 MSBuild 失败 - " + ex.Message; }

            if (exitCode == -1) return sb + "Error: 编译超时(" + (TimeoutMs / 1000) + " 秒)，已终止。";

            var diags = Parse(stdout, withWarnings);
            var errors = diags.Where(d => d.IsError).ToList();
            var warns = diags.Where(d => !d.IsError).ToList();

            if (exitCode == 0 && errors.Count == 0)
            {
                sb.AppendLine("✅ 编译成功。");
                if (withWarnings && warns.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("警告 " + warns.Count + " 条:");
                    foreach (var w in warns.Take(MaxDiagnostics)) sb.AppendLine("  " + w);
                }
                return sb.ToString();
            }

            sb.AppendLine("❌ 编译失败，错误 " + errors.Count + " 条"
                + (warns.Count > 0 ? "，警告 " + warns.Count + " 条" : "") + ":");
            sb.AppendLine();

            foreach (var e in errors.Take(MaxDiagnostics)) sb.AppendLine(e.ToString());

            if (errors.Count > MaxDiagnostics)
                sb.AppendLine("…还有 " + (errors.Count - MaxDiagnostics)
                    + " 条。先修上面这些，很多后续错误是它们引起的连锁反应。");

            if (withWarnings && warns.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("警告:");
                foreach (var w in warns.Take(10)) sb.AppendLine("  " + w);
            }

            if (errors.Count == 0)
            {
                // 退出码非 0 但没解析出错误行 —— 多半是项目文件本身或还原失败
                sb.AppendLine();
                sb.AppendLine("(未解析到具体错误行，MSBuild 退出码 " + exitCode + "。原始输出尾部:)");
                var tail = stdout.Split('\n');
                foreach (var l in tail.Skip(Math.Max(0, tail.Length - 15)))
                    sb.AppendLine("  " + l.TrimEnd());
            }

            return sb.ToString();
        }

        // ── 诊断解析 ──

        private sealed class Diag
        {
            public bool IsError;
            public string File;
            public int Line;
            public int Column;
            public string Code;
            public string Message;

            public override string ToString()
            {
                var loc = string.IsNullOrEmpty(File) ? "" : File + "(" + Line + "," + Column + ")  ";
                return loc + Code + ": " + Message;
            }

            public string Key { get { return File + "|" + Line + "|" + Code + "|" + Message; } }
        }

        // 形如: E:\proj\Foo.cs(42,17): error CS1061: “Bar”未包含“Baz”的定义
        private static readonly Regex DiagRe = new Regex(
            @"^(?<file>[^(]+)\((?<line>\d+),(?<col>\d+)\):\s*(?<sev>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<msg>.+?)(\s*\[[^\]]*\])?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static List<Diag> Parse(string output, bool includeWarnings)
        {
            var list = new List<Diag>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(output)) return list;

            foreach (var raw in output.Split('\n'))
            {
                var m = DiagRe.Match(raw.TrimEnd('\r'));
                if (!m.Success) continue;

                bool isError = string.Equals(m.Groups["sev"].Value, "error", StringComparison.OrdinalIgnoreCase);
                if (!isError && !includeWarnings) continue;

                var d = new Diag
                {
                    IsError = isError,
                    File = CodeWorkspace.Relative(m.Groups["file"].Value.Trim()),
                    Line = int.Parse(m.Groups["line"].Value),
                    Column = int.Parse(m.Groups["col"].Value),
                    Code = m.Groups["code"].Value,
                    Message = m.Groups["msg"].Value.Trim()
                };

                // MSBuild 并行构建会把同一条诊断输出多次
                if (seen.Add(d.Key)) list.Add(d);
            }

            return list;
        }

        // ── 进程 ──

        private static int Run(string msbuild, string project, string config, out string stdout)
        {
            var psi = new ProcessStartInfo
            {
                FileName = msbuild,
                Arguments = "\"" + project + "\""
                          + " /p:Configuration=" + (string.IsNullOrWhiteSpace(config) ? "Debug" : config)
                          + " /nologo /v:minimal /m /clp:NoSummary",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(project) ?? CodeWorkspace.Root
            };

            var sb = new StringBuilder();
            using (var proc = new Process { StartInfo = psi })
            {
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit(TimeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    stdout = sb.ToString();
                    return -1;
                }

                stdout = sb.ToString();
                return proc.ExitCode;
            }
        }

        // ── 定位 ──

        private static string FindProject()
        {
            var root = CodeWorkspace.Root;
            if (string.IsNullOrEmpty(root)) return null;

            try
            {
                var sln = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (sln != null) return sln;

                var proj = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                                    .Where(f => !CodeWorkspace.IsSkipped(f))
                                    .OrderBy(f => f.Length)   // 层级浅的更可能是主项目
                                    .FirstOrDefault();
                return proj;
            }
            catch { return null; }
        }

        /// <summary>
        /// 定位 MSBuild。
        ///
        /// 【绝不能退回 .NET Framework 自带的 v4.0.30319\MSBuild.exe】
        /// 那是 MSBuild 4.0,不认识 Roslyn 工具集,会退回传统编译器(C# 5)。
        /// 于是 .NET Framework 4.8 项目里的 $"..."、?.、=> 这些 C# 6+ 语法
        /// 会报出一堆语法错误(CS1525 之类),而真实原因是编译器版本不对 ——
        /// 这种错极难排查。宁可明确报"找不到 MSBuild",也不要给出误导性错误。
        ///
        /// 优先用 vswhere.exe 查询,比硬编码路径可靠:它是 VS 官方的安装定位器,
        /// 装在固定位置,能查到所有版本和版本(含 Build Tools)。
        /// </summary>
        private static string FindMsBuild()
        {
            var byVsWhere = FindViaVsWhere();
            if (byVsWhere != null) return byVsWhere;

            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            foreach (var baseDir in new[] { pf, pf86 })
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                foreach (var ver in new[] { "2022", "2019", "2017" })
                    foreach (var ed in new[] { "Enterprise", "Professional", "Community", "BuildTools", "Preview" })
                    {
                        var root = Path.Combine(baseDir, "Microsoft Visual Studio", ver, ed, "MSBuild");
                        // Current 对应 VS2019+,15.0 对应 VS2017;amd64 子目录是 64 位版
                        foreach (var toolset in new[] { "Current", "15.0" })
                        {
                            var bin = Path.Combine(root, toolset, "Bin");
                            var x64 = Path.Combine(bin, "amd64", "MSBuild.exe");
                            if (File.Exists(x64)) return x64;
                            var x86 = Path.Combine(bin, "MSBuild.exe");
                            if (File.Exists(x86)) return x86;
                        }
                    }
            }

            // PATH 里的(通常是开发人员命令提示符设置过的)
            try
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var d = dir.Trim();
                    // 排除 Framework 目录下的老 MSBuild,理由见上
                    if (d.IndexOf(@"\Microsoft.NET\Framework", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    var c = Path.Combine(d, "MSBuild.exe");
                    if (File.Exists(c)) return c;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 用 vswhere 查询 MSBuild 路径。vswhere 随 VS Installer 装在固定位置,
        /// 只要装过任意版本的 VS 2017+ 就有。
        /// </summary>
        private static string FindViaVsWhere()
        {
            try
            {
                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (string.IsNullOrEmpty(pf86)) return null;

                var vswhere = Path.Combine(pf86,
                    @"Microsoft Visual Studio\Installer\vswhere.exe");
                if (!File.Exists(vswhere)) return null;

                var psi = new ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-latest -products * "
                              + "-requires Microsoft.Component.MSBuild "
                              + "-find MSBuild\\**\\Bin\\MSBuild.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (var proc = Process.Start(psi))
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(15000);

                    var found = output
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(File.Exists)
                        .ToList();

                    if (found.Count == 0) return null;

                    // 优先 64 位:大工程编译内存占用高,32 位 MSBuild 容易 OOM
                    var x64 = found.FirstOrDefault(x =>
                        x.IndexOf("\\amd64\\", StringComparison.OrdinalIgnoreCase) >= 0);
                    return x64 ?? found[0];
                }
            }
            catch { return null; }
        }

        /// <summary>从 MSBuild 路径反推工具集,用于在结果里标明用的什么编译器。</summary>
        private static string ToolsetOf(string msbuildPath)
        {
            var p = msbuildPath ?? "";
            if (p.IndexOf("\\Current\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Roslyn (VS2019+，默认 C# 7.3，可由 csproj 的 LangVersion 提升)";
            if (p.IndexOf("\\15.0\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Roslyn (VS2017，默认 C# 7.3)";
            if (p.IndexOf(@"\Microsoft.NET\Framework", StringComparison.OrdinalIgnoreCase) >= 0)
                return "⚠ MSBuild 4.0 传统编译器，仅支持 C# 5，C# 6+ 语法会报语法错误";
            return "未知工具集";
        }
    }
}
