// TxTools.Agent / Core / ToolGate.cs
//
// 工具按组暴露。
//
// ── 为什么要做 ──
//   工具定义排在 prompt 最前面,40+ 个工具的 schema 是几万字符。
//   开销不在钱上(前缀缓存命中率 97%,每轮真正全价的只有几百 token),
//   而在【注意力】:工具数超过 20~30 之后模型的选择准确率明显下降 ——
//   表现为选了个沾边但不对的工具,或者该用专用工具时跑去写 run_csharp。
//
//   所以按任务类型只暴露相关的那一组。改源码的会话不需要 CEE 信号工具,
//   看场景的会话不需要 8 个代码工具。
//
// ── 必须按会话粒度决定 ──
//   工具集一旦在会话中途变化,prompt 前缀就变了,缓存整个作废,
//   之后每轮都按全价重算。所以 EnabledGroups 只在建 registry 时读一次,
//   会话内不再变动。要换工具集就开新对话。

using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.Agent.Core
{
    public static class ToolGate
    {
        /// <summary>
        /// 组定义:组名 -> 该组包含的工具名。
        /// 没列进任何组的工具视为"核心",永远暴露。
        /// </summary>
        private static readonly Dictionary<string, string[]> Groups =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                // 改别的插件源码。不打开工作区就用不上,平时纯属占位
                { "code", new[] {
                    "open_workspace", "code_outline", "code_read", "code_search",
                    "code_edit", "code_create_file", "code_revert", "code_build" } },

                // CEE 逻辑/信号/传感器。只在做虚拟调试类任务时需要
                { "cee", new[] {
                    "get_resource_logic_status", "list_cee_signals", "add_logic_to_resource",
                    "create_scl_container", "create_cee_module", "create_cee_signal",
                    "create_lb_sensor", "list_lb_elements", "connect_signal_to_lb", "copy_logic" } },

                // 文档生成
                { "doc", new[] {
                    "export_docx", "export_pptx", "export_table",
                    "render_pptx_template", "inspect_pptx_template" } },

                // 视图与截图
                { "view", new[] {
                    "capture_viewer_image", "screenshot_window", "set_camera_view",
                    "set_view_to_object", "analyze_viewport" } },

                // CATIA 集成
                { "catia", new[] { "catia_read_tree", "import_catia_tree_to_parts" } },

                // 本地知识库
                { "knowledge", new[] {
                    "search_knowledge", "read_knowledge",
                    "knowledge_status", "knowledge_reindex" } },
            };

        /// <summary>
        /// 默认启用的组。core(未分组的工具)始终启用,不需要列在这里。
        ///
        /// code 默认关闭 —— 8 个工具且只在改源码时有用,是最值得省的一组。
        /// cee 默认关闭 —— 10 个工具,做虚拟调试才用。
        /// </summary>
        private static readonly HashSet<string> _enabled =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "doc", "view", "catia", "knowledge" };

        private static readonly object _sync = new object();

        public static List<string> AllGroups()
        {
            return Groups.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> EnabledGroups()
        {
            lock (_sync) { return _enabled.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(); }
        }

        public static bool IsEnabled(string group)
        {
            lock (_sync) { return _enabled.Contains(group ?? ""); }
        }

        /// <summary>
        /// 开关一个组。【下次新建对话才生效】—— 会话中途改会击穿前缀缓存,
        /// 而且模型已经基于旧工具集做过规划,中途抽走工具只会让它困惑。
        /// </summary>
        public static bool SetEnabled(string group, bool on)
        {
            if (string.IsNullOrWhiteSpace(group) || !Groups.ContainsKey(group)) return false;
            lock (_sync)
            {
                if (on) _enabled.Add(group);
                else _enabled.Remove(group);
            }
            return true;
        }

        /// <summary>该工具当前是否应该暴露给模型。</summary>
        public static bool ShouldExpose(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            foreach (var kv in Groups)
                foreach (var n in kv.Value)
                    if (string.Equals(n, toolName, StringComparison.OrdinalIgnoreCase))
                        return IsEnabled(kv.Key);

            return true;   // 未分组 = 核心工具
        }

        /// <summary>
        /// 从持久化设置(UserPrefsStore)恢复启用的工具组。每次建会话前调用一次。
        /// 没存过(首次)保持代码默认值。
        /// </summary>
        public static void RestoreFromPrefs()
        {
            try
            {
                var prefs = UserPrefsStore.Load();
                var saved = prefs.EnabledToolGroups;
                if (saved == null || saved.Count == 0) return;   // 未设置过,用默认

                lock (_sync)
                {
                    _enabled.Clear();
                    foreach (var g in saved)
                        if (Groups.ContainsKey(g)) _enabled.Add(g);
                }
            }
            catch { /* 读取失败保持默认 */ }
        }

        /// <summary>当前启用的组清单(持久化用)。</summary>
        public static List<string> SnapshotEnabled()
        {
            lock (_sync) { return new List<string>(_enabled); }
        }

        /// <summary>给系统提示词用的一行说明:告诉模型哪些能力当前不可用。</summary>
        public static string DescribeDisabled()
        {
            var off = Groups.Keys.Where(k => !IsEnabled(k)).ToList();
            if (off.Count == 0) return "";

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "code", "改写外部项目源码" },
                { "cee", "CEE 逻辑/信号/传感器" },
                { "doc", "文档生成(docx/pptx/xlsx)" },
                { "view", "视图相机与截图" },
                { "catia", "CATIA 集成" },
                { "knowledge", "本地知识库" },
            };

            var parts = off.Select(k => names.ContainsKey(k) ? names[k] : k);
            return "【当前未启用的能力】" + string.Join("、", parts)
                 + "。用户要求这类操作时，说明需要先在设置里启用对应工具组并新建对话，不要试图用别的工具绕。";
        }
    }
}
