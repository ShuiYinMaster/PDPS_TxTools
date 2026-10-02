using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class MaterialAttributeData : BaseAttributeData {
        public ushort dataFlags;
        public RGBA ambientColor;
        public RGBA diffuseColor;
        public RGBA specularColor;
        public RGBA emissionColor;
        public float shiniess;
        public float reflectivity;
        public float bumpiness;
        public MaterialAttributeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            short version = 0;
            switch (ele.majorVersion) {
                case 8:
                    dataFlags = data.ReadU16();
                    if ((dataFlags & 0x2) == 0)
                        ambientColor = new RGBA(data);
                    else
                        ambientColor = new RGBA(data.ReadF32());
                    diffuseColor = new RGBA(data);
                    if ((dataFlags & 0x8) == 0)
                        specularColor = new RGBA(data);
                    else
                        specularColor = new RGBA(data.ReadF32());
                    if ((dataFlags & 0x4) == 0)
                        emissionColor = new RGBA(data);
                    else
                        emissionColor = new RGBA(data.ReadF32());
                    shiniess = data.ReadF32();
                    break;
                case 9:
                    version = data.ReadI16();
                    dataFlags = data.ReadU16();
                    ambientColor = version == 2 ? new RGBA(data.ReadF32()):new RGBA(data);
                    diffuseColor = version == 2 ? new RGBA(data.ReadF32()):new RGBA(data);
                    specularColor = version == 2 ? new RGBA(data.ReadF32()):new RGBA(data);
                    emissionColor = version == 2 ? new RGBA(data.ReadF32()):new RGBA(data);
                    shiniess = data.ReadF32();
                    if(version == 2)
                        reflectivity = data.ReadF32();
                    break;
                case 10:
                    version = data.ReadU8();
                    dataFlags = data.ReadU16();
                    ambientColor = new RGBA(data);
                    diffuseColor = new RGBA(data);
                    specularColor = new RGBA(data);
                    emissionColor = new RGBA(data);
                    shiniess = data.ReadF32();
                    reflectivity = data.ReadF32();
                    bumpiness = data.ReadF32();
                    base.ReadBaseAttributeDataFields2(data);
                    break;
            }
        }
    }
}
