using System;
using System.Collections.Generic;
using System.Linq;

namespace TxAgent.Core
{
    /// <summary>工具注册表。线程安全的读，注册建议在启动阶段一次性完成。</summary>
    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools =
            new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get { return _tools.Count; }
        }

        public void Register(ITool tool)
        {
            if (tool == null) throw new ArgumentNullException("tool");
            if (string.IsNullOrEmpty(tool.Name))
                throw new ArgumentException("工具必须有 Name", "tool");

            if (_tools.ContainsKey(tool.Name))
                throw new InvalidOperationException("工具名重复注册: " + tool.Name);

            _tools[tool.Name] = tool;
        }

        public void RegisterRange(IEnumerable<ITool> tools)
        {
            if (tools == null) return;
            foreach (var t in tools) Register(t);
        }

        public ITool Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            ITool tool;
            return _tools.TryGetValue(name, out tool) ? tool : null;
        }

        public IEnumerable<ITool> All()
        {
            return _tools.Values;
        }

        /// <summary>
        /// 导出给 LLM 的 schema 列表。
        /// readOnlyOnly=true 时只暴露只读工具——用于"先分析后写入"的两阶段执行。
        /// </summary>
        public IList<ToolSchema> ExportSchemas(bool readOnlyOnly)
        {
            return _tools.Values
                .Where(t => !readOnlyOnly || !t.IsWrite)
                .Select(t => new ToolSchema
                {
                    Name = t.Name,
                    Description = t.Description,
                    ParametersJsonSchema = t.ParametersJsonSchema
                })
                .ToList();
        }

        /// <summary>模型调了不存在的工具时，回灌一份可用工具清单帮它自纠。</summary>
        public string DescribeAvailable()
        {
            return string.Join(", ", _tools.Keys.OrderBy(k => k));
        }
    }
}
