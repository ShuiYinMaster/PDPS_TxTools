using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class XTB_RepData {
        public byte version;
        public byte subordinateFlag;
        public int xtMajorVersion;
        public int xtMinorVersion;
        public int xtBuildNUmber;
        public byte xtTransmitFormat;
        public int xtBRepDataLength;
        
        public XTB_RepData(Element ele) {
            
        }
    }

    public class IntegerAttribute {
        
    }
    
}
