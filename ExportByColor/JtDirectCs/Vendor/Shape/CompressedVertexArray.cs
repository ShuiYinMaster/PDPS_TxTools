using System;
using System.Collections.Generic;
using System.IO;

namespace DLAT.JTReader {
    public class CompressedVertexCoordinateArray {
        public List<float> vertexCoordinates;

        public CompressedVertexCoordinateArray(StreamReader data) {
            if (data.jtFile.majorVersion == 9)
                ReadV9(data);
            else
                ReadV10(data);

        }

        void ReadV9(StreamReader data) {
            int uniqueVertexCount = data.ReadI32();
            int numberComponents = data.ReadU8();
            PointQuantizerData pointQuantizerData = new PointQuantizerData(data);

            List<List<int>> vertexCoordExponentLists = new List<List<int>>();
            List<List<int>> vertexCoordMantissaeLists = new List<List<int>>();
            List<List<int>> vertexCoordCodeLists = new List<List<int>>();
            vertexCoordinates = new List<float>();
            int numberOfBits = pointQuantizerData.numberOfBits;
            if (numberOfBits == 0) {
                for (int i = 0; i < numberComponents; i++) {
                    List<int> exponents = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
                    List<int> mantissae = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
                    List<int> codeData = new List<int>();
                    for (int j = 0; j < exponents.Count; j++)
                        codeData.Add((exponents[j] << 23) | mantissae[j]);
                    vertexCoordExponentLists.Add(exponents);
                    vertexCoordMantissaeLists.Add(mantissae);
                    vertexCoordCodeLists.Add(codeData);
                }

                List<int> xCodeData = vertexCoordCodeLists[0];
                List<int> yCodeData = vertexCoordCodeLists[1];
                List<int> zCodeData = vertexCoordCodeLists[2];
                for (int i = 0; i < xCodeData.Count; i++) {
                    vertexCoordinates.Add(BitConverter.ToSingle(BitConverter.GetBytes(xCodeData[i]), 0));
                    vertexCoordinates.Add(BitConverter.ToSingle(BitConverter.GetBytes(yCodeData[i]), 0));
                    vertexCoordinates.Add(BitConverter.ToSingle(BitConverter.GetBytes(zCodeData[i]), 0));
                }
            }
            else if (numberOfBits > 0) {
                vertexCoordCodeLists.Add(Int32CDP.ReadVecU32(data, PredictorType.PredLag1));
                vertexCoordCodeLists.Add(Int32CDP.ReadVecU32(data, PredictorType.PredLag1));
                vertexCoordCodeLists.Add(Int32CDP.ReadVecU32(data, PredictorType.PredLag1));
                
                List<float> xValues =
                    CODEC.Dequantize(vertexCoordCodeLists[0], pointQuantizerData.xRange, numberOfBits);
                List<float> yValues =
                    CODEC.Dequantize(vertexCoordCodeLists[1], pointQuantizerData.yRange, numberOfBits);
                List<float> zValues =
                    CODEC.Dequantize(vertexCoordCodeLists[2], pointQuantizerData.zRange, numberOfBits);
                for (int i = 0; i < xValues.Count; i++) {
                    vertexCoordinates.Add(xValues[i]);
                    vertexCoordinates.Add(yValues[i]);
                    vertexCoordinates.Add(zValues[i]);
                }
            }
            else {
                throw new Exception("ERROR: Negative number of quantized bits: " + numberOfBits);
            }
            
            long readHash = data.ReadU32();
        }

        void ReadV10(StreamReader data) {
            int uniqueVertexCount = data.ReadI32();
            int numberComponents = data.ReadU8();
            PointQuantizerData pointQuantizerData = new PointQuantizerData(data);

            var xValues = new List<float>();
            var yValues = new List<float>();
            var zValues = new List<float>();
            vertexCoordinates = new List<float>();
            int numberOfBits = pointQuantizerData.numberOfBits;
            if (numberOfBits == 0) {
                var binaryVertexCoords = new List<int>[numberComponents];
                for (int i = 0; i < numberComponents; i++) {
                    binaryVertexCoords[i] = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
                }
                //convert binaryVertexCoords to x,y,z values
                xValues = CODEC.IntArrayToFloatArray(binaryVertexCoords[0]);
                yValues = CODEC.IntArrayToFloatArray(binaryVertexCoords[1]);
                zValues = CODEC.IntArrayToFloatArray(binaryVertexCoords[2]);
            }
            else if (numberOfBits > 0) {
                var vertexCoordCodeLists = new List<int>[3];
                vertexCoordCodeLists[0] = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
                vertexCoordCodeLists[1] = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
                vertexCoordCodeLists[2] = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
                
                xValues =
                    CODEC.Dequantize(vertexCoordCodeLists[0], pointQuantizerData.xRange, numberOfBits);
                yValues =
                    CODEC.Dequantize(vertexCoordCodeLists[1], pointQuantizerData.yRange, numberOfBits);
                zValues =
                    CODEC.Dequantize(vertexCoordCodeLists[2], pointQuantizerData.zRange, numberOfBits);
            }
            else {
                throw new Exception("ERROR: Negative number of quantized bits: " + numberOfBits);
            }
            for (int i = 0; i < xValues.Count; i++) {
                vertexCoordinates.Add(xValues[i]);
                vertexCoordinates.Add(yValues[i]);
                vertexCoordinates.Add(zValues[i]);
            }
            
            long readHash = data.ReadU32();
        }
    }
    public class CompressedVertexNormalArray {
        public List<float> normalCoordinates;

        public CompressedVertexNormalArray(StreamReader data) {
            if(data.jtFile.majorVersion == 9)
                ReadV9(data);
            else {
                ReadV10(data);
            }
        }

        public void ReadV9(StreamReader data) {
            var normalCount = data.ReadI32();
            var numberComponents = data.ReadU8();
            var quantizationBits = data.ReadU8();

            var vertexNormalExponentsLists = new List<List<int>>();
            var vertexNormalMantissaeLists = new List<List<int>>();
            var sextantCodes = new List<int>();
            var octantCodes = new List<int>();
            var thetaCodes = new List<int>();
            var psiCodes = new List<int>();
            normalCoordinates = new List<float>();
            var normalVectorLists = new List<List<int>>();

            if (quantizationBits == 0) {
                for (int i = 0; i < numberComponents; i++) {
                    List<int> exponents = Int32CDP.ReadVecI32(data, PredictorType.PredNull);
                    List<int> mantissae = Int32CDP.ReadVecI32(data, PredictorType.PredNull);

                    List<int> normalVectorData = new List<int>();
                    for (int j = 0; j < exponents.Count; j++) {
                        normalVectorData.Add((exponents[j] << 23) | mantissae[j]);
                    }

                    normalVectorLists.Add(normalVectorData);
                    vertexNormalExponentsLists.Add(exponents);
                    vertexNormalMantissaeLists.Add(mantissae);
                }

                List<int> xCodeData = normalVectorLists[0];
                List<int> yCodeData = normalVectorLists[1];
                List<int> zCodeData = normalVectorLists[2];
                for (int i = 0; i < xCodeData.Count; i++) {
                    normalCoordinates.Add(BitConverter.ToSingle(BitConverter.GetBytes(xCodeData[i]), 0));
                    normalCoordinates.Add(BitConverter.ToSingle(BitConverter.GetBytes(yCodeData[i]), 0));
                    normalCoordinates.Add(BitConverter.ToSingle(BitConverter.GetBytes(zCodeData[i]), 0));
                }
            }
            else if (quantizationBits > 0) {
                sextantCodes = Int32CDP.ReadVecI32(data, PredictorType.PredNull);
                octantCodes  = Int32CDP.ReadVecI32(data, PredictorType.PredNull);
                thetaCodes   = Int32CDP.ReadVecI32(data, PredictorType.PredNull);
                psiCodes     = Int32CDP.ReadVecI32(data, PredictorType.PredNull);

                var deeringCodec = new CODEC.DeeringNormalCodec(quantizationBits);
                for (int i = 0; i < psiCodes.Count; i++) {
                    var normal =
                        deeringCodec.convertCodeToVec(sextantCodes[i], octantCodes[i], thetaCodes[i], psiCodes[i]);
                    normalCoordinates.Add((float)normal.x);
                    normalCoordinates.Add((float)normal.y);
                    normalCoordinates.Add((float)normal.z);
                }
            }
            else {
                throw new Exception("ERROR: Negative number of quantized bits: " + quantizationBits);
            }

            var hash = data.ReadU32();
        }

        public void ReadV10(StreamReader data) {
            var normalCount = data.ReadI32();
            var numberComponents = data.ReadU8();
            var quantizationBits = data.ReadU8();
            
            var xValues = new List<float>();
            var yValues = new List<float>();
            var zValues = new List<float>();
            if (quantizationBits == 0) {
                var binaryVertexNormals = new List<int>[numberComponents];
                for (int i = 0; i < numberComponents; ++i)
                    binaryVertexNormals[i] = Int32CDP.ReadVecU32(data, PredictorType.PredNull);
                xValues = CODEC.IntArrayToFloatArray(binaryVertexNormals[0]);
                yValues = CODEC.IntArrayToFloatArray(binaryVertexNormals[1]);
                zValues = CODEC.IntArrayToFloatArray(binaryVertexNormals[2]);
            }
            else {
                var deeringNormalCodes = Int32CDP.ReadVecU32(data, PredictorType.PredNull);
                var deeringCodec = new CODEC.DeeringNormalCodec(quantizationBits);
                for (int i = 0; i < deeringNormalCodes.Count; i++) {
                    var (sextant, octant, theta, psi) =
                        deeringCodec.unpackCode((uint)deeringNormalCodes[i], quantizationBits);
                    var normal = deeringCodec.convertCodeToVec(sextant, octant, theta, psi);
                    xValues.Add((float)normal.x);
                    yValues.Add((float)normal.y);
                    zValues.Add((float)normal.z);
                }
            }

            normalCoordinates = new List<float>();
            for (int i = 0; i < xValues.Count; i++) {
                normalCoordinates.Add(xValues[i]);
                normalCoordinates.Add(yValues[i]);
                normalCoordinates.Add(zValues[i]);
            }
            
            var hash = data.ReadU32();
        }
        
    }
    public class CompressedVertexColorArray {
        public List<double> colorValues;
        public int Components { get; }

        public CompressedVertexColorArray(StreamReader data) {
            int count = data.ReadI32();
            Components = data.ReadU8();
            int quantizationBits = data.ReadU8();
            if (count < 0 || (Components != 3 && Components != 4) || quantizationBits > 8)
                throw new InvalidDataException("Invalid JT vertex colour array header");

            bool legacy = data.jtFile.majorVersion < 10;
            if (quantizationBits == 0) {
                var channels = new List<int>[Components];
                for (int channel = 0; channel < Components; channel++) {
                    if (legacy) {
                        var exponents = Int32CDP.ReadVecU32(data, PredictorType.PredNull);
                        var mantissae = Int32CDP.ReadVecU32(data, PredictorType.PredNull);
                        CheckCount(count, exponents, mantissae);
                        for (int i = 0; i < count; i++) exponents[i] = (exponents[i] << 23) | mantissae[i];
                        channels[channel] = exponents;
                        data.ReadU32();
                    } else {
                        channels[channel] = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
                        CheckCount(count, channels[channel]);
                    }
                }
                colorValues = new List<double>(checked(count * Components));
                for (int i = 0; i < count; i++)
                    foreach (var channel in channels)
                        colorValues.Add(BitConverter.ToSingle(BitConverter.GetBytes(channel[i]), 0));
            } else {
                var quantizer = new ColorQuantizerData(data);
                var quantizers = new[] { quantizer.rUniformQuantizerData, quantizer.gUniformQuantizerData,
                    quantizer.bUniformQuantizerData, quantizer.aUniformQuantizerData };
                int[] bits = quantizer.HsvBits ?? Array.ConvertAll(quantizers, q => q.numberOfBits);
                foreach (int width in bits)
                    if (width > 8) throw new NotSupportedException("Unsupported JT vertex colour channel width: " + width);

                List<int>[] channels = null;
                List<int> packed = null;
                if (legacy) {
                    channels = new List<int>[4];
                    for (int channel = 0; channel < channels.Length; channel++)
                        channels[channel] = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
                    for (int channel = 0; channel < Components; channel++)
                        if (bits[channel] != 0) CheckCount(count, channels[channel]);
                } else {
                    packed = Int32CDP.ReadVecU32(data, PredictorType.PredNull);
                    CheckCount(count, packed);
                }

                colorValues = new List<double>(checked(count * Components));
                var rgba = new double[4];
                for (int i = 0; i < count; i++) {
                    uint code = legacy ? 0 : unchecked((uint)packed[i]);
                    for (int channel = 3; channel >= 0; channel--) {
                        int mask = (1 << bits[channel]) - 1;
                        uint value = legacy
                            ? (mask == 0 || channel >= Components ? 0 : unchecked((uint)channels[channel][i]))
                            : code & (uint)mask;
                        code >>= bits[channel];
                        if (value > mask) throw new InvalidDataException("Invalid JT vertex colour code");
                        double min = quantizer.HsvBits == null ? quantizers[channel].min : 0;
                        double max = quantizer.HsvBits == null ? quantizers[channel].max : channel == 0 ? 6 : 1;
                        rgba[channel] = mask == 0 ? min : min + (max - min) * value / mask;
                    }
                    if (quantizer.HsvBits != null) HsvToRgb(rgba);
                    for (int channel = 0; channel < Components; channel++) colorValues.Add(rgba[channel]);
                }
            }
            data.ReadU32(); // Colour hash.
            foreach (double value in colorValues)
                if (double.IsNaN(value) || value < 0 || value > 1)
                    throw new InvalidDataException("Invalid JT vertex colour component");
        }

        private static void CheckCount(int count, params List<int>[] channels) {
            foreach (var channel in channels)
                if (channel.Count != count) throw new InvalidDataException("JT vertex colour count mismatch");
        }

        private static void HsvToRgb(double[] color) {
            double hue = color[0] % 6;
            double chroma = color[2] * color[1];
            double second = chroma * (1 - Math.Abs(hue % 2 - 1));
            double low = color[2] - chroma;
            switch ((int)hue) {
                case 0: color[0] = chroma + low; color[1] = second + low; color[2] = low; break;
                case 1: color[0] = second + low; color[1] = chroma + low; color[2] = low; break;
                case 2: color[0] = low; color[1] = chroma + low; color[2] = second + low; break;
                case 3: color[0] = low; color[1] = second + low; color[2] = chroma + low; break;
                case 4: color[0] = second + low; color[1] = low; color[2] = chroma + low; break;
                default: color[0] = chroma + low; color[1] = low; color[2] = second + low; break;
            }
        }
    }
    public class CompressedVertexTextureCoordinateArray {
        public List<double> textureCoordinates;

        public CompressedVertexTextureCoordinateArray(StreamReader data) {
            int textureCoordCount = data.ReadI32();
		    int numberComponents  = data.ReadU8();
		    int quantizationBits  = data.ReadU8();

		    List<List<int>> vertexTextureCoordExponentLists = new List<List<int>>();
		    List<List<int>> vertexTextureCoordMantissaeLists = new List<List<int>>();
		    List<List<int>> vertexTextureCodeLists = new List<List<int>>();
		    List<List<int>> textureCoordCodesLists = new List<List<int>>();
		    TextureQuantizerData textureQuantizerData = null;
		    List<Double> textureCoordinates = new List<Double>();

		    if(quantizationBits == 0){
			    for(int i = 0; i < numberComponents; i++){
				    List<int> exponents = Int32CDP.ReadVecI32(data, PredictorType.PredNull);
				    List<int> mantissae = Int32CDP.ReadVecI32(data, PredictorType.PredNull);

				    List<int> codeData = new List<int>();
				    for(int j = 0; j < exponents.Count; j++){
					    codeData.Add((exponents[j] << 23) | mantissae[j]);
				    }

				    vertexTextureCoordExponentLists.Add(exponents);
				    vertexTextureCoordMantissaeLists.Add(mantissae);
				    vertexTextureCodeLists.Add(codeData);
			    }

			    List<int> uCodeData = vertexTextureCodeLists[0];
			    List<int> vCodeData = vertexTextureCodeLists[1];
			    for(int i = 0; i < uCodeData.Count; i++) {
                    textureCoordinates.Add(BitConverter.ToSingle(BitConverter.GetBytes(uCodeData[i]), 0));
                    textureCoordinates.Add(BitConverter.ToSingle(BitConverter.GetBytes(vCodeData[i]), 0));
                }

		    } else {
			    textureQuantizerData = new TextureQuantizerData(data, numberComponents);

			    for(int i = 0; i < numberComponents; i++){
				    textureCoordCodesLists.Add(Int32CDP.ReadVecU32(data, PredictorType.PredLag1));
			    }

			    List<float> uValues = CODEC.Dequantize(textureCoordCodesLists[0], textureQuantizerData.uRange, quantizationBits);
			    List<float> vValues = CODEC.Dequantize(textureCoordCodesLists[1], textureQuantizerData.vRange, quantizationBits);
			    for(int i = 0; i < uValues.Count; i++){
				    textureCoordinates.Add(uValues[i]);
				    textureCoordinates.Add(vValues[i]);
			    }

			    // Ignore the hash value
			    var hash = data.ReadU32();

		    }
        }
    }
    public class CompressedVertexFlagArray {
        public List<int> vertexFlags;

        public CompressedVertexFlagArray(StreamReader data) {
            var count = data.ReadI32();
            vertexFlags = Int32CDP.ReadVecU32(data, PredictorType.PredNull);
        }
    }
    
}