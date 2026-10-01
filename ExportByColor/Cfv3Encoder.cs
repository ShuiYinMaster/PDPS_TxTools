using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TxTools.ExportByColor
{
    public sealed class Cfv3EncodingStats
    {
        public int Leaves { get; internal set; }
        public int SkeletonBytes { get; internal set; }
        public int SurfacicRepBytes { get; internal set; }
        public int OutputBytes { get; internal set; }
        // True when an R38 feature-CGR was converted to the compact geometry
        // layout required by the legacy CFV3 shell.
        public bool FeatureGeometryBridge { get; internal set; }
    }

    /// <summary>
    /// Converts compact-95 CGR, and the geometry bridge of R38 feature CGR,
    /// into the V5_CFV3 representation stream used by 3DXML archives.
    ///
    /// The shell and bitstream rules are based on paired CATIA exports. Keep
    /// the single-leaf and multi-leaf allocation layouts separate: CATIA V5
    /// accepts both, but a flattened multi-leaf stream is not displayed.
    /// </summary>
    public static class Cfv3Encoder
    {
        private const int LeafHeaderSize = 41;
        private const int V2PathSize = 1 + 4 * (41 + 1 + 1);
        private const int PickSize = 61;

        private static readonly byte[] LeafSignature = { 0xff, 0xff, 0x00, 0x00, 0x01 };
        private static readonly byte[] PickPrefix = Hex("ff3d000000db0f493fa5d46853");
        private static readonly byte[] SceneStreamPrefix = Hex(
            "6200201404fdff00000000010000803f00000000000000000000000000000000" +
            "00000000ff03000000ff010000000100");

        private static readonly byte[] BuiltinPrefix = Convert.FromBase64String(
            "VjVfQ0ZWMwAAACFsAAAC6v//////////AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAACXw0KAENBVElBX1Y1IENCMDAwMQBOA+gAQAAMAAoACwAAHqYA" +
            "ACYWAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAiAAAAAQAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAMYAAAAA36YucAHd" +
            "QDQAAABEAC4ANABlAGIANQAxAGUANQA4AGQAOAA4ADAAMAAwADAAMAAyADQAMQA4" +
            "AGEAMQA2AGEAOAAyADQANQAwADAAMAAwAAAAAAA2AAAAAgABAAAAAQAAAMYAAAAA" +
            "36YucAHdQDQAAAABAAAgpgAAAMYAAADGAAAAAAAAAAAAAAACAAAAAgAAAAkAAAAA" +
            "36YucAHdQDQAAAAKAE0AQQBJAE4AAAAAADYAAAADAAIAAAACAAAACQAAAADfpi5w" +
            "Ad1ANAAAAAEAAAIXAAAACQAAAAkAAAAAAAAAAAAAAAIAAAACAAAAAgAAAADfpi5w" +
            "Ad1ANAAAABAATwBwAHQAaQBvAG4AcwAAAAAANgAAAAQAAgAAAAIAAAACAAAAAN+m" +
            "LnAB3UA0AAAAAQAAAiAAAAACAAAAAgAAAAAAAAAAAAABGtAQAAAAAAABGtAQAAAAA" +
            "AD/AAAAAAEa0BAAAAAAAAYC");

        private static readonly byte[] BuiltinSuffix = Convert.FromBase64String(
            "AAAAxgAAAAIAAAAFAAAAPAAAAAcAAQAAAC4AQQBwAHAAbABpAGMAYQB0AGkAdgBlACAAQwBvAG4AdABhAGkAbgBlAHIAcwAAAAAAKAAAAAYAAgAAABoAUwB1AHIAZgBhAGMAaQBjAFIAZQBwAHMAAAAAACAAAAAFAAIAAAASAFMAawBlAGwAZQB0AG8AbgAAAAAAHgAAAAQAAgAAABAATwBwAHQAaQBvAG4AcwAAAAAAGAAAAAMAAgAAAAoATQBBAEkATgAAAAIAAAACAAAeRwAAAADfpi5wAd1ANAAAABIAUwBrAGUAbABlAHQAbwBuAAAAAAA2AAAABQACAAAAAgAAHkcAAAAA36YucAHdQDQAAAABAAACIgAAHkcAAB5HAAAAAAAAAAAAAAACAAAAAgAAAD0AAAAA36YucAHdQDQAAAAaAFMAdQByAGYAYQBjAGkAYwBSAGUAcABzAAAAAAA2AAAABgACAAAAAgAAAD0AAAAA36YucAHdQDQAAAABAAAgaQAAAD0AAAA9AAAAAAAAAAAAAAACAAAAAQAAAAAAAAAA36YucAHdQDQAAAAuAEEAcABwAGwAaQBjAGEAdABpAHYAZQAgAEMAbwBuAHQAYQBpAG4AZQByAHMAAAAAACIAAAAHAAEAAAABAAAAAAAAAADfpi5wAd1ANAAAAAAAAAAHAAAAAgAAAAkAAAAA36YucAHdQDQAAAAuAEEAcABwAGwAaQBjAGEAdABpAHYAZQAgAEMAbwBuAHQAYQBpAG4AZQByAHMAAAAAADYAAAAIAAIAAAACAAAACQAAAADfpi5wAd1ANAAAAAEAAAIJAAAACQAAAAkAAAAAAAAAAAAAAAcAAAACAAAABQAAAADfpi5wAd1ANAAAAB4ATQBhAGkAbgBEAGEAdABhAFMAdAByAGUAYQBtAAAAAAA2AAAACQACAAAAAgAAAAUAAAAA36YucAHdQDQAAAABAAACEgAAAAUAAAAFAAAAAAAAAAAAAAAHAAAAAQAAAAAAAAAA36YucAHdQDQAAAASAFYANABWADUAXwBGAEQAVAAAAAAAIgAAAAoAAQAAAAEAAAAAAAAAAN+mLnAB3UA0AAAAAAAAAAoAAAACAAAACQAAAADfpi5wAd1ANAAAABIAVgA0AFYANQBfAEYARABUAAAAAAA2AAAACwACAAAAAgAAAAkAAAAA36YucAHdQDQAAAABAAACAAAAAAkAAAAJAAAAAAAAAAAAAASqQ0JfX0VORAA=");

        private static readonly byte[] MultiPrefix = Convert.FromBase64String(
            "VjVfQ0ZWMwAAB7VYAAAC/v//////////AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAACXw0KAENBVElBX1Y1IENCMDAwMQBOA+gAQAAMAAoACwAHspIA" +
            "B7oWAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAiAAAAAQAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAMYAAAAA36Sn0AHd" +
            "QDQAAABEAC4ANABlAGIANQAxAGUANQA4AGQAOAA4ADAAMAAwADAAMAAyADQAMQA4" +
            "AGEAMQA2AGEAOAAxADQANQAwADAAMAAwAAAAAAA2AAAAAgABAAAAAQAAAMYAAAAA" +
            "36Sn0AHdQDQAAAABAAe0kgAAAMYAAADGAAAAAAAAAAAAAAACAAAAAgAAAAkAAAAA" +
            "36Sn0AHdQDQAAAAKAE0AQQBJAE4AAAAAADYAAAADAAIAAAACAAAACQAAAADfpKfQ" +
            "Ad1ANAAAAAEAB7IgAAAACQAAAAkAAAAAAAAAAAAAAAIAAAACAAAAAgAAAADfpKfQ" +
            "Ad1ANAAAABAATwBwAHQAaQBvAG4AcwAAAAAANgAAAAQAAgAAAAIAAAACAAAAAN+k" +
            "p9AB3UA0AAAAAQAHsikAAAACAAAAAgAAAAAAAAAAAAA=");

        private static readonly byte[] MultiMiddle = Convert.FromBase64String(
            "ARrQEAAAAAAAARrQEAAAAAAA/wAAAAABGtAQAAAAAAAGAg==");

        private static readonly byte[] MultiSuffix = Convert.FromBase64String(
            "AAAAxgAAAAIAAAAFAAAAPAAAAAcAAQAAAC4AQQBwAHAAbABpAGMAYQB0AGkAdgBl" +
            "ACAAQwBvAG4AdABhAGkAbgBlAHIAcwAAAAAAKAAAAAYAAgAAABoAUwB1AHIAZgBh" +
            "AGMAaQBjAFIAZQBwAHMAAAAAACAAAAAFAAIAAAASAFMAawBlAGwAZQB0AG8AbgAA" +
            "AAAAHgAAAAQAAgAAABAATwBwAHQAaQBvAG4AcwAAAAAAGAAAAAMAAgAAAAoATQBB" +
            "AEkATgAAAAIAAAACAAewDgAAAADfpKfQAd1ANAAAABIAUwBrAGUAbABlAHQAbwBu" +
            "AAAAAABKAAAABQACAAAAAgAHsA4AAAAA36Sn0AHdQDQAAAACAAACAAAHsAkAB7AJ" +
            "AAAAAAAAAAAAB7IrAAAABQAAAAUAB7AJAAAAAAAAAAIAAAACAAACYgAAAADfpKfQ" +
            "Ad1ANAAAABoAUwB1AHIAZgBhAGMAaQBjAFIAZQBwAHMAAAAAADYAAAAGAAIAAAAC" +
            "AAACYgAAAADfpKfQAd1ANAAAAAEAB7IwAAACYgAAAmIAAAAAAAAAAAAAAAIAAAAB" +
            "AAAAAAAAAADfpKfQAd1ANAAAAC4AQQBwAHAAbABpAGMAYQB0AGkAdgBlACAAQwBv" +
            "AG4AdABhAGkAbgBlAHIAcwAAAAAAIgAAAAcAAQAAAAEAAAAAAAAAAN+kp9AB3UA0" +
            "AAAAAAAAAAcAAAACAAAACQAAAADfpKfQAd1ANAAAAC4AQQBwAHAAbABpAGMAYQB0" +
            "AGkAdgBlACAAQwBvAG4AdABhAGkAbgBlAHIAcwAAAAAANgAAAAgAAgAAAAIAAAAJ" +
            "AAAAAN+kp9AB3UA0AAAAAQAHshIAAAAJAAAACQAAAAAAAAAAAAAABwAAAAIAAAAF" +
            "AAAAAN+kp9AB3UA0AAAAHgBNAGEAaQBuAEQAYQB0AGEAUwB0AHIAZQBhAG0AAAAA" +
            "ADYAAAAJAAIAAAACAAAABQAAAADfpKfQAd1ANAAAAAEAB7IbAAAABQAAAAUAAAAA" +
            "AAAAAAAAAAcAAAABAAAAAAAAAADfpKfQAd1ANAAAABIAVgA0AFYANQBfAEYARABU" +
            "AAAAAAAiAAAACgABAAAAAQAAAAAAAAAA36Sn0AHdQDQAAAAAAAAACgAAAAIAAAAJ" +
            "AAAAAN+kp9AB3UA0AAAAEgBWADQAVgA1AF8ARgBEAFQAAAAAADYAAAALAAIAAAAC" +
            "AAAACQAAAADfpKfQAd1ANAAAAAEAB7IJAAAACQAAAAkAAAAAAAAAAAAABL5DQl9f" +
            "RU5EAA==");

        private sealed class LeafRecord
        {
            public int Start;
            public int DataEnd;
            public int PayloadStart;
        }

        private sealed class ParsedLeaf
        {
            public LeafRecord Record;
            public int VertexCount;
            public int BitmapStart;
            public byte[] CoordinateBlock;
            public byte[] RawCoordinates;
            public short[] NormalShorts;
            public byte[] NormalCodes;
            public float Tolerance;
            public int PrimitiveCount;
            public int FaceCount;
            public int IndexCount;
            public int[] Indices;
            public byte[] ColorRecord;
        }

        private struct CompactValue
        {
            public int Value;
            public int End;
        }

        private sealed class GeometryStreams
        {
            public byte[] Skeleton;
            public byte[] Picks;
            public int Leaves;
        }

        private sealed class CatBitWriter : IDisposable
        {
            private readonly MemoryStream _stream = new MemoryStream();
            private readonly BinaryWriter _writer;
            private long _wordPosition = -1;
            private uint _word;
            private int _remaining;

            public CatBitWriter() { _writer = new BinaryWriter(_stream); }
            public long Length { get { return _stream.Length; } }

            public void WriteRaw(byte[] value) { WriteRaw(value, 0, value.Length); }

            public void WriteRaw(byte[] value, int offset, int count)
            {
                _stream.Position = _stream.Length;
                _writer.Write(value, offset, count);
            }

            public void WriteBit(int value) { WriteBits((uint)value, 1); }

            public void WriteBits(uint value, int count)
            {
                while (count > 0)
                {
                    if (_remaining == 0)
                    {
                        _stream.Position = _stream.Length;
                        _wordPosition = _stream.Position;
                        _writer.Write(0u);
                        _word = 0;
                        _remaining = 32;
                    }
                    int take = Math.Min(count, _remaining);
                    int bitPosition = 32 - _remaining;
                    uint mask = take == 32 ? uint.MaxValue : ((1u << take) - 1u);
                    _word |= (value & mask) << bitPosition;
                    _remaining -= take;
                    value >>= take;
                    count -= take;
                    PatchUInt32(_wordPosition, _word);
                }
            }

            public void WriteFixedWidthArray(int[] values, int width)
            {
                for (int i = 0; i < values.Length; i++)
                    WriteBits(checked((uint)values[i]), width);
            }

            public void PatchUInt32(long position, uint value)
            {
                long saved = _stream.Position;
                _stream.Position = position;
                _writer.Write(value);
                _stream.Position = saved;
            }

            public byte[] ToArray() { return _stream.ToArray(); }

            public void Dispose()
            {
                _writer.Dispose();
                _stream.Dispose();
            }
        }

        public static Cfv3EncodingStats ConvertFile(string cgrPath, string outputPath, string identity)
        {
            if (string.IsNullOrWhiteSpace(cgrPath)) throw new ArgumentNullException("cgrPath");
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentNullException("outputPath");
            byte[] source = File.ReadAllBytes(cgrPath);
            string bridgePath = null;
            Cfv3EncodingStats stats;
            try
            {
                bool featureBridge = CgrFeatureCfv3Adapter.HasFeatureLeaf(source);
                if (featureBridge)
                {
                    bridgePath = cgrPath + "." + Guid.NewGuid().ToString("N") + ".cfv3-compact.cgr";
                    CgrFeatureCfv3Adapter.WriteCompactBridge(source, bridgePath);
                    source = File.ReadAllBytes(bridgePath);
                }
                byte[] output = Encode(source, identity, out stats);
                stats.FeatureGeometryBridge = featureBridge;
                string temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    File.WriteAllBytes(temporary, output);
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Move(temporary, outputPath);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            finally
            {
                if (bridgePath != null && File.Exists(bridgePath)) File.Delete(bridgePath);
            }
            return stats;
        }

        public static byte[] Encode(byte[] source, string identity, out Cfv3EncodingStats stats)
        {
            if (source == null || source.Length == 0) throw new ArgumentException("CGR 数据为空", "source");
            GeometryStreams streams = EncodeGeometryStreams(source);
            byte[] result = WrapCfv3(streams.Skeleton, streams.Picks, identity);
            stats = new Cfv3EncodingStats
            {
                Leaves = streams.Leaves,
                SkeletonBytes = streams.Skeleton.Length,
                SurfacicRepBytes = streams.Picks.Length,
                OutputBytes = result.Length
            };
            return result;
        }

        internal static void ShellIdentityValues(string identity, out ushort discriminator, out uint token)
        {
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(identity));
            discriminator = (ushort)((digest[4] << 8) | digest[5]);
            token = (((uint)digest[16] << 24) | ((uint)digest[17] << 16) |
                     ((uint)digest[18] << 8) | digest[19]) & 0xfffffff0u;
        }

        private static GeometryStreams EncodeGeometryStreams(byte[] source)
        {
            List<LeafRecord> records = FindLeaves(source);
            if (records.Count == 0) throw new InvalidDataException("CGR 中没有 compact-95 叶块");
            int rootCountSize = records.Count <= 0xfe ? 1 : 5;
            int rootStart = records[0].Start - V2PathSize - 41 - rootCountSize;
            RequireRange(source, rootStart, 41, "V2 根节点头");
            if (source[rootStart] != 0x62 || source[rootStart + 1] != 0x00)
                throw new InvalidDataException("V2 根节点头不符合预期");
            CompactValue rootCount = ReadCompact(source, rootStart + 41);
            if (rootCount.Value != records.Count)
                throw new InvalidDataException("V2 根节点子项数与叶块数不一致");

            byte[] skeleton;
            using (var writer = new CatBitWriter())
            {
                writer.WriteRaw(SceneStreamPrefix);
                writer.WriteRaw(source, rootStart, 41);
                writer.WriteRaw(EncodeCompact(records.Count, true));
                for (int ordinal = 0; ordinal < records.Count; ordinal++)
                {
                    LeafRecord record = records[ordinal];
                    writer.WriteRaw(new byte[] { 0 });
                    foreach (byte[] header in V2BranchHeaders(source, record.Start))
                    {
                        writer.WriteRaw(header);
                        writer.WriteRaw(EncodeCompact(1, true));
                        writer.WriteRaw(new byte[] { 0 });
                    }
                    WriteLeaf(writer, source, record);
                    writer.WriteRaw(EncodeCompact(checked(ordinal * PickSize), false));
                }
                skeleton = writer.ToArray();
            }

            List<int> pickPositions = FindAll(source, PickPrefix);
            if (pickPositions.Count != records.Count)
                throw new InvalidDataException("CGR pick 记录数与叶块数不一致: " + pickPositions.Count + "/" + records.Count);
            byte[] picks = new byte[checked(records.Count * PickSize)];
            for (int i = 0; i < pickPositions.Count; i++)
            {
                RequireRange(source, pickPositions[i], PickSize, "pick 记录");
                Buffer.BlockCopy(source, pickPositions[i], picks, i * PickSize, PickSize);
            }
            return new GeometryStreams { Skeleton = skeleton, Picks = picks, Leaves = records.Count };
        }

        private static List<byte[]> V2BranchHeaders(byte[] source, int leafStart)
        {
            int position = leafStart - V2PathSize;
            RequireRange(source, position, V2PathSize, "V2 叶路径");
            if (source[position++] != 0) throw new InvalidDataException("V2 叶路径分隔符异常");
            var headers = new List<byte[]>(4);
            for (int i = 0; i < 4; i++)
            {
                if (source[position] != 0x62 || source[position + 1] != 0x00)
                    throw new InvalidDataException("V2 分支头不符合预期");
                byte[] header = new byte[41];
                Buffer.BlockCopy(source, position, header, 0, 41);
                headers.Add(header);
                position += 41;
                CompactValue count = ReadCompact(source, position);
                if (count.Value != 1) throw new InvalidDataException("V2 分支必须只有一个子项");
                position = count.End;
                if (source[position++] != 0) throw new InvalidDataException("V2 分支分隔符异常");
            }
            if (position != leafStart) throw new InvalidDataException("V2 叶路径长度异常");
            return headers;
        }

        private static void WriteLeaf(CatBitWriter writer, byte[] source, LeafRecord record)
        {
            ParsedLeaf leaf = ParseLeaf(source, record);
            long headerStart = writer.Length;
            byte[] header = new byte[LeafHeaderSize];
            Buffer.BlockCopy(source, record.Start, header, 0, LeafHeaderSize);
            Array.Clear(header, 2, 4);
            writer.WriteRaw(header);

            int rawPrefixLength = leaf.BitmapStart - record.PayloadStart - 1;
            if (rawPrefixLength < 0) throw new InvalidDataException("V2 坐标前缀异常");
            writer.WriteRaw(source, record.PayloadStart, rawPrefixLength);

            bool rawMode = leaf.RawCoordinates.Length < leaf.CoordinateBlock.Length;
            writer.WriteBit(rawMode ? 1 : 0);
            writer.WriteRaw(rawMode ? leaf.RawCoordinates : leaf.CoordinateBlock);
            writer.WriteRaw(EncodeV3Normals(DecodeV2Normals(leaf)));
            writer.WriteRaw(BitConverter.GetBytes(leaf.Tolerance));
            writer.WriteRaw(new byte[] { 0x00, 0x01, 0x02, 0x0a });
            writer.WriteRaw(EncodeCompact(leaf.PrimitiveCount, false));
            writer.WriteRaw(new byte[] { 0x01, 0x41 });
            writer.WriteRaw(EncodeCompact(leaf.FaceCount, false));
            writer.WriteRaw(EncodeCompact(leaf.IndexCount, true));
            writer.WriteFixedWidthArray(leaf.Indices, Math.Max(1, BitLength(leaf.VertexCount)));
            writer.WriteRaw(leaf.ColorRecord);
            writer.PatchUInt32(headerStart + 2, checked((uint)(writer.Length - headerStart - 1)));
        }

        private static ParsedLeaf ParseLeaf(byte[] source, LeafRecord record)
        {
            int position = record.PayloadStart;
            RequireBytes(source, position, LeafSignature, "叶块签名");
            position += LeafSignature.Length;
            CompactValue first = ReadCompact(source, position); position = first.End;
            CompactValue second = ReadCompact(source, position); position = second.End;
            if (first.Value != second.Value) throw new InvalidDataException("叶块顶点数不一致");
            int vertexCount = first.Value;
            RequireRange(source, position, 4, "坐标前导");
            for (int i = 0; i < 4; i++) if (source[position + i] != 0) throw new InvalidDataException("坐标前导异常");
            position += 4;

            int bitmapStart = position;
            int bitmapSize = checked((vertexCount + 3) / 4);
            RequireRange(source, position, bitmapSize, "坐标位图");
            byte[] bitmap = new byte[bitmapSize];
            Buffer.BlockCopy(source, position, bitmap, 0, bitmapSize);
            position += bitmapSize;
            CompactValue valueCount = ReadCompact(source, position); position = valueCount.End;
            int valuesStart = position;
            int valuesEnd = checked(valuesStart + valueCount.Value * 4);
            RequireRange(source, valuesStart, valueCount.Value * 4, "坐标值");
            position = valuesEnd;
            byte[] coordinateBlock = new byte[valuesEnd - bitmapStart];
            Buffer.BlockCopy(source, bitmapStart, coordinateBlock, 0, coordinateBlock.Length);

            byte[] rawCoordinates = new byte[checked(vertexCount * 12)];
            int valuePosition = valuesStart;
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                int target = vertex * 12;
                int code = (bitmap[vertex / 4] >> (2 * (vertex % 4))) & 3;
                if (code == 0)
                {
                    RequireRange(source, valuePosition, 12, "XYZ 坐标值");
                    Buffer.BlockCopy(source, valuePosition, rawCoordinates, target, 12);
                    valuePosition += 12;
                }
                else
                {
                    if (vertex == 0) throw new InvalidDataException("首顶点不能复用坐标");
                    int previous = target - 12;
                    if (code == 1) Buffer.BlockCopy(rawCoordinates, previous, rawCoordinates, target, 12);
                    else if (code == 2)
                    {
                        Buffer.BlockCopy(rawCoordinates, previous, rawCoordinates, target, 8);
                        RequireRange(source, valuePosition, 4, "Z 坐标值");
                        Buffer.BlockCopy(source, valuePosition, rawCoordinates, target + 8, 4);
                        valuePosition += 4;
                    }
                    else
                    {
                        Buffer.BlockCopy(rawCoordinates, previous, rawCoordinates, target, 4);
                        RequireRange(source, valuePosition, 8, "YZ 坐标值");
                        Buffer.BlockCopy(source, valuePosition, rawCoordinates, target + 4, 8);
                        valuePosition += 8;
                    }
                }
            }
            if (valuePosition != valuesEnd) throw new InvalidDataException("坐标位图与值数量不一致");

            CompactValue normalCount = ReadCompact(source, position); position = normalCount.End;
            if (normalCount.Value != vertexCount * 2) throw new InvalidDataException("V2 法向 short 数量异常");
            RequireRange(source, position, normalCount.Value * 2, "法向 short 数组");
            short[] normalShorts = new short[normalCount.Value];
            for (int i = 0; i < normalShorts.Length; i++) normalShorts[i] = ReadInt16LE(source, position + i * 2);
            position += normalCount.Value * 2;
            int normalCodeSize = (vertexCount + 1) / 2;
            RequireRange(source, position, normalCodeSize, "法向编码");
            byte[] normalCodes = new byte[normalCodeSize];
            Buffer.BlockCopy(source, position, normalCodes, 0, normalCodeSize);
            position += normalCodeSize;

            RequireRange(source, position, 4, "容差");
            float tolerance = BitConverter.ToSingle(source, position); position += 4;
            RequireBytes(source, position, new byte[] { 0x00, 0x01, 0x02, 0x0a }, "图元标记");
            position += 4;
            CompactValue primitiveCount = ReadCompact(source, position); position = primitiveCount.End;
            RequireBytes(source, position, new byte[] { 0x01, 0x41 }, "三角列表标记");
            position += 2;
            CompactValue faceCount = ReadCompact(source, position); position = faceCount.End;
            CompactValue indexCount = ReadCompact(source, position); position = indexCount.End;
            if (indexCount.Value != faceCount.Value * 3) throw new InvalidDataException("三角面/索引数量不一致");
            int indexWidth = vertexCount > 255 ? 2 : 1;
            RequireRange(source, position, indexCount.Value * indexWidth, "三角索引");
            int[] indices = new int[indexCount.Value];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = indexWidth == 1 ? source[position + i] :
                    (source[position + i * 2] << 8) | source[position + i * 2 + 1];
            }
            position += indexCount.Value * indexWidth;
            RequireRange(source, position, 8, "颜色记录");
            byte[] color = new byte[8];
            Buffer.BlockCopy(source, position, color, 0, 8);
            position += 8;
            if (color[0] != 0x20 || color[1] != 0x04 || color[2] != 0x04 || color[3] != 0xff || color[4] != 0xff)
                throw new InvalidDataException("颜色记录不符合 compact-95 格式");
            if (position != record.DataEnd) throw new InvalidDataException("叶块存在未解析数据: " + (record.DataEnd - position));

            return new ParsedLeaf
            {
                Record = record,
                VertexCount = vertexCount,
                BitmapStart = bitmapStart,
                CoordinateBlock = coordinateBlock,
                RawCoordinates = rawCoordinates,
                NormalShorts = normalShorts,
                NormalCodes = normalCodes,
                Tolerance = tolerance,
                PrimitiveCount = primitiveCount.Value,
                FaceCount = faceCount.Value,
                IndexCount = indexCount.Value,
                Indices = indices,
                ColorRecord = color
            };
        }

        private static List<float[]> DecodeV2Normals(ParsedLeaf leaf)
        {
            var normals = new List<float[]>(leaf.VertexCount);
            float inverse = F32(1.0 / 32767.0);
            for (int i = 0; i < leaf.VertexCount; i++)
            {
                float first = F32(leaf.NormalShorts[2 * i] * inverse);
                float second = F32(leaf.NormalShorts[2 * i + 1] * inverse);
                int code = (leaf.NormalCodes[i / 2] >> (4 * (i % 2))) & 0xf;
                // V2 stores two minor components.  Older compact output can
                // omit X/Y/Z (codes 0-5), while the simplified writer emitted
                // only 4/5.  Normalize old layouts into the 4/5 calculation
                // below so both data streams remain readable by the CFV3 path.
                float x = first;
                float y = second;
                if (code == 0 || code == 1)
                {
                    y = first;
                    float remainder = F32(1.0f - F32(F32(first * first) + F32(second * second)));
                    x = CatSqrt(remainder < 0f ? 0f : remainder);
                    if (code == 1) x = -x;
                    code = second < 0f ? 5 : 4;
                }
                else if (code == 2 || code == 3)
                {
                    x = first;
                    float remainder = F32(1.0f - F32(F32(first * first) + F32(second * second)));
                    y = CatSqrt(remainder < 0f ? 0f : remainder);
                    if (code == 3) y = -y;
                    code = second < 0f ? 5 : 4;
                }
                float squared = F32(F32(x * x) + F32(y * y));
                float z = CatSqrt(F32(1.0f - squared));
                if (code == 5) z = -z;
                else if (code != 4) throw new InvalidDataException("V2 法向编码异常: " + code);
                normals.Add(new[] { x, y, z });
            }
            return normals;
        }

        private static byte[] EncodeV3Normals(List<float[]> normals)
        {
            var shorts = new List<short>(normals.Count * 2);
            byte[] codes = new byte[(normals.Count + 1) / 2];
            for (int i = 0; i < normals.Count; i++)
            {
                float x = normals[i][0], y = normals[i][1], z = normals[i][2];
                int code;
                float a, b;
                bool stores = false;
                if (x == 1f && y == 0f && z == 0f) code = 6;
                else if (x == -1f && y == 0f && z == 0f) code = 7;
                else if (x == 0f && y == 1f && z == 0f) code = 8;
                else if (x == 0f && y == -1f && z == 0f) code = 9;
                else if (x == 0f && y == 0f && z == 1f) code = 10;
                else if (x == 0f && y == 0f && z == -1f) code = 11;
                else
                {
                    float ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
                    if (ax > ay && (ay >= az || ax > az))
                    {
                        code = x < 0f ? 1 : 0; a = y; b = z;
                    }
                    else if (ay > az)
                    {
                        code = y < 0f ? 3 : 2; a = x; b = z;
                    }
                    else
                    {
                        code = z < 0f ? 5 : 4; a = x; b = y;
                    }
                    shorts.Add((short)(int)F32(a * 32767f));
                    shorts.Add((short)(int)F32(b * 32767f));
                    stores = true;
                }
                codes[i / 2] |= (byte)(code << (4 * (i % 2)));
                if (!stores) { }
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(EncodeCompact(shorts.Count, false));
                foreach (short value in shorts) writer.Write(value);
                writer.Write(codes);
                return stream.ToArray();
            }
        }

        private static float CatSqrt(float value)
        {
            if (value <= 0f) return 0f;
            float epsilon = F32(1.0 / 32767.0);
            float guess;
            if ((double)value <= 0.03) guess = F32(0.05);
            else
            {
                float denominator = F32(value + F32(1.0));
                float reciprocal = F32(F32(1.0) / denominator);
                double estimate = ((double)reciprocal + 0.25) * value + 0.25;
                guess = F32(estimate);
            }
            for (int i = 0; i < 64; i++)
            {
                float quotient = F32(value / guess);
                float summed = F32(quotient + guess);
                float updated = F32((double)summed * 0.5);
                float delta = F32(guess - updated);
                if (-epsilon < delta && delta < epsilon) return updated;
                guess = updated;
            }
            throw new ArithmeticException("CAT 法向平方根迭代未收敛");
        }

        private static byte[] WrapCfv3(byte[] skeleton, byte[] picks, string identity)
        {
            return picks.Length == PickSize
                ? WrapSingleExtent(skeleton, picks, identity)
                : WrapMultiExtent(skeleton, picks, identity);
        }

        private static byte[] WrapSingleExtent(byte[] skeleton, byte[] picks, string identity)
        {
            byte[] prefix = (byte[])BuiltinPrefix.Clone();
            byte[] suffix = (byte[])BuiltinSuffix.Clone();
            PersonalizeShell(prefix, suffix, identity);
            int sceneStart = prefix.Length;
            int pickStart = checked(sceneStart + skeleton.Length);
            int suffixStart = checked(pickStart + picks.Length);
            int totalSize = checked(suffixStart + suffix.Length);

            Be32(prefix, 0x5b, checked(skeleton.Length + picks.Length + 34));
            Be32(prefix, 0x5f, checked(totalSize + 448));
            int directoryIndex = checked(suffixStart + 0xc6);
            Be32(prefix, 0x08, directoryIndex);
            Be32(prefix, 0x0c, totalSize - directoryIndex);
            Be32(prefix, 0x12c, suffixStart);
            foreach (int position in new[] { 0xcc, 0x100, 0x118, 0x11c }) Be32(suffix, position, skeleton.Length);
            Be32(suffix, 0x114, sceneStart);
            foreach (int position in new[] { 0x130, 0x16c, 0x184, 0x188 }) Be32(suffix, position, picks.Length);
            Be32(suffix, 0x180, pickStart);

            return Concat(totalSize, prefix, skeleton, picks, suffix);
        }

        private static byte[] WrapMultiExtent(byte[] skeleton, byte[] picks, string identity)
        {
            if (picks.Length <= PickSize || picks.Length % PickSize != 0)
                throw new InvalidDataException("多叶 3DRep 的 pick 流长度异常");
            if (skeleton.Length < 5 || skeleton[skeleton.Length - 5] != 0xff)
                throw new InvalidDataException("多叶 Skeleton 必须以扩展 Compact 偏移结束");
            byte[] prefix = (byte[])MultiPrefix.Clone();
            byte[] suffix = (byte[])MultiSuffix.Clone();
            PersonalizeShell(prefix, suffix, identity);
            int firstLength = skeleton.Length - 5;
            int firstStart = prefix.Length;
            int middleStart = checked(firstStart + firstLength);
            int secondStart = checked(middleStart + MultiMiddle.Length);
            int pickStart = checked(secondStart + 5);
            int suffixStart = checked(pickStart + picks.Length);
            int totalSize = checked(suffixStart + suffix.Length);

            Be32(prefix, 0x5b, checked(skeleton.Length + picks.Length + MultiMiddle.Length));
            Be32(prefix, 0x5f, checked(totalSize + 448));
            int directoryIndex = checked(suffixStart + 0xc6);
            Be32(prefix, 0x08, directoryIndex);
            Be32(prefix, 0x0c, totalSize - directoryIndex);
            Be32(prefix, 0x12c, suffixStart);
            Be32(prefix, 0x188, checked(middleStart + 23));
            Be32(prefix, 0x1ea, checked(middleStart + 32));

            foreach (int position in new[] { 0xcc, 0x100 }) Be32(suffix, position, skeleton.Length);
            Be32(suffix, 0x114, firstStart);
            foreach (int position in new[] { 0x118, 0x11c }) Be32(suffix, position, firstLength);
            Be32(suffix, 0x128, secondStart);
            foreach (int position in new[] { 0x12c, 0x130 }) Be32(suffix, position, 5);
            Be32(suffix, 0x134, firstLength);
            foreach (int position in new[] { 0x144, 0x180, 0x198, 0x19c }) Be32(suffix, position, picks.Length);
            Be32(suffix, 0x194, pickStart);
            Be32(suffix, 0x280, checked(middleStart + 9));
            Be32(suffix, 0x2f0, checked(middleStart + 18));
            Be32(suffix, 0x3a4, middleStart);

            byte[] result = new byte[totalSize];
            int offset = 0;
            Copy(prefix, result, ref offset);
            Buffer.BlockCopy(skeleton, 0, result, offset, firstLength); offset += firstLength;
            Copy(MultiMiddle, result, ref offset);
            Buffer.BlockCopy(skeleton, firstLength, result, offset, 5); offset += 5;
            Copy(picks, result, ref offset);
            Copy(suffix, result, ref offset);
            if (offset != totalSize) throw new InvalidDataException("多叶 CFV3 长度核算失败");
            return result;
        }

        private static void PersonalizeShell(byte[] prefix, byte[] suffix, string identity)
        {
            if (identity == null) return;
            ushort discriminator; uint token;
            ShellIdentityValues(identity, out discriminator, out token);
            int namePosition = IndexOf(prefix, new byte[] { 0x44, 0x00, 0x2e, 0x00 }, 0);
            if (namePosition < 0) throw new InvalidDataException("CFV3 shell 身份名称缺失");
            byte[] oldName = new byte[68];
            Buffer.BlockCopy(prefix, namePosition, oldName, 0, oldName.Length);
            byte[] newName = Encoding.Unicode.GetBytes("D.4eb51e58d88000002418a16a" + discriminator.ToString("x4") + "0000");
            if (newName.Length != oldName.Length) throw new InvalidDataException("CFV3 身份名称宽度发生变化");
            Buffer.BlockCopy(newName, 0, prefix, namePosition, newName.Length);

            int tokenPosition = namePosition - 11;
            RequireRange(prefix, tokenPosition, 8, "CFV3 身份 token");
            byte[] oldToken = new byte[8];
            Buffer.BlockCopy(prefix, tokenPosition, oldToken, 0, 8);
            if (oldToken[4] != 0x01 || oldToken[5] != 0xdd || oldToken[6] != 0x40 || oldToken[7] != 0x34)
                throw new InvalidDataException("CFV3 shell 身份 token 缺失");
            byte[] newToken = (byte[])oldToken.Clone();
            newToken[0] = (byte)(token >> 24); newToken[1] = (byte)(token >> 16);
            newToken[2] = (byte)(token >> 8); newToken[3] = (byte)token;
            if (ReplaceAll(prefix, oldToken, newToken) == 0 || ReplaceAll(suffix, oldToken, newToken) == 0)
                throw new InvalidDataException("CFV3 身份 token 没有目录引用");
        }

        private static List<LeafRecord> FindLeaves(byte[] data)
        {
            var records = new List<LeafRecord>();
            int position = 0;
            while (position + 1 < data.Length)
            {
                position = IndexOf(data, new byte[] { 0x95, 0xff }, position);
                if (position < 0) break;
                if (position + LeafHeaderSize + LeafSignature.Length <= data.Length)
                {
                    uint declared = ReadUInt32LE(data, position + 2);
                    long dataEndLong = (long)position + declared + 1;
                    int payloadStart = position + LeafHeaderSize;
                    if (declared >= LeafHeaderSize - 1 && dataEndLong <= data.Length &&
                        Matches(data, payloadStart, LeafSignature))
                    {
                        int dataEnd = checked((int)dataEndLong);
                        records.Add(new LeafRecord { Start = position, DataEnd = dataEnd, PayloadStart = payloadStart });
                        position = dataEnd;
                        continue;
                    }
                }
                position += 2;
            }
            return records;
        }

        private static CompactValue ReadCompact(byte[] data, int position)
        {
            RequireRange(data, position, 1, "Compact 值");
            int first = data[position++];
            if (first != 0xff) return new CompactValue { Value = first, End = position };
            RequireRange(data, position, 4, "扩展 Compact 值");
            uint value = ReadUInt32LE(data, position);
            if (value > int.MaxValue) throw new InvalidDataException("Compact 值超出 Int32");
            return new CompactValue { Value = (int)value, End = position + 4 };
        }

        private static byte[] EncodeCompact(int value, bool forceExtended)
        {
            if (value < 0) throw new ArgumentOutOfRangeException("value");
            if (!forceExtended && value <= 0xfe) return new[] { (byte)value };
            byte[] result = new byte[5]; result[0] = 0xff;
            byte[] raw = BitConverter.GetBytes((uint)value);
            Buffer.BlockCopy(raw, 0, result, 1, 4);
            return result;
        }

        private static int BitLength(int value)
        {
            int result = 0;
            while (value > 0) { result++; value >>= 1; }
            return result;
        }

        private static float F32(double value) { return (float)value; }

        private static byte[] Concat(int totalSize, params byte[][] blocks)
        {
            byte[] result = new byte[totalSize]; int offset = 0;
            foreach (byte[] block in blocks) Copy(block, result, ref offset);
            if (offset != totalSize) throw new InvalidDataException("CFV3 长度核算失败");
            return result;
        }

        private static void Copy(byte[] source, byte[] target, ref int offset)
        {
            Buffer.BlockCopy(source, 0, target, offset, source.Length); offset += source.Length;
        }

        private static void Be32(byte[] buffer, int position, int value)
        {
            RequireRange(buffer, position, 4, "BE32 patch");
            uint item = checked((uint)value);
            buffer[position] = (byte)(item >> 24); buffer[position + 1] = (byte)(item >> 16);
            buffer[position + 2] = (byte)(item >> 8); buffer[position + 3] = (byte)item;
        }

        private static uint ReadUInt32LE(byte[] data, int position)
        {
            RequireRange(data, position, 4, "UInt32");
            return (uint)(data[position] | (data[position + 1] << 8) |
                (data[position + 2] << 16) | (data[position + 3] << 24));
        }

        private static short ReadInt16LE(byte[] data, int position)
        {
            return unchecked((short)(data[position] | (data[position + 1] << 8)));
        }

        private static List<int> FindAll(byte[] data, byte[] needle)
        {
            var result = new List<int>(); int position = 0;
            while (position <= data.Length - needle.Length)
            {
                int found = IndexOf(data, needle, position);
                if (found < 0) break;
                result.Add(found); position = found + 1;
            }
            return result;
        }

        private static int IndexOf(byte[] data, byte[] needle, int start)
        {
            if (needle.Length == 0) return start;
            int end = data.Length - needle.Length;
            for (int i = Math.Max(0, start); i <= end; i++)
            {
                int j = 0;
                while (j < needle.Length && data[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static int ReplaceAll(byte[] buffer, byte[] oldValue, byte[] newValue)
        {
            int count = 0, position = 0;
            while (true)
            {
                int found = IndexOf(buffer, oldValue, position);
                if (found < 0) return count;
                Buffer.BlockCopy(newValue, 0, buffer, found, newValue.Length);
                count++; position = found + newValue.Length;
            }
        }

        private static bool Matches(byte[] data, int position, byte[] expected)
        {
            if (position < 0 || position + expected.Length > data.Length) return false;
            for (int i = 0; i < expected.Length; i++) if (data[position + i] != expected[i]) return false;
            return true;
        }

        private static void RequireBytes(byte[] data, int position, byte[] expected, string label)
        {
            if (!Matches(data, position, expected)) throw new InvalidDataException(label + "不符合预期");
        }

        private static void RequireRange(byte[] data, int position, int count, string label)
        {
            if (position < 0 || count < 0 || (long)position + count > data.Length)
                throw new EndOfStreamException(label + "越界");
        }

        private static byte[] Hex(string text)
        {
            byte[] result = new byte[text.Length / 2];
            for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            return result;
        }
    }
}
