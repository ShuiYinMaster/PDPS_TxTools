using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class InfiniteLightAttributeData : BaseLightData {
        DirF32 direction;
        public InfiniteLightAttributeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            short version = 0;
            switch (ele.majorVersion) {
                case 8:
                    direction = new DirF32(data);
                    version = data.ReadI16();
                    if(version == 1)
                        coordSystem = data.ReadI32();
                    break;
                case 9:
                    version = data.ReadI16();
                    direction = new DirF32(data);
                    if(version == 2) {
                        nonshadowAlphaFactor = data.ReadF32();
                        shadowAlphaFactor = data.ReadF32();
                    }
                    break;
                case 10:
                    version = data.ReadU8();
                    direction = new DirF32(data);
                    break;
            }
        }

    }
}
