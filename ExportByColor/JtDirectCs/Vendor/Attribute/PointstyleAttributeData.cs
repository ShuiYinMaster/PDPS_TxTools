using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class PointstyleAttributeData : BaseAttributeData {
        public byte dataFlags;
        public float pointSize;
        public PointstyleAttributeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            var version = 0;
            switch (ele.majorVersion) {
                case 8:
                    version = data.ReadI16();
                    dataFlags = data.ReadU8();
                    pointSize = data.ReadF32();
                    break;
                case 9:
                    version = data.ReadI16();
                    dataFlags = data.ReadU8();
                    pointSize = data.ReadF32();
                    break;
                case 10:
                    version = data.ReadU8();
                    dataFlags = data.ReadU8();
                    pointSize = data.ReadF32();
                    base.ReadBaseAttributeDataFields2(data);
                    break;
            }
        }
    }
}
