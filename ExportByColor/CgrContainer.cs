using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Globalization;

namespace TxTools.ExportByColor
{
    // Observed CFV2 layout, not a general CATIA compound-file implementation.
    // Directory structures and storage indexes are serialized from fields.
    // Identity, timestamps, flags and non-container payloads retain the accepted
    // encoder profile until their CATIA semantics have been tested separately.
    internal static class CgrContainer
    {
        internal const string Version = "CFV2-20261016-R4-counted-identity";
        private sealed class Node
        {
            internal int Id, Parent, Kind, Fragments;
            internal string Name;
            internal ulong Created, Modified;
            internal byte[] Data;
            internal readonly List<Extent> Extents = new List<Extent>();
        }

        private struct Extent { internal int Physical, Length, Logical; }
        // These decode as FILETIME-shaped values. Their lifecycle/identity
        // role is not proven; retain the accepted values rather than minting new ones.
        private const ulong Created = 0x01dd3ad504bb9430UL;
        // Four byte-swapped uint32 words rendered as unpadded lower-case hex. Controlled
        // native COM exports identify word1 as the producing CATIA process ID;
        // word2 is Unix seconds, also present in Header +5. Word0 remains unknown;
        // word3 increases during native allocation but its full rules are unknown.
        private static readonly CgrStorageIdentity AcceptedIdentity =
            new CgrStorageIdentity(0x581eb54e, 0x00000d20, 0x6a9813d7, 0x000023ea);

        internal static void Write(Stream output, byte[] scene, byte[] picks, double[] bounds,
                                   bool compact95, int sceneFragments = 26, int pickFragments = 25,
                                   CgrStorageIdentity? identity = null, CgrVisualizationAccuracy? accuracy = null)
        {
            if (output == null || !output.CanSeek || !output.CanWrite || output.Position != 0 || output.Length != 0)
                throw new ArgumentException("Container requires an empty, seekable output stream");
            if (scene == null || picks == null || sceneFragments < 1 || pickFragments < 1 ||
                scene.Length < sceneFragments || picks.Length < pickFragments)
                throw new ArgumentException("Stream shorter than fragment count");
            if (bounds == null || bounds.Length != 6) throw new ArgumentException("Expected XYZ minimum/maximum bounds");
            for (int i = 0; i < 6; i++)
                if (double.IsNaN(bounds[i]) || double.IsInfinity(bounds[i]) || float.IsInfinity((float)bounds[i]))
                    throw new ArgumentException("Non-finite container bounds");
            for (int i = 0; i < 3; i++)
                if (bounds[i] > bounds[i + 3]) throw new ArgumentException("Inverted container bounds");

            CgrStorageIdentity storageIdentity = identity ?? AcceptedIdentity;
            CgrVisualizationAccuracy visualization = accuracy ?? CgrVisualizationAccuracy.Accepted;
            visualization.Validate();
            var nodes = new List<Node> { new Node { Id = 1, Kind = 0, Name = "", Data = new byte[0] } };
            Add(nodes, 0, 1, storageIdentity.ToString(), 0, null);
            Add(nodes, 2, 2, "MAIN", 0x01dd3ad504acee30UL, Control(1));
            Add(nodes, 2, 1, "Describe", 0x01dd3ad504ad1540UL, null);
            Add(nodes, 4, 2, "Describe", 0x01dd3ad504ad1540UL, Control());
            Add(nodes, 4, 2, "MainDataStream", 0x01dd3ad504ad1540UL, Describe(compact95));
            Add(nodes, 2, 1, "Header", 0x01dd3ad504adb180UL, null);
            Add(nodes, 7, 2, "Header", 0x01dd3ad504adb180UL, Control());
            Add(nodes, 7, 2, "MainDataStream", 0x01dd3ad504adb180UL, BoundsPayload(bounds, storageIdentity.UnixSeconds, visualization));
            Add(nodes, 2, 1, "SceneGraph", 0x01dd3ad504adb180UL, null);
            Add(nodes, 10, 2, "SceneGraph", 0x01dd3ad504adb180UL, Control());
            Add(nodes, 10, 2, "MainDataStream", 0x01dd3ad504adb180UL, scene, sceneFragments);
            Add(nodes, 10, 2, "SurfacicReps", 0x01dd3ad504add890UL, picks, pickFragments);
            long timeDelta = ((long)storageIdentity.UnixSeconds - AcceptedIdentity.UnixSeconds) * 10000000L;
            foreach (var node in nodes)
            {
                if (node.Kind == 1) node.Data = StorageIndex(nodes, node);
                if (node.Kind != 0)
                {
                    node.Created = ShiftTime(node.Created, timeDelta);
                    node.Modified = ShiftTime(node.Modified, timeDelta);
                }
            }

            output.Write(new byte[64], 0, 64);
            int streamBytes = 0;
            foreach (var node in nodes)
            {
                if (node.Kind == 0) continue;
                if (node.Kind == 2) streamBytes = checked(streamBytes + node.Data.Length);
                int logical = 0;
                for (int i = 0; i < node.Fragments; i++)
                {
                    int length = node.Data.Length / node.Fragments + (i < node.Data.Length % node.Fragments ? 1 : 0);
                    node.Extents.Add(new Extent { Physical = checked((int)output.Position), Length = length, Logical = logical });
                    output.Write(node.Data, logical, length);
                    logical += length;
                }
            }
            int directoryStart = checked((int)output.Position);
            byte[] directory = Directory(nodes, streamBytes, directoryStart);
            output.Write(directory, 0, directory.Length);
            byte[] header = Header(directoryStart, directory.Length);
            output.Position = 0;
            output.Write(header, 0, header.Length);
            output.Position = output.Length;
        }

        private static void Add(List<Node> nodes, int parent, int kind, string name, ulong modified, byte[] data, int fragments = 1)
        {
            nodes.Add(new Node { Id = nodes.Count + 1, Parent = parent, Kind = kind, Name = name,
                                 Created = Created, Modified = modified, Data = data, Fragments = fragments });
        }

        private static ulong ShiftTime(ulong value, long delta)
        {
            return value == 0 ? 0 : checked((ulong)(checked((long)value + delta)));
        }

        // Shared leading uint32 across the observed control/Describe records.
        // Its interpretation and the 7-byte Describe tail are still unknown.
        private const uint StreamFormatTag = 0x0010d056;
        private static byte[] Control(uint state = 0)
        {
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)1); w.Write(StreamFormatTag); w.Write(state);
                return ms.ToArray();
            }
        }

        private static byte[] Describe(bool compact95)
        {
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                w.Write(StreamFormatTag); w.Write(compact95 ? 22U : 7U); w.Write(7U);
                // Native LineicCgrMode adds bit 0x20, but also changes geometry
                // streams. Do not expose a header-only toggle for that feature.
                w.Write(new byte[] { 0xb1, 1, 11, 1, 8, 2, 0x4f });
                return ms.ToArray();
            }
        }

        private static byte[] BoundsPayload(double[] bounds, uint unixSeconds, CgrVisualizationAccuracy accuracy)
        {
            // COM-controlled exports identify +10/+11/+15/+19/+20. Remaining
            // flags and appearance slots preserve the accepted profile values.
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)255); w.Write(62U); w.Write(unixSeconds);
                w.Write((byte)0); w.Write((byte)(accuracy.Fixed3D ? 1 : 0));
                w.Write(accuracy.Accuracy3D); w.Write(accuracy.CurveAccuracy3D);
                w.Write((byte)(accuracy.Fixed2D ? 1 : 0)); w.Write(accuracy.Accuracy2D);
                w.Write((byte)0); w.Write((byte)1); w.Write((byte)1);
                foreach (double value in bounds) w.Write((float)value);
                w.Write(new byte[] { 0, 0, 0, 255 }); w.Write(0f); w.Write(1f);
                return ms.ToArray();
            }
        }

        private static byte[] StorageIndex(List<Node> nodes, Node owner)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                U32(w, 0); U32(w, owner.Id);
                int count = 0;
                foreach (var child in nodes) if (child.Parent == owner.Id) count++;
                U32(w, count);
                // CATIA's accepted profile indexes children in descending ID order.
                for (int i = nodes.Count - 1; i >= 0; i--)
                {
                    var child = nodes[i];
                    if (child.Parent != owner.Id) continue;
                    byte[] name = Encoding.BigEndianUnicode.GetBytes(child.Name + "\0");
                    U32(w, checked(14 + name.Length)); U32(w, child.Id); U16(w, child.Kind);
                    U32(w, name.Length); w.Write(name);
                }
                int length = checked((int)ms.Length); ms.Position = 0; U32(w, length);
                return ms.ToArray();
            }
        }

        private static byte[] Directory(List<Node> nodes, int streamBytes, int directoryStart)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("CATIA_V5 CB0001\0"));
                w.Write((byte)0x4e); U16(w, 1000); U16(w, 64);
                // +21 stays 12 even in native point files with only 12 nodes;
                // its meaning is unknown. +23/+25 count non-root/all nodes.
                U16(w, 12); U16(w, nodes.Count - 1); U16(w, nodes.Count);
                U32(w, streamBytes); U32(w, 0); w.Write(new byte[29]);
                foreach (var node in nodes)
                {
                    if (node.Kind != 0)
                    {
                        if (node.Parent == 0)
                        {
                            // Root-child descriptor: 12-byte zero prefix, class,
                            // data length, metadata, counted UTF-16BE name. The low
                            // half of its name length was previously misread as 'D'.
                            w.Write(new byte[12]); U32(w, node.Kind); U32(w, node.Data.Length);
                            Metadata(w, node);
                            byte[] rootName = Encoding.BigEndianUnicode.GetBytes(node.Name + "\0");
                            U32(w, rootName.Length); w.Write(rootName);
                        }
                        else
                        {
                            U32(w, node.Parent); U32(w, node.Kind); U32(w, node.Data.Length);
                            Metadata(w, node);
                            byte[] name = Encoding.BigEndianUnicode.GetBytes(node.Name + "\0");
                            U32(w, name.Length); w.Write(name);
                        }
                    }
                    U32(w, checked(86 + 20 * node.Extents.Count)); U32(w, node.Id); U16(w, node.Kind);
                    U32(w, node.Kind); U32(w, node.Data.Length); Metadata(w, node);
                    U32(w, node.Extents.Count);
                    foreach (var extent in node.Extents)
                    {
                        U32(w, extent.Physical); U32(w, extent.Length); U32(w, extent.Length);
                        U32(w, extent.Logical); U32(w, 0);
                    }
                }
                int length = checked((int)ms.Length + 12);
                U32(w, length); w.Write(Encoding.ASCII.GetBytes("CB__END\0"));
                ms.Position = 31; U32(w, checked(directoryStart + length));
                return ms.ToArray();
            }
        }

        private static void Metadata(BinaryWriter w, Node node)
        {
            U32(w, 0); FileTime(w, node.Created); FileTime(w, node.Modified); FileTime(w, node.Modified);
            U32(w, node.Kind == 0 ? 0 : node.Parent == 0 ? 0x80000011U : 0x11U);
            w.Write(new byte[32]);
        }

        private static void FileTime(BinaryWriter w, ulong value)
        {
            U32(w, (uint)value); U32(w, (uint)(value >> 32));
        }

        private static byte[] Header(int directoryStart, int directoryLength)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("V5_CFV2\0"));
                U32(w, directoryStart); U32(w, directoryLength);
                U32(w, uint.MaxValue); U32(w, uint.MaxValue);
                w.Write(new byte[35]); w.Write(new byte[] { 2, 0x5f, 13, 10, 0 });
                return ms.ToArray();
            }
        }

        private static void U16(BinaryWriter w, int value)
        {
            ushort v = checked((ushort)value); w.Write((byte)(v >> 8)); w.Write((byte)v);
        }
        private static void U32(BinaryWriter w, int value) { U32(w, checked((uint)value)); }
        private static void U32(BinaryWriter w, uint value)
        {
            w.Write((byte)(value >> 24)); w.Write((byte)(value >> 16)); w.Write((byte)(value >> 8)); w.Write((byte)value);
        }
    }

    // Header metadata only. Setting these values does not tessellate geometry;
    // callers must supply scene data consistent with their chosen accuracy.
    internal struct CgrVisualizationAccuracy
    {
        internal readonly bool Fixed3D, Fixed2D;
        internal readonly float Accuracy3D, CurveAccuracy3D, Accuracy2D;
        internal static readonly CgrVisualizationAccuracy Accepted =
            new CgrVisualizationAccuracy(true, .2f, 1f, true, .02f);

        internal CgrVisualizationAccuracy(bool fixed3D, float accuracy3D, float curveAccuracy3D,
                                          bool fixed2D, float accuracy2D)
        {
            Fixed3D = fixed3D; Accuracy3D = accuracy3D; CurveAccuracy3D = curveAccuracy3D;
            Fixed2D = fixed2D; Accuracy2D = accuracy2D;
            Validate();
        }

        internal void Validate()
        {
            foreach (float value in new[] { Accuracy3D, CurveAccuracy3D, Accuracy2D })
                if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0)
                    throw new ArgumentException("Visualization accuracy must be finite and positive");
        }
    }

    internal struct CgrStorageIdentity
    {
        internal readonly uint Word0, ProcessId, UnixSeconds, Word3;
        internal CgrStorageIdentity(uint word0, uint processId, uint unixSeconds, uint word3)
        { Word0 = word0; ProcessId = processId; UnixSeconds = unixSeconds; Word3 = word3; }

        public override string ToString()
        {
            var text = new StringBuilder(".", 33);
            foreach (uint word in new[] { Word0, ProcessId, UnixSeconds, Word3 })
                text.Append(Swap(word).ToString("x", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static uint Swap(uint value)
        {
            return (value >> 24) | ((value >> 8) & 0xff00U) | ((value << 8) & 0xff0000U) | (value << 24);
        }

        internal static CgrStorageIdentity Parse(string text, uint? unixSeconds = null)
        {
            if (text != null && text.Length == 34 && text.StartsWith("D.", StringComparison.Ordinal))
            {
                // Compatibility with the old probe's diagnostic spelling.
                var words = new uint[4];
                for (int word = 0; word < 4; word++)
                    words[word] = Swap(uint.Parse(text.Substring(2 + 8 * word, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                return new CgrStorageIdentity(words[0], words[1], words[2], words[3]);
            }
            if (text == null || text.Length < 5 || text.Length > 33 || text[0] != '.')
                throw new FormatException("Expected dot followed by four unpadded hex words");
            string raw = text.Substring(1).ToLowerInvariant();
            foreach (char ch in raw)
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
                    throw new FormatException("Invalid identity hex digit");
            var candidates = new List<CgrStorageIdentity>();
            for (int a = 1; a <= 8; a++) for (int b = 1; b <= 8; b++) for (int c = 1; c <= 8; c++)
            {
                int d = raw.Length - a - b - c;
                if (d < 1 || d > 8) continue;
                string[] chunks = { raw.Substring(0, a), raw.Substring(a, b), raw.Substring(a + b, c), raw.Substring(a + b + c) };
                bool canonical = true;
                foreach (string chunk in chunks) if (chunk.Length > 1 && chunk[0] == '0') canonical = false;
                if (!canonical) continue;
                uint[] words = Array.ConvertAll(chunks, value => Swap(uint.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)));
                candidates.Add(new CgrStorageIdentity(words[0], words[1], words[2], words[3]));
            }
            if (candidates.Count != 1 && unixSeconds.HasValue)
                candidates.RemoveAll(value => value.UnixSeconds != unixSeconds.Value);
            if (candidates.Count != 1)
                throw new FormatException("Identity partition requires unambiguous timestamp context; candidates=" + candidates.Count);
            return candidates[0];
        }
    }
}
