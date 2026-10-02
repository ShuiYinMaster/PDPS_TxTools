using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class BaseShapeLODData {
        public byte version;

        public BaseShapeLODData(Element ele) {
            var data = ele.dataStream;
            version = ele.majorVersion == 9 ? (byte)data.ReadI16() : data.ReadU8();
        }
    }
    
    public class VertexShapeLODData {
        public byte version;
        public ulong vertexBindings;

        public TopoMeshCompressedLODData topoMeshCompressedLODData;
        public TopoMeshTopologicallyCompressedLODData topoMeshTopologicallyCompressedLODData;
        
        public int bindingAttrubutes;
        public QuantizationParameters parameters;

        public int objectID = -1;
        
        public VertexShapeLODData(Element ele) {
            var data = ele.dataStream;
            if (ele.majorVersion > 8) {
                var baseShapeLODData = new BaseShapeLODData(ele);
                version = ele.majorVersion == 9 ? (byte)data.ReadI16() : data.ReadU8();
                ulong vertexBinding = data.ReadU64();

                if (ele.majorVersion > 9) {
                    var headerLen = data.ReadI32();
                    var typeID = new GUID(data);
                    var baseType = data.ReadU8();
                    objectID = data.ReadI32();
                }
                
                if(ele.objectTypeID == Tri_StripSetShapeLODData.typeID) {
                    topoMeshTopologicallyCompressedLODData = new TopoMeshTopologicallyCompressedLODData(ele);
                } else {
                    // Skip two unknown bytes
                    //data.ReadBytes(2);
                    topoMeshCompressedLODData = new TopoMeshCompressedLODData(ele);
                }
            }
            else {
                version = (byte)data.ReadI16();
                bindingAttrubutes = data.ReadI32();
                parameters = new QuantizationParameters(data);
            }
        }

        public List<float> GetVertices() {
            if(topoMeshCompressedLODData != null)
                return topoMeshCompressedLODData.GetVertices();
            
            var  repData = topoMeshTopologicallyCompressedLODData.repData;
            var topologicallyCompressedVertexRecords = repData.vertexRecords;
            var coordinateArray = topologicallyCompressedVertexRecords.compressedVertexCoordinateArray;
            return coordinateArray.vertexCoordinates;
        }

        public List<List<int>> GetIndices() {
            if(topoMeshCompressedLODData != null)
                return topoMeshCompressedLODData.GetIndices();
            return topoMeshTopologicallyCompressedLODData.GetIndices();
        }
        public List<float> GetNormals() {
            if(topoMeshCompressedLODData != null)
                return topoMeshCompressedLODData.GetNormals();
            return topoMeshTopologicallyCompressedLODData.GetNormals();
        }
    }

    public class TopoMeshLODData {
        public byte version;
        public uint vertexRecordsObjectId;

        public TopoMeshLODData(Element ele) {
            var data = ele.dataStream;
            if (ele.majorVersion > 8)
                version = data.ReadVersionNumber();
            vertexRecordsObjectId = data.ReadU32();
        }
        
    }

    public class TopoMeshCompressedLODData : TopoMeshLODData {
        public byte versionNumber;
        public TopoMeshCompressedRepDataV1 repDataV1;
        public TopoMeshCompressedRepDataV2 repDataV2;

        public TopoMeshCompressedLODData(Element ele) : base(ele) {
            var data = ele.dataStream;
            if (ele.majorVersion > 8)
                versionNumber = data.ReadVersionNumber();
            if (versionNumber == 1)
                repDataV1 = new TopoMeshCompressedRepDataV1(ele);
            else if (version == 2)
                repDataV2 = new TopoMeshCompressedRepDataV2(ele);
            else {
                throw new Exception("Found invalid version number: " + versionNumber);
            }
        }

        public List<float> GetVertices() {
            if (repDataV1 != null)
                return repDataV1.compressedVertexCoordinateArray.vertexCoordinates;
            if (repDataV2 != null) {
                return repDataV2.
                    topoMeshCompressedRepDataV1.compressedVertexCoordinateArray.vertexCoordinates;
            }
            return null;
        }
        
        public List<float> GetNormals() {
            if (repDataV1 != null)
                return repDataV1.compressedVertexNormalArray.normalCoordinates;
            if (repDataV2 != null) {
                return repDataV2.
                    topoMeshCompressedRepDataV1.compressedVertexNormalArray.normalCoordinates;
            }
            return null;
        }
        
        public List<List<int>> GetIndices() {
            throw new NotImplementedException("");
            return null;
        }
        
    }

    public class TopoMeshTopologicallyCompressedLODData : TopoMeshLODData {
        public byte versionNumber;
        public TopologicallyCompressedRepData repData;

        public TopoMeshTopologicallyCompressedLODData(Element ele) : base(ele) {
            var data = ele.dataStream;
            if (ele.majorVersion > 8)
                versionNumber = data.ReadVersionNumber();
            repData = new TopologicallyCompressedRepData(ele);
        }

        public List<float> GetVertices() {
            var topologicallyCompressedVertexRecords = repData.vertexRecords;
            var coordinateArray = topologicallyCompressedVertexRecords.compressedVertexCoordinateArray;
            return coordinateArray.vertexCoordinates;
        }
        public List<float> GetNormals() {
            var topologicallyCompressedVertexRecords = repData.vertexRecords;
            var coordinateArray = topologicallyCompressedVertexRecords.compressedVertexNormalArray;
            return coordinateArray.normalCoordinates;
        }
        public List<List<int>> GetIndices() {
            return repData.GetIndices();
        }
        
    }


    public static class VertexBinding {
        public static int VertexCoordinate(this ulong vertexBindings) {
            if ((vertexBindings & (1 << 0)) != 0) return 2;
            if ((vertexBindings & (1 << 1)) != 0) return 3;
            if ((vertexBindings & (1 << 2)) != 0) return 4;
            return 0;
        }

        public static int Normal(this ulong vertexBindings) {
            if ((vertexBindings & (1 << 3)) != 0) return 3;
            return 0;
        }

        public static int Color(this ulong vertexBindings) {
            if ((vertexBindings & (1 << 4)) != 0) return 3;
            if ((vertexBindings & (1 << 5)) != 0) return 4;
            return 0;
        }

        public static int VertexFlag(this ulong vertexBindings) {
            if ((vertexBindings & (1 << 6)) != 0) return 1;
            return 0;
        }

        public static int TextureCoordinate(this ulong vertexBindings, int index) {
            if ((vertexBindings & (1ul << (index * 4 + 8))) != 0) return 1;
            if ((vertexBindings & (1ul << (index * 4 + 9))) != 0) return 2;
            if ((vertexBindings & (1ul << (index * 4 + 10))) != 0) return 3;
            if ((vertexBindings & (1ul << (index * 4 + 11))) != 0) return 4;
            return 0;
        }

        public static int AuxiliaryVertexField(this ulong vertexBindings) {
            if ((vertexBindings & (1ul << 63)) != 0) return 1;
            return 0;
        }
    }
}