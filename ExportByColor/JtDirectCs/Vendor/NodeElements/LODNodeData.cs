using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class LODNodeData : GroupNodeData {
        public List<float> reservedFieldVecF32;
        public int reservedFieldI32;
        public LODNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            short version = 0;
            if (ele.majorVersion == 9)
                version = data.ReadI16();
            if(ele.majorVersion == 10)
                version = data.ReadU8();
            else {
                reservedFieldVecF32 = data.ReadVecF32();
                reservedFieldI32 = data.ReadI32();
            }
        }
    }
}
