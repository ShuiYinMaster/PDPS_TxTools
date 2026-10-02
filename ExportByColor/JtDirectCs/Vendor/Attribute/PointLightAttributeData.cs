using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class PointLightAttributeData : BaseLightData {
        public HCoordF32 position;
        public AttenuationCoefficients attenuationCoefficients;
        public float spreadAngle;
        public DirF32 spotDirection;
        public int spotIntensity;

        public int coordSystem;

        public PointLightAttributeData(Element ele) : base(ele) { 
            var data = ele.dataStream;
            short version = 0;
            switch (ele.majorVersion) {
                case 8:
                    position = new HCoordF32(data);
                    attenuationCoefficients = new AttenuationCoefficients(data);
                    spreadAngle = data.ReadF32();
                    spotDirection = new DirF32(data);
                    spotIntensity = data.ReadI32();
                    version = data.ReadI16();
                    if (version == 1)
                        coordSystem = data.ReadI32();
                    break;
                case 9:
                    version = data.ReadI16();
                    position = new HCoordF32(data);
                    attenuationCoefficients = new AttenuationCoefficients(data);
                    spreadAngle = data.ReadF32();
                    spotDirection = new DirF32(data);
                    spotIntensity = data.ReadI32();
                    if(version == 2) {
                        nonshadowAlphaFactor = data.ReadF32();
                        shadowAlphaFactor = data.ReadF32();
                    }
                    break;
                case 10:
                    version = data.ReadU8();
                    position = new HCoordF32(data);
                    attenuationCoefficients = new AttenuationCoefficients(data);
                    spreadAngle = data.ReadF32();
                    spotDirection = new DirF32(data);
                    spotIntensity = data.ReadI32();
                    break;
            }
        }

    }
}
