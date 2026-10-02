using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class PartNodeData : MetaDataNodeData {
        public PartNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            byte version = data.ReadVersionNumber();
            var emptyField = data.ReadI32();
        }
    }
}
