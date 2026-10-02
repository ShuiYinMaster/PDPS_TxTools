using System.Collections.Generic;
using System.IO;

namespace DLAT.JTReader {
    public class TopoMeshCompressedRepDataV1 {
        public List<int> faceGroupListIndices;
        public List<int> primitiveListIndices;
        public List<int> vertexListIndices;
        public CompressedVertexCoordinateArray compressedVertexCoordinateArray;
        public CompressedVertexNormalArray compressedVertexNormalArray;
        public CompressedVertexColorArray compressedVertexColorArray;
        public CompressedVertexTextureCoordinateArray[] compressedVertexTextureCoordinateArrays;
        public CompressedVertexFlagArray compressedVertexFlagArray;

        public TopoMeshCompressedRepDataV1(Element ele) {
            var data = ele.dataStream;
            var isPolyLineShape = ObjectTypeIdentifiers.types[ele.objectTypeID] ==
                                  typeof(PolyLineSetShapeLODData);
            int numberOfFaceGroupListIndices = -1;
            if (isPolyLineShape || ele.minorVersion != 9) 
                numberOfFaceGroupListIndices = data.ReadI32();

            int numberOfPrimitiveListIndices = data.ReadI32();
            int numberOfVertexListIndices = data.ReadI32();

            faceGroupListIndices = ele.majorVersion < 10?
                    (isPolyLineShape?
                        Int32CDP.ReadVecI32(data, PredictorType.PredNull):
                        null):
                    Int32CDP.ReadVecI32(data, PredictorType.PredLag1);

            primitiveListIndices = Int32CDP.ReadVecI32(data, ele.majorVersion < 10?
                PredictorType.PredNull:
                PredictorType.PredLag1);
            vertexListIndices = Int32CDP.ReadVecI32(data, ele.majorVersion < 10?
                PredictorType.PredNull:
                PredictorType.PredLag1);

            int fgpvListIndicesHash = data.ReadI32();
            ulong vertexBindings = data.ReadU64();

            var quantizationParameters = new QuantizationParameters(data);

            int numberOfVertexRecords = data.ReadI32();
            if (numberOfVertexRecords == 0)
                return;

            if (ele.majorVersion < 10) {
                int numberOfUniqueVertexCoordinates = data.ReadI32();
            }

            List<int> uniqueVertexCoordinateLengthList = Int32CDP.ReadVecI32(data, PredictorType.PredNull);

            int uniqueVertexListMapHash = data.ReadI32();

            compressedVertexCoordinateArray = null;
            if ((vertexBindings & 0x07) != 0) { // Check for bits 1-3
                compressedVertexCoordinateArray = new CompressedVertexCoordinateArray(data);
            }

            compressedVertexNormalArray = null;
            if ((vertexBindings & 0x08) != 0) { // Check for bit 4
                compressedVertexNormalArray = new CompressedVertexNormalArray(data);
            }

            compressedVertexColorArray = null;
            if ((vertexBindings & 0x30) != 0) { // Check for bits 5-6
                compressedVertexColorArray = new CompressedVertexColorArray(data);
            }

            compressedVertexTextureCoordinateArrays = new CompressedVertexTextureCoordinateArray[8];

            for (var i = 0; i < 8; ++i) {
                if (vertexBindings.TextureCoordinate(i) != 0)
                    compressedVertexTextureCoordinateArrays[i] = new CompressedVertexTextureCoordinateArray(data);
            }
            //
            // if ((vertexBindings & 0xf00) != 0) { // Check for bits 9-12
            //     compressedVertexTextureCoordinateArrays[0] = new CompressedVertexTextureCoordinateArray(data);
            // }
            //
            // if ((vertexBindings & 0xf000) != 0) { // Check for bits 13-16
            //     compressedVertexTextureCoordinateArrays[1] = new CompressedVertexTextureCoordinateArray(data);
            // }
            //
            // if ((vertexBindings & 0xf0000) != 0) { // Check for bits 17-20
            //     compressedVertexTextureCoordinateArrays[2] = new CompressedVertexTextureCoordinateArray(data);
            // }
            //
            // if ((vertexBindings & 0xf00000) != 0) { // Check for bits 21-24
            //     compressedVertexTextureCoordinateArrays[3] = new CompressedVertexTextureCoordinateArray(data);
            // }
            //
            // if ((vertexBindings & 0xf000000) != 0) { // Check for bits 25-28
            //     compressedVertexTextureCoordinateArrays[4] = new CompressedVertexTextureCoordinateArray(data);
            // }
            //
            // if ((vertexBindings & 0xf0000000) != 0) { // Check for bits 29-32
            //     compressedVertexTextureCoordinateArrays[5] = new CompressedVertexTextureCoordinateArray(data);
            // }
            //
            // if ((vertexBindings & 0xf00000000ul) != 0) { // Check for bits 33-36
            //     compressedVertexTextureCoordinateArrays[6] = new CompressedVertexTextureCoordinateArray(data);
            // }
            //
            // if ((vertexBindings & 0xf000000000ul) != 0) { // Check for bits 37-40
            //     compressedVertexTextureCoordinateArrays[7] = new CompressedVertexTextureCoordinateArray(data);
            // }

            compressedVertexFlagArray = null;
            if ((vertexBindings & 0x8000000000000000ul) != 0) { // Check for bit 64
                compressedVertexFlagArray = new CompressedVertexFlagArray(data);
            }
        }
    }
}