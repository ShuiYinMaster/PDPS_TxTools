using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class SwitchNodeData : GroupNodeData {
        public uint selectedChild;
        public SwitchNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            short version = 0;
            if (ele.majorVersion < 10)
                version = data.ReadI16();
            else
                version = data.ReadU8();
            selectedChild = data.ReadU32();
        }
    }
}
