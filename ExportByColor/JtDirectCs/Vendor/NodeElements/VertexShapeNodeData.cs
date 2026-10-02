using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class VertexShapeNodeData : BaseShapeNodeData {
        public ulong vertexBinding;
        public int normalBinding;
        public int textureCoordBinding;
        public int colorBinding;
        public QuantizationParameters quantizationParameters;
        public VertexShapeNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            short version = 0;

            switch(ele.majorVersion) {
                case 8:
                    normalBinding = data.ReadI32();
                    textureCoordBinding = data.ReadI32();
                    colorBinding = data.ReadI32();
                    quantizationParameters = new QuantizationParameters(data);
                    break;
                case 9:
                    
                    version = data.ReadI16();
                    vertexBinding = data.ReadU64();
                    quantizationParameters = new QuantizationParameters(data);
                    if(version == 1 && data.Length - data.Position >= 8)
                        vertexBinding = data.ReadU64();
                    break;
                case 10:
                    version = data.ReadU8();
                    vertexBinding = data.ReadU64();
                    break;
            }
        }
    }
}
