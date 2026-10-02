using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class PrimitiveSetShapeNodeData : BaseShapeNodeData {
        public ulong vertexBindings;
        public int textureCoordBinding;
        public int corlorBinding;
        public PrimitiveSetQuantizationParameters perimitiveSetQuantizationParameters;
        public int textureCoordGenType;
        public PrimitiveSetShapeNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;

            short version = 0;
            switch (ele.majorVersion) {
                case 8:
                    textureCoordBinding = data.ReadI32();
                    corlorBinding = data.ReadI32();
                    perimitiveSetQuantizationParameters = new PrimitiveSetQuantizationParameters(data);
                    version = data.ReadI16();
                    if(version == 1)
                        textureCoordGenType = data.ReadI32();
                    break;
                case 9:
                    version = data.ReadI16();
                    textureCoordBinding = data.ReadI32();
                    corlorBinding = data.ReadI32();
                    textureCoordGenType = data.ReadI32();
                    version = data.ReadI16();
                    perimitiveSetQuantizationParameters = new PrimitiveSetQuantizationParameters(data);
                    break;
                case 10:
                    version = data.ReadU8();
                    vertexBindings = data.ReadU64();
                    textureCoordGenType = data.ReadI32();
                    version = data.ReadU8();
                    perimitiveSetQuantizationParameters = new PrimitiveSetQuantizationParameters(data);
                    break;
            }
        }
    }
}
