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
            _map[tool.Name] = tool; // 同名后注册者覆盖
        }

        public bool TryGet(string name, out ITxAgentTool tool)
        {
            return _map.TryGetValue(name ?? string.Empty, out tool);
        }

        public bool Remove(string name)
        {
            return _map.Remove(name ?? string.Empty);
        }

        public List<ToolDef> ToToolDefs()
        {
            var list = new List<ToolDef>(_map.Count);
            foreach (var t in _map.Values)
            {
                list.Add(new ToolDef
                {
                    Type = "function",
                    Function = new FunctionDef
                    {
                        Name = t.Name,
                        Description = t.Description,
                        Parameters = t.InputSchema
                    }
                });
            }
            return list;
        }
    }
}
