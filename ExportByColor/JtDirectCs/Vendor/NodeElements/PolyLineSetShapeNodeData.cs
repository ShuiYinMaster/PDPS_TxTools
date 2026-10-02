using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class PolyLineSetShapeNodeData : VertexShapeNodeData {
        float areaFactor;
        public PolyLineSetShapeNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            short version = 0;
            if (ele.majorVersion == 9)
                version = data.ReadI16();
            if (ele.minorVersion == 10)
                version = data.ReadU8();
            areaFactor = data.ReadF32();
        }
    }
}
