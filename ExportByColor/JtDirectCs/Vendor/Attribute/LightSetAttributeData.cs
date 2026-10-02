using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class LightSetAttributeData : BaseAttributeData {
        public int[] lightObjectID;
        public LightSetAttributeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            short version = 0;
            int lightCount = 0;
            switch (ele.majorVersion) {
                case 8:
                    lightCount = data.ReadI32();
                    lightObjectID = new int[lightCount];
                    for (int i = 0; i < lightCount; ++i)
                        lightObjectID[i] = data.ReadI32();
                    break;
                case 9:
                    version = data.ReadI16();
                    lightCount = data.ReadI32();
                    lightObjectID = new int[lightCount];
                    for (int i = 0; i < lightCount; ++i)
                        lightObjectID[i] = data.ReadI32();
                    break;
                case 10:
                    version = data.ReadU8();
                    lightCount = data.ReadI32();
                    lightObjectID = new int[lightCount];
                    for (int i = 0; i < lightCount; ++i)
                        lightObjectID[i] = data.ReadI32();
                    base.ReadBaseAttributeDataFields2(data);
                    break;
            }
        }
    }
}
