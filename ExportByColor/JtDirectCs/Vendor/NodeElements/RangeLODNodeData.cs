using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class RangeLODNodeData : LODNodeData {
        public List<float> rangeLimits;
        public CoordF32 center;
        public RangeLODNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            short version = 0;
            if (ele.majorVersion > 8)
                version = data.ReadVersionNumber();
                
            rangeLimits = data.ReadVecF32();
            center = new CoordF32(data);
        }
    }
}
