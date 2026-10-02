using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class GroupNodeData : BaseNodeData {
        public int[] childNodeObjectID;
        public GroupNodeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            if (ele.majorVersion == 9)
                data.ReadI16(); //versionNumber
            else if(ele.majorVersion == 10)
                data.ReadU8();
            var childCount = data.ReadI32();
            childNodeObjectID = new int[childCount];
            for (int i = 0; i < childCount; ++i) {
                childNodeObjectID[i] = data.ReadI32();
                //Debug.Log("Child:" + childNodeObjectID[i], 2);
            }
        }
    }
}
