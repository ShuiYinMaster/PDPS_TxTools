using System;
using System.IO;

namespace TxTools.ExportByColor
{
    public static partial class CgrWriter
    {
        public const string SceneRecordVersion = "CGRSCENE-20261016-R7-variable-attributes";
        private const byte FaceSuffix = 1;
        private const uint FirstId = 2047;
        private const byte SceneVolumeContributionBit = 0x10; // 95 COM volume gate; no solid-validity assertion.
        private const byte AcceptedFeatureFlags = 0x20 | SceneVolumeContributionBit;

        // Default 35-byte common header; the compact inheritance mask can grow
        // by four bytes. The fixed LE graphics word packs width (bits 4..9),
        // line type (bits 10..15), and retained low flags.
        // Radius/center and object links have
        // independent native evidence; attribute/flag slots retain the accepted
        // profile values without assigning unproven physical semantics.
        private static void WriteSceneCommon(BinaryWriter w, byte flag2, byte graphicsLow, byte field11,
            float radius, float scalar16, float[] center, uint objectId, uint attributeWord = 1,
            byte encoding5 = 0xfd, uint inheritanceMask = 0, byte graphicsHigh = 4, byte opacity = 255)
        {
            Compact(w, inheritanceMask); w.Write(flag2);
            w.Write((ushort)(graphicsLow | (graphicsHigh << 8))); w.Write(encoding5); w.Write(opacity);
            w.Write((byte)(attributeWord >> 24)); w.Write((byte)(attributeWord >> 16));
            w.Write((byte)(attributeWord >> 8)); w.Write((byte)attributeWord);
            w.Write(field11); w.Write(radius); w.Write(scalar16);
            for (int k = 0; k < 3; k++) w.Write(center == null ? 0f : center[k]);
            w.Write(objectId);
        }

        private static void WriteNode62(BinaryWriter w, byte flag2, byte category, byte field11,
            float radius, float scalar16, float[] center, uint objectId, ushort layerCode, ushort nodeOptions,
            uint attributeWord = 1)
        {
            w.Write((byte)0x62);
            WriteSceneCommon(w, flag2, category, field11, radius, scalar16, center, objectId, attributeWord);
            WriteSceneNodeAttributes(w, layerCode, nodeOptions);
        }

        private static void WriteSceneNodeAttributes(BinaryWriter w, ushort layerCode, ushort nodeOptions)
        {
            // COM SetLayer confirms a fixed u16 LE layer, not an FF-prefixed
            // compact integer. 1023 is native no-layer; 65535 is retained in
            // the accepted legacy branch profile. Options remain uninterpreted.
            w.Write(layerCode); w.Write(nodeOptions); w.Write((byte)0); // No matrix.
        }

        private static long BeginSceneEnvelope(BinaryWriter w, byte tag)
        {
            long start = w.BaseStream.Position;
            w.Write(tag); w.Write((byte)255); w.Write(0U);
            return start;
        }

        private static void EndSceneEnvelope(BinaryWriter w, long start, byte tag)
        {
            long end = w.BaseStream.Position;
            // Native A3 counts from its length word; 95 also counts its FF byte.
            uint length = checked((uint)(end - start - (tag == 0xa3 ? 2 : 1)));
            w.BaseStream.Position = start + 2; w.Write(length); w.BaseStream.Position = end;
        }

        private static void WriteLegacyRoot(BinaryWriter w, uint childCount, float radius = 0, float[] center = null)
        {
            WriteNode62(w, 0, 5, 1, radius, 1f, center, 0, ushort.MaxValue, 0);
            Compact(w, childCount);
        }

        private static byte[] WriteAcceptedFeatureScene(byte[] payload, float radius, float[] center, uint pickOffset)
        {
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                long shell = BeginSceneEnvelope(w, 0xa3);
                WriteSceneCommon(w, 0, 4, 1, radius, 0, center, 2);
                WriteSceneNodeAttributes(w, 1023, 1); CompactExtended(w, 2); w.Write((byte)0);
                WriteNode62(w, AcceptedFeatureFlags, 4, 0, radius, 0, center, 3, 1023, 1);
                CompactExtended(w, 1); w.Write((byte)0);
                WriteNode62(w, AcceptedFeatureFlags, 4, 0, radius, 0, center, 19, 1023, 1);
                CompactExtended(w, 1); w.Write((byte)0);
                long leaf = BeginSceneEnvelope(w, 0x95);
                WriteSceneCommon(w, AcceptedFeatureFlags, 4, 0, radius, 0, center, FirstId - 4);
                w.Write(payload); EndSceneEnvelope(w, leaf, 0x95);
                Compact(w, pickOffset); // Outside the 95 span, inside the A3 span.
                w.Write((byte)0);
                WriteNode62(w, 0, 4, 0, 1f, 0, null, 18, 1023, 1, 0);
                CompactExtended(w, 0);
                w.Write(1f); EndSceneEnvelope(w, shell, 0xa3);
                return ms.ToArray();
            }
        }

        private static byte[] WriteAcceptedLegacyScene(byte[] payload, float radius, float[] center, uint pickOffset=0)
        {
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                WriteLegacyRoot(w, 1, radius, center); w.Write((byte)0);
                w.Write(WriteAcceptedFeatureScene(payload, radius, center, pickOffset));
                return ms.ToArray();
            }
        }

        private static void WriteCompactNode(BinaryWriter w, double[] bounds, bool branch)
        {
            var center = new[] { Finite(bounds[0]), Finite(bounds[1]), Finite(bounds[2]) };
            WriteNode62(w, 0x20, 5, 0, Finite(bounds[9] + Math.Max(.2, bounds[9] * 1e-6)),
                        0, center, 0, branch ? ushort.MaxValue : (ushort)1023, branch ? (ushort)0 : (ushort)1);
        }

        private static byte[] WriteCompactLeaf(byte[] payload, double[] bounds, uint pickOffset)
        {
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                long start = BeginSceneEnvelope(w, 0x95);
                var center = new[] { Finite(bounds[0]), Finite(bounds[1]), Finite(bounds[2]) };
                WriteSceneCommon(w, 0x20, 4, 0, Finite(bounds[9] + Math.Max(.2, bounds[9] * 1e-6)),
                                 0, center, 0, 0xffffff01U, 255);
                w.Write(payload); EndSceneEnvelope(w, start, 0x95); Compact(w, pickOffset);
                return ms.ToArray();
            }
        }

        private static void WritePickControls(BinaryWriter w)
        {
            // Field layout confirmed; physical meaning of these controls is unknown.
            w.Write((float)(Math.PI / 4)); w.Write(1e12f); w.Write((byte)0);
        }

        private static void WriteCompactPick(BinaryWriter w, double[] bounds)
        {
            w.Write((byte)255); w.Write(61U); WritePickControls(w);
            w.Write(0U); w.Write(ushort.MaxValue);
            for (int k = 0; k < 3; k++) w.Write(Finite(bounds[k] - bounds[k + 3]));
            for (int k = 0; k < 3; k++) w.Write(Finite(bounds[k] + bounds[k + 3]));
            for (int k = 0; k < 3; k++) w.Write(Finite(bounds[k]));
            w.Write(Finite(bounds[9])); w.Write((byte)1);
        }
    }
}
