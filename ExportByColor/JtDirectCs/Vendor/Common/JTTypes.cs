using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;


namespace DLAT.JTReader {
    public class GUID {
        byte[] id;
        public GUID(byte[] guid) {
            id = guid;
        }
        public GUID(StreamReader data) {
            id = data.ReadBytes(16);
        }

        public GUID(string str) {
            var sp = str.Split('-');
            var res = new List<byte>();
            foreach (var s in sp) {
                var rev = new List<byte>();
                for (var i = 0; i < s.Length; i += 2)
                    rev.Add(Convert.ToByte(s.Substring(i, 2), 16));
                rev.Reverse();
                res.AddRange(rev);
            }

            id = res.ToArray();

        }
        public override int GetHashCode() {
            var hashCode = 0;
            for (int i = 0; i < 16; i += 4)
                hashCode ^= BitConverter.ToInt32(id, i);
            return hashCode;
        }
        public override bool Equals(object obj) {
            var otherID = obj == null ? null : (obj as GUID).id;
            if (otherID == null && id == null)
                return true;
            if (otherID == null || id == null)
                return false;
            return otherID.SequenceEqual(id);
        }
        public static bool operator ==(GUID x, GUID y) {
            return x.Equals(y);
        }
        public static bool operator !=(GUID x, GUID y) {
            return !x.Equals(y);
        }
        public override string ToString() {
            string res = "";
            res += BitConverter.ToUInt32(id, 0).ToString("X8") + "-";
            res += BitConverter.ToUInt16(id, 4).ToString("X4") + "-";
            res += BitConverter.ToUInt16(id, 6).ToString("X4") + "-";
            res += id[8].ToString("X2") + "-";
            res += id[9].ToString("X2") + "-";
            res += id[10].ToString("X2") + "-";
            res += id[11].ToString("X2") + "-";
            res += id[12].ToString("X2") + "-";
            res += id[13].ToString("X2") + "-";
            res += id[14].ToString("X2") + "-";
            res += id[15].ToString("X2");
            return res;
        }
        public bool isEOE() {
            for (var i = 0; i < id.Length; i++)
                if (id[i] != 0xFF)
                    return false;
            return true;
        }
    }
    public class DirF32 {
        public float x, y, z;
        public DirF32(StreamReader data) {
            x = data.ReadF32();
            y = data.ReadF32();
            z = data.ReadF32();
        }
    }
    public class DirF64 {
        public double x, y, z;
        public DirF64(StreamReader data) {
            x = data.ReadF64();
            y = data.ReadF64();
            z = data.ReadF64();
        }

        public DirF64(double _x, double _y, double _z) {
            x = _x;
            y = _y;
            z = _z;
        }
    }
    public class CoordF32 {
        public float x, y, z;
        public CoordF32(StreamReader data) {
            x = data.ReadF32();
            y = data.ReadF32();
            z = data.ReadF32();
        }
    }
    public class CoordF64 {
        public double x, y, z;
        public CoordF64(StreamReader data) {
            x = data.ReadF64();
            y = data.ReadF64();
            z = data.ReadF64();
        }
    }
    public class HCoordF32 {
        public float x, y, z, w;
        public HCoordF32(StreamReader data) {
            x = data.ReadF32();
            y = data.ReadF32();
            z = data.ReadF32();
            w = data.ReadF32();
        }
    }
    public class CountRange {
        public int minCount;
        public int maxCount;
        public CountRange(StreamReader data) {
            minCount = data.ReadI32();
            maxCount = data.ReadI32();
        }
    }
    public class Mx4F32 {
        public float[] raw = new float[16];
        public Mx4F32(StreamReader data) {
            for (int i = 0; i < 16; i++)
                raw[i] = data.ReadF32();
        }
        public Mx4F32(StreamReader data,uint storedValuesMask) {
            for (int i = 0; i < 16; i += 5)
                raw[i] = 1;
            for (int i = 0; i < 16; ++i)
                raw[i] = ((storedValuesMask << i) & 0x8000) == 0 ? raw[i] : data.ReadF32();
        }

        public Mx4F64 ToMx4F64() {
            Mx4F64 res = new Mx4F64();
            for (int i = 0; i < 16; ++i)
                res.raw[i] = raw[i];
            return res;
        }

    }
    public class Mx4F64 {
        public double[] raw = new double[16];
        public Mx4F64() { }
        public Mx4F64(StreamReader data) {
            for (int i = 0; i < 16; i++)
                raw[i] = data.ReadF64();
        }
        public Mx4F64(StreamReader data, uint storedValuesMask) {
            for (int i = 0; i < 16; i += 5)
                raw[i] = 1;
            for (int i = 0; i < 16; ++i)
                raw[i] = ((storedValuesMask << i) & 0x8000) == 0 ? raw[i] : data.ReadF64();
        }
    }
    public class PlaneF32 {
        public float a, b, c, d;
        public PlaneF32(StreamReader data) {
            a = data.ReadF32();
            b = data.ReadF32();
            c = data.ReadF32();
            d = data.ReadF32();
        }
    }
    public class BBoxF32 {
        public CoordF32 minCorner;
        public CoordF32 maxCorner;
        public BBoxF32(StreamReader data) {
            minCorner = new CoordF32(data);
            maxCorner = new CoordF32(data);
        }
    }
    public class RGBA {
        public float r, g, b, a;
        public RGBA(StreamReader data) {
            r = data.ReadF32();
            g = data.ReadF32();
            b = data.ReadF32();
            a = data.ReadF32();
        }
        public RGBA(float _r,float _g,float _b,float _a) {
            r = _r;
            g = _g;
            b = _b;
            a = _a;
        }
        public RGBA(float grey) {
            r = g = b = grey;
            a = 1;
        }
    }

    public class QuantizationParameters {
        public byte bitsPerVertex;
        public byte normalBitsFactor;
        public byte bitsPerTextureCoord;
        public byte bitsPerColor;
        public QuantizationParameters(StreamReader data) {
            bitsPerVertex = data.ReadU8();
            normalBitsFactor = data.ReadU8();
            bitsPerTextureCoord = data.ReadU8();
            bitsPerColor = data.ReadU8();
        }
    }

    public class PrimitiveSetQuantizationParameters {
        public byte bitsPerVertex;
        public byte bitsPerColor;
        public PrimitiveSetQuantizationParameters(StreamReader data) {
            bitsPerVertex = data.ReadU8();
            bitsPerColor = data.ReadU8();
        }
    }

    static class SegmentTypes {
        static readonly Dictionary<int, (string, bool)> types = new Dictionary<int, (string, bool)> {
            //id, name, surpot ZLIB
            { 0, (/*WTF*/"There is no Type 0 in the manual", true /*anyway I found out that it support compress*/) },
            { 1, ("Logical Scene Graph", true) },
            { 2, ("JT B-Rep", true) },
            { 3, ("PMI Data", true) },
            { 4, ("Meta Data", true) },
            { 6, ("Shape", false) },
            { 7, ("Shape LOD0", false) },
            { 8, ("Shape LOD1", false) },
            { 9, ("Shape LOD2", false) },
            { 10, ("Shape LOD3", false) },
            { 11, ("Shape LOD4", false) },
            { 12, ("Shape LOD5", false) },
            { 13, ("Shape LOD6", false) },
            { 14, ("Shape LOD7", false) },
            { 15, ("Shape LOD8", false) },
            { 16, ("Shape LOD9", false) },
            { 17, ("XT B-Rep", true) },
            { 18, ("Wireframe Representation", true) },
            { 20, ("ULP", true) },
            { 23, ("STT", true) },
            { 24, ("LWPA", true) },
            { 30, ("MultiXT B-Rep", true) },
            { 31, ("InfoSegment", true) },
            { 32, ("Reserved", true) },
            { 33, ("STEP B-Reo", true) },
        };
        public static (string, bool) GetType(int typeID) {
            return types[typeID];
        }
    }
    static class ObjectTypeIdentifiers {
        public static GUID EOF = new GUID(new byte[]{0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                                                     0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF});
        public static Dictionary<string, string> typeStrings = new Dictionary<string, string> {
            {"FFFFFFFF-FFFF-FFFF-FF-FF-FF-FF-FF-FF-FF-FF", "Identifier to signal End-Of-Elements."},
            {"10DD1035-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Base Node Element"},
            {"10DD101B-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Group Node Element"},
            {"10DD102A-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Instance Node Element"},
            {"10DD102C-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "LOD Node Element"},
            {"CE357245-38FB-11D1-A5-06-00-60-97-BD-C6-E1", "Meta Data Node Element"},
            {"D239E7B6-DD77-4289-A0-7D-B0-EE-79-F7-94-94", "NULL Shape Node Element"},
            {"CE357244-38FB-11D1-A5-06-00-60-97-BD-C6-E1", "Part Node Element"},
            {"10DD103E-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Partition Node Element"},
            {"10DD104C-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Range LOD Node Element"},
            {"10DD10F3-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Switch Node Element"},
            {"10DD1059-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Base Shape Node Element"},
            {"98134716-0010-0818-19-98-08-00-09-83-5D-5A", "Point Set Shape Node Element"},
            {"10DD1048-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Polygon Set Shape Node Element"},
            {"10DD1046-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Polyline Set Shape Node Element"},
            {"E40373C1-1AD9-11D3-9D-AF-00-A0-C9-C7-DD-C2", "Primitive Set Shape Node Element"},
            {"10DD1077-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Tri-Strip Set Shape Node Element"},
            {"10DD107F-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Vertex Shape Node Element"},
            {"10DD1001-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Base Attribute Element"},
            {"10DD1014-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Draw Style Attribute Element"},
            {"10DD1083-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Geometric Transform Attribute Element"},
            {"10DD1028-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Infinite Light Attribute Element"},
            {"10DD1096-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Light Set Attribute Element"},
            {"10DD10C4-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Linestyle Attribute Element"},
            {"10DD1030-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Material Attribute Element"},
            {"10DD1045-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Point Light Attribute Element"},
            {"8D57C010-E5CB-11D4-84-0E-00-A0-D2-18-2F-9D", "Pointstyle Attribute Element"},
            {"10DD1073-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Texture Image Attribute Element"},
            {"10DD104B-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Base Property Atom Element"},
            {"CE357246-38FB-11D1-A5-06-00-60-97-BD-C6-E1", "Date Property Atom Element"},
            {"10DD102B-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Integer Property Atom Element"},
            {"10DD1019-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Floating Point Property Atom Element"},
            {"E0B05BE5-FBBD-11D1-A3-A7-00-AA-00-D1-09-54", "Late Loaded Property Atom Element"},
            {"10DD1004-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "JT Object Reference Property AtomElement"},
            {"10DD106E-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "String Property Atom Element"},
            {"873A70C0-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "JT B-Rep Element"},
            {"CE357249-38FB-11D1-A5-06-00-60-97-BD-C6-E1", "PMI Manager Meta Data Element"},
            {"CE357247-38FB-11D1-A5-06-00-60-97-BD-C6-E1", "Property Proxy Meta Data Element"},
            {"3E637AED-2A89-41F8-A9-FD-55-37-37-03-96-82", "Null Shape LOD Element"},
            {"98134716-0011-0818-19-98-08-00-09-83-5D-5A", "Point Set Shape LOD Element"},
            {"10DD109F-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Polygon Set Shape LOD Element"},
            {"10DD10A1-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Polyline Set Shape LOD Element"},
            {"E40373C2-1AD9-11D3-9D-AF-00-A0-C9-C7-DD-C2", "Primitive Set Shape Element"},
            {"10DD10AB-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Tri-Strip Set Shape LOD Element"},
            {"10DD10B0-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Vertex Shape LOD Element"},
            {"873A70E0-2AC9-11D1-9B-6B-00-80-C7-BB-59-97", "XT B-Rep Element"                   },
            {"873A70D0-2AC8-11D1-9B-6B-00-80-C7-BB-59-97", "Wireframe Rep Element"              },
            {"4CC7A521-0728-11D3-9D-8B-00-A0-C9-C7-DD-C2", "Wire Harness Set Shape Node"        }, //8
            {"4CC7A523-0728-11D3-9D-8B-00-A0-C9-C7-DD-C2", "Wire Harness Set Shape Element"     }, //8
            {"AA1B831D-6E47-4FEE-A8-65-CD-7E-1F-2F-39-DB", "Shader Effects Attribute Element"   }, //8 9
            {"AD8DCCC2-7A80-456D-B0-D5-DD-3A-0B-8D-21-E7", "Fragment Shader Attribute Element"  }, //8 9
            {"2798BCAD-E409-47AD-BD-46-0B-37-1F-D7-5D-61", "Vertex Shader Attribute Element"    }, //8 9
            {"D67F8EA8-F524-4879-92-8C-4C-3A-56-1F-B9-3A", "JT LWPA Element "                   }, //9 10
            {"F338A4AF-D7D2-41C5-BC-F2-C5-5A-88-B2-1E-73", "JT ULP Element"                     }, //9 10
            {"92F5B094-6499-4D2D-92-AA-60-D0-5A-44-32-CF", "Mapping TriPlanar Element"          }, //9 10
            {"72475FD1-2823-4219-A0-6C-D9-E6-E3-9A-45-C1", "Mapping Sphere Element"             }, //9 10
            {"3E70739D-8CB0-41EF-84-5C-A1-98-D4-00-3B-3F", "Mapping Cylinder Element"           }, //9 10
            {"A3CFB921-BDEB-48D7-B3-96-8B-8D-0E-F4-85-A0", "Mapping Plane Element"              }, //9 10
            {"AA1B831D-6E47-4FEE-A8-65-CD-7E-1F-2F-39-DC", "Texture Coordinate Generator Attribute Element"},//9 10
        };
        public static Dictionary<GUID, Type> types = new Dictionary<GUID, Type> {
            {new GUID("FFFFFFFF-FFFF-FFFF-FF-FF-FF-FF-FF-FF-FF-FF"), null                                     }, //"Identifier to signal End-Of-Elements."
            {new GUID("10DD1035-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(BaseNodeData)                     }, //"Base Node Element"
            {new GUID("10DD101B-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(GroupNodeData)                    }, //"Group Node Element"
            {new GUID("10DD102A-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(InstanceNodeData)                 }, //"Instance Node Element"
            {new GUID("10DD102C-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(LODNodeData)                      }, //"LOD Node Element"
            {new GUID("CE357245-38FB-11D1-A5-06-00-60-97-BD-C6-E1"), typeof(MetaDataNodeData)                 }, //"Meta Data Node Element"
            {new GUID("D239E7B6-DD77-4289-A0-7D-B0-EE-79-F7-94-94"), typeof(NullShapeNodeData)                }, //"NULL Shape Node Element"
            {new GUID("CE357244-38FB-11D1-A5-06-00-60-97-BD-C6-E1"), typeof(PartNodeData)                     }, //"Part Node Element"
            {new GUID("10DD103E-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(PartitionNodeData)                }, //"Partition Node Element"
            {new GUID("10DD104C-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(RangeLODNodeData)                 }, //"Range LOD Node Element"
            {new GUID("10DD10F3-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(SwitchNodeData)                   }, //"Switch Node Element"
            {new GUID("10DD1059-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(BaseShapeNodeData)                }, //"Base Shape Node Element"
            {new GUID("98134716-0010-0818-19-98-08-00-09-83-5D-5A"), typeof(PointSetShapeNodeData)            }, //"Point Set Shape Node Element"
            {new GUID("10DD1048-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(PolygonSetShapeNodeData)          }, //"Polygon Set Shape Node Element"
            {new GUID("10DD1046-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(PolyLineSetShapeNodeData)         }, //"Polyline Set Shape Node Element"
            {new GUID("E40373C1-1AD9-11D3-9D-AF-00-A0-C9-C7-DD-C2"), typeof(PrimitiveSetShapeNodeData)        }, //"Primitive Set Shape Node Element"
            {new GUID("10DD1077-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(Tri_StripSetShapeNodeData)        }, //"Tri-Strip Set Shape Node Element"
            {new GUID("10DD107F-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(VertexShapeNodeData)              }, //"Vertex Shape Node Element"
            {new GUID("10DD1001-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(BaseAttributeData)                }, //"Base Attribute Element"
            {new GUID("10DD1014-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(DrawStyleAttributeData)           }, //"Draw Style Attribute Element"
            {new GUID("10DD1083-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(GeometricTransformAttributeData)  }, //"Geometric Transform Attribute Element"
            {new GUID("10DD1028-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(InfiniteLightAttributeData)       }, //"Infinite Light Attribute Element"
            {new GUID("10DD1096-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(LightSetAttributeData)            }, //"Light Set Attribute Element"
            {new GUID("10DD10C4-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(LinestyleAttributeData)           }, //"Linestyle Attribute Element"
            {new GUID("10DD1030-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(MaterialAttributeData)            }, //"Material Attribute Element"
            {new GUID("10DD1045-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(PointLightAttributeData)          }, //"Point Light Attribute Element"
            {new GUID("8D57C010-E5CB-11D4-84-0E-00-A0-D2-18-2F-9D"), typeof(PointstyleAttributeData)          }, //"Pointstyle Attribute Element"
            {new GUID("10DD1073-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(TextureImageAttributeData)        }, //"Texture Image Attribute Element"
            {new GUID("10DD104B-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(BasePropertyAtomData)             }, //"Base Property Atom Element"
            {new GUID("CE357246-38FB-11D1-A5-06-00-60-97-BD-C6-E1"), typeof(DatePropertyAtomData)             }, //"Date Property Atom Element"
            {new GUID("10DD102B-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(IntegerPropertyAtomData)          }, //"Integer Property Atom Element"
            {new GUID("10DD1019-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(FloatingPropertyAtomData)         }, //"Floating Point Property Atom Element"
            {new GUID("E0B05BE5-FBBD-11D1-A3-A7-00-AA-00-D1-09-54"), typeof(LateLoadedPropertyAtomData)       }, //"Late Loaded Property Atom Element"
            {new GUID("10DD1004-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(JTObjectReferencePropertyAtomData)}, //"JT Object Reference Property AtomElement"
            {new GUID("10DD106E-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(StringPropertyAtomData)           }, //"String Property Atom Element"
            {new GUID("873A70C0-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(JTB_RepData)                      }, //"JT B-Rep Element"
            {new GUID("CE357249-38FB-11D1-A5-06-00-60-97-BD-C6-E1"), typeof(PMIManagerMetaData)               }, //"PMI Manager Meta Data Element"
            {new GUID("CE357247-38FB-11D1-A5-06-00-60-97-BD-C6-E1"), typeof(PropertyProxyMetaData)            }, //"Property Proxy Meta Data Element"
            {new GUID("3E637AED-2A89-41F8-A9-FD-55-37-37-03-96-82"), typeof(NullShapeNodeData)                }, //"Null Shape LOD Element"
            {new GUID("98134716-0011-0818-19-98-08-00-09-83-5D-5A"), typeof(PointSetShapeLODData)             }, //"Point Set Shape LOD Element"
            {new GUID("10DD109F-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(PolygonSetShapeLODData)           }, //"Polygon Set Shape LOD Element"
            {new GUID("10DD10A1-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(PolyLineSetShapeLODData)         }, //"Polyline Set Shape LOD Element"
            {new GUID("E40373C2-1AD9-11D3-9D-AF-00-A0-C9-C7-DD-C2"), typeof(PrimitiveSetShapeData)            }, //"Primitive Set Shape Element"
            {new GUID("10DD10AB-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(Tri_StripSetShapeLODData)         }, //"Tri-Strip Set Shape LOD Element"
            {new GUID("10DD10B0-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(VertexShapeLODData)               }, //"Vertex Shape LOD Element"
            {new GUID("873A70E0-2AC9-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(XTB_RepData)                      }, //"XT B-Rep Element"
            {new GUID("873A70D0-2AC8-11D1-9B-6B-00-80-C7-BB-59-97"), typeof(WireframeRepData)                 }, //"Wireframe Rep Element"
            {new GUID("4CC7A521-0728-11D3-9D-8B-00-A0-C9-C7-DD-C2"), typeof(WireHarnessSetShapeNodeData) /*8*/}, //"Wire Harness Set Shape Node"
            {new GUID("4CC7A523-0728-11D3-9D-8B-00-A0-C9-C7-DD-C2"), typeof(WireHarnessSetShapeData)     /*8*/}, //"Wire Harness Set Shape Element"
            {new GUID("AA1B831D-6E47-4FEE-A8-65-CD-7E-1F-2F-39-DB"), typeof(ShaderEffectsAttributeData) /*89*/}, //"Shader Effects Attribute Element"
            {new GUID("AD8DCCC2-7A80-456D-B0-D5-DD-3A-0B-8D-21-E7"), typeof(FragmentShaderAttributeData)/*89*/}, //"Fragment Shader Attribute Element"
            {new GUID("2798BCAD-E409-47AD-BD-46-0B-37-1F-D7-5D-61"), typeof(VertexShaderAttributeData)  /*89*/}, //"Vertex Shader Attribute Element"
            {new GUID("D67F8EA8-F524-4879-92-8C-4C-3A-56-1F-B9-3A"), typeof(JTLWPAData)                /*910*/}, //"JT LWPA Element "
            {new GUID("F338A4AF-D7D2-41C5-BC-F2-C5-5A-88-B2-1E-73"), typeof(JTULPData)                 /*910*/}, //9 10
            {new GUID("92F5B094-6499-4D2D-92-AA-60-D0-5A-44-32-CF"), typeof(MappingTriPlanarData)      /*910*/}, //Mapping TriPlanar Element"
            {new GUID("72475FD1-2823-4219-A0-6C-D9-E6-E3-9A-45-C1"), typeof(MappingSphereData)         /*910*/}, //"Mapping Sphere Element"
            {new GUID("3E70739D-8CB0-41EF-84-5C-A1-98-D4-00-3B-3F"), typeof(MappingCylinderData)       /*910*/}, //"Mapping Cylinder Element"
            {new GUID("A3CFB921-BDEB-48D7-B3-96-8B-8D-0E-F4-85-A0"), typeof(MappPlaneData)             /*910*/}, //"Mapping Plane Element"
            {new GUID("AA1B831D-6E47-4FEE-A8-65-CD-7E-1F-2F-39-DC"), typeof(TextureCoordinateGeneratorAttributeData)},//"Texture Coordinate Generator Attribute Element"
        };
        public static string GetTypeString(string typeID) {
            return typeStrings[typeID];
        }
        public static string GetTypeString(GUID typeID) {
            return typeStrings[typeID.ToString()];
        }

    }
}
