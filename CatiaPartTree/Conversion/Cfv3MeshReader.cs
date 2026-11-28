using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TxTools.CatiaPartTree.Conversion
{
    // UVR geometry in a 3DXML archive is stored as a CFV3 Skeleton stream.
    // Raw bytes and a single persistent little-endian bit stream are interleaved.
    internal static class Cfv3MeshReader
    {
        private static readonly byte[] SkeletonName = Encoding.BigEndianUnicode.GetBytes("Skeleton\0");

        private static int BigEndian(byte[] data, int offset)
        {
            if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("CFV3 目录截断");
            return data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3];
        }

        private static byte[] Skeleton(byte[] data)
        {
            int directory = BigEndian(data, 8), length = BigEndian(data, 12);
            if (directory < 64 || length < 54 || (long)directory + length > data.Length)
                throw new InvalidDataException("CFV3 目录范围无效");
            int end = directory + length, record = -1;
            for (int offset = directory + 4; offset <= end - SkeletonName.Length - 54; offset++)
            {
                if (data[offset] != 0 || data[offset + 1] != 'S' || BigEndian(data, offset - 4) != SkeletonName.Length)
                    continue;
                if (!SkeletonName.SequenceEqual(new ArraySegment<byte>(data, offset, SkeletonName.Length))) continue;
                if (record >= 0) throw new InvalidDataException("CFV3 有多个 Skeleton 记录");
                record = offset + SkeletonName.Length;
            }
            if (record < 0) throw new InvalidDataException("CFV3 缺少 Skeleton 流");
            int size = BigEndian(data, record), logicalSize = BigEndian(data, record + 14);
            int extents = BigEndian(data, record + 30);
            if (data[record + 8] != 0 || data[record + 9] != 2 || extents < 1 ||
                size != 34L + 20L * extents || size > end - record || logicalSize < 1 || logicalSize > data.Length)
                throw new InvalidDataException("CFV3 Skeleton 记录无效");
            var result = new byte[logicalSize];
            int position = 0;
            for (int i = 0; i < extents; i++)
            {
                int extent = record + 34 + 20 * i, start = BigEndian(data, extent), count = BigEndian(data, extent + 4);
                if (start < 64 || count < 0 || count != BigEndian(data, extent + 8) ||
                    position != BigEndian(data, extent + 12) || (long)start + count > data.Length || count > logicalSize - position)
                    throw new InvalidDataException("CFV3 Skeleton extent 范围无效");
                Buffer.BlockCopy(data, start, result, position, count);
                position += count;
            }
            if (position != logicalSize) throw new InvalidDataException("CFV3 Skeleton 长度不匹配");
            return result;
        }

        private sealed class SceneNode
        {
            internal Frame12 Local;
            internal MeshShape Mesh;
            internal readonly List<SceneNode> Children = new List<SceneNode>();
        }

        private sealed class Reader
        {
            private static readonly double[] Identity = Frame12.Identity.Components;
            private readonly byte[] _data;
            private readonly bool _packed;
            private readonly Dictionary<int, SceneNode> _references = new Dictionary<int, SceneNode>();
            private int _position, _end, _bitsLeft, _expanded;
            private uint _word;
            private int _leafCount, _faceCount, _emptyLeafCount;

            internal Reader(byte[] data, bool packed) { _data = data; _packed = packed; _end = data.Length; }

            private void Need(int count)
            {
                if (count < 0 || count > _end - _position) throw new InvalidDataException("CFV3 Skeleton 数据截断");
            }
            private void Skip(int count) { Need(count); _position += count; }
            private byte Byte() { Need(1); return _data[_position++]; }
            private uint UInt32() { Need(4); uint result = BitConverter.ToUInt32(_data, _position); _position += 4; return result; }
            private int Count() { byte value = Byte(); return value == 0xff ? checked((int)UInt32()) : value; }
            private double Float()
            {
                Need(4); float value = BitConverter.ToSingle(_data, _position); _position += 4;
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("CFV3 浮点值无效");
                return value;
            }
            private int Bits(int count)
            {
                if (_bitsLeft >= count)
                {
                    int result = (int)(_word & ((1u << count) - 1));
                    _word >>= count;
                    _bitsLeft -= count;
                    return result;
                }
                int lowBits = _bitsLeft;
                uint low = _word;
                _word = UInt32();
                int highBits = count - lowBits;
                int value = (int)(low | ((_word & ((1u << highBits) - 1)) << lowBits));
                _word >>= highBits;
                _bitsLeft = 32 - highBits;
                return value;
            }

            internal MeshShape Read(string name, string source, string hash)
            {
                var root = Node(0);
                int remaining = _end - _position;
                if (_packed && remaining == 4 && Float() != 1)
                    throw new NotSupportedException("CGR 使用未支持的场景比例");
                if (!_packed && remaining == 1 && Byte() != 0)
                    throw new NotSupportedException("CGR SceneGraph 结束标记未适配");
                if (_position != _end) throw new NotSupportedException("CGR 场景流含未解析的节点数据");
                var vertices = new List<double[]>();
                var normals = new List<double[]>();
                var triangles = new List<int[]>();
                var colors = new List<byte[]>();
                void Append(SceneNode node, Frame12 parent, int depth)
                {
                    if (depth > 100 || ++_expanded > 100000)
                        throw new InvalidDataException("CFV3 场景引用展开数或层级超限");
                    var frame = node.Local == null ? parent : parent == null ? node.Local : parent.ThenChild(node.Local);
                    var mesh = node.Mesh;
                    if (mesh != null && frame != null)
                    {
                        int collapsed;
                        mesh = mesh.BakeInstanceForJt(frame, out collapsed);
                    }
                    if (mesh != null)
                    {
                        int first = vertices.Count;
                        vertices.AddRange(mesh.vertices);
                        normals.AddRange(mesh.normals);
                        foreach (var t in mesh.triangles) triangles.Add(new[] { first + t[0], first + t[1], first + t[2] });
                        colors.AddRange(mesh.rgb);
                    }
                    foreach (var child in node.Children) Append(child, frame, depth + 1);
                }
                Append(root, null, 0);
                if (triangles.Count == 0)
                    throw new NotSupportedException("CFV3 表示中没有可转换的三角面（叶=" + _leafCount +
                        "，面=" + _faceCount + "，空叶=" + _emptyLeafCount + "）");
                var result = new MeshShape { name = name, instanceId = "source/1", sourceFile = source, sourceSha256 = hash,
                    vertices = vertices.ToArray(), triangles = triangles.ToArray(), rgb = colors.ToArray(), normals = normals.ToArray(),
                    nativeNormalBindings = normals.All(value => value != null) };
                result.Validate();
                result.FillMissingNormals();
                return result;
            }

            private SceneNode Node(int depth)
            {
                if (depth > 100) throw new InvalidDataException("CFV3 场景层级超限");
                int start = _position;
                byte tag = Byte();
                if (tag == 0x95) return new SceneNode { Mesh = Leaf(start) };
                var node = new SceneNode();
                int restoreEnd = -1;
                if (tag == 0xa3)
                {
                    // The size field is relative to the tag/prefix. Root A3 scenes
                    // cover the whole stream; nested A3 scenes cover only their own
                    // payload and may carry trailing scene data.
                    int size = Count();
                    long a3End = (long)start + 2 + size;
                    if (a3End > _end || a3End < start + 41)
                        throw new InvalidDataException("CFV3 A3 场景长度超出父场景（offset=" + start +
                            ", size=" + size + ", end=" + _end + "）");
                    if (a3End < _end)
                    {
                        restoreEnd = _end;
                        _end = (int)a3End;
                    }
                    if (_packed)
                    {
                        // This prefix field can occupy one byte or a marker plus Int32.
                        Skip(start + 6 - _position);
                        Count();
                        Skip(34);
                    }
                    else Skip(start + 41 - _position);
                    int header = Count();
                    if (_packed && (header == 0x00010103 || header == 0x01010103))
                        ReadSceneMaterial(header == 0x01010103);
                    else if (header != 0x0103)
                        throw new NotSupportedException("CFV3 A3 场景头变体未适配（offset=" + start +
                            ", value=" + header + "）");
                }
                else if (tag == 0x62)
                {
                    if (_packed)
                    {
                        Count();
                        Skip(34);
                    }
                    else Skip(35);
                    Need(5);
                    uint header = _packed && _data[_position] == 0xff ? BitConverter.ToUInt32(_data, _position + 1) : 0;
                    if (header == 0x00010103 || header == 0x01010103)
                    {
                        Skip(5);
                        ReadSceneMaterial(header == 0x01010103);
                    }
                    else
                    {
                        Skip(4);
                        byte matrixFlag = Byte();
                        if (matrixFlag == 1)
                        {
                            var m = new double[16];
                            for (int i = 0; i < m.Length; i++) m[i] = Float();
                            if (m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1)
                                throw new NotSupportedException("CFV3 使用非仿射场景变换（offset=" + start + "）");
                            var values = new[] { m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10], m[12], m[13], m[14] };
                            if (!values.SequenceEqual(Identity)) node.Local = new Frame12(values);
                        }
                        else if (matrixFlag != 0) throw new NotSupportedException("CFV3 场景变换标志未适配：" + matrixFlag +
                            "（offset=" + start + "）");
                    }
                }
                else throw new NotSupportedException("CFV3 场景节点未适配：" + tag.ToString("X2") +
                    "（offset=" + start + "）");
                int count = Count();
                Need(count);
                for (int i = 0; i < count; i++)
                {
                    byte link = Byte();
                    if (link == 0) { node.Children.Add(Node(depth + 1)); continue; }
                    if (link != 1 && link != 2) throw new NotSupportedException("CFV3 场景引用标志未适配：" + link);
                    int id = Count();
                    if (!_references.TryGetValue(id, out var child))
                    {
                        if (link == 2) throw new InvalidDataException("CFV3 场景引用不存在：" + id);
                        child = Node(depth + 1);
                        _references.Add(id, child);
                    }
                    node.Children.Add(child);
                }
                if (restoreEnd >= 0)
                {
                    if (_position > _end)
                        throw new InvalidDataException("CFV3 A3 子场景长度溢出（offset=" + start + "）");
                    _position = _end;
                    _end = restoreEnd;
                }
                return node;
            }

            private void ReadSceneMaterial(bool hasAppearanceSlot)
            {
                // Scene appearance precedes the children. JT currently stores face RGB,
                // so consume the environment image and its sampling parameters without applying them.
                if (hasAppearanceSlot) Count();
                if (Byte() != 1) throw new NotSupportedException("CFV3 场景材质头未适配");
                Skip(15 * sizeof(float));
                if (Byte() != 1 || Byte() != 1)
                    throw new NotSupportedException("CFV3 场景环境贴图标志未适配");
                Count(); // Environment map reference.
                Need(1);
                if (_data[_position] == 0xff)
                {
                    // The image is embedded on its first use. Later slots may reference it.
                    Skip(6);
                    int width = Count(), height = Count(), bytes = Count();
                    if (width == 0 || height == 0 || bytes % 4 != 0 || (long)width * height != bytes / 4)
                        throw new InvalidDataException("CFV3 场景环境贴图长度无效");
                    if (Byte() != 3 || Byte() != 1) throw new NotSupportedException("CFV3 场景环境贴图布局未适配");
                    Skip(2 * sizeof(float));
                    Skip(bytes);
                    Skip(Count()); // Environment image name.
                }
                if (Byte() != 0 || Byte() != 0 || Byte() != 1 || Byte() != 5 || Byte() != 3 || Byte() != 1 || Byte() != 1)
                    throw new NotSupportedException("CFV3 场景环境贴图参数未适配");
                Skip(19 * sizeof(float) + 3);
            }

            private MeshShape Leaf(int start)
            {
                _leafCount++;
                if (Byte() != 0xff) throw new NotSupportedException("CFV3 网格叶头未适配");
                int end = checked(start + 1 + (int)UInt32());
                int sceneEnd = _end;
                if (end > sceneEnd || end - _position < 43) throw new InvalidDataException("CFV3 网格叶范围无效");
                _end = end;
                Skip(35);
                if (Byte() != 0xff || Byte() != 0xff) throw new NotSupportedException("CFV3 网格叶标志未适配");
                byte variant = Byte();
                if ((variant != 0 && variant != 2) || Byte() != 0 || Byte() != 1)
                    throw new NotSupportedException("CFV3 网格叶类型未适配：" + variant);
                int nv = Count(), nn = Count();
                if (nv < 3 || nv > (end - _position) * 4L || nn > (end - _position) * 2L)
                    throw new InvalidDataException("CFV3 顶点或法向数量无效");
                if (Byte() != 0 || Byte() != 0 || Byte() != 0) throw new NotSupportedException("CFV3 顶点前缀未适配");
                int raw = _packed ? Bits(1) : Byte();
                if (raw > 1) throw new NotSupportedException("CGR 顶点编码标志未适配");
                var points = Coordinates(nv, raw != 0);
                var vertexNormals = Normals(nn, !_packed && variant == 0);
                if (Float() < 0 || Byte() != 0) throw new InvalidDataException("CFV3 图元过渡无效");
                byte transition = Byte();
                if (transition < 1 || transition > 5 || Byte() != 2 || Byte() != 10)
                    throw new NotSupportedException("CGR 图元布局未适配");
                int domains = Count();
                Need(domains);
                int width = 0;
                if (_packed)
                    for (int n = nv; n > 0; n >>= 1) width++;
                else width = nv > 65535 ? 24 : nv > 255 ? 16 : 8;

                var groups = new List<List<int[]>>();
                var domainNormals = new List<double[]>();
                for (int i = 0; i < domains; i++)
                {
                    if (Byte() != 1) throw new NotSupportedException("CFV3 图元标志未适配");
                    byte flags = Byte();
                    // 0x40 is an empty face domain with an index count of zero and a style record.
                    if (flags < (_packed ? 0x40 : 0x41) || flags > 0x4f)
                        throw new NotSupportedException("CFV3 图元类型未适配：" + flags.ToString("X2"));
                    int singles = (flags & 1) == 0 ? 0 : Count();
                    int strips = (flags & 2) == 0 ? 0 : Count();
                    int fans = (flags & 4) == 0 ? 0 : Count();
                    int total = Count();
                    var normal = (flags & 8) == 0 ? null : Unit(Float(), Float(), Float());
                    if (total * (long)width > (_end - _position) * 8L + _bitsLeft ||
                        singles * 3L + 3L * (strips + (long)fans) > total)
                        throw new InvalidDataException("CFV3 图元计数无效");
                    var lengths = new int[checked(strips + fans)];
                    long consumed = singles * 3L;
                    for (int j = 0; j < lengths.Length; j++)
                    {
                        lengths[j] = _packed ? Bits(width) : Count();
                        if (lengths[j] < 3) throw new InvalidDataException("CFV3 三角条带过短");
                        consumed += lengths[j];
                    }
                    if (total != consumed) throw new InvalidDataException("CFV3 三角索引计数不一致");
                    var faces = new List<int[]>();
                    int Index()
                    {
                        int index;
                        if (_packed) index = Bits(width);
                        else
                        {
                            index = Byte();
                            for (int k = 8; k < width; k += 8) index = index << 8 | Byte();
                        }
                        if (index >= nv) throw new InvalidDataException("CFV3 三角索引超出顶点范围");
                        return index;
                    }
                    for (int j = 0; j < singles; j++) faces.Add(new[] { Index(), Index(), Index() });
                    for (int j = 0; j < lengths.Length; j++)
                    {
                        int a = Index(), b = Index();
                        for (int k = 2; k < lengths[j]; k++)
                        {
                            int c = Index();
                            faces.Add(j < strips && (k & 1) != 0 ? new[] { b, a, c } : new[] { a, b, c });
                            if (j < strips) a = b;
                            b = c;
                        }
                    }
                    groups.Add(faces);
                    domainNormals.Add(normal);
                }
                _faceCount += groups.Sum(faces => faces.Count);
                var vertices = new List<double[]>();
                var normals = new List<double[]>();
                var triangles = new List<int[]>();
                var colors = new List<byte[]>();
                var byCorner = new Dictionary<(int Point, int NormalDomain), int>();
                for (int i = 0; i < groups.Count; i++)
                {
                    int styleStart = _position;
                    byte style = Byte();
                    if ((style != 0x20 && style != 0x30 && style != 0xa0 && style != 0xb0) ||
                        Byte() != 4 || Byte() != 4 || Byte() != 255)
                        throw new NotSupportedException("CFV3 面颜色布局未适配：" + style.ToString("X2") +
                            "（offset=" + styleStart + "）");
                    byte opacity = Byte();
                    // 0xA0 repeats the same record layout with a non-opaque value;
                    // the JT mesh stores RGB only, so opacity is ignored like 0xB0.
                    if (style != 0xa0 && style != 0xb0 && opacity != 255)
                        throw new NotSupportedException("CFV3 面颜色的透明度布局未适配");
                    byte b = Byte(), g = Byte(), r = Byte();
                    var color = new[] { r, g, b };
                    var normal = domainNormals[i];
                    foreach (var face in groups[i])
                    {
                        if (face[0] == face[1] || face[1] == face[2] || face[2] == face[0]) continue;
                        var triangle = new int[3];
                        for (int corner = 0; corner < 3; corner++)
                        {
                            int point = face[corner];
                            var key = (point, normal == null ? -1 : i);
                            if (!byCorner.TryGetValue(key, out int index))
                            {
                                index = vertices.Count;
                                byCorner.Add(key, index);
                                vertices.Add(points[point]);
                                normals.Add(normal ?? (nn == nv ? vertexNormals[point] : null));
                            }
                            triangle[corner] = index;
                        }
                        triangles.Add(triangle);
                        colors.Add(color);
                    }
                }
                if (_packed) ReadLineDomains(nv, width);
                else if (_position < end)
                {
                    if (Byte() != 1) throw new NotSupportedException("CFV3 网格叶附加数据未适配");
                    _position = end;
                }
                _end = sceneEnd;
                Count(); // SurfacicReps pick offset.
                if (triangles.Count == 0) { _emptyLeafCount++; return null; }
                var leaf = new MeshShape { instanceId = "leaf", vertices = vertices.ToArray(),
                    triangles = triangles.ToArray(), rgb = colors.ToArray(), normals = normals.ToArray() };
                var filtered = leaf.RemoveDegenerateTriangles();
                if (filtered == null) _emptyLeafCount++;
                return filtered;
            }

            private void ReadLineDomains(int vertexCount, int indexWidth)
            {
                // Lines are not exported as faces, but their indices share the bit stream
                // with triangle indices and the coordinate mode of subsequent leaves.
                while (_position < _end)
                {
                    if (Byte() != 1) throw new NotSupportedException("CFV3 轮廓线组标志未适配");
                    Byte(); // Line domain attribute.
                    int lines = Count();
                    for (int i = 0; i < lines; i++)
                    {
                        byte kind = Byte();
                        if (kind == 3)
                        {
                            int points = Count();
                            if (Byte() != 1) throw new NotSupportedException("CFV3 轮廓线坐标布局未适配（offset=" + (_position - 1) + "）");
                            Skip(checked(points * 3 * sizeof(float)));
                            continue;
                        }
                        if (kind != 2) throw new NotSupportedException("CFV3 轮廓线类型未适配：" + kind +
                            "（offset=" + (_position - 1) + "）");
                        int count = Count();
                        if ((long)count * indexWidth > (_end - _position) * 8L + _bitsLeft)
                            throw new InvalidDataException("CFV3 轮廓线索引数据截断");
                        for (int j = 0; j < count; j++)
                        {
                            if (Bits(indexWidth) >= vertexCount)
                                throw new InvalidDataException("CFV3 轮廓线索引超出顶点范围");
                        }
                    }
                    int styleStart = _position;
                    byte styleTag = Byte(), style = Byte(), attributes = Byte();
                    // These line styles share the same five-byte payload.
                    bool supported = ((style == 0x24 || style == 0x94) && attributes == 4) ||
                        (style == 0x74 && attributes == 0x0b) || (style == 0x14 && attributes == 0x10);
                    if (styleTag != 0x10 || !supported)
                        throw new NotSupportedException("CFV3 轮廓线样式未适配（offset=" + styleStart + "）");
                    Skip(5); // Line style payload.
                }
            }

            private double[][] Coordinates(int count, bool raw)
            {
                int bitmap = _position, expected = -1;
                if (!raw)
                {
                    Skip((count + 3) / 4);
                    int values = Count();
                    Need(checked(values * 4));
                    expected = _position + values * 4;
                }
                else Need(checked(count * 12));
                var result = new double[count][];
                double x = 0, y = 0, z = 0;
                for (int i = 0; i < count; i++)
                {
                    int code = raw ? 0 : (_data[bitmap + i / 4] >> (2 * (i % 4))) & 3;
                    if (code == 0) x = Float();
                    if (code == 0 || code == 3) y = Float();
                    if (code != 1) z = Float();
                    result[i] = new[] { x, y, z };
                }
                if (expected >= 0 && _position != expected) throw new InvalidDataException("CFV3 坐标位图计数不一致");
                return result;
            }

            private double[][] Normals(int count, bool zSignOnly)
            {
                if (count == 0) return Array.Empty<double[]>();
                int words = Count(), start = _position;
                Skip(checked(words * 2));
                int map = _position;
                Skip((count + 1) / 2);
                var result = new double[count][];
                int used = 0;
                for (int i = 0; i < count; i++)
                {
                    int code = (_data[map + i / 2] >> (4 * (i % 2))) & 15;
                    if (code > 11 || (zSignOnly && code != 4 && code != 5))
                        throw new NotSupportedException("CGR 法向主轴码未适配");
                    var normal = new double[3];
                    int axis = (code >= 6 ? code - 6 : code) / 2;
                    double sign = (code & 1) == 0 ? 1 : -1;
                    if (code >= 6) normal[axis] = sign;
                    else
                    {
                        if (used > words - 2) throw new InvalidDataException("CFV3 法向分量截断");
                        double a = BitConverter.ToInt16(_data, start + used * 2) / 32767.0;
                        double b = BitConverter.ToInt16(_data, start + used * 2 + 2) / 32767.0;
                        used += 2;
                        normal[axis == 0 ? 1 : 0] = a;
                        normal[axis == 2 ? 1 : 2] = b;
                        normal[axis] = sign * Math.Sqrt(Math.Max(0, 1 - a * a - b * b));
                        normal = Unit(normal[0], normal[1], normal[2]);
                    }
                    result[i] = normal;
                }
                if (used != words) throw new InvalidDataException("CFV3 法向分量计数不一致");
                return result;
            }
            private static double[] Unit(double x, double y, double z)
            {
                double length = Math.Sqrt(x * x + y * y + z * z);
                return length == 0 ? null : new[] { x / length, y / length, z / length };
            }
        }

        internal static MeshShape Read(byte[] data, string name, string source, string digest)
        {
            if (data.Length < 80 || Encoding.ASCII.GetString(data, 0, 8) != "V5_CFV3\0")
                throw new InvalidDataException("输入不是 V5_CFV3 CGR");
            return new Reader(Skeleton(data), true).Read(name, source, digest);
        }

        internal static MeshShape ReadCompactCgr(byte[] graph, string name, string source, string digest)
        {
            return new Reader(graph, false).Read(name, source, digest);
        }
    }
}
