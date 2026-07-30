// =============================================================================
//  PythonHost.cs  —  TxAgent IronPython 执行宿主
// -----------------------------------------------------------------------------
//  目标环境: Process Simulate 2402 内嵌 IronPython 2.7.7 / .NET Framework 4.8 / C# 7.3
//
//  设计要点:
//    1. 零编译期引用 —— IronPython.dll / Microsoft.Scripting.dll / Tecnomatix.Engineering.dll
//       全部反射 late-bound 加载。本文件可单独编译，不依赖任何 NuGet 包或 PDPS SDK 引用。
//    2. 复用 PDPS 进程内已加载的 IronPython 程序集(若已加载)，避免版本冲突。
//    3. __future__ 影响的是"每个编译单元",因此 division/print_function/unicode_literals
//       必须逐次拼在用户代码前面(offset = 1 行,报错行号已自动回退)。
//    4. Probe 模式 = 照常开 undo 事务,但结束时无条件回滚 —— agent 可零成本探测 API。
//    5. 超时通过 sys.settrace 看门狗实现(需引擎 Tracing 选项),独立编译单元下发,
//       不污染用户代码的行号。
//
//  用法:
//      var host = new PythonHost(new PythonHostOptions {
//          TecnomatixRoot   = @"D:\Program Files\Tecnomatix_2402",
//          LibPaths         = { @"D:\TxTools\IronPythonLib" },
//          ReferenceDlls    = { @"D:\Program Files\Tecnomatix_2402\eMPower\Tecnomatix.Engineering.dll",
//                               @"D:\Program Files\Tecnomatix_2402\eMPower\TxEuOlpUtil.dll",
//                               @"D:\TxTools\TxTools.Common.dll" },
//          MainThreadContext = PsContext.MainThread,   // 你现有的 SynchronizationContext
//      });
//      var r = host.Run(code, PythonRunMode.Probe);
//      string forLlm = r.ToAgentText();
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TxTools.Agent.Scripting
{
    #region ---------- 公共数据类型 ----------

    /// <summary>执行模式。</summary>
    public enum PythonRunMode
    {
        /// <summary>探测模式：照常执行，但结束时无条件回滚，场景不受影响。</summary>
        Probe,
        /// <summary>执行模式：成功提交，异常回滚。</summary>
        Execute
    }

    public enum LintSeverity { Warning, Error }

    /// <summary>预检发现的问题。Message 直接回喂给模型，因此写成可照做的中文。</summary>
    public sealed class PythonLintIssue
    {
        public LintSeverity Severity;
        public int Line;            // 1-based，相对用户原始代码
        public string Code;         // 稳定标识，便于统计
        public string Message;

        public override string ToString()
        {
            string tag = Severity == LintSeverity.Error ? "错误" : "警告";
            return Line > 0
                ? string.Format(CultureInfo.InvariantCulture, "[{0}] 第 {1} 行 ({2}): {3}", tag, Line, Code, Message)
                : string.Format(CultureInfo.InvariantCulture, "[{0}] ({1}): {2}", tag, Code, Message);
        }
    }

    public sealed class PythonExecResult
    {
        public bool Success;
        public PythonRunMode Mode;
        public string Output = "";
        public string ErrorOutput = "";
        public string ErrorType = "";
        public string ErrorMessage = "";
        public string Traceback = "";
        public int ErrorLine;               // 已回退 prologue 偏移
        public int ErrorColumn;
        public bool RolledBack;
        public bool UndoAvailable;
        /// <summary>当前 PS 版本是否支持程序化回滚。2402 下为 false。</summary>
        public bool CanRollback;
        public bool TimedOut;
        public long DurationMs;
        public List<PythonLintIssue> LintIssues = new List<PythonLintIssue>();
        /// <summary>经过 sanitize 后实际送去执行的代码（不含 __future__ 前缀），供排查用。</summary>
        public string EffectiveCode = "";

        public bool HasLintErrors
        {
            get { return LintIssues.Any(i => i.Severity == LintSeverity.Error); }
        }

        /// <summary>压缩成一段适合直接塞回 LLM 上下文的文本。</summary>
        public string ToAgentText(int maxOutputChars = 8000)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Success ? "== 执行成功 ==" : "== 执行失败 ==");
            string undoState = RolledBack ? "已回滚"
                             : UndoAvailable ? "已包进一次 undo 事务（用户可 Ctrl+Z 撤销）"
                             : "无 undo 事务保护";
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "模式: {0} | 耗时: {1} ms | {2}",
                Mode == PythonRunMode.Probe ? "probe(只读探测)" : "execute(提交变更)",
                DurationMs, undoState));

            if (LintIssues.Count > 0)
            {
                sb.AppendLine("-- 预检 --");
                foreach (var i in LintIssues) sb.AppendLine(i.ToString());
            }

            if (!string.IsNullOrEmpty(Output))
            {
                sb.AppendLine("-- 输出 --");
                sb.AppendLine(Truncate(Output, maxOutputChars));
            }

            if (!string.IsNullOrEmpty(ErrorOutput))
            {
                sb.AppendLine("-- stderr --");
                sb.AppendLine(Truncate(ErrorOutput, 2000));
            }

            if (!Success)
            {
                sb.AppendLine("-- 异常 --");
                if (TimedOut) sb.AppendLine("脚本超时被中止。若确需大规模循环，请改用 C# 原生工具路径。");
                if (!string.IsNullOrEmpty(ErrorType))
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}: {1}", ErrorType, ErrorMessage));
                if (ErrorLine > 0)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "位置: 第 {0} 行 第 {1} 列", ErrorLine, ErrorColumn));
                if (!string.IsNullOrEmpty(Traceback))
                    sb.AppendLine(Truncate(Traceback, 4000));
            }
            return sb.ToString().TrimEnd();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + Environment.NewLine
                 + string.Format(CultureInfo.InvariantCulture, "...[已截断，共 {0} 字符]", s.Length);
        }
    }

    public sealed class PythonHostOptions
    {
        /// <summary>Tecnomatix 安装根目录，用于搜索 IronPython.dll。可留空，则仅搜索插件目录与已加载程序集。</summary>
        public string TecnomatixRoot;

        /// <summary>额外的 IronPython 标准库 Lib 目录（PDPS 未部署，需自带）。</summary>
        public List<string> LibPaths = new List<string>();

        /// <summary>bootstrap 时通过 clr.AddReferenceToFileAndPath 引入的程序集绝对路径。</summary>
        public List<string> ReferenceDlls = new List<string>();

        /// <summary>bootstrap 时执行的 from X import * 列表。</summary>
        public List<string> StarImports = new List<string> { "Tecnomatix.Engineering" };

        /// <summary>PS 主线程的同步上下文。为 null 则假定调用方已在主线程。</summary>
        public SynchronizationContext MainThreadContext;

        /// <summary>脚本超时秒数。&lt;=0 表示不启用看门狗（可显著提速）。</summary>
        public double TimeoutSeconds = 30.0;

        /// <summary>看门狗每 N 次 trace 事件检查一次时间，越大越快、响应越钝。</summary>
        public int WatchdogCheckInterval = 2000;

        /// <summary>启用 Frames 选项：traceback 更完整，但有性能代价。</summary>
        public bool EnableFrames = true;

        /// <summary>undo 上下文名称。</summary>
        public string UndoContextName = "TxAgent Python Script";

        /// <summary>
        /// Probe 模式要求 undo 管理器提供程序化回滚，否则拒绝执行。
        /// PS 2402 的 TxUndoTransactionManager 只有 StartTransaction/EndTransaction，
        /// 没有 Undo()，因此这里默认 false —— probe 的安全性改由工具层的静态只读检查承担，
        /// 事务包裹只作为兜底（用户仍可 Ctrl+Z 撤销整段脚本的改动）。
        /// </summary>
        public bool RequireRollbackForProbe = false;

        /// <summary>诊断日志回调。</summary>
        public Action<string> Log;
    }

    #endregion

    #region ---------- 输出捕获 ----------

    internal sealed class CaptureStream : Stream
    {
        private readonly MemoryStream _buf = new MemoryStream();
        private readonly object _gate = new object();

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { lock (_gate) return _buf.Length; } }
        public override long Position
        {
            get { lock (_gate) return _buf.Position; }
            set { throw new NotSupportedException(); }
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate) _buf.Write(buffer, offset, count);
        }

        public void Reset()
        {
            lock (_gate) { _buf.SetLength(0); _buf.Position = 0; }
        }

        /// <summary>取出内容并清空。UTF-8 解码（引擎输出编码已设为 UTF-8）。</summary>
        public string Drain()
        {
            lock (_gate)
            {
                if (_buf.Length == 0) return "";
                string s = new UTF8Encoding(false).GetString(_buf.ToArray());
                _buf.SetLength(0);
                _buf.Position = 0;
                return s.Replace("\r\n", "\n");
            }
        }
    }

    #endregion

    #region ---------- 源码预检 / 改写 ----------

    /// <summary>
    /// Python 3 方言 → IronPython 2.7 的预处理。
    /// 原则：能安全机械转换的就转（f-string、类型注解），不能确定的一律报错回喂，
    /// 绝不做可能改变语义的"聪明"转换。
    /// 所有改写保持行数不变，因此报错行号可直接对应用户原始代码。
    /// </summary>
    public static class PythonSanitizer
    {
        // IronPython 2.7.7 编译进 DLL 的模块（sys.path=['.'] 时仍可用）。
        // 未列出的不代表一定没有，只是无法确认 —— 会降级为 Warning。
        private static readonly HashSet<string> BuiltinModules = new HashSet<string>(StringComparer.Ordinal)
        {
            "sys","clr","time","math","cmath","re","itertools","datetime","operator",
            "struct","array","binascii","errno","gc","imp","marshal","thread","exceptions",
            "_random","_codecs","_socket","signal","msvcrt","nt","_functools","_collections",
            "_weakref","_sre","cPickle","cStringIO","future_builtins","__future__"
        };

        // Python 3 才有的模块，在 2.7 下必然 ImportError。
        private static readonly HashSet<string> Python3OnlyModules = new HashSet<string>(StringComparer.Ordinal)
        {
            "pathlib","enum","typing","dataclasses","asyncio","statistics","queue",
            "configparser","builtins","io_ext","concurrent","secrets","ipaddress",
            "unittest.mock","functools32","tkinter","reprlib","copyreg","winreg"
        };

        private enum SegKind { Code, Str, Comment }

        private sealed class Seg
        {
            public int Start;       // 含前缀的起始索引
            public int End;         // 尾后索引
            public SegKind Kind;
            public string Prefix = "";
            public char Quote;
            public bool Triple;
            public int BodyStart;   // 引号后
            public int BodyEnd;     // 结束引号前
        }

        // ---------------------------------------------------------------- 入口

        /// <summary>预检并改写。返回改写后的代码；issues 收集所有问题。</summary>
        public static string Sanitize(string source, List<PythonLintIssue> issues, bool libPathsConfigured)
        {
            if (issues == null) issues = new List<PythonLintIssue>();
            if (source == null) return "";

            string src = source.Replace("\r\n", "\n").Replace("\r", "\n").TrimStart('\uFEFF');

            // 1) f-string 转换（会改变列宽，不改变行数）
            src = ConvertFStrings(src, issues);
            if (issues.Any(i => i.Severity == LintSeverity.Error)) return src;

            // 2) 类型注解剥离（用空格原地覆盖，行数与列位置不变）
            src = StripAnnotations(src, issues);

            // 3) 纯检测项
            DetectUnsupported(src, issues);
            DetectImports(src, issues, libPathsConfigured);
            DetectIndentMix(src, issues);

            return src;
        }

        // ---------------------------------------------------------------- 扫描器

        private static List<Seg> Scan(string s)
        {
            var segs = new List<Seg>();
            int n = s.Length, i = 0;

            while (i < n)
            {
                char c = s[i];

                if (c == '#')
                {
                    int j = s.IndexOf('\n', i);
                    if (j < 0) j = n;
                    segs.Add(new Seg { Start = i, End = j, Kind = SegKind.Comment });
                    i = j;
                    continue;
                }

                if (IsIdentStart(c))
                {
                    int j = i;
                    while (j < n && IsIdentPart(s[j])) j++;
                    string ident = s.Substring(i, j - i);
                    if (j < n && (s[j] == '"' || s[j] == '\'') && IsStringPrefix(ident))
                    {
                        var seg = ReadString(s, i, j, ident);
                        segs.Add(seg);
                        i = seg.End;
                        continue;
                    }
                    segs.Add(new Seg { Start = i, End = j, Kind = SegKind.Code });
                    i = j;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    var seg = ReadString(s, i, i, "");
                    segs.Add(seg);
                    i = seg.End;
                    continue;
                }

                segs.Add(new Seg { Start = i, End = i + 1, Kind = SegKind.Code });
                i++;
            }
            return segs;
        }

        private static Seg ReadString(string s, int start, int quotePos, string prefix)
        {
            int n = s.Length;
            char q = s[quotePos];
            bool triple = quotePos + 2 < n && s[quotePos + 1] == q && s[quotePos + 2] == q;
            bool raw = prefix.IndexOf('r') >= 0 || prefix.IndexOf('R') >= 0;

            int bodyStart = quotePos + (triple ? 3 : 1);
            int i = bodyStart;

            while (i < n)
            {
                char c = s[i];
                if (c == '\\' && !raw) { i += 2; continue; }
                if (c == '\\' && raw) { i += 2; continue; }   // raw 串里反斜杠仍会转义引号
                if (!triple && c == '\n') break;              // 未闭合，交给编译器报错
                if (c == q)
                {
                    if (!triple) { i++; break; }
                    if (i + 2 < n && s[i + 1] == q && s[i + 2] == q) { i += 3; break; }
                }
                i++;
            }
            if (i > n) i = n;

            int bodyEnd = i - (triple ? 3 : 1);
            if (bodyEnd < bodyStart) bodyEnd = bodyStart;

            return new Seg
            {
                Start = start,
                End = Math.Min(i, n),
                Kind = SegKind.Str,
                Prefix = prefix,
                Quote = q,
                Triple = triple,
                BodyStart = bodyStart,
                BodyEnd = bodyEnd
            };
        }

        private static bool IsIdentStart(char c) { return char.IsLetter(c) || c == '_'; }
        private static bool IsIdentPart(char c) { return char.IsLetterOrDigit(c) || c == '_'; }

        private static bool IsStringPrefix(string p)
        {
            if (p.Length == 0 || p.Length > 3) return false;
            foreach (char c in p.ToLowerInvariant())
                if (c != 'r' && c != 'b' && c != 'u' && c != 'f') return false;
            return true;
        }

        /// <summary>把字符串内容与注释统一替换为空格（保留换行），用于结构扫描。</summary>
        private static string BuildMask(string s, List<Seg> segs)
        {
            var arr = s.ToCharArray();
            foreach (var seg in segs)
            {
                if (seg.Kind == SegKind.Code) continue;
                for (int i = seg.Start; i < seg.End && i < arr.Length; i++)
                    if (arr[i] != '\n') arr[i] = ' ';
            }
            return new string(arr);
        }

        private static int LineOf(string s, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < s.Length; i++) if (s[i] == '\n') line++;
            return line;
        }

        // ---------------------------------------------------------------- f-string

        private static string ConvertFStrings(string src, List<PythonLintIssue> issues)
        {
            var segs = Scan(src);
            var edits = new List<KeyValuePair<Seg, string>>();

            for (int k = 0; k < segs.Count; k++)
            {
                var seg = segs[k];
                if (seg.Kind != SegKind.Str) continue;
                if (seg.Prefix.IndexOf('f') < 0 && seg.Prefix.IndexOf('F') < 0) continue;

                int line = LineOf(src, seg.Start);

                if (seg.Triple)
                {
                    issues.Add(new PythonLintIssue
                    {
                        Severity = LintSeverity.Error,
                        Line = line,
                        Code = "PY2-FSTRING-TRIPLE",
                        Message = "检测到三引号 f-string。IronPython 2.7 不支持 f-string，且跨行无法安全自动转换。" +
                                  "请自行改写为 \"...{0}...\".format(x) 形式。"
                    });
                    continue;
                }

                // 相邻字面量隐式拼接会让 .format() 改写产生语法错误
                if (HasAdjacentStringLiteral(segs, k))
                {
                    issues.Add(new PythonLintIssue
                    {
                        Severity = LintSeverity.Error,
                        Line = line,
                        Code = "PY2-FSTRING-ADJACENT",
                        Message = "f-string 与相邻字符串字面量隐式拼接，无法自动转换。请合并为单个字符串并改用 .format()。"
                    });
                    continue;
                }

                string reason;
                string replacement = BuildFormatCall(src, seg, out reason);
                if (replacement == null)
                {
                    issues.Add(new PythonLintIssue
                    {
                        Severity = LintSeverity.Error,
                        Line = line,
                        Code = "PY2-FSTRING",
                        Message = "IronPython 2.7 不支持 f-string，且此处无法自动转换（" + reason +
                                  "）。请改用 \"...{0}...\".format(x)。"
                    });
                    continue;
                }

                edits.Add(new KeyValuePair<Seg, string>(seg, replacement));
                issues.Add(new PythonLintIssue
                {
                    Severity = LintSeverity.Warning,
                    Line = line,
                    Code = "PY2-FSTRING-FIXED",
                    Message = "f-string 已自动转换为 .format()。IronPython 2.7 不支持 f-string，后续请直接写 .format()。"
                });
            }

            if (edits.Count == 0) return src;

            var sb = new StringBuilder(src);
            foreach (var e in edits.OrderByDescending(e => e.Key.Start))
            {
                sb.Remove(e.Key.Start, e.Key.End - e.Key.Start);
                sb.Insert(e.Key.Start, e.Value);
            }
            return sb.ToString();
        }

        private static bool HasAdjacentStringLiteral(List<Seg> segs, int k)
        {
            // 仅跳过注释段；遇到 Code 段即停止
            Func<int, int, bool> probeSkipWs = (idx, dir) =>
            {
                int i = idx + dir;
                while (i >= 0 && i < segs.Count)
                {
                    var s = segs[i];
                    if (s.Kind == SegKind.Str) return true;
                    if (s.Kind == SegKind.Comment) { i += dir; continue; }
                    i += dir;
                    return false;
                }
                return false;
            };
            return probeSkipWs(k, +1) || probeSkipWs(k, -1);
        }

        /// <summary>把 f-string 段转换为 "...".format(...)。无法安全转换返回 null。</summary>
        private static string BuildFormatCall(string src, Seg seg, out string reason)
        {
            reason = "";
            string body = src.Substring(seg.BodyStart, seg.BodyEnd - seg.BodyStart);

            var lit = new StringBuilder();
            var args = new List<string>();
            int i = 0, n = body.Length;

            while (i < n)
            {
                char c = body[i];

                if (c == '{')
                {
                    if (i + 1 < n && body[i + 1] == '{') { lit.Append("{{"); i += 2; continue; }

                    int close = FindPlaceholderEnd(body, i);
                    if (close < 0) { reason = "花括号不配对"; return null; }

                    string inner = body.Substring(i + 1, close - i - 1);
                    string expr, conv, spec;
                    if (!SplitPlaceholder(inner, out expr, out conv, out spec, out reason)) return null;

                    lit.Append('{').Append(args.Count.ToString(CultureInfo.InvariantCulture));
                    if (!string.IsNullOrEmpty(conv)) lit.Append('!').Append(conv);
                    if (!string.IsNullOrEmpty(spec)) lit.Append(':').Append(spec);
                    lit.Append('}');
                    args.Add(expr.Trim());

                    i = close + 1;
                    continue;
                }

                if (c == '}')
                {
                    if (i + 1 < n && body[i + 1] == '}') { lit.Append("}}"); i += 2; continue; }
                    reason = "出现未配对的 '}'";
                    return null;
                }

                lit.Append(c);
                i++;
            }

            string prefix = new string(seg.Prefix.Where(ch => ch != 'f' && ch != 'F').ToArray());
            string q = seg.Quote.ToString();
            string literal = prefix + q + lit + q;

            if (args.Count == 0) return literal;
            return literal + ".format(" + string.Join(", ", args) + ")";
        }

        /// <summary>从 '{' 起找到配对 '}'，跳过嵌套括号与字符串。</summary>
        private static int FindPlaceholderEnd(string body, int open)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = open; i < body.Length; i++)
            {
                char c = body[i];
                if (quote != '\0')
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '{' || c == '[' || c == '(') depth++;
                else if (c == '}' || c == ']' || c == ')')
                {
                    depth--;
                    if (depth == 0 && c == '}') return i;
                }
            }
            return -1;
        }

        private static bool SplitPlaceholder(string inner, out string expr, out string conv, out string spec, out string reason)
        {
            expr = inner; conv = ""; spec = ""; reason = "";

            if (inner.Trim().Length == 0) { reason = "空占位符"; return false; }
            if (inner.TrimEnd().EndsWith("=", StringComparison.Ordinal))
            {
                reason = "使用了 Python 3.8 的 {x=} 调试写法";
                return false;
            }

            int depth = 0; char quote = '\0';
            int colonAt = -1, bangAt = -1;

            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (quote != '\0')
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '{' || c == '[' || c == '(') { depth++; continue; }
                if (c == '}' || c == ']' || c == ')') { depth--; continue; }
                if (depth != 0) continue;

                if (c == '!' && i + 1 < inner.Length && "rsa".IndexOf(inner[i + 1]) >= 0
                    && (i + 2 >= inner.Length || inner[i + 2] == ':'))
                {
                    bangAt = i;
                }
                else if (c == ':' && colonAt < 0)
                {
                    colonAt = i;
                }
            }

            int cut = inner.Length;
            if (bangAt >= 0)
            {
                conv = inner.Substring(bangAt + 1, 1);
                cut = bangAt;
                if (bangAt + 2 < inner.Length && inner[bangAt + 2] == ':')
                    spec = inner.Substring(bangAt + 3);
            }
            else if (colonAt >= 0)
            {
                cut = colonAt;
                spec = inner.Substring(colonAt + 1);
            }

            expr = inner.Substring(0, cut);

            if (Regex.IsMatch(expr, @"\blambda\b"))
            {
                reason = "表达式中含 lambda，冒号归属无法判定";
                return false;
            }
            if (expr.Trim().Length == 0) { reason = "占位符表达式为空"; return false; }
            if (spec.IndexOf('{') >= 0) { reason = "格式说明符中含嵌套占位符"; return false; }
            return true;
        }

        // ---------------------------------------------------------------- 类型注解

        private static string StripAnnotations(string src, List<PythonLintIssue> issues)
        {
            var segs = Scan(src);
            string mask = BuildMask(src, segs);
            var arr = src.ToCharArray();
            bool touched = false;

            // --- def 形参与返回值注解 ---
            foreach (Match m in Regex.Matches(mask, @"\bdef\s+[A-Za-z_]\w*\s*\("))
            {
                int open = m.Index + m.Length - 1;
                int close = MatchBracket(mask, open);
                if (close < 0) continue;

                int depth = 0;
                int paramStart = open + 1;
                for (int i = open + 1; i <= close; i++)
                {
                    char c = mask[i];
                    if (c == '(' || c == '[' || c == '{') { depth++; continue; }
                    if (c == ')' || c == ']' || c == '}')
                    {
                        if (i == close) { StripParamAnnotation(arr, mask, paramStart, i, ref touched); break; }
                        depth--; continue;
                    }
                    if (c == ',' && depth == 0)
                    {
                        StripParamAnnotation(arr, mask, paramStart, i, ref touched);
                        paramStart = i + 1;
                    }
                }

                // 返回值注解： ) -> X :
                int arrow = -1, tail = close + 1;
                while (tail < mask.Length && mask[tail] != ':' && mask[tail] != '\n')
                {
                    if (mask[tail] == '-' && tail + 1 < mask.Length && mask[tail + 1] == '>') { arrow = tail; break; }
                    tail++;
                }
                if (arrow >= 0)
                {
                    int colon = arrow;
                    int d2 = 0;
                    while (colon < mask.Length)
                    {
                        char c = mask[colon];
                        if (c == '(' || c == '[' || c == '{') d2++;
                        else if (c == ')' || c == ']' || c == '}') d2--;
                        else if (c == ':' && d2 == 0) break;
                        colon++;
                    }
                    if (colon < mask.Length) { Blank(arr, arrow, colon, ref touched); }
                }
            }

            // --- 语句级变量注解： name: T = v ---
            int[] depthAt = ComputeBracketDepth(mask);
            foreach (Match m in Regex.Matches(mask, @"(?m)^([ \t]*)([A-Za-z_]\w*)[ \t]*:[ \t]*[^\n=]+?(=|$)"))
            {
                int lineStart = m.Index;
                if (lineStart < depthAt.Length && depthAt[lineStart] != 0) continue;   // 在括号/字典内部，跳过
                int colon = src.IndexOf(':', m.Index + m.Groups[1].Length + m.Groups[2].Length);
                if (colon < 0) continue;

                bool hasAssign = m.Groups[3].Value == "=";
                int end = hasAssign ? m.Index + m.Length - 1 : m.Index + m.Length;
                Blank(arr, colon, end, ref touched);
                issues.Add(new PythonLintIssue
                {
                    Severity = LintSeverity.Warning,
                    Line = LineOf(src, colon),
                    Code = "PY2-VARANNOT-FIXED",
                    Message = "已移除变量类型注解。IronPython 2.7 不支持 PEP 526 语法，后续请不要写类型注解。"
                });
            }

            if (touched && !issues.Any(i => i.Code == "PY2-ANNOT-FIXED" || i.Code == "PY2-VARANNOT-FIXED"))
            {
                issues.Add(new PythonLintIssue
                {
                    Severity = LintSeverity.Warning,
                    Line = 0,
                    Code = "PY2-ANNOT-FIXED",
                    Message = "已移除函数类型注解。IronPython 2.7 不支持类型注解，后续请不要写。"
                });
            }
            return new string(arr);
        }

        private static void StripParamAnnotation(char[] arr, string mask, int start, int end, ref bool touched)
        {
            int depth = 0;
            int colon = -1;
            for (int i = start; i < end; i++)
            {
                char c = mask[i];
                if (c == '(' || c == '[' || c == '{') { depth++; continue; }
                if (c == ')' || c == ']' || c == '}') { depth--; continue; }
                if (depth != 0) continue;
                if (c == ':' && colon < 0) { colon = i; continue; }
                if (c == '=' && colon >= 0) { Blank(arr, colon, i, ref touched); return; }
            }
            if (colon >= 0) Blank(arr, colon, end, ref touched);
        }

        private static void Blank(char[] arr, int start, int end, ref bool touched)
        {
            for (int i = start; i < end && i < arr.Length; i++)
                if (arr[i] != '\n') { if (arr[i] != ' ') touched = true; arr[i] = ' '; }
        }

        private static int MatchBracket(string mask, int open)
        {
            int depth = 0;
            for (int i = open; i < mask.Length; i++)
            {
                char c = mask[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static int[] ComputeBracketDepth(string mask)
        {
            var d = new int[mask.Length + 1];
            int cur = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                d[i] = cur;
                char c = mask[i];
                if (c == '(' || c == '[' || c == '{') cur++;
                else if (c == ')' || c == ']' || c == '}') cur = Math.Max(0, cur - 1);
            }
            d[mask.Length] = cur;
            return d;
        }

        // ---------------------------------------------------------------- 纯检测

        private sealed class Rule
        {
            public string Pattern, Code, Message;
            public LintSeverity Severity = LintSeverity.Error;
        }

        private static readonly Rule[] Rules =
        {
            new Rule { Pattern = @"(?m)^\s*nonlocal\b", Code = "PY2-NONLOCAL",
                       Message = "IronPython 2.7 不支持 nonlocal。请改用可变容器（如 state = [0]，然后 state[0] += 1）。" },
            new Rule { Pattern = @"\byield\s+from\b", Code = "PY2-YIELDFROM",
                       Message = "IronPython 2.7 不支持 yield from。请改为 for x in it: yield x。" },
            new Rule { Pattern = @"(?m)^\s*async\s+def\b", Code = "PY2-ASYNC",
                       Message = "IronPython 2.7 不支持 async/await。PDPS SDK 调用必须同步执行，请改为普通函数。" },
            new Rule { Pattern = @"(?<![A-Za-z_0-9])await\s+[A-Za-z_]", Code = "PY2-AWAIT",
                       Message = "IronPython 2.7 不支持 await。请改为同步调用。" },
            new Rule { Pattern = @":=", Code = "PY2-WALRUS",
                       Message = "IronPython 2.7 不支持海象运算符 :=。请拆成独立的赋值语句。" },
            new Rule { Pattern = @"(?m)^\s*print\s+(?![(=])\S", Code = "PY2-PRINT-STMT",
                       Message = "宿主已启用 from __future__ import print_function，print 是函数。请写 print(x)。" },
        };

        private static void DetectUnsupported(string src, List<PythonLintIssue> issues)
        {
            var segs = Scan(src);
            string mask = BuildMask(src, segs);

            foreach (var rule in Rules)
            {
                foreach (Match m in Regex.Matches(mask, rule.Pattern))
                {
                    issues.Add(new PythonLintIssue
                    {
                        Severity = rule.Severity,
                        Line = LineOf(src, m.Index),
                        Code = rule.Code,
                        Message = rule.Message
                    });
                }
            }

            // .NET 互操作高频坑：泛型尖括号
            foreach (Match m in Regex.Matches(mask, @"\b(TxObjectList|List|Dictionary|IEnumerable|ITxObjectCollection)\s*<"))
            {
                issues.Add(new PythonLintIssue
                {
                    Severity = LintSeverity.Error,
                    Line = LineOf(src, m.Index),
                    Code = "IPY-GENERIC-ANGLE",
                    Message = "IronPython 实例化 .NET 泛型用方括号，不是尖括号：TxObjectList[ITxObject]()。" +
                              "更推荐直接调用 TxApi 封装层，避免直接碰泛型。"
                });
            }

            // LINQ 扩展方法未导入
            if (Regex.IsMatch(mask, @"\.(Where|Select|OrderBy|FirstOrDefault|ToList|Any|Count)\s*\(")
                && !Regex.IsMatch(mask, @"clr\s*\.\s*ImportExtensions"))
            {
                issues.Add(new PythonLintIssue
                {
                    Severity = LintSeverity.Warning,
                    Line = 0,
                    Code = "IPY-LINQ",
                    Message = "使用了 LINQ 扩展方法但未 clr.ImportExtensions(System.Linq)。" +
                              "IronPython 下更推荐直接用 Python 的列表推导式。"
                });
            }
        }

        private static void DetectImports(string src, List<PythonLintIssue> issues, bool libPathsConfigured)
        {
            var segs = Scan(src);
            string mask = BuildMask(src, segs);

            foreach (Match m in Regex.Matches(mask, @"(?m)^\s*(?:import|from)\s+([A-Za-z_][\w\.]*)"))
            {
                string mod = m.Groups[1].Value;
                string root = mod.Split('.')[0];
                int line = LineOf(src, m.Index);

                if (Python3OnlyModules.Contains(mod) || Python3OnlyModules.Contains(root))
                {
                    issues.Add(new PythonLintIssue
                    {
                        Severity = LintSeverity.Error,
                        Line = line,
                        Code = "PY2-MODULE-PY3ONLY",
                        Message = "模块 " + mod + " 是 Python 3 专有，IronPython 2.7 下不存在。" +
                                  "文件与路径请改用 System.IO，枚举请用普通常量。"
                    });
                    continue;
                }

                if (BuiltinModules.Contains(root)) continue;
                if (root == "Tecnomatix" || root == "System" || root == "TxTools" || root == "TxEuOlpUtil") continue;

                if (!libPathsConfigured)
                {
                    issues.Add(new PythonLintIssue
                    {
                        Severity = LintSeverity.Error,
                        Line = line,
                        Code = "PY2-MODULE-NOLIB",
                        Message = "模块 " + mod + " 不是 IronPython 内建模块，而当前宿主未配置标准库 Lib 目录，" +
                                  "import 必然失败。请改用 .NET 等价物（json → System.Web.Script.Serialization，" +
                                  "os/os.path → System.IO），或让管理员为宿主配置 LibPaths。"
                    });
                }
            }
        }

        private static void DetectIndentMix(string src, List<PythonLintIssue> issues)
        {
            bool tab = false, space = false;
            foreach (var line in src.Split('\n'))
            {
                if (line.Length == 0) continue;
                if (line[0] == '\t') tab = true;
                else if (line[0] == ' ') space = true;
            }
            if (tab && space)
            {
                issues.Add(new PythonLintIssue
                {
                    Severity = LintSeverity.Error,
                    Line = 0,
                    Code = "PY-INDENT-MIX",
                    Message = "同时使用了制表符和空格缩进。请统一使用 4 个空格。"
                });
            }
        }
    }

    #endregion

    #region ---------- 宿主 ----------

    public sealed class PythonHost : IDisposable
    {
        // __future__ 影响的是每个编译单元，必须逐次拼接。恰好 1 行，报错行号回退 1。
        private const string FuturePrologue =
            "from __future__ import division, print_function, unicode_literals, absolute_import";
        private const int PrologueLines = 1;

        // 注意：绝对不能用 "<txagent>" 这类 CPython 风格的伪路径。
        // DLR 在启用 Tracing/Frames 时会把它当真实路径送进 .NET 的 Path API，
        // 而 '<' '>' '"' '|' 都在 Path.GetInvalidPathChars() 里，会抛
        // ArgumentException: Illegal characters in path.
        private const string ScriptPath = "txagent_script.py";

        private readonly PythonHostOptions _opt;
        private readonly object _gate = new object();

        private Assembly _ipyAsm, _scriptingAsm;
        private string _ipyDir;
        private dynamic _engine, _scope;
        private object _sckStatements;
        private object _exceptionOps;
        private MethodInfo _formatExceptionMi;
        private CaptureStream _stdout, _stderr;
        private bool _initialized, _disposed;
        private ResolveEventHandler _resolveHandler;

        public PythonHost(PythonHostOptions options)
        {
            _opt = options ?? new PythonHostOptions();
        }

        public bool IsInitialized { get { return _initialized; } }

        private void Log(string msg)
        {
            var f = _opt.Log;
            if (f != null) { try { f("[PythonHost] " + msg); } catch { } }
        }

        // ------------------------------------------------------------ 初始化

        public void Initialize()
        {
            lock (_gate)
            {
                if (_initialized) return;

                string phase = "定位程序集";
                try
                {
                    LocateAssemblies();
                    phase = "创建引擎";
                    CreateEngine();
                    phase = "bootstrap";
                    Bootstrap();
                }
                catch (PythonHostException) { throw; }
                catch (Exception ex)
                {
                    throw new PythonHostException(
                        "初始化在【" + phase + "】阶段失败: " + ex.GetType().Name + ": " + ex.Message, ex);
                }

                _initialized = true;
                Log("初始化完成，IronPython 目录: " + (_ipyDir ?? "(进程内已加载)"));
            }
        }

        private void LocateAssemblies()
        {
            // 1) 优先复用 PDPS 进程内已加载的程序集 —— 零版本冲突
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (_ipyAsm == null && string.Equals(n, "IronPython", StringComparison.OrdinalIgnoreCase)) _ipyAsm = a;
                if (_scriptingAsm == null && string.Equals(n, "Microsoft.Scripting", StringComparison.OrdinalIgnoreCase)) _scriptingAsm = a;
            }
            if (_ipyAsm != null && _scriptingAsm != null)
            {
                try { _ipyDir = Path.GetDirectoryName(_ipyAsm.Location); } catch { }
                HookResolve();
                return;
            }

            // 2) 磁盘搜索
            var roots = new List<string>();
            string self = null;
            try { self = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { }
            if (!string.IsNullOrEmpty(self)) roots.Add(self);
            try { roots.Add(AppDomain.CurrentDomain.BaseDirectory); } catch { }
            if (!string.IsNullOrEmpty(_opt.TecnomatixRoot)) roots.Add(_opt.TecnomatixRoot);

            string found = null;
            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
            {
                found = SafeFindFile(root, "IronPython.dll", 4);
                if (found != null) break;
            }
            if (found == null)
                throw new PythonHostException(
                    "未找到 IronPython.dll。请设置 PythonHostOptions.TecnomatixRoot 指向 Tecnomatix 安装目录，" +
                    "或把 IronPython.dll / Microsoft.Scripting.dll / Microsoft.Dynamic.dll 放到插件目录。");

            _ipyDir = Path.GetDirectoryName(found);
            HookResolve();

            _ipyAsm = _ipyAsm ?? Assembly.LoadFrom(found);
            string scriptingPath = Path.Combine(_ipyDir, "Microsoft.Scripting.dll");
            if (_scriptingAsm == null)
            {
                if (!File.Exists(scriptingPath))
                    throw new PythonHostException("找到了 IronPython.dll 但同目录缺少 Microsoft.Scripting.dll: " + _ipyDir);
                _scriptingAsm = Assembly.LoadFrom(scriptingPath);
            }
        }

        private void HookResolve()
        {
            if (_resolveHandler != null || string.IsNullOrEmpty(_ipyDir)) return;
            _resolveHandler = (s, e) =>
            {
                try
                {
                    string name = new AssemblyName(e.Name).Name;
                    string p = Path.Combine(_ipyDir, name + ".dll");
                    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
                }
                catch { return null; }
            };
            AppDomain.CurrentDomain.AssemblyResolve += _resolveHandler;
        }

        private static string SafeFindFile(string root, string fileName, int maxDepth)
        {
            try
            {
                string direct = Path.Combine(root, fileName);
                if (File.Exists(direct)) return direct;
                if (maxDepth <= 0) return null;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var r = SafeFindFile(dir, fileName, maxDepth - 1);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        private void CreateEngine()
        {
            Type pythonType = _ipyAsm.GetType("IronPython.Hosting.Python", true);
            MethodInfo create = pythonType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "CreateEngine"
                                  && m.GetParameters().Length == 1
                                  && typeof(IDictionary<string, object>).IsAssignableFrom(m.GetParameters()[0].ParameterType));
            if (create == null)
                throw new PythonHostException("IronPython.Hosting.Python.CreateEngine(IDictionary) 未找到，IronPython 版本可能不兼容。");

            var engineOpts = new Dictionary<string, object>();
            if (_opt.TimeoutSeconds > 0) engineOpts["Tracing"] = true;   // sys.settrace 必需
            if (_opt.EnableFrames) engineOpts["Frames"] = true;          // 完整 traceback

            _engine = create.Invoke(null, new object[] { engineOpts });

            Type sck = _scriptingAsm.GetType("Microsoft.Scripting.SourceCodeKind", true);
            _sckStatements = Enum.Parse(sck, "Statements");

            _stdout = new CaptureStream();
            _stderr = new CaptureStream();
            var utf8 = new UTF8Encoding(false);
            dynamic io = _engine.Runtime.IO;
            io.SetOutput(_stdout, utf8);
            io.SetErrorOutput(_stderr, utf8);

            Type expOps = _scriptingAsm.GetType("Microsoft.Scripting.Hosting.ExceptionOperations", true);

            // _engine 是 dynamic，若直接链式调用会让整条 LINQ 变成动态调度，
            // 传 lambda 会触发 CS1977。先固定为 object/Type，切回静态绑定。
            object engineObj = (object)_engine;
            Type engineType = engineObj.GetType();
            MethodInfo getSvc = engineType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition);
            if (getSvc != null)
            {
                try
                {
                    _exceptionOps = getSvc.MakeGenericMethod(expOps).Invoke(engineObj, new object[] { new object[0] });
                    _formatExceptionMi = expOps.GetMethod("FormatException", new[] { typeof(Exception) });
                }
                catch (Exception ex) { Log("ExceptionOperations 获取失败: " + ex.Message); }
            }

            _scope = _engine.CreateScope();
        }

        private void Bootstrap()
        {
            var sb = new StringBuilder();
            sb.AppendLine("import sys");
            sb.AppendLine("import clr");

            foreach (var lib in _opt.LibPaths.Where(p => !string.IsNullOrEmpty(p)))
                sb.AppendLine("sys.path.append(r'" + lib.Replace("'", "\\'") + "')");

            foreach (var dll in _opt.ReferenceDlls.Where(p => !string.IsNullOrEmpty(p)))
            {
                sb.AppendLine("try:");
                sb.AppendLine("    clr.AddReferenceToFileAndPath(r'" + dll.Replace("'", "\\'") + "')");
                sb.AppendLine("except Exception, _e:");
                sb.AppendLine("    print('[bootstrap] AddReference failed: ' + str(_e))");
            }

            foreach (var ns in _opt.StarImports.Where(s => !string.IsNullOrEmpty(s)))
            {
                sb.AppendLine("try:");
                sb.AppendLine("    exec('from " + ns + " import *')");
                sb.AppendLine("except Exception, _e:");
                sb.AppendLine("    print('[bootstrap] import " + ns + " failed: ' + str(_e))");
            }

            // ---- 看门狗工厂（每次执行时由宿主单独下发 settrace，不占用户代码行号）----
            sb.AppendLine(@"
def __tx_make_trace(deadline, every):
    import time
    _n = [0]
    def _t(frame, event, arg):
        _n[0] += 1
        if _n[0] >= every:
            _n[0] = 0
            if time.time() > deadline:
                raise RuntimeError('__TX_TIMEOUT__')
        return _t
    return _t
");

            // ---- 探测辅助：agent 的主力工具 ----
            sb.AppendLine(@"
def tx_dir(o, key=None):
    '''列出对象成员及其类型。key 为子串过滤（不区分大小写）。'''
    names = dir(o)
    if key:
        k = key.lower()
        names = [n for n in names if k in n.lower()]
    for n in sorted(names):
        if n.startswith('__'):
            continue
        try:
            v = getattr(o, n)
            print('%-44s %s' % (n, type(v).__name__))
        except Exception, e:
            print('%-44s <error: %s>' % (n, e))
    print('-- %d members --' % len(names))

def tx_type(o):
    '''打印对象的 .NET 完整类型名与继承链。'''
    import clr
    try:
        t = clr.GetClrType(type(o))
        print(t.FullName)
        b = t.BaseType
        while b is not None:
            print('  <- ' + b.FullName)
            b = b.BaseType
        for i in t.GetInterfaces():
            print('  :: ' + i.FullName)
    except Exception, e:
        print('%s (not a CLR type: %s)' % (type(o).__name__, e))

def tx_sig(o, name):
    '''打印某个 .NET 方法的全部重载签名。'''
    try:
        m = getattr(o, name)
        print(m.__doc__)
    except Exception, e:
        print('error: %s' % e)
");

            var issues = new List<PythonLintIssue>();
            ExecuteUnit(sb.ToString(), false, issues);
            string boot = _stdout.Drain();
            if (!string.IsNullOrEmpty(boot)) Log("bootstrap 输出:\n" + boot);
            _stderr.Drain();
        }

        // ------------------------------------------------------------ 执行

        /// <summary>仅做预检，不执行。</summary>
        public List<PythonLintIssue> Lint(string code)
        {
            var issues = new List<PythonLintIssue>();
            PythonSanitizer.Sanitize(code, issues, _opt.LibPaths.Any(p => !string.IsNullOrEmpty(p)));
            return issues;
        }

        /// <summary>执行脚本。自动 marshal 到 PS 主线程。</summary>
        /// <param name="undoLabel">
        /// 本次执行的 undo 分组名，出现在用户的 Ctrl+Z 历史里。留空则用 Options.UndoContextName。
        /// 【配方执行务必传】配方不走审批，undo 是唯一的兜底手段；
        /// 全都叫 "TxAgent Python Script" 的话，连跑几个配方后用户认不出该撤到哪一步。
        /// </param>
        public PythonExecResult Run(string code, PythonRunMode mode, string undoLabel = null)
        {
            var ctx = _opt.MainThreadContext;
            if (ctx != null && !ReferenceEquals(SynchronizationContext.Current, ctx))
            {
                PythonExecResult result = null;
                ctx.Send(_ => { result = RunGuarded(code, mode, undoLabel); }, null);
                return result ?? Failed(mode, "MainThreadMarshalFailed", "主线程调度未返回结果。");
            }
            return RunGuarded(code, mode, undoLabel);
        }

        private PythonExecResult RunGuarded(string code, PythonRunMode mode, string undoLabel)
        {
            try { return RunCore(code, mode, undoLabel); }
            catch (Exception ex) { return Failed(mode, ex.GetType().Name, ex.Message); }
        }

        private static PythonExecResult Failed(PythonRunMode mode, string type, string msg)
        {
            return new PythonExecResult { Success = false, Mode = mode, ErrorType = type, ErrorMessage = msg };
        }

        private PythonExecResult RunCore(string code, PythonRunMode mode, string undoLabel)
        {
            var res = new PythonExecResult { Mode = mode };
            var sw = Stopwatch.StartNew();

            lock (_gate)
            {
                if (!_initialized)
                {
                    try { Initialize(); }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        res.ErrorType = "HostInitFailed"; res.ErrorMessage = ex.Message;
                        res.DurationMs = sw.ElapsedMilliseconds;
                        return res;
                    }
                }

                // ---- 1. 预检 / 改写 ----
                bool libConfigured = _opt.LibPaths.Any(p => !string.IsNullOrEmpty(p));
                string effective = PythonSanitizer.Sanitize(code, res.LintIssues, libConfigured);
                res.EffectiveCode = effective;

                if (res.HasLintErrors)
                {
                    sw.Stop();
                    res.Success = false;
                    res.ErrorType = "LintError";
                    res.ErrorMessage = "预检未通过，脚本未执行（场景未被触碰）。请按上面的提示修改后重试。";
                    res.DurationMs = sw.ElapsedMilliseconds;
                    return res;
                }

                _stdout.Drain();
                _stderr.Drain();

                // ---- 2. 语法编译预检（仍未触碰场景）----
                dynamic compiled;
                try
                {
                    dynamic source = CreateSource(FuturePrologue + "\n" + effective);
                    compiled = source.Compile();
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    FillSyntaxError(res, ex);
                    res.Success = false;
                    res.DurationMs = sw.ElapsedMilliseconds;
                    return res;
                }

                // ---- 3. 事务内执行 ----
                var undo = new UndoScope(
                    string.IsNullOrWhiteSpace(undoLabel) ? _opt.UndoContextName : undoLabel, Log);
                res.UndoAvailable = undo.Available;
                res.CanRollback = undo.CanRollback;
                bool ok = false;

                if (mode == PythonRunMode.Probe && _opt.RequireRollbackForProbe && !undo.CanRollback)
                {
                    undo.Finish(false);
                    sw.Stop();
                    res.Success = false;
                    res.ErrorType = "RollbackUnavailable";
                    res.ErrorMessage = "已配置 probe 必须可程序化回滚，但当前 undo 管理器不提供该能力，已拒绝执行。\n" +
                                       (string.IsNullOrEmpty(undo.Diagnostic) ? "" : undo.Diagnostic + "\n") +
                                       "PS 的 undo 只能分组供用户 Ctrl+Z。请把 RequireRollbackForProbe 设为 false，" +
                                       "改由静态只读检查保证 probe 安全。";
                    res.DurationMs = sw.ElapsedMilliseconds;
                    return res;
                }

                try
                {
                    StartWatchdog();
                    try
                    {
                        compiled.Execute(_scope);
                        ok = true;
                    }
                    finally { StopWatchdog(); }
                }
                catch (Exception ex)
                {
                    ok = false;
                    res.TimedOut = (ex.Message ?? "").IndexOf("__TX_TIMEOUT__", StringComparison.Ordinal) >= 0;
                    FillRuntimeError(res, ex);
                }
                finally
                {
                    // Probe 模式无条件回滚；Execute 模式失败才回滚
                    bool rollback = (mode == PythonRunMode.Probe) || !ok;
                    res.RolledBack = undo.Finish(rollback);
                }

                res.Success = ok;
                res.Output = _stdout.Drain();
                res.ErrorOutput = _stderr.Drain();
                sw.Stop();
                res.DurationMs = sw.ElapsedMilliseconds;
                return res;
            }
        }

        private dynamic CreateSource(string text)
        {
            return _engine.CreateScriptSourceFromString(text, ScriptPath, (dynamic)_sckStatements);
        }

        private void ExecuteUnit(string text, bool prependFuture, List<PythonLintIssue> issues)
        {
            dynamic source = CreateSource(prependFuture ? FuturePrologue + "\n" + text : text);
            source.Execute(_scope);
        }

        private void StartWatchdog()
        {
            if (_opt.TimeoutSeconds <= 0) return;
            try
            {
                double deadline = UnixNow() + _opt.TimeoutSeconds;
                _scope.SetVariable("__tx_deadline", deadline);
                _scope.SetVariable("__tx_every", Math.Max(1, _opt.WatchdogCheckInterval));
                CreateSource("import sys\nsys.settrace(__tx_make_trace(__tx_deadline, __tx_every))").Execute(_scope);
            }
            catch (Exception ex) { Log("看门狗启动失败（将无超时保护）: " + ex.Message); }
        }

        private void StopWatchdog()
        {
            if (_opt.TimeoutSeconds <= 0) return;
            try { CreateSource("import sys\nsys.settrace(None)").Execute(_scope); }
            catch (Exception ex) { Log("看门狗关闭失败: " + ex.Message); }
        }

        private static double UnixNow()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        // ------------------------------------------------------------ 错误信息

        private void FillSyntaxError(PythonExecResult res, Exception ex)
        {
            res.ErrorType = "SyntaxError";
            res.ErrorMessage = ex.Message;

            int line = 0, col = 0;
            try
            {
                var t = ex.GetType();
                var pl = t.GetProperty("Line");
                var pc = t.GetProperty("Column");
                if (pl != null) line = Convert.ToInt32(pl.GetValue(ex, null), CultureInfo.InvariantCulture);
                if (pc != null) col = Convert.ToInt32(pc.GetValue(ex, null), CultureInfo.InvariantCulture);
            }
            catch { }

            res.ErrorLine = Math.Max(0, line - PrologueLines);
            res.ErrorColumn = col;
            res.Traceback = AdjustLineNumbers(ex.ToString());

            if (res.ErrorLine > 0)
            {
                string bad = GetLine(res.EffectiveCode, res.ErrorLine);
                if (!string.IsNullOrEmpty(bad))
                    res.ErrorMessage += Environment.NewLine + "出错行内容: " + bad.Trim();
            }
        }

        private void FillRuntimeError(PythonExecResult res, Exception ex)
        {
            res.ErrorType = ex.GetType().Name;
            res.ErrorMessage = ex.Message;

            if (_exceptionOps != null && _formatExceptionMi != null)
            {
                try
                {
                    string formatted = _formatExceptionMi.Invoke(_exceptionOps, new object[] { ex }) as string;
                    if (!string.IsNullOrEmpty(formatted)) res.Traceback = AdjustLineNumbers(formatted);
                }
                catch { }
            }
            if (string.IsNullOrEmpty(res.Traceback)) res.Traceback = AdjustLineNumbers(ex.ToString());

            var m = Regex.Match(res.Traceback, @"line\s+(\d+)");
            if (m.Success)
            {
                int l; if (int.TryParse(m.Groups[1].Value, out l)) res.ErrorLine = l;
            }

            if (res.TimedOut)
            {
                res.ErrorType = "TimeoutError";
                res.ErrorMessage = string.Format(CultureInfo.InvariantCulture,
                    "脚本执行超过 {0} 秒被中止。", _opt.TimeoutSeconds);
            }
        }

        /// <summary>把 traceback 里的 line N 回退 prologue 偏移，使其对应用户原始代码。</summary>
        private static string AdjustLineNumbers(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, @"(?<=\bline\s)(\d+)", m =>
            {
                int v;
                if (!int.TryParse(m.Value, out v)) return m.Value;
                return Math.Max(1, v - PrologueLines).ToString(CultureInfo.InvariantCulture);
            });
        }

        private static string GetLine(string text, int lineNo)
        {
            if (string.IsNullOrEmpty(text) || lineNo < 1) return null;
            var lines = text.Split('\n');
            return lineNo <= lines.Length ? lines[lineNo - 1] : null;
        }

        // ------------------------------------------------------------ 会话管理

        /// <summary>清空 scope 变量并重新 bootstrap（agent 开新任务时调用）。</summary>
        public void ResetScope()
        {
            lock (_gate)
            {
                if (!_initialized) { Initialize(); return; }
                try
                {
                    _scope = _engine.CreateScope();
                    Bootstrap();
                    Log("scope 已重置。");
                }
                catch (Exception ex) { Log("scope 重置失败: " + ex.Message); }
            }
        }

        /// <summary>列出当前 scope 中的用户变量名（供 agent 了解上下文）。</summary>
        public List<string> GetScopeVariables()
        {
            var list = new List<string>();
            try
            {
                foreach (string name in _scope.GetVariableNames())
                    if (!name.StartsWith("__") && !name.StartsWith("tx_")) list.Add(name);
            }
            catch { }
            return list;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_resolveHandler != null)
                    AppDomain.CurrentDomain.AssemblyResolve -= _resolveHandler;
            }
            catch { }
            try { if (_engine != null) _engine.Runtime.Shutdown(); } catch { }
            _engine = null; _scope = null;
            _initialized = false;
        }

        // ------------------------------------------------------------ Undo 事务

        /// <summary>
        /// 反射访问 PS 的 undo 管理器，避免对 Tecnomatix.Engineering 的编译期依赖。
        /// 属性名与方法名在不同 PS 版本间可能不同，因此按候选列表逐个尝试；
        /// 全部失败时把 TxApplication 的真实静态成员清单写进 Diagnostic —— 
        /// 这样错误信息本身就包含答案，不依赖调用方是否接了日志。
        /// 任何一步失败都降级为"无事务"，绝不假装回滚成功。
        /// </summary>
        private sealed class UndoScope
        {
            private static readonly string[] ManagerMembers =
            {
                "ActiveUndoManager", "UndoRedoManager", "UndoManager",
                "ActiveUndoRedoManager", "ActiveUndo", "Undo"
            };
            private static readonly string[] OpenNames =
            { "StartTransaction", "OpenUndoContext", "BeginUndoContext", "StartUndoContext", "BeginTransaction" };
            private static readonly string[] CloseNames =
            { "EndTransaction", "CloseUndoContext", "EndUndoContext", "CommitTransaction" };
            // PS 2402 的 TxUndoTransactionManager 只有 StartTransaction/EndTransaction/ClearAllTransactions，
            // 没有任何回滚方法。保留候选列表只是为了兼容将来版本，实测应为 CanRollback == false。
            private static readonly string[] UndoNames =
            { "Undo", "UndoLast", "UndoTransaction", "Rollback", "RollbackTransaction" };

            private object _mgr;
            private readonly Action<string> _log;
            private bool _opened;
            private MethodInfo _undoMi;

            /// <summary>失败时的可读诊断（含真实成员清单）。成功时为空。</summary>
            public string Diagnostic = "";

            /// <summary>事务已开启。注意：这只保证"改动被分组、用户可 Ctrl+Z"，不代表能程序化回滚。</summary>
            public bool Available { get { return _mgr != null && _opened; } }

            /// <summary>该 undo 管理器是否提供程序化回滚。PS 2402 下为 false。</summary>
            public bool CanRollback { get { return _undoMi != null; } }

            public UndoScope(string name, Action<string> log)
            {
                _log = log;
                var sb = new StringBuilder();
                try
                {
                    Type app = FindType("Tecnomatix.Engineering.TxApplication");
                    if (app == null)
                    {
                        Diagnostic = "未找到 Tecnomatix.Engineering.TxApplication 类型（程序集可能未加载）。";
                        Log(Diagnostic);
                        return;
                    }

                    // --- 1) 找 undo 管理器 ---
                    object mgr = null;
                    string usedMember = null;
                    foreach (var mn in ManagerMembers)
                    {
                        object v;
                        if (!TryGetStatic(app, mn, out v, sb)) continue;
                        if (v != null) { mgr = v; usedMember = mn; break; }
                        sb.AppendLine("  " + mn + " 存在但返回 null");
                    }

                    if (mgr == null)
                    {
                        Diagnostic = "在 TxApplication 上找不到可用的 undo 管理器。"
                                   + (sb.Length > 0 ? "\n尝试记录:\n" + sb : "")
                                   + "\nTxApplication 的静态成员如下（请据此告知正确名称）:\n"
                                   + DumpStaticMembers(app);
                        Log(Diagnostic);
                        return;
                    }

                    // --- 2) 开启事务 ---
                    Type mt = mgr.GetType();
                    MethodInfo open = FindMethod(mt, OpenNames, new[] { typeof(string) });
                    if (open == null) open = FindMethod(mt, OpenNames, Type.EmptyTypes);

                    if (open == null)
                    {
                        Diagnostic = "已取到 undo 管理器 " + usedMember + " (类型 " + mt.FullName
                                   + ")，但找不到开启事务的方法。\n该类型的公共实例方法如下:\n"
                                   + DumpInstanceMethods(mt);
                        Log(Diagnostic);
                        return;
                    }

                    open.Invoke(mgr, open.GetParameters().Length == 1 ? new object[] { name } : null);
                    _mgr = mgr;
                    _opened = true;
                    _undoMi = FindMethod(mt, UndoNames, Type.EmptyTypes);
                    if (_undoMi == null)
                        Log("undo 管理器 " + mt.Name + " 不提供程序化回滚，只能分组供用户 Ctrl+Z。");
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    Diagnostic = "开启 undo 事务时抛异常: " + inner.GetType().Name + ": " + inner.Message;
                    Log(Diagnostic);
                    _mgr = null;
                    _opened = false;
                }
            }

            /// <summary>关闭事务。rollback=true 时执行 Undo。返回是否确实回滚。</summary>
            public bool Finish(bool rollback)
            {
                if (!Available) return false;
                Type mt = _mgr.GetType();
                bool rolledBack = false;

                try
                {
                    MethodInfo close = FindMethod(mt, CloseNames, Type.EmptyTypes);
                    if (close != null) close.Invoke(_mgr, null);
                    else Log("未找到关闭事务的方法，候选: " + string.Join("/", CloseNames));
                }
                catch (Exception ex) { Log("关闭 undo 事务失败: " + Unwrap(ex)); }

                if (rollback)
                {
                    if (_undoMi == null)
                    {
                        Log("该 undo 管理器不提供回滚方法，无法自动撤销。");
                        return false;
                    }
                    try { _undoMi.Invoke(_mgr, null); rolledBack = true; }
                    catch (Exception ex) { Log("回滚失败: " + Unwrap(ex)); }
                }
                return rolledBack;
            }

            // ---------------------------------------------------------- 反射助手

            private static bool TryGetStatic(Type t, string member, out object value, StringBuilder sb)
            {
                value = null;
                try
                {
                    var p = t.GetProperty(member, BindingFlags.Public | BindingFlags.Static);
                    if (p != null && p.CanRead) { value = p.GetValue(null, null); return true; }

                    var f = t.GetField(member, BindingFlags.Public | BindingFlags.Static);
                    if (f != null) { value = f.GetValue(null); return true; }

                    var m = t.GetMethod(member, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (m != null && m.ReturnType != typeof(void)) { value = m.Invoke(null, null); return true; }
                }
                catch (Exception ex)
                {
                    if (sb != null) sb.AppendLine("  " + member + " 访问抛异常: " + Unwrap(ex));
                }
                return false;
            }

            private static MethodInfo FindMethod(Type t, string[] names, Type[] sig)
            {
                foreach (var n in names)
                {
                    try
                    {
                        var m = t.GetMethod(n, BindingFlags.Public | BindingFlags.Instance, null, sig, null);
                        if (m != null) return m;
                    }
                    catch { }
                }
                return null;
            }

            private static string DumpStaticMembers(Type t)
            {
                var sb = new StringBuilder();
                try
                {
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static)
                                       .OrderBy(p => p.Name))
                        sb.AppendLine("  [prop] " + p.Name + " : " + Short(p.PropertyType));

                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                       .Where(m => !m.IsSpecialName && m.GetParameters().Length == 0)
                                       .OrderBy(m => m.Name))
                        sb.AppendLine("  [method] " + m.Name + "() : " + Short(m.ReturnType));
                }
                catch (Exception ex) { sb.AppendLine("  (枚举成员失败: " + ex.Message + ")"); }
                return sb.Length == 0 ? "  (无)" : sb.ToString().TrimEnd();
            }

            private static string DumpInstanceMethods(Type t)
            {
                var sb = new StringBuilder();
                try
                {
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                       .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                                       .OrderBy(m => m.Name))
                    {
                        sb.Append("  ").Append(m.Name).Append('(')
                          .Append(string.Join(", ", m.GetParameters().Select(p => Short(p.ParameterType)).ToArray()))
                          .Append(") : ").AppendLine(Short(m.ReturnType));
                    }
                }
                catch (Exception ex) { sb.AppendLine("  (枚举方法失败: " + ex.Message + ")"); }
                return sb.Length == 0 ? "  (无)" : sb.ToString().TrimEnd();
            }

            private static string Short(Type t)
            {
                if (t == null) return "?";
                return t.Namespace != null && t.Namespace.StartsWith("System", StringComparison.Ordinal)
                     ? t.Name : t.FullName ?? t.Name;
            }

            private static string Unwrap(Exception ex)
            {
                var e = ex.InnerException ?? ex;
                return e.GetType().Name + ": " + e.Message;
            }

            private void Log(string m) { if (_log != null) { try { _log("[UndoScope] " + m); } catch { } } }

            private static Type FindType(string fullName)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { var t = a.GetType(fullName, false); if (t != null) return t; }
                    catch { }
                }
                return null;
            }
        }
    }

    public sealed class PythonHostException : Exception
    {
        public PythonHostException(string message) : base(message) { }
        public PythonHostException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion
}