using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TxTools.CatiaPartTree.Conversion
{
    // One geometric occurrence in source coordinates. The flat list is written
    // first and keeps the layout understood by earlier converters.
    public sealed class AssemblyPartRecord
    {
        public string GeometryPath;
        public string Name;
        public double[] Frame;
        // Only native CATProduct assemblies carry the referenced CATPart digest.
        public string Digest;
    }

    // Product structure used when several source occurrences are written into
    // one JT. A part node refers to its record in the flat manifest prefix.
    public sealed class AssemblyStructureNode
    {
        public string Name;
        public bool IsPart;
        public int PartIndex = -1;
        public readonly List<AssemblyStructureNode> Children = new List<AssemblyStructureNode>();

        public void Add(AssemblyStructureNode child)
        {
            if (child != null) Children.Add(child);
        }
    }

    internal sealed class AssemblyStructure
    {
        internal AssemblyPartRecord[] Parts;
        // Null when the manifest only contains the flat prefix.
        internal AssemblyStructureNode Root;
    }

    // The hierarchy section is appended after the flat part list. A converter
    // that predates it stops after the parts and writes a flat JT; this reader
    // still accepts such a manifest and reports a null root.
    internal static class AssemblyManifest
    {
        private const int MaxParts = 100000;
        private const int MaxNodes = 200000;

        internal static void Write(Stream stream, IList<AssemblyPartRecord> parts, AssemblyStructureNode root, bool withDigest)
        {
            if (parts == null || root == null) throw new InvalidDataException("装配清单缺少几何实例或层级。");
            if (parts.Count < 1 || parts.Count > MaxParts) throw new InvalidDataException("装配几何实例数无效。");
            var nodes = new List<AssemblyStructureNode>();
            var parents = new List<int>();
            var pending = new Stack<KeyValuePair<AssemblyStructureNode, int>>();
            pending.Push(new KeyValuePair<AssemblyStructureNode, int>(root, -1));
            while (pending.Count != 0)
            {
                var item = pending.Pop();
                int index = nodes.Count;
                nodes.Add(item.Key);
                parents.Add(item.Value);
                if (nodes.Count > MaxNodes) throw new InvalidDataException("装配层级节点数超过上限。");
                for (int i = item.Key.Children.Count - 1; i >= 0; i--)
                    pending.Push(new KeyValuePair<AssemblyStructureNode, int>(item.Key.Children[i], index));
            }
            if (root.IsPart) throw new InvalidDataException("装配根节点不能是几何实例。");
            var used = new bool[parts.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (!node.IsPart) continue;
                if (node.PartIndex < 0 || node.PartIndex >= parts.Count)
                    throw new InvalidDataException("装配层级引用了不存在的几何实例：" + (node.Name ?? ""));
                if (used[node.PartIndex]) throw new InvalidDataException("装配层级重复引用几何实例：" + (node.Name ?? ""));
                used[node.PartIndex] = true;
                if (node.Children.Count != 0) throw new InvalidDataException("装配零件节点不能包含子节点：" + (node.Name ?? ""));
            }
            for (int i = 0; i < used.Length; i++)
                if (!used[i]) throw new InvalidDataException("装配层级未引用几何实例：" + (parts[i].Name ?? ""));
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(parts.Count);
                foreach (var part in parts)
                {
                    if (part == null || string.IsNullOrWhiteSpace(part.GeometryPath))
                        throw new InvalidDataException("装配几何路径为空。");
                    writer.Write(part.GeometryPath);
                    writer.Write(part.Name ?? string.Empty);
                    if (part.Frame == null || part.Frame.Length != 12) throw new InvalidDataException("装配实例位姿无效。");
                    foreach (double value in part.Frame) writer.Write(value);
                    if (withDigest) writer.Write(part.Digest ?? string.Empty);
                }
                writer.Write(nodes.Count);
                for (int i = 0; i < nodes.Count; i++)
                {
                    writer.Write(parents[i]);
                    writer.Write(nodes[i].IsPart ? (byte)1 : (byte)0);
                    writer.Write(nodes[i].Name ?? string.Empty);
                    if (nodes[i].IsPart) writer.Write(nodes[i].PartIndex);
                }
                writer.Flush();
            }
        }

        internal static AssemblyStructure Read(Stream stream, bool withDigest)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                int count = reader.ReadInt32();
                if (count < 1 || count > MaxParts) throw new InvalidDataException("装配几何实例数无效。");
                var parts = new AssemblyPartRecord[count];
                for (int i = 0; i < count; i++)
                {
                    var part = new AssemblyPartRecord
                    {
                        GeometryPath = reader.ReadString(),
                        Name = reader.ReadString(),
                        Frame = new double[12]
                    };
                    for (int k = 0; k < part.Frame.Length; k++) part.Frame[k] = reader.ReadDouble();
                    if (withDigest) part.Digest = reader.ReadString();
                    if (string.IsNullOrWhiteSpace(part.GeometryPath))
                        throw new InvalidDataException("装配几何路径为空。");
                    parts[i] = part;
                }
                if (reader.BaseStream.Position >= reader.BaseStream.Length)
                    return new AssemblyStructure { Parts = parts, Root = null };

                int nodeCount = reader.ReadInt32();
                if (nodeCount < 1 || nodeCount > MaxNodes) throw new InvalidDataException("装配层级节点数无效。");
                var nodes = new AssemblyStructureNode[nodeCount];
                var parents = new int[nodeCount];
                for (int i = 0; i < nodeCount; i++)
                {
                    parents[i] = reader.ReadInt32();
                    if (i == 0 ? parents[i] != -1 : (parents[i] < 0 || parents[i] >= i))
                        throw new InvalidDataException("装配层级父子关系无效。");
                    byte kind = reader.ReadByte();
                    if (kind > 1) throw new InvalidDataException("装配层级节点类型无效。");
                    var node = new AssemblyStructureNode { Name = reader.ReadString(), IsPart = kind == 1 };
                    if (node.IsPart)
                    {
                        node.PartIndex = reader.ReadInt32();
                        if (node.PartIndex < 0 || node.PartIndex >= count)
                            throw new InvalidDataException("装配层级引用了不存在的几何实例：" + node.Name);
                    }
                    nodes[i] = node;
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length)
                    throw new InvalidDataException("装配清单包含多余数据。");
                if (nodes[0].IsPart) throw new InvalidDataException("装配根节点不能是几何实例。");
                var used = new bool[count];
                for (int i = 1; i < nodeCount; i++)
                {
                    if (nodes[parents[i]].IsPart) throw new InvalidDataException("装配零件节点不能包含子节点：" + nodes[parents[i]].Name);
                    nodes[parents[i]].Add(nodes[i]);
                    if (nodes[i].IsPart)
                    {
                        if (used[nodes[i].PartIndex]) throw new InvalidDataException("装配层级重复引用几何实例：" + nodes[i].Name);
                        used[nodes[i].PartIndex] = true;
                    }
                }
                return new AssemblyStructure { Parts = parts, Root = nodes[0] };
            }
        }
    }
}
