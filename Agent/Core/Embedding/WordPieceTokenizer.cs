// TxTools.Agent / Core / Embedding / WordPieceTokenizer.cs
//
// BERT 系嵌入模型(bge / m3e / text2vec 等)配套的 WordPiece 分词器。
//
// 为什么要自己写:ONNX Runtime 只做张量推理,不含分词。而 .NET Framework 4.8 上
// 没有现成可靠的 BERT 分词器包 —— 引一个新依赖又是一次版本冲突风险
// (Newtonsoft 那次已经证明代价)。好在中文场景下 WordPiece 很简单:
// 汉字基本是一字一 token,复杂的贪心最长匹配只在英文/数字上才起作用。
//
// 需要的文件:模型目录下的 vocab.txt(每行一个 token,行号即 id)。
// 各家 BERT 模型都带这个文件,格式统一。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TxTools.Agent.Core
{
    public sealed class WordPieceTokenizer
    {
        private readonly Dictionary<string, int> _vocab;
        private readonly int _unk, _cls, _sep, _pad;
        private readonly bool _lowerCase;

        public int MaxLength { get; set; }

        public WordPieceTokenizer(string vocabPath, bool lowerCase = true)
        {
            if (!File.Exists(vocabPath))
                throw new FileNotFoundException("找不到 vocab.txt: " + vocabPath);

            _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
            var lines = File.ReadAllLines(vocabPath, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimEnd('\r', '\n');
                if (t.Length == 0) continue;
                if (!_vocab.ContainsKey(t)) _vocab[t] = i;
            }

            _lowerCase = lowerCase;
            _unk = Id("[UNK]", 100);
            _cls = Id("[CLS]", 101);
            _sep = Id("[SEP]", 102);
            _pad = Id("[PAD]", 0);
            MaxLength = 512;
        }

        private int Id(string token, int fallback)
        {
            int v;
            return _vocab.TryGetValue(token, out v) ? v : fallback;
        }

        /// <summary>编码成 [CLS] ... [SEP],超长截断。返回 ids 与等长的 mask。</summary>
        public void Encode(string text, out long[] ids, out long[] mask)
        {
            var tokens = new List<int> { _cls };

            // 留出 [CLS] 与 [SEP] 两个位置
            int budget = MaxLength - 2;

            foreach (var word in BasicSplit(text ?? ""))
            {
                if (tokens.Count - 1 >= budget) break;
                foreach (var piece in WordToPieces(word))
                {
                    if (tokens.Count - 1 >= budget) break;
                    tokens.Add(piece);
                }
            }

            tokens.Add(_sep);

            ids = new long[tokens.Count];
            mask = new long[tokens.Count];
            for (int i = 0; i < tokens.Count; i++) { ids[i] = tokens[i]; mask[i] = 1; }
        }

        /// <summary>批量编码,右侧 pad 到本批最长。</summary>
        public void EncodeBatch(IList<string> texts, out long[,] ids, out long[,] mask, out int seqLen)
        {
            var all = new List<long[]>();
            var masks = new List<long[]>();
            int max = 1;

            foreach (var t in texts)
            {
                long[] i2, m2;
                Encode(t, out i2, out m2);
                all.Add(i2);
                masks.Add(m2);
                if (i2.Length > max) max = i2.Length;
            }

            seqLen = max;
            ids = new long[all.Count, max];
            mask = new long[all.Count, max];

            for (int r = 0; r < all.Count; r++)
                for (int c = 0; c < max; c++)
                {
                    bool has = c < all[r].Length;
                    ids[r, c] = has ? all[r][c] : _pad;
                    mask[r, c] = has ? masks[r][c] : 0;
                }
        }

        // ── 基础切分 ──
        //
        // 规则:空白切开;标点单独成词;CJK 每字单独成词(这是中文能简单处理的关键);
        // 其余连续字母数字算一个词,交给 WordPiece 做贪心最长匹配。

        private IEnumerable<string> BasicSplit(string text)
        {
            var buf = new StringBuilder();

            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (buf.Length > 0) { yield return Flush(buf); }
                    continue;
                }

                if (IsCjk(ch) || IsPunct(ch))
                {
                    if (buf.Length > 0) { yield return Flush(buf); }
                    yield return ch.ToString();
                    continue;
                }

                buf.Append(_lowerCase ? char.ToLowerInvariant(ch) : ch);
            }

            if (buf.Length > 0) yield return Flush(buf);
        }

        private static string Flush(StringBuilder sb)
        {
            var s = sb.ToString();
            sb.Length = 0;
            return s;
        }

        /// <summary>贪心最长匹配。首片直接查表,后续片加 "##" 前缀。</summary>
        private IEnumerable<int> WordToPieces(string word)
        {
            if (word.Length == 0) yield break;

            // 超长词直接 UNK,避免 O(n^2) 退化
            if (word.Length > 100) { yield return _unk; yield break; }

            int start = 0;
            var pieces = new List<int>();

            while (start < word.Length)
            {
                int end = word.Length;
                int found = -1;

                while (start < end)
                {
                    var sub = word.Substring(start, end - start);
                    if (start > 0) sub = "##" + sub;

                    int id;
                    if (_vocab.TryGetValue(sub, out id)) { found = id; break; }
                    end--;
                }

                if (found < 0) { pieces.Clear(); pieces.Add(_unk); break; }

                pieces.Add(found);
                start = end;
            }

            foreach (var p in pieces) yield return p;
        }

        private static bool IsCjk(char c)
        {
            int cp = c;
            return (cp >= 0x4E00 && cp <= 0x9FFF)    // 基本汉字
                || (cp >= 0x3400 && cp <= 0x4DBF)    // 扩展 A
                || (cp >= 0xF900 && cp <= 0xFAFF)    // 兼容汉字
                || (cp >= 0x3040 && cp <= 0x30FF);   // 日文假名
        }

        private static bool IsPunct(char c)
        {
            if (char.IsPunctuation(c) || char.IsSymbol(c)) return true;
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat == UnicodeCategory.OtherPunctuation;
        }
    }
}
