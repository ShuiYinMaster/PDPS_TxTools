using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class LSGNode {
        public LSGNode parent = null;
        public List<LSGNode> child = new List<LSGNode>();
        public List<Element> attributes = new List<Element>();
        public Dictionary<string, object> property = new Dictionary<string, object>();
        public int id;
        public object elementData;

        public LSGNode(LSG lsg, Element ele, LSGNode parentNode = null) {
            parent = parentNode;
            id = ele.objectID;
            elementData = ele.elementData;
            
            if(lsg.nodes.ContainsKey(id))
                return;
            lsg.nodes.Add(id, this);
            
            bool read = false;
            
            if (elementData is BaseNodeData) {
                var nodeData = elementData as BaseNodeData;
                foreach (var attr in nodeData.attributeObjectID) {
                    attributes.Add(ele.segment.file.elements[attr]);
                }
            }
            if (elementData is GroupNodeData) {
                read = true;
                var nodeData = elementData as GroupNodeData;
                foreach (var childID in nodeData.childNodeObjectID) {
                    child.Add(new LSGNode(lsg, ele.segment.file.elements[childID], this));
                }
            }

            if (elementData is InstanceNodeData) {
                var nodeData = elementData as InstanceNodeData;
                child.Add(new LSGNode(lsg, ele.segment.file.elements[nodeData.childNodeObjectID], this));
            }
            
            
            
            if (ele.segment.file.propertyTable.nodePropertyTable.TryGetValue(id, out var table)) {
                read = true;
                foreach (var prop in table.propertyAtomObjectID) {

                    if(!ele.segment.file.elements.ContainsKey(prop.Key) 
                        || ele.segment.file.elements[prop.Key].elementData == null)
                        continue;
                    if(!ele.segment.file.elements.ContainsKey(prop.Value) 
                        ||ele.segment.file.elements[prop.Value].elementData == null)
                        continue;
                    var key = ele.segment.file.elements[prop.Key].elementData.ToString();
                    var value = ele.segment.file.elements[prop.Value].elementData;

                    if (value is LateLoadedPropertyAtomData)
                        value = ele.segment.file.segments[(value as LateLoadedPropertyAtomData).segmeentID];

                    if (!property.ContainsKey(key))
                        property.Add(key, value);


                    if (value is PropertyProxyMetaData) {
                        
                        var proxy = value as PropertyProxyMetaData;
                        foreach (var p in proxy.property) {
                            var k = p.Key;
                            var t = p.Value.Item1;
                            var v = p.Value.Item2;
                            if(property.ContainsKey(k))
                                continue;
                            property.Add(k, v);
                        }
                    }
                }
            }

            //Console.WriteLine(read + " " + elementData.ToString());

        }
        
        
        
        public override string ToString() {
            return id + " " + elementData;
        }
    }
    
    public class LSG {
        public LSGNode root;
        public Dictionary<int,LSGNode> nodes = new Dictionary<int, LSGNode>();
        public TxKinSolver txKin = null;
        public LSG(JTFile jtFile) {
            var lsgSegment = jtFile.LSGSegment;
            root = new LSGNode(this, lsgSegment.elements.First());
            if (jtFile.txKinModelingBuffer != null)
                txKin = new TxKinSolver(jtFile.txKinModelingBuffer);
        }
    }
}
