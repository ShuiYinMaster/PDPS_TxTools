using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class ShaderEffectsAttributeData : BaseAttributeData {
        public byte version;
        public uint enableFlag;
        public float envMapReflectivity;
        public int bumpMapTextureChannel;
        public float bumpinessFactor;
        public uint bumpMapNormalSpace;
        public uint phongShadingFlag;
        public uint reservedField;

        public ShaderEffectsAttributeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            version = data.ReadVersionNumber();
            if (version != 1)
                return;
            enableFlag = data.ReadU32();
            envMapReflectivity = data.ReadF32();
            bumpMapTextureChannel = data.ReadI32();
            bumpinessFactor = data.ReadF32();
            bumpMapNormalSpace = data.ReadU32();
            phongShadingFlag = data.ReadU32();
            reservedField = data.ReadU32();
        }
        
    }
}
