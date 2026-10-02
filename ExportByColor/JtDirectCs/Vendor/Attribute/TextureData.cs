using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class TextureData {
        public int textureType;
        public TextureEnviroment enviroment;
        public TextureCoordGenerationParameters coordGenerationParameters;
        public int textureChannel;
        public int texCoordChannel;
        public uint emptyField;
        public uint inlineImageStoregeFlag;
        public int imageCount;
        public List<InlineTextureImageData> inlineTextureImageData;
        public List<string> externalStorageName;
        public int vers;

        public TextureData(Element ele, int vers) {
            this.vers = vers;
            if(ele.majorVersion == 8 && vers == 1) {
                ReadMajorVersion8Vers_1(ele);
                return;
            }
            var data = ele.dataStream;

            if(ele.majorVersion == 9 && vers > 1) {
                var stub = new TextureData(ele, vers - 1);
            }

            textureType = data.ReadI32();
            enviroment = new TextureEnviroment(ele, vers);
            coordGenerationParameters = new TextureCoordGenerationParameters(data);
            textureChannel = data.ReadI32();
            if(ele.majorVersion == 10)
                texCoordChannel = data.ReadI32();
            emptyField = data.ReadU32();
            inlineImageStoregeFlag = ele.majorVersion > 8 ? data.ReadU8() : (byte)data.ReadU32();
            imageCount = data.ReadI32();
            inlineTextureImageData = new List<InlineTextureImageData>();
            externalStorageName = new List<string>();
            for (int i = 0; i < imageCount; i++) {
                if (inlineImageStoregeFlag == 1)
                    inlineTextureImageData.Add(new InlineTextureImageData(ele, vers));
                else
                    externalStorageName.Add(data.ReadMbString());
            }
        }

        private void ReadMajorVersion8Vers_1(Element ele) {
            inlineTextureImageData = new List<InlineTextureImageData> {
                new InlineTextureImageData(ele, 1) 
            };
            enviroment = new TextureEnviroment(ele, 1);
        }
    }

    public class TextureEnviroment {
        public int borderMode;
        public int mipmapMagnificationFilter;
        public int mipmapMinificationFilter;
        public int S_DimenWrapMode;
        public int T_DimenWrapMode;
        public int R_DimenWrapMode;
        public int blenderType;
        public int internalCompressionLevel;
        public RGBA blendColor;
        public RGBA borderColor;
        public Mx4F32 textureTransform;
        public TextureEnviroment(Element ele, int vers) {
            var data = ele.dataStream;
            if (ele.majorVersion == 8 && vers == 1)
                ReadMajorVersion8Vers_1(data);
            else
                ReadStandard(data);
        }
        private void ReadStandard(StreamReader data) {
            borderMode = data.ReadI32();
            mipmapMagnificationFilter = data.ReadI32();
            mipmapMinificationFilter = data.ReadI32();
            S_DimenWrapMode = data.ReadI32();
            T_DimenWrapMode = data.ReadI32();
            R_DimenWrapMode = data.ReadI32();
            blenderType = data.ReadI32();
            internalCompressionLevel = data.ReadI32();
            blendColor = new RGBA(data);
            borderColor = new RGBA(data);
            textureTransform = new Mx4F32(data);
        }

        private void ReadMajorVersion8Vers_1(StreamReader data) {
            mipmapMagnificationFilter = data.ReadI32();
            mipmapMinificationFilter = data.ReadI32();
            S_DimenWrapMode = data.ReadI32();
            T_DimenWrapMode = data.ReadI32();
            var textureFunctionData = data.ReadI32();
            blendColor = new RGBA(data);
            textureTransform = new Mx4F32(data);
        }
    }
    public class TextureCoordGenerationParameters {
        public int[] texCoordGenMode;
        public PlaneF32[] texCoordReferencePlane;
        public TextureCoordGenerationParameters(StreamReader data) {
            texCoordGenMode = new int[4];
            texCoordReferencePlane = new PlaneF32[4];
            for (int i = 0; i < 4; ++i)
                texCoordGenMode[i] = data.ReadI32();
            for (int i = 0; i < 4; ++i)
                texCoordReferencePlane[i] = new PlaneF32(data);
        }
    }
    public class InlineTextureImageData {
        public ImageFormatDescription imageFormatDescription;
        public List<byte[]> mipmaps;
        public InlineTextureImageData(Element ele, int vers) {
            if(ele.majorVersion == 8 && vers == 1) {
                ReadMajor8Vers_1(ele);
                return;
            }
            var data = ele.dataStream;
            imageFormatDescription = new ImageFormatDescription(ele, vers);
            var totalImageDataSize = data.ReadI32();
            mipmaps = new List<byte[]>();
            var totalRead = 0;
            for (int i = 0; i < imageFormatDescription.mipmapsCount; i++) {
                var mimapImageByteCount = data.ReadI32();
                var mipmapImageTexelData = data.ReadBytes(mimapImageByteCount);
                mipmaps.Add(mipmapImageTexelData);
                totalRead += mimapImageByteCount;
            }
        }
        private void ReadMajor8Vers_1(Element ele) {
            var data = ele.dataStream;
            var numberOfBytes = data.ReadI32();
            if (numberOfBytes == 0)
                return;
            imageFormatDescription = new ImageFormatDescription(ele, 1);
            mipmaps = new List<byte[]> {
                data.ReadBytes(numberOfBytes)
            };
        }
    }
    public class ImageFormatDescription {
        public uint pixelFormat;
        public uint pixelDataType;
        public short dimensionality;
        public short rowAlignment;
        public short width;
        public short height;
        public short depth;
        public short numberBorderTexels;
        public uint sharedImageFlags;
        public short mipmapsCount;
        public ImageFormatDescription(Element ele, int vers) {
            var data = ele.dataStream;
            if (ele.majorVersion == 8 && vers == 1) {
                ReadMajorVersion8Vers_1(data);
                return;
            }
            pixelFormat = data.ReadU32();
            pixelDataType = data.ReadU32();
            dimensionality = data.ReadI16();
            rowAlignment = data.ReadI16();
            width = data.ReadI16();
            height = data.ReadI16();
            depth = data.ReadI16();
            numberBorderTexels = data.ReadI16();
            sharedImageFlags = data.ReadU32();
            mipmapsCount = data.ReadI16();
        }

        private void ReadMajorVersion8Vers_1(StreamReader data) {
            pixelFormat = data.ReadU32();
            pixelDataType = data.ReadU32();
            dimensionality = data.ReadI16();
            width = (short)data.ReadI32();
            height = (short)data.ReadI32();
            var mipmapsFlag = data.ReadU32();
            sharedImageFlags = data.ReadU32();
        }
    }
}
