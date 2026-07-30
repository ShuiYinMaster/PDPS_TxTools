// TxTools.CatiaPartTree / Core / TreeEditNode.cs
// CATIA 目录树的可编辑模型：GUI 调整后的树直接用于在 PS 创建零件树。
// 从 Agent.Core.Catia.CatiaProductNode 转换而来（复用 CatiaTreeReader 读取）。

using System.Collections.Generic;
using TxTools.Agent.Core.Catia;

namespace TxTools.CatiaPartTree.Core
{
    public sealed class TreeEditNode
    {
        public string Name;
        public string PartNumber;
        public string Revision;
        public string Definition;
        public bool IsAssembly;

        /// <summary>是否勾选导入（创建/归类时跳过未勾选节点）。</summary>
        public bool Include = true;

        /// <summary>GUI 展开状态（刷新后恢复）。</summary>
        public bool IsExpanded;

        public TreeEditNode Parent;
        public readonly List<TreeEditNode> Children = new List<TreeEditNode>();

        public bool IsLeaf { get { return Children.Count == 0; } }

        public int DescendantCount()
        {
            int n = Children.Count;
            foreach (var c in Children) n += c.DescendantCount();
            return n;
        }

        public int IncludedCount()
        {
            int n = Include ? 1 : 0;
            foreach (var c in Children) n += c.IncludedCount();
            return n;
        }

        public void AddChild(TreeEditNode child)
        {
            child.Parent = this;
            Children.Add(child);
        }

        public void RemoveChild(TreeEditNode child)
        {
            Children.Remove(child);
            child.Parent = null;
        }

        public static TreeEditNode FromCatia(CatiaProductNode catia, TreeEditNode parent)
        {
            var n = new TreeEditNode
            {
                Name = catia.Name,
                PartNumber = catia.PartNumber,
                Revision = catia.Revision,
                Definition = catia.Definition,
                IsAssembly = catia.IsAssembly,
                Parent = parent
            };
            foreach (var c in catia.Children)
                n.Children.Add(FromCatia(c, n));
            return n;
        }
    }
}
