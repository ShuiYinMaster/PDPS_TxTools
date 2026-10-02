using System;
using System.Collections.Generic;
using System.IO;

namespace DLAT.JTReader {
    public class UniformQuantizerData {
        public float min;
        public float max;
        public int numberOfBits;

        public UniformQuantizerData(StreamReader data) {
            min = data.ReadF32();
            max = data.ReadF32();
            numberOfBits = data.ReadU8();
            if (numberOfBits < 0 || numberOfBits > 32)
                throw new Exception("unexpected number of bits");
        }
    }
    public class PointQuantizerData {
        public UniformQuantizerData xUniformQuantizerData;
        public UniformQuantizerData yUniformQuantizerData;
        public UniformQuantizerData zUniformQuantizerData;

        public PointQuantizerData(StreamReader data) {
            xUniformQuantizerData = new UniformQuantizerData(data);
            yUniformQuantizerData = new UniformQuantizerData(data);
            zUniformQuantizerData = new UniformQuantizerData(data);
        }

        public float[] xRange {
            get { return new float[] { xUniformQuantizerData.min, xUniformQuantizerData.max }; }
        }

        public float[] yRange {
            get { return new float[] { yUniformQuantizerData.min, yUniformQuantizerData.max }; }
        }

        public float[] zRange {
            get { return new float[] { zUniformQuantizerData.min, zUniformQuantizerData.max }; }
        }

        public int numberOfBits {
            get {
                var numberOfBitsX = xUniformQuantizerData.numberOfBits;
                var numberOfBitsY = yUniformQuantizerData.numberOfBits;
                var numberOfBitsZ = zUniformQuantizerData.numberOfBits;

                if ((numberOfBitsX != numberOfBitsY) || (numberOfBitsX != numberOfBitsZ)) {
                    throw new Exception("ERROR: Number of quantized bits differs!");
                }

                return numberOfBitsX;
            }
        }
    }
    public class ColorQuantizerData {
        public UniformQuantizerData rUniformQuantizerData;
        public UniformQuantizerData gUniformQuantizerData;
        public UniformQuantizerData bUniformQuantizerData;
        public UniformQuantizerData aUniformQuantizerData;

        public ColorQuantizerData(StreamReader data) {
            int hsvFlag = data.ReadU8();
            int numberOfHueBits;
            int numberOfSaturationBits;
            int numberOfValueBits;
            int numberOfAlphaBits;
            if (hsvFlag == 1) {
                numberOfHueBits = data.ReadU8();
                numberOfSaturationBits = data.ReadU8();
                numberOfValueBits = data.ReadU8();
                numberOfAlphaBits = data.ReadU8();
            }
            else {
                rUniformQuantizerData = new UniformQuantizerData(data);
                gUniformQuantizerData = new UniformQuantizerData(data);
                bUniformQuantizerData = new UniformQuantizerData(data);
                aUniformQuantizerData = new UniformQuantizerData(data);
            }
        }

        public float[] rRange {
            get { return new float[] { rUniformQuantizerData.min, rUniformQuantizerData.max }; }
        }

        public float[] gRange {
            get { return new float[] { gUniformQuantizerData.min, gUniformQuantizerData.max }; }
        }

        public float[] bRange {
            get { return new float[] { bUniformQuantizerData.min, bUniformQuantizerData.max }; }
        }

        public float[] aRange {
            get { return new float[] { aUniformQuantizerData.min, aUniformQuantizerData.max }; }
        }

        public int numberOfBits {
            get {
                int numberOfBitsX = rUniformQuantizerData.numberOfBits;
                int numberOfBitsY = gUniformQuantizerData.numberOfBits;
                int numberOfBitsZ = bUniformQuantizerData.numberOfBits;

                if ((numberOfBitsX != numberOfBitsY) || (numberOfBitsX != numberOfBitsZ)) {
                    throw new Exception("ERROR: Number of quantized bits differs!");
                }

                return numberOfBitsX;
            }
        }
    }
    public class TextureQuantizerData {
        public UniformQuantizerData[] uniformQuantizerDatas;
        public TextureQuantizerData(StreamReader data, int numberComponents) {
            UniformQuantizerData[] uniformQuantizerDatas = new UniformQuantizerData[numberComponents];

            for(int i = 0; i < numberComponents; i++) {
                uniformQuantizerDatas[i] = new UniformQuantizerData(data);
            }
        }
        public float[] uRange {
            get { return new float[] { uniformQuantizerDatas[0].min, uniformQuantizerDatas[0].max }; }
        }
        public float[] vRange {
            get { return new float[] { uniformQuantizerDatas[1].min, uniformQuantizerDatas[1].max }; }
        }
    }
        public class QuantizedVertexCoordArray {
        public PointQuantizerData pointQuantizerData;
        public int vertexCount;
        public List<int> xVertexCoordinates;
        public List<int> yVertexCoordinates;
        public List<int> zVertexCoordinates;
        public QuantizedVertexCoordArray(StreamReader data) {
            pointQuantizerData = new PointQuantizerData(data);
            vertexCount = data.ReadI32();
            xVertexCoordinates = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
            yVertexCoordinates = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
            zVertexCoordinates = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);

        }
        public List<float> GetVertices() {
            var xVertices = CODEC.Dequantize(xVertexCoordinates,pointQuantizerData.xRange,pointQuantizerData.numberOfBits);
            var yVertices = CODEC.Dequantize(yVertexCoordinates,pointQuantizerData.yRange,pointQuantizerData.numberOfBits);
            var zVertices = CODEC.Dequantize(zVertexCoordinates,pointQuantizerData.zRange,pointQuantizerData.numberOfBits);
            var vertices = new List<float>();
            for (int i = 0; i < xVertexCoordinates.Count; i++) {
                vertices.Add(xVertices[i]);
                vertices.Add(yVertices[i]);
                vertices.Add(zVertices[i]);
            }

            return vertices;
        }
    }
    public class QuantizedVertexNormalArray {
        public int numberOfBits;
        public int normalCount;
        public List<int> sextantCodes;
        public List<int> octantCodes;
        public List<int> thetaCodes;
        public List<int> psiCodes;
        public List<float> normals;
        public QuantizedVertexNormalArray(StreamReader data) {
            numberOfBits = data.ReadU8();
            normalCount = data.ReadI32();
            sextantCodes = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
            octantCodes = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
            thetaCodes = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
            psiCodes = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
            normals = new List<float>();
            var deeringCodec = new CODEC.DeeringNormalCodec(numberOfBits);
            for (int i = 0; i < psiCodes.Count; i++) {
                var normal = deeringCodec.convertCodeToVec(sextantCodes[i], octantCodes[i], thetaCodes[i], psiCodes[i]);
                normals.Add((float)normal.x);
                normals.Add((float)normal.y);
                normals.Add((float)normal.z);
            }
        }
    }
    public class QuantizedVertexColorArray {
        public ColorQuantizerData colorQuantizerData;
        public List<int> hueRedCodes;
        public List<int> satGreenCodes;
        public List<int> valBlueCodes;
        public List<int> alphaCodes;
        public List<int> colorCodes;
        public QuantizedVertexColorArray(StreamReader data) {
            colorQuantizerData = new ColorQuantizerData(data);
            int numberOfBits = data.ReadU8();
            int numberOfColorFloats = data.ReadU8();
            int componentArraysFlag = data.ReadU8();
            if ((componentArraysFlag != 0) && (componentArraysFlag != 1))
                throw new Exception("Found invalid component arrays flag: " + componentArraysFlag);

            if(componentArraysFlag == 0) {
                colorCodes = Int32CDP.ReadVecU32(data, PredictorType.PredNull);
            } else {
                hueRedCodes = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
                satGreenCodes = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
                valBlueCodes = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
                alphaCodes = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
            }
        }
        public List<double> GetColors() {
            var r = CODEC.Dequantize(hueRedCodes, colorQuantizerData.rRange, colorQuantizerData.numberOfBits);
            var g = CODEC.Dequantize(satGreenCodes, colorQuantizerData.gRange, colorQuantizerData.numberOfBits);
            var b = CODEC.Dequantize(valBlueCodes, colorQuantizerData.bRange, colorQuantizerData.numberOfBits);
            var a = CODEC.Dequantize(alphaCodes, colorQuantizerData.aRange, colorQuantizerData.numberOfBits);
            List<double> colors = new List<double>();
            for (int i = 0; i < r.Count; ++i) {
                colors.Add(r[i]);
                colors.Add(g[i]);
                colors.Add(b[i]);
                colors.Add(a[i]);
            }
            return colors;
        }
    }
    public class QuantizedVertexTextureCoordArray {
        public TextureQuantizerData textureQuantizerData;
        public List<int> uTextureCoordCodes;
        public List<int> vTextureCoordCodes;
        public QuantizedVertexTextureCoordArray(StreamReader data) {
            textureQuantizerData = new TextureQuantizerData(data, 2);
            int suggestedNumberOfBits = data.ReadU8();
            if (suggestedNumberOfBits < 0 || suggestedNumberOfBits > 24)
                throw new Exception("Found invalid suggested number of bits: " + suggestedNumberOfBits);
            uTextureCoordCodes = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
            vTextureCoordCodes = Int32CDP.ReadVecU32(data, PredictorType.PredLag1);
        }
    }
}