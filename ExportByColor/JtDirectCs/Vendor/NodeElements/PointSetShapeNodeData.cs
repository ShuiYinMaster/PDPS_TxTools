using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class PointSetShapeNodeData:VertexShapeNodeData {
        public float areaFactor;
        public ulong vertexBindings = 0;
        public PointSetShapeNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;

            short version = 1;
            if (ele.majorVersion == 9)
                version = data.ReadI16();
            if (ele.minorVersion == 10)
                version = data.ReadU8();
            
            areaFactor = data.ReadF32();
            // if (version == 1)
            //     vertexBindings = data.ReadU64();
        }
    }
}
