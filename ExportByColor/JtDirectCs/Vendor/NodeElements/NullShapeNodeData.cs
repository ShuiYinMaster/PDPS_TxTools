using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class NullShapeNodeData : BaseShapeNodeData {
        public short versionNumber;
        public NullShapeNodeData(Element ele):base(ele) {
            if (ele.majorVersion < 10)
                versionNumber = ele.dataStream.ReadI16();
            else
                versionNumber = ele.dataStream.ReadU8();
        }
    }
}
