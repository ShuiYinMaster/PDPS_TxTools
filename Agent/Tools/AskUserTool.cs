// TxTools.Agent / Tools / Ui / AskUserTool.cs
// 让 AI 主动向用户提问,简化多轮 clarification 流程。
//
// 五种形态:
//   confirm       是/否
//   choice        单选(必须给 options),可 allow_custom 让用户自行填写
//   multi_choice  多选(必须给 options)
//   input         自由输入,可 multiline
//   form          混合表单 —— 一个弹窗里同时放上述任意组合,一次问完
//
// 【渲染优先级】
//   1) AskUserBridge —— HTML 聊天面板内的 askuser-modal,五种形态全支持,与整体 UI 一致。
//   2) IAgentLoop.AskUserRequest —— 旧委托,只能带 (question, kind, options),
//      仅 confirm/choice(无自定义) 走得通。
//   3) AskUserDialog —— 内置 WinForms 窗口,前两条都不可用时兜底,保证功能不断。
//
// 【线程】实现 ITxOffUiThreadTool —— 本工具阻塞等待用户点击,
// 绝不能被封送到 PS 主线程,否则主线程卡在等待里、点击又派发不出去,整个 PS UI 冻死。

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Ui;

namespace TxTools.Agent.Core
{
    public sealed class AskUserTool : ITxAgentTool, ITxOffUiThreadTool
    {
        /// <summary>等待用户回复的上限(仅内置对话框路径生效)。</summary>
        public static int TimeoutMs = AskUserDialog.DefaultTimeoutMs;

        public string Name { get { return "ask_user"; } }

        public string Description
        {
            get
            {
                return "主动向用户弹窗提问并阻塞等待回复。"
                     + "kind=confirm(是/否) | choice(单选,给 options) | multi_choice(多选,给 options) | "
                     + "input(自由输入) | form(混合表单,一个弹窗问多个问题)。"
                     + "【要问多个问题时一律用 form，不要连续弹好几次】—— 每次弹窗都打断用户一次。"
                     + "form 传 fields 数组，每项含 name/label/type/options/default，"
                     + "type 可为 confirm|choice|multi_choice|input，返回 JSON 对象(name -> 用户的回答)。"
                     + "【不要什么都用 confirm】—— 只能回是/否拿不到有效信息，会逼用户再打一轮字；"
                     + "有备选方案就用 choice，选项之外可能还有别的答案时加 allow_custom=true。"
                     + "适用：关键决策分歧、破坏性操作前的语义确认、缺参数补齐、从批量对象里挑子集。"
                     + "返回：confirm 返 yes/no；choice 返选中项；multi_choice 返逗号分隔多项；"
                     + "input 返输入字符串；form 返 JSON；用户取消或超时会明确说明。";
            }
        }

        public bool IsReadOnly { get { return true; } }   // 不改场景,只跟用户交互

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'required': ['question','kind'],
                    'properties': {
                        'question':     { 'type': 'string', 'description': '给用户看的问题或表单标题(简明中文)' },
                        'kind':         { 'type': 'string', 'enum': ['confirm','choice','multi_choice','input','form'] },
                        'options':      { 'type': 'array', 'items': { 'type': 'string' }, 'description': 'choice/multi_choice 的选项数组' },
                        'default':      { 'type': 'string', 'description': '可选。input 预填内容 / choice 默认选中项 / confirm 默认值(yes|no)' },
                        'allow_custom': { 'type': 'boolean', 'description': '可选。choice 时额外提供“其他(自行填写)”，默认 false' },
                        'multiline':    { 'type': 'boolean', 'description': '可选。input 时使用多行输入框，默认 false' },
                        'fields': {
                            'type': 'array',
                            'description': 'kind=form 时必填。每个字段一项，按顺序自上而下渲染',
                            'items': {
                                'type': 'object',
                                'required': ['name','type'],
                                'properties': {
                                    'name':         { 'type': 'string', 'description': '结果 JSON 里的键名' },
                                    'label':        { 'type': 'string', 'description': '字段标题，留空用 name' },
                                    'type':         { 'type': 'string', 'enum': ['confirm','choice','multi_choice','input'] },
                                    'options':      { 'type': 'array', 'items': { 'type': 'string' } },
                                    'default':      { 'type': 'string' },
                                    'allow_custom': { 'type': 'boolean' },
                                    'multiline':    { 'type': 'boolean' }
                                }
                            }
                        }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var question = ToolInputHelpers.String(input["question"]);
            var kind = (ToolInputHelpers.String(input["kind"], "confirm") ?? "confirm").ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(question))
                return "Error: question 必需";

            if (kind != "confirm" && kind != "choice" && kind != "multi_choice"
                && kind != "input" && kind != "form")
                return "Error: kind 必须为 confirm|choice|multi_choice|input|form";

            var defaultValue = ToolInputHelpers.String(input["default"]);
            bool allowCustom = Bool(input["allow_custom"]);
            bool multiline = Bool(input["multiline"]);

            List<string> options = null;
            List<AskField> fields = null;

            if (kind == "choice" || kind == "multi_choice")
            {
                options = StringList(input["options"] as JArray);
                if (options == null || options.Count == 0)
                    return "Error: kind=" + kind + " 时 options 不能为空";
            }
            else if (kind == "form")
            {
                string err;
                fields = ParseFields(input["fields"] as JArray, out err);
                if (err != null) return err;
            }

            // ── 1) HTML 聊天面板 ──
            if (AskUserBridge.IsAvailable)
            {
                try
                {
                    var payload = BuildPayload(question, kind, options, defaultValue,
                                               allowCustom, multiline, fields);
                    // 【勿改回 payload.ToString(Formatting.None)】—— 该重载在本环境运行期不存在,
                    // 会抛 MissingMethodException。JsonConvert.SerializeObject 的签名跨版本稳定。
                    var answer = AskUserBridge.Ask(JsonConvert.SerializeObject(payload));
                    return answer == null ? CancelHint() : answer;
                }
                catch (Exception ex)
                {
                    Warn("HTML 面板路径失败，降级: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // ── 2) 旧委托(只带得动 confirm / 无自定义 choice) ──
            string reply;
            if (kind != "form" && kind != "multi_choice" && !allowCustom && !multiline
                && TryAskViaHost(question, kind, options, out reply))
            {
                return reply ?? CancelHint();
            }

            // ── 3) 内置 WinForms 对话框兜底 ──
            try
            {
                if (kind == "form")
                {
                    var result = AskUserDialog.ShowForm(question, fields, TimeoutMs);
                    if (AskUserDialog.IsTimeout(result)) return TimeoutHint();
                    if (result == null) return CancelHint();

                    var jo = new JObject();
                    foreach (var kv in result) jo[kv.Key] = kv.Value;
                    return JsonConvert.SerializeObject(jo);
                }

                var r = AskUserDialog.Show(question, kind, options, defaultValue,
                                           allowCustom, multiline, TimeoutMs);
                if (r == AskUserDialog.TimedOut) return TimeoutHint();
                if (r == null) return CancelHint();
                return r;
            }
            catch (Exception ex)
            {
                return "Error: 弹窗异常 - " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        // ── 负载构造 ──

        private static JObject BuildPayload(
            string question, string kind, List<string> options, string defaultValue,
            bool allowCustom, bool multiline, List<AskField> fields)
        {
            var jo = new JObject();
            jo["question"] = question;
            jo["kind"] = kind;

            if (options != null) jo["options"] = new JArray(options.ToArray());
            if (!string.IsNullOrEmpty(defaultValue)) jo["default"] = defaultValue;
            if (allowCustom) jo["allowCustom"] = true;
            if (multiline) jo["multiline"] = true;

            if (fields != null)
            {
                var arr = new JArray();
                foreach (var f in fields)
                {
                    var fo = new JObject();
                    fo["name"] = f.Name;
                    if (!string.IsNullOrEmpty(f.Label)) fo["label"] = f.Label;
                    fo["type"] = f.Type;
                    if (f.Options != null) fo["options"] = new JArray(new List<string>(f.Options).ToArray());
                    if (!string.IsNullOrEmpty(f.Default)) fo["default"] = f.Default;
                    if (f.AllowCustom) fo["allowCustom"] = true;
                    if (f.Multiline) fo["multiline"] = true;
                    arr.Add(fo);
                }
                jo["fields"] = arr;
            }

            return jo;
        }

        private static List<AskField> ParseFields(JArray arr, out string error)
        {
            error = null;

            if (arr == null || arr.Count == 0)
            {
                error = "Error: kind=form 时 fields 不能为空";
                return null;
            }

            var fields = new List<AskField>(arr.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < arr.Count; i++)
            {
                var o = arr[i] as JObject;
                if (o == null) { error = "Error: fields[" + i + "] 不是对象"; return null; }

                var name = ToolInputHelpers.String(o["name"]);
                var type = (ToolInputHelpers.String(o["type"], "input") ?? "input").ToLowerInvariant();

                if (string.IsNullOrWhiteSpace(name))
                { error = "Error: fields[" + i + "].name 必需"; return null; }

                if (!seen.Add(name))
                { error = "Error: fields 里 name 重复: " + name; return null; }

                if (type != "confirm" && type != "choice" && type != "multi_choice" && type != "input")
                { error = "Error: fields[" + i + "].type 必须为 confirm|choice|multi_choice|input"; return null; }

                var opts = StringList(o["options"] as JArray);
                if ((type == "choice" || type == "multi_choice") && (opts == null || opts.Count == 0))
                { error = "Error: fields[" + i + "] (" + name + ") 为 " + type + "，options 不能为空"; return null; }

                fields.Add(new AskField
                {
                    Name = name,
                    Label = ToolInputHelpers.String(o["label"]),
                    Type = type,
                    Options = opts,
                    Default = ToolInputHelpers.String(o["default"]),
                    AllowCustom = Bool(o["allow_custom"]),
                    Multiline = Bool(o["multiline"])
                });
            }

            return fields;
        }

        // ── 旧委托路径 ──

        private static bool TryAskViaHost(string question, string kind, List<string> options, out string reply)
        {
            reply = null;

            IAgentLoop loop = null;
            try
            {
                loop = AgentLoop.Current;
                if (loop == null || loop.AskUserRequest == null)
                {
                    var harness = TxTools.Agent.Harness.HarnessAgentLoop.Current;
                    if (harness != null && harness.AskUserRequest != null) loop = harness;
                }
            }
            catch { loop = null; }

            if (loop == null || loop.AskUserRequest == null) return false;

            try
            {
                reply = loop.AskUserRequest(question, kind, options == null ? null : options.ToArray());
                return true;
            }
            catch (NotImplementedException)
            {
                Warn("UI 委托未实现 kind=" + kind + "，改用内置对话框。");
                return false;
            }
            catch (InvalidOperationException ex)
            {
                // 委托实现直接操作了 UI 控件,而本工具跑在后台线程 -> 跨线程访问异常。
                Warn("UI 委托跨线程访问失败，改用内置对话框: " + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Warn("UI 委托异常，改用内置对话框: " + ex);
                return false;
            }
        }

        // ── 辅助 ──

        private static string TimeoutHint()
        {
            return "(timeout) 用户在限定时间内没有回复。不要重复提问，"
                 + "改为按最保守的方案继续，或直接说明你在等什么信息然后结束本轮。";
        }

        private static string CancelHint()
        {
            return "(cancelled) 用户取消了本次提问。不要重复弹同一个问题，"
                 + "改为直接说明你需要什么信息，或按最保守的方案继续。";
        }

        private static List<string> StringList(JArray arr)
        {
            if (arr == null) return null;
            var list = new List<string>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
                list.Add(arr[i] != null ? arr[i].ToString() : "");
            return list;
        }

        private static bool Bool(JToken t)
        {
            if (t == null) return false;
            try
            {
                if (t.Type == JTokenType.Boolean) return (bool)t;
                bool b;
                return bool.TryParse(t.ToString(), out b) && b;
            }
            catch { return false; }
        }

        private static void Warn(string msg)
        {
            try { AuditLog.Write("[warn] [ask_user] " + msg); } catch { }
        }
    }
}