using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    /// <summary>
    /// Under construction
    /// </summary>
    public class JTB_RepData {
        public byte version;
        public TopologicalEntityCount topologicalEntityCounts;
        public GeometricEntityCount geometricEntityCounts;
        public JTB_RepData(Element ele) {
            var data = ele.dataStream;
            switch (ele.majorVersion) {
                case 8:
                case 9:
                    version = (byte)(ele.majorVersion == 8 ? data.ReadI32() : data.ReadI16());
                    var reserved = data.ReadU32();
                    topologicalEntityCounts = new TopologicalEntityCount(data);
                    geometricEntityCounts = new GeometricEntityCount(data);
                    var reserved2 = new CoordF64(data);
                    var reserved3 = data.ReadF64();
                    break;
                case 10:
                    version = data.ReadU8();
                    topologicalEntityCounts = new TopologicalEntityCount(data);
                    geometricEntityCounts = new GeometricEntityCount(data);
                    break;
            }


        }
    }

    public struct TopologicalEntityCount {
        public int regionCount;
        public int shellCount;
        public int faceCount;
        public int loopCount;
        public int coedgeCount;
        public int edgeCount;
        public int vertexCount;
        public TopologicalEntityCount(StreamReader data) {
            regionCount = data.ReadI32();
            shellCount = data.ReadI32();
            faceCount = data.ReadI32();
            loopCount = data.ReadI32();
            coedgeCount = data.ReadI32();
            edgeCount = data.ReadI32();
            vertexCount = data.ReadI32();
        }
    }
    public struct GeometricEntityCount {
        public int surfaceCount;
        public int PCSCurveCount;
        public int MCSCurveCount;
        public int pointCount;
        public GeometricEntityCount(StreamReader data) {
            surfaceCount = data.ReadI32();
            PCSCurveCount = data.ReadI32();
            MCSCurveCount = data.ReadI32();
            pointCount = data.ReadI32();
        }
    }
    public class TopologyData {
        public RegionsTopologyData regionsTopologyData;
        public ShellsTopologyData shellsTopologyData;
        public FacesTopologyData facesTopologyData;
        public LoopsTopologyData loopsTopologyData;
        public CoedgesTopologyData coedgesTopologyData;
        public EdgesTopologyData edgesTopologyData;
        public VerticesTopologyData verticesTopologyData;
        public TopologyData(Element ele, TopologicalEntityCount count) {
            var data = ele.dataStream;
            regionsTopologyData = new RegionsTopologyData(data);
            if (count.shellCount > 0)
                shellsTopologyData = new ShellsTopologyData(data);
            if (count.faceCount > 0)
                facesTopologyData = new FacesTopologyData(data);
            if (count.loopCount > 0)
                loopsTopologyData = new LoopsTopologyData(data);
            if (count.coedgeCount > 0)
                coedgesTopologyData = new CoedgesTopologyData(data);
            if (count.edgeCount > 0)
                edgesTopologyData = new EdgesTopologyData(data);
            if (count.vertexCount > 0)
                verticesTopologyData = new VerticesTopologyData(data);
        }
    }
    public class RegionsTopologyData {
        public List<int> firstShellIndices;
        public List<int> lastShellIndices;
        public List<int> regionTags;
        public RegionsTopologyData(StreamReader data) {
            firstShellIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            lastShellIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            regionTags = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
        }
    }
    public class ShellsTopologyData {
        public List<int> firstFaceIndices;
        public List<int> lastFaceIndices;
        public List<int> shellTags;
        public List<int> shellAntiHoleFlags;
        public ShellsTopologyData(StreamReader data) {
            firstFaceIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            lastFaceIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            shellTags = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            shellAntiHoleFlags = Int32CDP.ReadVecI32(data, PredictorType.PredXor1);
        }
    }
    public class FacesTopologyData {
        public List<int> firstTrimLoopIndices;
        public List<int> lastTrimLoopIndices;
        public List<int> surfaceIndices;
        public List<int> faceTags;
        public List<int> faceReverseNormalFlags;
        public FacesTopologyData(StreamReader data) {
            firstTrimLoopIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            lastTrimLoopIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            surfaceIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            faceTags = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            faceReverseNormalFlags = Int32CDP.ReadVecI32(data, PredictorType.PredXor1);
        }
    }
    public class LoopsTopologyData {
        public List<int> firstCoedgeIndices;
        public List<int> lastCoedgeIndices;
        public List<int> loopTags;
        public List<int> antiHoleFlags;
        public LoopsTopologyData(StreamReader data) {
            firstCoedgeIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            lastCoedgeIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            loopTags = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            antiHoleFlags = Int32CDP.ReadVecI32(data, PredictorType.PredXor1);
        }
    }
    public class CoedgesTopologyData {
        public List<int> edgeIndices;
        public List<int> PCSCurveIndices;
        public List<int> coEdgeTags;
        public List<int> MCSCurveReversedFlags;
        public CoedgesTopologyData(StreamReader data) {
            edgeIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            PCSCurveIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            coEdgeTags = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            MCSCurveReversedFlags = Int32CDP.ReadVecI32(data, PredictorType.PredXor1);
        }
    }
    public class EdgesTopologyData {
        public List<int> startVertexIndices;
        public List<int> endVertexIndices;
        public List<int> MCSCurveIndices;
        public List<int> edgeTags;
        public EdgesTopologyData(StreamReader data) {
            startVertexIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            endVertexIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            MCSCurveIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            edgeTags = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
        }
    }
    public class VerticesTopologyData {
        public List<int> pointIndices;
        public List<int> vertexTags;
        public VerticesTopologyData(StreamReader data) {
            pointIndices = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            vertexTags = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
        }
    }


    public class CompressedEntityListForNonTrivialVectorData {
        public List<int> entitiesOfKnotTypeExistFlags;
        public CompressedEntityListForNonTrivialVectorData(StreamReader data) {
            entitiesOfKnotTypeExistFlags = data.ReadVecI32();
            List<int>[] entityIndexCodes = new List<int>[4];
            for (int i = 0; i < 4; i++) {
                if (entitiesOfKnotTypeExistFlags[i] != 1) 
                    continue;
                entityIndexCodes[i] = Int32CDP.ReadVecI32(data, PredictorType.PredStride1);
            }
        }
    }
    public class CompressedControlPointWeightsData {
        public int weightsCount;
        List<int> weightIndices;
        List<int> weightValues;
        public CompressedControlPointWeightsData(StreamReader data) {
            weightsCount = data.ReadI32();
            weightIndices = Int32CDP.ReadVecI32(data, PredictorType.PredStride1);
            //weightValues = Int32CDP(data, PredictorType.PredLag1);
        }
    }
    public class SurfacesGeometricData {

    }
}
