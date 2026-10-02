using DLAT.JTReader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class BaseLightData {
        public short version;
        public RGBA ambientColor;
        public RGBA diffuseColor;
        public RGBA specularColor;
        public float brightness;
        public int coordSystem;
        public byte shadowCasterFlag;
        public float shadowOpacity;

        //shadow opacity in 9.0
        public float nonshadowAlphaFactor;
        public float shadowAlphaFactor;

        public BaseLightData(Element ele) {
            var data = ele.dataStream;
            var version = 0;
            switch (ele.majorVersion) {
                case 8:
                    ele.objectID = data.ReadI32();
                    ambientColor = new RGBA(data);
                    diffuseColor = new RGBA(data);
                    specularColor = new RGBA(data);
                    brightness = data.ReadF32();
                    break;
                case 9:
                    version = data.ReadI16();
                    ambientColor = new RGBA(data);
                    diffuseColor = new RGBA(data);
                    specularColor = new RGBA(data);
                    brightness = data.ReadF32();
                    coordSystem = data.ReadI32();
                    shadowCasterFlag = data.ReadU8();
                    shadowOpacity = data.ReadF32();
                    break;
                case 10:
                    version = data.ReadU8();
                    //Logical Element Header Compressed (compressed element header) ?
                    ambientColor = new RGBA(data);
                    diffuseColor = new RGBA(data);
                    specularColor = new RGBA(data);
                    brightness = data.ReadF32();
                    coordSystem = data.ReadI32();
                    shadowCasterFlag = data.ReadU8();
                    shadowOpacity = data.ReadF32();
                    nonshadowAlphaFactor = data.ReadF32();
                    shadowAlphaFactor = data.ReadF32();
                    break;
            }
        }

    }
}
