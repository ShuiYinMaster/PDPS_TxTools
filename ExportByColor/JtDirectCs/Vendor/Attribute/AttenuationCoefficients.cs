using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class AttenuationCoefficients {
        public float constantAttenuation;
        public float linearAttenuation;
        public float quadraticAttenuation;
        public AttenuationCoefficients(StreamReader data) {
            constantAttenuation  = data.ReadF32();
            linearAttenuation    = data.ReadF32();
            quadraticAttenuation = data.ReadF32();
        }

    }
}
