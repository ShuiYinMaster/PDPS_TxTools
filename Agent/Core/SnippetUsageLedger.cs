// TxTools.Agent / Core / SnippetUsageLedger.cs
//
// 片段复用的「归因台账」——把"取出片段"和"这次复用到底成没成"这两件事对上。
//
// ── 为什么要有它 ──
//   原来 get_snippet 一取出就 IncrementUsage(即记一次成功)。
//   但"取出"≠"用成功":模型可能取出来看一眼发现不对、改得面目全非、
//   或者压根没执行就换了思路。取出即成功等于把命中率无条件写成 100%,
//   健康度、TopN 排序、不可靠判定全部建立在假数据上 ——
//   它不会报错,只会慢慢失真到不可用。这正是"静默的错误答案"那一类。
//
// ── 归因规则 ──
//   get_snippet 取出时只登记「待判定」,不动任何计数。
//   随后 run_csharp / run_python 执行完毕时回调 NoteExecution(执行的代码, 是否成功):
//   拿执行代码与各待判定片段做标识符重合度比对,命中者才回填成功/失败。
//
//   两条保守规则,宁可不计也不错记:
//   1) 重合度不到阈值 —— 不判定,继续挂着(模型可能先跑别的探查代码,稍后才用片段)。
//   2) 有两个以上片段都够阈值且分数接近 —— 不猜是哪个,双方都记「未判定」并出池。
//      这跟 patch_snippet 里 old_text 命中多次就报错是同一条原则:
//      命中多个的场合不要替调用方选。
//
// ── 三态计数 ──
//   成功 / 失败 / 未判定。健康度只用前两者当分母。
//   会话中断、用户换话题、取出后没执行任何代码 —— 这些是"未判定",不是"失败"。
//   一律记失败会把噪声算进成功率,跟"取出即成功"是同一个病的镜像。
//
// 台账只存内存:未判定项本来就不进统计,进程退出丢掉正好。
// 成功/失败一经判定立即落盘到 SnippetStore。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TxTools.Agent.Core
{
    /// <summary>一条待判定的片段取用记录。</summary>
    public sealed class PendingUsage
    {
        public string SnippetName { get; set; }

        /// <summary>片段语言。归因只在同语言之间进行。</summary>
        public string Lang { get; set; }

        /// <summary>片段代码里的标识符及其权重,用于比对。</summary>
        public Dictionary<string, int> Tokens { get; set; }

        /// <summary>权重总和,做分母。</summary>
        public int TotalWeight { get; set; }

        public DateTime RetrievedUtc { get; set; }
    }

    public static class SnippetUsageLedger
    {
        /// <summary>待判定项的存活时长。超时说明取出后就没再执行相关代码,记未判定。</summary>
        public static int ExpireMinutes = 30;

        /// <summary>
        /// 判定为"用上了"的重合度下限。
        /// 太低会把无关代码算成复用,太高则容不下正常的改写(换对象名、加个循环)。
        /// 0.5 意味着片段里过半的骨架标识符出现在了执行代码中。
        /// </summary>
        public static double MatchThreshold = 0.5;

        /// <summary>
        /// 第一名要比第二名高出这个倍数才算"明确是它"。
        /// 否则视为分不清,双方都记未判定。
        /// </summary>
        public static double AmbiguityRatio = 1.5;

        /// <summary>标识符太少的片段没法做归因,直接不登记。</summary>
        public static int MinTokenWeight = 6;

        private static readonly List<PendingUsage> _pending = new List<PendingUsage>();
        private static readonly object _lock = new object();

        // ── 登记 ──

        /// <summary>
        /// get_snippet 取出片段时调用。只登记,不动任何计数。
        /// 同名片段重复取出只保留最新一次(刷新时间戳)。
        /// </summary>
        public static void Register(string snippetName, string code, string lang)
        {
            if (string.IsNullOrWhiteSpace(snippetName) || string.IsNullOrWhiteSpace(code)) return;

            var normLang = SnippetStore.NormalizeLang(lang);
            var tokens = Tokenize(code);
            int total = tokens.Values.Sum();

            if (total < MinTokenWeight)
            {
                // 片段太短或几乎没有可辨识的标识符 —— 归因必然不准,索性不登记。
                // 不登记的后果是这条片段的成功率永远停在初值,
                // 比记一个瞎猜出来的数字要好。
                try
                {
                    AuditLog.Write("[info] [Snippet] " + snippetName
                        + " 标识符过少,本次取用不做复用归因。");
                }
                catch { }
                return;
            }

            lock (_lock)
            {
                _pending.RemoveAll(p => string.Equals(p.SnippetName, snippetName,
                                                      StringComparison.OrdinalIgnoreCase));
                _pending.Add(new PendingUsage
                {
                    SnippetName = snippetName,
                    Lang = normLang,
                    Tokens = tokens,
                    TotalWeight = total,
                    RetrievedUtc = DateTime.UtcNow
                });
            }
        }

        // ── 判定 ──

        /// <summary>
        /// 【异步版,工具执行路径用这个】
        /// 代码执行完毕后调用,回填最匹配片段的成功/失败。
        /// 判定要读写片段文件,跟本次执行结果无关,没理由让用户等它。
        /// </summary>
        public static void NoteExecutionAsync(string executedCode, bool success, string lang)
        {
            if (string.IsNullOrWhiteSpace(executedCode)) return;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { NoteExecution(executedCode, success, lang); }
                catch (Exception ex)
                {
                    try { AuditLog.Write("[warn] [Snippet] 复用归因失败: " + ex.Message); } catch { }
                }
            });
        }

        /// <summary>
        /// 同步版。会做文件 I/O,执行路径请用 NoteExecutionAsync。
        /// 返回被回填的片段名;未判定或无命中返回 null。
        /// </summary>
        public static string NoteExecution(string executedCode, bool success, string lang)
        {
            if (string.IsNullOrWhiteSpace(executedCode)) return null;

            var normLang = SnippetStore.NormalizeLang(lang);

            List<string> undecided = null;
            string hitName = null;

            // 【落盘一律在锁外】RecordUndecided / RecordOutcome 会遍历片段目录并写文件，
            // 在锁里做这些等于让每次片段取出都排队等一次磁盘 I/O。
            // 锁只保护 _pending 这个列表本身。
            lock (_lock)
            {
                undecided = ExpireLocked();
                if (_pending.Count == 0) goto done;

                var exec = Tokenize(executedCode);

                // 【只跟同语言的待判定项比】C# 片段和 Python 代码会共用一大批
                // Tx* 标识符，跨语言比对必然出现高重合度的假命中 ——
                // 表现是"取出一段 C# 片段、接着跑了段 Python"被记成这段 C# 用成功了。
                var scored = _pending
                    .Where(p => string.Equals(p.Lang, normLang, StringComparison.Ordinal))
                    .Select(p => new { Item = p, Score = Overlap(p, exec) })
                    .OrderByDescending(x => x.Score)
                    .ToList();

                if (scored.Count == 0) goto done;

                var best = scored[0];

                // 规则一:够不上阈值就别判 —— 模型可能在跑别的探查代码,片段稍后才用。
                if (best.Score < MatchThreshold) goto done;

                // 规则二:第二名也够阈值且咬得很紧 —— 分不清是哪个,不猜。
                if (scored.Count > 1)
                {
                    var second = scored[1];
                    if (second.Score >= MatchThreshold && best.Score < second.Score * AmbiguityRatio)
                    {
                        if (undecided == null) undecided = new List<string>();
                        undecided.Add(best.Item.SnippetName);
                        undecided.Add(second.Item.SnippetName);
                        _pending.Remove(best.Item);
                        _pending.Remove(second.Item);

                        try
                        {
                            AuditLog.Write("[info] [Snippet] 执行代码同时匹配 "
                                + best.Item.SnippetName + " 与 " + second.Item.SnippetName
                                + "(" + best.Score.ToString("0.00") + " / "
                                + second.Score.ToString("0.00") + "),不做归因。");
                        }
                        catch { }

                        goto done;
                    }
                }

                hitName = best.Item.SnippetName;
                _pending.Remove(best.Item);
            }

        done:
            FlushUndecided(undecided);
            if (hitName == null) return null;

            SnippetStore.RecordOutcome(hitName, success);
            try
            {
                AuditLog.Write("[info] [Snippet] 复用归因: " + hitName
                    + " → " + (success ? "成功" : "失败"));
            }
            catch { }

            return hitName;
        }

        /// <summary>
        /// 会话结束/切换时调用:把还挂着的待判定项全部记为未判定并清空。
        /// 不调用也不会出错(靠超时兜底),但调用了统计更及时。
        /// </summary>
        public static void FlushAll()
        {
            List<string> names;
            lock (_lock)
            {
                names = _pending.Select(p => p.SnippetName).ToList();
                _pending.Clear();
            }
            FlushUndecided(names);
        }

        // ── 内部 ──

        /// <summary>清掉超时项,返回它们的名字(在锁内调用)。</summary>
        private static List<string> ExpireLocked()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-ExpireMinutes);
            var dead = _pending.Where(p => p.RetrievedUtc < cutoff).ToList();
            if (dead.Count == 0) return null;

            foreach (var d in dead) _pending.Remove(d);
            return dead.Select(d => d.SnippetName).ToList();
        }

        /// <summary>把未判定项落盘。返回 null,方便在 return 处直接调用。</summary>
        private static string FlushUndecided(List<string> names)
        {
            if (names == null || names.Count == 0) return null;
            foreach (var n in names)
            {
                try { SnippetStore.RecordUndecided(n); } catch { }
            }
            return null;
        }

        /// <summary>
        /// 重合度 = 片段标识符里出现在执行代码中的权重占比。
        /// 方向是"片段的内容有多少被用上了",不是"执行代码有多少来自片段" ——
        /// 后者会因为模型在片段外面套了大段新逻辑而被稀释。
        /// </summary>
        private static double Overlap(PendingUsage p, Dictionary<string, int> exec)
        {
            if (p == null || p.TotalWeight <= 0 || exec == null || exec.Count == 0) return 0;

            int hit = 0;
            foreach (var kv in p.Tokens)
                if (exec.ContainsKey(kv.Key)) hit += kv.Value;

            return (double)hit / p.TotalWeight;
        }

        private static readonly Regex StringLit = new Regex("\"[^\"]*\"", RegexOptions.Compiled);
        private static readonly Regex Comment = new Regex(@"//[^\n]*|/\*.*?\*/",
            RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex Word = new Regex(@"[A-Za-z_][A-Za-z0-9_]{2,}",
            RegexOptions.Compiled);

        /// <summary>
        /// 语言关键字和到处都是的通用名 —— 留着只会让任意两段 C# 看起来都很像。
        /// </summary>
        private static readonly HashSet<string> Stop = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "var","int","long","double","float","bool","char","byte","string","object","void",
            "new","null","true","false","this","base","out","ref","params",
            "if","else","for","foreach","while","switch","case","break","continue","return",
            "try","catch","finally","throw","using","namespace","class","struct","enum",
            "public","private","protected","internal","static","readonly","const","sealed",
            "override","virtual","abstract","interface","partial","where","yield","lock",
            "List","Dictionary","HashSet","Array","String","Math","DateTime","Exception",
            "Count","Length","Add","Remove","Contains","ToString","Trim","Substring",
            "item","items","list","result","results","value","values","index","name","names",
            "temp","tmp","obj","str","sb","log","Console","WriteLine","AppendLine","Append",
            // Python 侧的等价噪声词。不加的话每段 Python 代码都自带一批共同标识符，
            // 任意两段之间的重合度被垫高，阈值就失去意义。
            "print","format","range","len","import","from","def","self","None","True","False",
            "elif","not","and","or","in","is","pass","lambda","clr","sys","enumerate","append"
        };

        /// <summary>
        /// 抽标识符并加权:Tx 开头的类型/方法是 PS 特有的骨架,权重 3;
        /// 其余普通标识符权重 1。这样"两段都用了 for 循环"不会被误判成同一段代码,
        /// 而"两段都调了 TxTypeFilter + GetAllDescendants"才真正说明问题。
        /// </summary>
        public static Dictionary<string, int> Tokenize(string code)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(code)) return map;

            var t = Comment.Replace(code, " ");
            t = StringLit.Replace(t, " ");

            foreach (Match m in Word.Matches(t))
            {
                var w = m.Value;
                if (Stop.Contains(w)) continue;

                int weight = w.StartsWith("Tx", StringComparison.Ordinal)
                          || w.StartsWith("ITx", StringComparison.Ordinal) ? 3 : 1;

                if (!map.ContainsKey(w)) map[w] = weight;
            }
            return map;
        }

        /// <summary>当前待判定池,供 snippet_health 诊断。</summary>
        public static string Describe()
        {
            List<PendingUsage> snapshot;
            lock (_lock) { snapshot = _pending.OrderBy(p => p.RetrievedUtc).ToList(); }

            if (snapshot.Count == 0) return "复用待判定池为空。";

            var sb = new StringBuilder();
            sb.AppendLine("复用待判定 " + snapshot.Count + " 条（"
                + ExpireMinutes + " 分钟内未执行相关代码则记未判定）:");
            foreach (var p in snapshot)
                sb.Append("  ").Append(p.SnippetName)
                  .Append("  [").Append(p.Lang).Append("]")
                  .Append("  取出于 ")
                  .Append(((int)(DateTime.UtcNow - p.RetrievedUtc).TotalMinutes))
                  .AppendLine(" 分钟前");
            return sb.ToString();
        }
    }
}
