// TxTools.Agent / Core / ToolRegistry.cs
// 工具注册表:按名注册/查找,并构造发给 API 的工具声明列表。
//
// v3: 新增 Tools 只读枚举,供 UI 列出全部已注册工具。

using System;
using System.Collections.Generic;

namespace TxTools.Agent.Core
{
    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, ITxAgentTool> _map =
            new Dictionary<string, ITxAgentTool>(StringComparer.Ordinal);

        public int Count { get { return _map.Count; } }

        /// <summary>已注册工具的只读枚举(按注册顺序不保证,内部是 Dictionary)。供 UI 展示。</summary>
        public IEnumerable<ITxAgentTool> Tools { get { return _map.Values; } }

        public void Register(ITxAgentTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            if (string.IsNullOrWhiteSpace(tool.Name))
                throw new ArgumentException("工具必须有非空的 Name。");

            // 同名后注册者覆盖。这在配方(save_recipe 动态生成工具)撞上内置工具名时
            // 会【静默替换】—— 内置工具凭空消失，排查起来毫无头绪。
            // 覆盖行为保留(有时确实想覆盖)，但必须留下痕迹。
            ITxAgentTool existing;
            if (_map.TryGetValue(tool.Name, out existing) && !ReferenceEquals(existing, tool))
            {
                var msg = "[ToolRegistry] 工具名重复注册: \"" + tool.Name + "\"，"
                        + existing.GetType().Name + " 被 " + tool.GetType().Name + " 覆盖。";
                System.Diagnostics.Debug.WriteLine(msg);
                try { AuditLog.Write("[warn] " + msg); } catch { }
            }

            _map[tool.Name] = tool;
        }

        public bool TryGet(string name, out ITxAgentTool tool)
        {
            return _map.TryGetValue(name ?? string.Empty, out tool);
        }

        public bool Remove(string name)
        {
            return _map.Remove(name ?? string.Empty);
        }

        /// <summary>
        /// 构造发给 API 的工具声明。
        ///
        /// 【顺序必须稳定】工具定义排在请求最前面,是 prompt 前缀的一部分。
        /// Dictionary 的遍历顺序不保证(受插入顺序、扩容 rehash 影响),
        /// 顺序一变整个前缀缓存就击穿 —— DeepSeek 缓存命中 0.02元/M vs 未命中 1元/M,
        /// 差 50 倍。按 Name 排序,把顺序钉死。
        /// </summary>
        public List<ToolDef> ToToolDefs()
        {
            var ordered = new List<ITxAgentTool>(_map.Values);
            ordered.Sort(delegate (ITxAgentTool a, ITxAgentTool b)
            {
                return string.CompareOrdinal(a.Name, b.Name);
            });

            var list = new List<ToolDef>(ordered.Count);
            foreach (var t in ordered)
            {
                // 安全网: function.name 必须匹配 ^[a-zA-Z0-9_-]+$。
                // RecipeTool.Name 已做过净化, 此处兜底处理任何漏网的非 ASCII 工具名。
                var fnName = t.Name;
                if (!System.Text.RegularExpressions.Regex.IsMatch(fnName, @"^[a-zA-Z0-9_-]+$"))
                {
                    fnName = Recipe.ToApiSafeName(fnName);
                }
                list.Add(new ToolDef
                {
                    Type = "function",
                    Function = new FunctionDef
                    {
                        Name = fnName,
                        Description = t.Description,
                        Parameters = t.InputSchema
                    }
                });
            }
            return list;
        }
    }
}
