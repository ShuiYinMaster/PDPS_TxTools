using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class LateLoadedPropertyAtomData : BasePropertyAtomData {
        public GUID segmeentID;
        public int segmentType;
        public int payloadObjectID;
        public LateLoadedPropertyAtomData(Element ele) : base(ele) {
            var data = ele.dataStream;
            var version = 0;
            if (ele.majorVersion > 8)
                version = ele.majorVersion > 9 ? data.ReadU8() : data.ReadI16();
            segmeentID = new GUID(data);
            segmentType = data.ReadI32();
            if(ele.majorVersion < 9)
                return;
            payloadObjectID = data.ReadI32();
            if (data.Length - data.Position >= 4) {
                var reserved = data.ReadI32();
            }
        }
    }
}
