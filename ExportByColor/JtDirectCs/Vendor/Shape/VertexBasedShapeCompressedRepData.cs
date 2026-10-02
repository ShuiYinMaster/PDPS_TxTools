using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace DLAT.JTReader {
    public class VertexBasedShapeCompressedRepData {
        public const byte BINDING_NONE = 0;
        /** Binding: Per vertex */
        public const byte BINDING_PER_VERTEX = 1;
        /** Binding: Per facet */
        public const byte BINDING_PER_FACET = 2;
        /** Binding: Per primitive */
        public const byte BINDING_PER_PRIMITIVE = 3;
        public short versionNumber;
        public byte normalBinding;
        public byte textureCoordBinding;
        public byte colorBinding;
        public QuantizationParameters parameters;

        public List<int> indices;
        public LosslessCompressedRawVertexData losslessCompressedRawVertex;
        public LossyQuantizedRawVertexData lossyQuantizedRawVertex;
        
        public VertexBasedShapeCompressedRepData(StreamReader data) {
            versionNumber = data.ReadI16();
            normalBinding = data.ReadU8();
            if (normalBinding > 3)
                throw new Exception("Found invalid normal binding: " + normalBinding);
            textureCoordBinding = data.ReadU8();
            if (textureCoordBinding > 3)
                throw new Exception("Found invalid texture binding: " + textureCoordBinding);
            colorBinding = data.ReadU8();
            if (colorBinding > 3)
                throw new Exception("Found invalid color binding: " + colorBinding);
            parameters = new QuantizationParameters(data);
            indices = Int32CDP.ReadVecI32(data, PredictorType.PredStride1);
            if (parameters.bitsPerVertex == 0)
                losslessCompressedRawVertex = new LosslessCompressedRawVertexData(data, textureCoordBinding, colorBinding, normalBinding);
            else
                lossyQuantizedRawVertex = new LossyQuantizedRawVertexData(data, textureCoordBinding, colorBinding, normalBinding);
        }

        public List<float> GetVertices() {
            if (losslessCompressedRawVertex != null)
                return losslessCompressedRawVertex.vertices;
            else
                return lossyQuantizedRawVertex.GetVertices();
        }

        public List<float> GetNormals() {
            if (losslessCompressedRawVertex != null)
                return losslessCompressedRawVertex.normals;
            else
                return lossyQuantizedRawVertex.GetVertices();
        }
        public List<List<int>> GetIndices() {
            var res = new List<List<int>>();
            var tri = new List<int>();
            var normals = new List<int>();
            for (var i = 1; i < indices.Count; ++i) {
                for (var j = indices[i - 1]; j < indices[i] - 2; ++j) {
                    var si = ((j - indices[i - 1]) % 2 == 0 ? new int[3] { 0, 1, 2 } : new int[3] { 1, 0, 2 }) ;
                    tri.AddRange(new [] { j + si[0], j + si[1], j + si[2] });
                }
            }
            res.Add(tri);
            res.Add(tri);
            return res;
        }
        public List<float> GetColors() {
            if (losslessCompressedRawVertex != null)
                return losslessCompressedRawVertex.colors;
            else
                return lossyQuantizedRawVertex.GetColors();
        }

    }
    public class LosslessCompressedRawVertexData {
        public List<float> textureCoordinates;
        public List<float> colors;
        public List<float> normals;
        public List<float> vertices;
        public LosslessCompressedRawVertexData(StreamReader data,int textureCoordBinding, int colorBinding, int normalBinding) {
            var uncompressedDataSize = data.ReadI32();
            var compressedDataSize = data.ReadI32();
            float[] rawVertexData = null;

            //uncompressed raw data
            if(compressedDataSize < 0) {
                compressedDataSize *= -1;
                rawVertexData = new float[compressedDataSize / 4];
                for (var i = 0; i < rawVertexData.Length; i++) {
                    rawVertexData[i] = data.ReadF32();
                }
                //zlib compressed raw data
            } else if(compressedDataSize > 0) {
                var zlibHeader = data.ReadBytes(2);
                var compressedBytes = new MemoryStream(data.ReadBytes(compressedDataSize - 2));
                var uncompressedStream = new StreamReader(CODEC.DecompressZLIB(compressedBytes), data.jtFile);
                if (uncompressedStream.Length != uncompressedDataSize) {
                    throw new Exception("ZLIB decompression seems to be failed! Expected length: " + uncompressedDataSize + " -> resulting length: " + uncompressedStream.Length);
                }

                rawVertexData = new float[uncompressedStream.Length / 4];
                for (var i = 0; i < rawVertexData.Length; i++) {
                    rawVertexData[i] = uncompressedStream.ReadF32();
                }
            } else {
                throw new Exception("Found invalid compressed data size: " + compressedDataSize);
            }

            // Create derived lists
            textureCoordinates = new List<float>();
            colors = new List<float>();
            normals = new List<float>();
            vertices = new List<float>();

            var readTextureCoordinate = textureCoordBinding == VertexBasedShapeCompressedRepData.BINDING_PER_VERTEX;
            var readColor = colorBinding == VertexBasedShapeCompressedRepData.BINDING_PER_VERTEX;
            var readNormal = normalBinding == VertexBasedShapeCompressedRepData.BINDING_PER_VERTEX;

            for (int i = 0; i < rawVertexData.Length;) {
                if (readTextureCoordinate) {
                    // Read U, V
                    textureCoordinates.Add(rawVertexData[i++]);
                    textureCoordinates.Add(rawVertexData[i++]);
                }

                if (readColor) {
                    // Read R, G, B
                    colors.Add(rawVertexData[i++]);
                    colors.Add(rawVertexData[i++]);
                    colors.Add(rawVertexData[i++]);
                }

                if (readNormal) {
                    // Read NX, NY, NZ
                    normals.Add(rawVertexData[i++]);
                    normals.Add(rawVertexData[i++]);
                    normals.Add(rawVertexData[i++]);
                }

                // Read X, Y, Z
                vertices.Add(rawVertexData[i++]);
                vertices.Add(rawVertexData[i++]);
                vertices.Add(rawVertexData[i++]);
            }
        }
    }
    public class LossyQuantizedRawVertexData {
        public QuantizedVertexCoordArray quantizedVertexCoordArray;
        public QuantizedVertexNormalArray quantizedNormalArray;
        public QuantizedVertexTextureCoordArray quantizedTextureCoordArray;
        public QuantizedVertexColorArray quantizedVertexColorArray;
        public List<int> vertexDataIndices;
        public LossyQuantizedRawVertexData(StreamReader data, int textureCoordBinding,int colorBinding,int normalBinding) {
            quantizedVertexCoordArray = new QuantizedVertexCoordArray(data);
            if(normalBinding !=0)
                quantizedNormalArray = new QuantizedVertexNormalArray(data);
            if(textureCoordBinding != 0)
                quantizedTextureCoordArray = new QuantizedVertexTextureCoordArray(data);
            if(colorBinding != 0)
                quantizedVertexColorArray = new QuantizedVertexColorArray(data);
            vertexDataIndices = Int32CDP.ReadVecU32(data, PredictorType.PredStripIndex);  
        }
        public List<float> GetVertices() {
            var unsortedVertices = quantizedVertexCoordArray.GetVertices();
            var sortedVertices = new List<float>();
            foreach (int vertexIndex in vertexDataIndices) {
                sortedVertices.Add(unsortedVertices[vertexIndex * 3]);
                sortedVertices.Add(unsortedVertices[(vertexIndex * 3) + 1]);
                sortedVertices.Add(unsortedVertices[(vertexIndex * 3) + 2]);
            }
            return sortedVertices;
        }
        public List<float> GetNormals() {
            var unsortedNotmals = quantizedNormalArray.normals;
            var sortedNormals = new List<float>();
            foreach (int vertexIndex in vertexDataIndices) {
                sortedNormals.Add(unsortedNotmals[vertexIndex * 3]);
                sortedNormals.Add(unsortedNotmals[(vertexIndex * 3) + 1]);
                sortedNormals.Add(unsortedNotmals[(vertexIndex * 3) + 2]);
            }
            return sortedNormals;
        }
        public List<float> GetColors() {
            List<float> res = new List<float>();
            foreach (var v in quantizedVertexColorArray.GetColors())
                res.Add((float)v);
            return res;
        }
    }
}