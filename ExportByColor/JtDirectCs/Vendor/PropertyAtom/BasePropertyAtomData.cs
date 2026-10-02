using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class BasePropertyAtomData {
        public short version;
        public uint stateFlags;
        public BasePropertyAtomData(Element ele) {
            var data = ele.dataStream;
            if (ele.majorVersion == 8)
                ele.objectID = data.ReadI32();
            else
                version = data.ReadVersionNumber();
            stateFlags = data.ReadU32();
        }
    }
}
