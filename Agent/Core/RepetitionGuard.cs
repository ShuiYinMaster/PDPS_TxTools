// TxTools.Agent / Core / RepetitionGuard.cs
//
// 流式输出的退化循环检测。
//
// ── 要挡的是什么 ──
//   长上下文 + 宽松输出预算下，模型会陷入自我强化的重复:
//   同一句"让我发送被控端脚本。构造好。执行。读取结果。"连写七八十遍，
//   一口气烧掉一万多 token，全程没有任何 tool_call。
//   这是通用失败模式，官方端点同样会出现。
//
//   解码惩罚(frequency_penalty)能降低发生概率，但降不到零 ——
//   真发生时必须能自己止损，而不是干等用户手动点停止。
//
// ── 判据 ──
//   在输出末尾找一个长度 L 的块，看它是否连续重复了 N 次。
//   只查尾部固定窗口，不做全文匹配 —— 检测要跟得上流式速度，
//   而退化循环一定是发生在尾部的。
//
// ── 为什么阈值不能设太松 ──
//   正常输出里也有合法重复:markdown 表格、代码里的相似行、列表项。
//   所以要求"连续、完全一致、且块本身有一定长度"三个条件同时满足。
//   宁可漏检也不要误杀 —— 误杀会打断正常的长输出。

using System;
using System.Text;

namespace TxTools.Agent.Core
{
    public sealed class RepetitionGuard
    {
        /// <summary>检测窗口:只看最近这么多字符。</summary>
        public int WindowChars = 4000;

        /// <summary>重复块的长度下限。太短容易误杀("。\n\n"这种)。</summary>
        public int MinBlockChars = 12;

        /// <summary>重复块的长度上限。超过这个长度的重复通常是合法内容。</summary>
        public int MaxBlockChars = 400;

        /// <summary>连续重复多少次算退化。</summary>
        public int RepeatThreshold = 6;

        /// <summary>累计多少新字符检查一次。逐字符检查太费。</summary>
        public int CheckEveryChars = 300;

        private readonly StringBuilder _buf = new StringBuilder();
        private int _sinceCheck;

        /// <summary>触发时的重复块，便于日志说明是卡在什么内容上。</summary>
        public string DetectedBlock { get; private set; }

        public bool Tripped { get; private set; }

        public void Reset()
        {
            _buf.Length = 0;
            _sinceCheck = 0;
            Tripped = false;
            DetectedBlock = null;
        }

        /// <summary>喂入一个流式分片。返回 true 表示检测到退化，调用方应中断本次生成。</summary>
        public bool Feed(string delta)
        {
            if (Tripped) return true;
            if (string.IsNullOrEmpty(delta)) return false;

            _buf.Append(delta);
            _sinceCheck += delta.Length;

            // 只保留窗口，前面的丢掉
            if (_buf.Length > WindowChars)
                _buf.Remove(0, _buf.Length - WindowChars);

            if (_sinceCheck < CheckEveryChars) return false;
            _sinceCheck = 0;

            var block = FindRepeatingTail(_buf.ToString());
            if (block == null) return false;

            Tripped = true;
            DetectedBlock = block;
            return true;
        }

        /// <summary>
        /// 在尾部找连续重复的块。找到返回该块，否则 null。
        ///
        /// 从短块开始试:退化循环的周期通常不长，先试短的能更早命中，
        /// 也避免把"两段相似的长文本"误判成重复。
        /// </summary>
        private string FindRepeatingTail(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            int max = Math.Min(MaxBlockChars, text.Length / RepeatThreshold);

            for (int len = MinBlockChars; len <= max; len++)
            {
                var tail = text.Substring(text.Length - len);

                // 空白构成的块不算 —— 换行和缩进本来就会大量重复
                if (IsBlank(tail)) continue;

                int count = 1;
                int pos = text.Length - len;

                while (pos - len >= 0)
                {
                    if (string.CompareOrdinal(text, pos - len, tail, 0, len) != 0) break;
                    count++;
                    pos -= len;
                    if (count >= RepeatThreshold) return tail;
                }
            }

            return null;
        }

        private static bool IsBlank(string s)
        {
            foreach (var c in s)
                if (!char.IsWhiteSpace(c)) return false;
            return true;
        }

        /// <summary>回灌给模型的提示。说清发生了什么以及该怎么改，而不只是"出错了"。</summary>
        public string BuildHint()
        {
            var sb = new StringBuilder();
            sb.AppendLine("【已中断:检测到输出陷入重复循环】");
            sb.AppendLine("你在反复输出同一段话而没有实际发出工具调用，本次生成已被系统截断。");

            if (!string.IsNullOrEmpty(DetectedBlock))
            {
                var b = DetectedBlock.Replace("\r", " ").Replace("\n", " ").Trim();
                if (b.Length > 80) b = b.Substring(0, 80) + "…";
                sb.Append("重复的内容: \"").Append(b).AppendLine("\"");
            }

            sb.AppendLine();
            sb.AppendLine("这通常意味着当前这一步对你来说太复杂了。请换个做法:");
            sb.AppendLine("  1. 【直接发工具调用】不要先用文字描述你打算调什么，想好就调；");
            sb.AppendLine("  2. 【把大步骤拆小】一次只做一件能立刻验证的事；");
            sb.AppendLine("  3. 【中间数据落盘】需要跨步骤携带的坐标表、映射关系、对象清单，"
                        + "写成文件再读，不要在对话里逐条罗列 —— 几十行以上的数据放在上下文里"
                        + "既占篇幅又容易抄错；");
            sb.Append("  4. 实在做不下去就停下来告诉用户卡在哪里，让他决定，不要空转。");

            return sb.ToString();
        }
    }
}
