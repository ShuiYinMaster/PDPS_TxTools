using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class PartitionNodeData : GroupNodeData {
        public int partitionFlags;
        public string fileName;
        public BBoxF32 transformedBBox;
        public float area;
        public CountRange vertexCountRange;
        public CountRange nodeCountRange;
        public CountRange polygonCountRange;
        public BBoxF32 untransformedBBox;

        public PartitionNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;

            if (ele.majorVersion == 10 && ele.minorVersion < 5) {
                var localVersionFlag = data.ReadU8();
            }

            partitionFlags = data.ReadI32();
            fileName = data.ReadMbString();
            transformedBBox = new BBoxF32(data);
            area = data.ReadF32();
            vertexCountRange = new CountRange(data);
            nodeCountRange = new CountRange(data);
            polygonCountRange = new CountRange(data);
            //if((partitionFlags & 0x00000001) != 0)
            if(data.Length - data.Position >= 24 && (partitionFlags & 0x00000001) != 0)
                untransformedBBox = new BBoxF32(data);

            //Debug.Log("Flag:" + partitionFlags + " FileName:" + fileName, 2);

        }

    }
}
