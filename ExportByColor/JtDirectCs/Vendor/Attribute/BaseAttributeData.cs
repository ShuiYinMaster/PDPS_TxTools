using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class BaseAttributeData {
        public byte stateFlags;
        public uint fieldInhibitFlags;
        public uint fieldFinalFlags;
        public short version = 0;

        public uint v2PaletteIndex = 0xFFFFFFFF;
        public BaseAttributeData(Element ele) {
            var data = ele.dataStream;
            if (ele.majorVersion == 8)
                ele.objectID = data.ReadI32();
            else
                version = data.ReadVersionNumber();
            stateFlags = data.ReadU8();
            fieldInhibitFlags = data.ReadU32();
            if(ele.majorVersion == 10)
                fieldFinalFlags = data.ReadU32();
        }

        public void ReadBaseAttributeDataFields2(StreamReader data) {
            if (version == 2)
                v2PaletteIndex = data.ReadU32();
        }
    }
}
