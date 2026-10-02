using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    /// <summary>
    /// Constructor WIP
    /// </summary>
    public class PMIManagerMetaData {
        public short version;
        public PMIEntities entities;
        public PMIAssociations associations;
        public PMIUserAttributes userAttributes;
        public PMIStringTable stringTable;
        public PMIModelViews modelViews;
        public GenericPMIEntities genericEntities;
        public PMICADTagData cadTagData;

        public PMIManagerMetaData(Element ele) { }

    }

    public class PMIEntities {
        public PMIDimensionEntities dimensionEntities;
        public PMINoteEntities noteEntities;
        public PMIDatumFeatureSymbolEntities datumFeatureSymbolEntities;
        public PMIDatumTargetEntities datumTargetEntities;
        public PMIFeatureControlFrameEntities featureControlFrameEntities;
        public PMILineWeldEntities lineWeldEntities;
        public PMISpotWeldEntities spotWeldEntities;
        public PMISurfaceFinishEntities surfaceFinishEntities;
        public PMIMeasurementPointEntities measurementPointEntities;
        public PMILocatorEntities locatorEntities;
        public PMIReferenceGeometryEntities referenceGeometryEntities;
        public PMIDesignGroupEntities designGroupEntities;
        public PMICoordinateSystemEntities coordinateSystemEntities;
    }

    public class PMIDimensionEntities {
        public int dimensionCount;
        public PMI2DData dimensions;
    }
    public class PMI2DData : PMIBaseData {
        public int textEntityCount;
        public TwoDTextData texts;
        public NonTextPolylineData nonTextPolylineData;
    }
    public class PMIBaseData {
        public int userLable;
        public byte twoDFrameFlag;
        public TwoDReferenceFrame referenceFrame;
        public int textHeight;
        public byte symbolValidFlag;
    }
    public class TwoDReferenceFrame {
        public CoordF32 origin;
        public CoordF32 x;
        public CoordF32 y;
    }
    public class TwoDTextData {
        public int stringId;
        public int font;
        public TextBox textBox;
        public TextPolylineData textPolylineData;
    }
    public struct TextBox {
        public float originX;
        public float originY;
        public float right;
        public float bottom;
        public float left;
        public float top;
        public TextBox(StreamReader data) {
            originX = data.ReadF32();
            originY = data.ReadF32();
            right = data.ReadF32();
            bottom = data.ReadF32();
            left = data.ReadF32();
            top = data.ReadF32();
        }
    }
    public struct TextPolylineData {
        public short[] polylineSegmentIndex;
        public List<float> polylineVertexCoords;
        public TextPolylineData(StreamReader data) {
            var count = data.ReadI32();
            polylineSegmentIndex = new short[count];
            for (int i = 0; i < count; ++i)
                polylineSegmentIndex[i] = data.ReadI16();
            polylineVertexCoords = data.ReadVecF32();
        }
    }
    public struct NonTextPolylineData {
        public short[] polylineSegmentIndex;
        public short[] polylineType;
        public List<float> polylineVertexCoords;
        public NonTextPolylineData(StreamReader data, byte version) {
            var count = data.ReadI32();
            polylineSegmentIndex = new short[count];
            for (int i = 0; i < count; ++i)
                polylineSegmentIndex[i] = data.ReadI16();

            if (version > 4) {
                count = data.ReadI32();
                polylineType = new short[count];
                for (int i = 0; i < count; ++i)
                    polylineType[i] = data.ReadI16();
            } else
                polylineType = null;

            polylineVertexCoords = data.ReadVecF32();
        }
    }

    public class PMI2DDataEntities {
        public List<PMI2DData> datas;
    }
    public class PMINoteEntities : PMI2DDataEntities {
        public List<uint> urlFlag;
    }
    public class PMIDatumFeatureSymbolEntities : PMI2DDataEntities { }
    public class PMIDatumTargetEntities : PMI2DDataEntities { }
    public class PMIFeatureControlFrameEntities : PMI2DDataEntities { };
    public class PMILineWeldEntities : PMI2DDataEntities { };
    public class PMI3DData : PMIBaseData {
        public int stringID;
        public short polylineDeimensionality;
        public int polylineSegmentIndexCount;
        public short polylineSegmentIndex;
        public List<float> polylineVertexCoords;
    }
    public class PMI3DDataEntities {
        public List<PMI3DData> datas;
        public List<CoordF32> pos;
        public List<DirF32> dir0;
        public List<DirF32> dir1;
        public List<DirF32> dir2;
    }
    public class PMISpotWeldEntities : PMI3DDataEntities {
        public List<CoordF32> weldPoints { get { return pos; } }
        public List<DirF32> approachDirection { get { return dir0; } }
        public List<DirF32> clampingDirection { get { return dir1; } }
        public List<DirF32> normalDirection { get { return dir2; } }
    }
    public class PMISurfaceFinishEntities : PMI2DDataEntities { }
    public class PMIMeasurementPointEntities : PMI3DDataEntities {
        public List<CoordF32> location { get { return pos; } }
        public List<DirF32> measurementDirection { get { return dir0; } }
        public List<DirF32> coordinateDirection { get { return dir1; } }
        public List<DirF32> normalDirection { get { return dir2; } }
    }
    public class PMILocatorEntities : PMI2DDataEntities { };
    public class PMIReferenceGeometryEntities {
        public List<PMI3DData> datas;
    };
    public class PMIDesignGroupEntities {
        public List<PMIDesignGroup> designGroups;
    }
    public class PMIDesignGroup{
        public int groupNameStringID;
        public List<DesignGroupAttribute> attributes;
    }
    public class DesignGroupAttribute {
        public int attributeType;
        public int val;
        public double val2;
        public int labelStringID;
        public int descriptionStringID;
    }
    public class PMICoordinateSystemEntities {
        public class CoordSys {
            public int nameStringID;
            public CoordF32 origin;
            public CoordF32 x;
            public CoordF32 y;
        }
        public List <CoordSys> coordSysList;
    }

    public class PMIAssociations {
        public class Association {
            public int sourceData;
            public int destinationData;
            public int reasonCode;
            public int sourceOwningEntityStringId;
            public int destinationOwningEntityStringId;
        }
        List <Association> associationsList;
    }
    public class PMIUserAttributes {
        public class UserAttribute {
            public int keyStringId;
            public int valueStringId;
        }
        public List<UserAttribute> attributes;
    }
    public class PMIStringTable {
        List<string> pmiString;
    }
    public class PMIModelViews {
        public class ModelView {
            public DirF32 eyeDirection;
            public float angle;
            public CoordF32 eyePosition;
            public CoordF32 targetPoint;
            public CoordF32 viewAngle;
            public float viewportDiameter;
            public int activeFlag;
            public int vewID;
            public int viewNameStringID;
        }
        public List<ModelView> views;
    }
    public class GenericPMIEntities {
        public class GenericEntity {
            PMI2DData data;
            List<PMIProperty> properties;
            int entityTypeNameStringId;
            int parentTypeNameStringId;
            short entityType;
            short parentType;
            short userFlag;
        }
        List<GenericEntity> entities;
    }
    public class PMIProperty {
        public PMIPropertyAtom key;
        public PMIPropertyAtom value;
    }
    public class PMIPropertyAtom {
        public string value;
        public uint hiddenFlag;
    }
    public class PMICADTagData {
        public List<int> cadTagIndex;
        public CompressedCADTagData tags;
    }
    public class CompressedCADTagData {
        public int dataLength;
        public int versionNumber;
        public int cadTagCount;
        public List<int> cadTagTypes;
        public List<int> cadTagType_1;
        public CompressedCADTagType_2Data type2;
    }
    public class CompressedCADTagType_2Data {
        List<int> first;
        List<int> second;
    }
}
