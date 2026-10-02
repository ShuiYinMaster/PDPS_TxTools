using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class InstanceNodeData : BaseNodeData {
        public int childNodeObjectID;
        public InstanceNodeData(Element ele) : base(ele) {
            short version = 0;
            if(ele.majorVersion == 9)
                version = ele.dataStream.ReadI16();
            else if(ele.majorVersion == 10)
                version = ele.dataStream.ReadU8();
            childNodeObjectID = ele.dataStream.ReadI32();
        }
    }
}
