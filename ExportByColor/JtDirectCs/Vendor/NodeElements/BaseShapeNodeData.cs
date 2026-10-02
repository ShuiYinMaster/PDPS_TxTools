using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class BaseShapeNodeData : BaseNodeData {
        public BBoxF32 untransformedBBox;
        public float area;
        public CountRange vertexCountRange;
        public CountRange nodeCountRange;
        public CountRange polygonCountRange;
        public uint size;
        public float compressionLevel;
        public BaseShapeNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;

            short version = 0;
            BBoxF32 transformedBBox;
            switch (ele.majorVersion) {
                case 8:
                    transformedBBox = new BBoxF32(data);
                    untransformedBBox = new BBoxF32(data);
                    area = data.ReadF32();
                    vertexCountRange = new CountRange(data);
                    nodeCountRange = new CountRange(data);
                    polygonCountRange = new CountRange(data);
                    size = (uint)data.ReadI32();
                    compressionLevel = data.ReadF32();
                    break;
                case 9:
                    version = data.ReadI16();
                    transformedBBox = new BBoxF32(data);
                    untransformedBBox = new BBoxF32(data);
                    area = data.ReadF32();
                    vertexCountRange = new CountRange(data);
                    nodeCountRange = new CountRange(data);
                    polygonCountRange = new CountRange(data);
                    size = (uint)data.ReadI32();
                    compressionLevel = data.ReadF32();
                    break;
                case 10:
                    version = data.ReadU8();
                    untransformedBBox = new BBoxF32(data);
                    area = data.ReadF32();
                    vertexCountRange = new CountRange(data);
                    nodeCountRange = new CountRange(data);
                    polygonCountRange = new CountRange(data);
                    size = data.ReadU32();
                    compressionLevel = data.ReadF32();
                    break;
            }
        }
    }
}
