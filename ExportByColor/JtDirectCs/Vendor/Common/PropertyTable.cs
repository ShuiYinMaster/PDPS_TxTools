using DLAT.JTReader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StreamReader = DLAT.JTReader.StreamReader;

namespace JTReader {
    public class PropertyTable {
        public short versionNumber;
        public Dictionary<int, NodePropertyTable> nodePropertyTable;
        public PropertyTable(StreamReader data) {
            versionNumber = data.ReadI16();
            var nodePropertyTableCount = data.ReadI32();
            nodePropertyTable = new Dictionary<int, NodePropertyTable>();
            for (int i = 0; i < nodePropertyTableCount; ++i) {
                var nodeObjectId = data.ReadI32();
                var nodeTable = new NodePropertyTable(data);
                nodePropertyTable.Add(nodeObjectId, nodeTable);
            }
        }
    }
    public class NodePropertyTable {
        public Dictionary<int, int> propertyAtomObjectID;
        public NodePropertyTable(StreamReader data) {
            propertyAtomObjectID = new Dictionary<int, int>();
            for (; ; ) {
                var key = data.ReadI32();
                if (key == 0)
                    return;
                var value = data.ReadI32();
                propertyAtomObjectID.Add(key, value);
            }
        }
    }
}
