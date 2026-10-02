using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class LinestyleAttributeData : BaseAttributeData {
        public byte dataFlags;
        public float lineWidth;

        public LinestyleAttributeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            short version = 0;
            switch (ele.majorVersion) {
                case 8:
                    dataFlags = data.ReadU8();
                    lineWidth = data.ReadF32();
                    break;
                case 9:
                    version = data.ReadI16();
                    dataFlags = data.ReadU8();
                    lineWidth = data.ReadF32();
                    break;
                case 10:
                    version = data.ReadU8();
                    dataFlags = data.ReadU8();
                    lineWidth = data.ReadF32();
                    base.ReadBaseAttributeDataFields2(data);
                    break;
            }
        }
    }
}
