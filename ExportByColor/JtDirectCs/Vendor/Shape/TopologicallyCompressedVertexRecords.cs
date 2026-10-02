using System.IO;

namespace DLAT.JTReader {
    public class TopologicallyCompressedVertexRecords {
        public CompressedVertexCoordinateArray compressedVertexCoordinateArray;
        public CompressedVertexNormalArray compressedVertexNormalArray;
        public CompressedVertexColorArray compressedVertexColorArray;

        public TopologicallyCompressedVertexRecords(StreamReader data) {
            ulong vertexBindings = data.ReadU64();
            var quantizationParameters = new QuantizationParameters(data);
            var numberOfTopologicalVertices = data.ReadI32();

            if (numberOfTopologicalVertices <= 0)
                return;

            var numberOfVertexAttributes = data.ReadI32();
            
            //CompressedVertexCoordinateArray compressedVertexCoordinateArray = null;
            if (vertexBindings.VertexCoordinate() != 0) { // Check for bits 1-3
                compressedVertexCoordinateArray = new CompressedVertexCoordinateArray(data);
            }

            //CompressedVertexNormalArray compressedVertexNormalArray = null;
            if ((vertexBindings.Normal()) != 0) { // Check for bit 4
                compressedVertexNormalArray = new CompressedVertexNormalArray(data);
            }

            //CompressedVertexColorArray compressedVertexColorArray = null;
            if ((vertexBindings.Color()) != 0) { // Check for bits 5-6
                compressedVertexColorArray = new CompressedVertexColorArray(data);
            }
            
            CompressedVertexFlagArray compressedVertexFlagArray = null;
            if ((vertexBindings & 0x40) != 0) { // Check for bit 7
                compressedVertexFlagArray = new CompressedVertexFlagArray(data);
            }
            
            CompressedVertexTextureCoordinateArray[] compressedVertexTextureCoordinateArrays =
                new CompressedVertexTextureCoordinateArray[8];
            for (int i = 0; i < 8; ++i)
                if (vertexBindings.TextureCoordinate(i) != 0)
                    compressedVertexTextureCoordinateArrays[i] = new CompressedVertexTextureCoordinateArray(data);

        }
    }
}