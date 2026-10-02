using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class MetaDataNodeData : GroupNodeData {
        public short versionNumber;
        public MetaDataNodeData(Element ele) : base(ele) {
            if(ele.majorVersion < 10)
                versionNumber = ele.dataStream.ReadI16();
            else
                versionNumber = ele.dataStream.ReadU8();
        }
    }
}
