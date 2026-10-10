using System;
using System.Collections.Generic;
using System.IO;

namespace TxTools.ExportByColor
{
    public static partial class CgrWriter
    {
        // This is tessellated topology, not a reconstruction of analytic CAD surfaces.
        // The A3 shell and face/edge records follow the user-accepted fresh_planar_box.
        private sealed class FeatureEdge95
        {
            internal int A,B;
            internal readonly List<int> Corners=new List<int>();
        }
        private sealed class FeatureBoundary95
        {
            internal int A,B,Face,TopologyA,TopologyB;
            internal int Other=-1;
            internal List<int> Polyline;
        }

        private static void WeldFeatureVertices(ref List<float[]> vertices,ref List<Face> faces)
        {
            var output=new List<float[]>();var result=new List<Face>(faces.Count);
            var maps=new Dictionary<int,Dictionary<Tuple<float,float,float>,int>>();
            foreach(var source in faces)
            {
                Dictionary<Tuple<float,float,float>,int> map;
                if(!maps.TryGetValue(source.Surface,out map))
                {map=new Dictionary<Tuple<float,float,float>,int>();maps.Add(source.Surface,map);}
                var face=source;face.Idx=new int[3];
                for(int k=0;k<3;k++)
                {
                    var v=vertices[source.Idx[k]];var key=Tuple.Create(v[0],v[1],v[2]);int index;
                    if(!map.TryGetValue(key,out index)){index=output.Count;map.Add(key,index);output.Add(v);}
                    face.Idx[k]=index;
                }
                result.Add(face);
            }
            vertices=output;faces=result;
        }

        private static void BuildLineFace95(List<float[]> vertices,List<Face> faces,string path,int faceLimit,Action<string> progress,bool smoothDomains)
        {
            var groups=new Dictionary<Tuple<int,int>,List<Face>>();var order=new List<List<Face>>();
            foreach(var f in faces)
            {
                var key=Tuple.Create(f.Surface,f.R|(f.G<<8)|(f.B<<16)|((f.Opacity??255)<<24));List<Face> group;
                if(!groups.TryGetValue(key,out group)){group=new List<Face>();groups.Add(key,group);order.Add(group);}
                group.Add(f);
            }
            var children=new List<byte[]>();long changed=0,domains=0,edgeCount=0;int vertexCount=0;
            int encodedVertices=0;
            uint nextId=FirstId;
            using(var picks=new MemoryStream())
            {
                Action<List<float[]>,List<float[]>,List<int[]>,List<int[]>,Face> write=(vs,ns,tris,topology,color)=>
                {
                    CompactFeatureVertices95(ref vs,ref ns,ref tris);
                    encodedVertices+=vs.Count;
                    int nf,ne;byte[] payload,pick;
                    BuildFeaturePayload95(vs,ns,tris,topology,color,nextId,smoothDomains,out payload,out pick,out nf,out ne);
                    nextId=checked(nextId+(uint)nf+(uint)ne);
                    children.Add(WrapFeature95(vs,payload,checked((uint)picks.Position)));
                    picks.Write(pick,0,pick.Length);domains+=nf;edgeCount+=ne;
                };
                // Reuse the established hard-edge normal splitting. Topological indices are
                // carried separately, so a shading split never becomes a missing adjacency.
                foreach(var group in order)
                    CompactGroup95(vertices,group,children,picks,ref changed,ref vertexCount,write,faceLimit,
                        smoothDomains?MaxFeatureVertsPerChunk:MaxVertsPerChunk);
                byte[] scene;
                using(var ms=new MemoryStream())using(var w=new BinaryWriter(ms))
                {
                    w.Write(ScenePrefix,0,41);Compact(w,checked((uint)children.Count));
                    foreach(var child in children){w.Write((byte)0);w.Write(child);}
                    scene=ms.ToArray();
                }
                SetRootSphere(scene,vertices);
                WriteContainer(scene,picks.ToArray(),path,vertices);
            }
            if(progress!=null)progress("[CGR 线面] "+(smoothDomains?"连续面压缩":"逐平面兼容")+"；"+children.Count+" 块，"+domains+" 个面域，"+edgeCount+" 条边，"+encodedVertices+" 顶点；"+new FileInfo(path).Length+" 字节");
        }

        private static bool SameFeaturePlane95(List<float[]> vs,List<int[]> triangles,double[][] normals,int seed,int candidate)
        {
            // Compare every candidate with the seed plane, not just its neighbor: this
            // prevents a chain of small turns from turning a curved surface into one plane.
            if(Dot95(normals[seed],normals[candidate])<1-1e-12)return false;
            var origin=vs[triangles[seed][0]];double span=1;
            foreach(int i in triangles[seed])for(int k=0;k<3;k++)span=Math.Max(span,Math.Abs((double)vs[i][k]-origin[k]));
            double tolerance=Math.Max(1e-7,span*1e-8);
            foreach(int i in triangles[candidate])
            {
                double distance=0;for(int k=0;k<3;k++)distance+=((double)vs[i][k]-origin[k])*normals[seed][k];
                if(Math.Abs(distance)>tolerance)return false;
            }
            return true;
        }

        private static void BuildFeaturePayload95(List<float[]> vs,List<float[]> ns,List<int[]> triangles,List<int[]> topology,
            Face color,uint firstId,bool smoothDomains,out byte[] payload,out byte[] pick,out int faceCount,out int edgeCount)
        {
            if(vs.Count>65536||triangles.Count!=topology.Count||ns.Count!=vs.Count)
                throw new InvalidDataException("Invalid line/face block");
            var edgeMap=new Dictionary<long,FeatureEdge95>(EdgeKeyComparer.Instance);var edges=new List<FeatureEdge95>();
            var normals=new double[triangles.Count][];
            for(int f=0;f<triangles.Count;f++)
            {
                var t=triangles[f];normals[f]=Normal95(vs[t[0]],vs[t[1]],vs[t[2]]);
                for(int k=0;k<3;k++)
                {
                    int a=topology[f][k],b=topology[f][(k+1)%3];long key=EdgeKey(a,b);FeatureEdge95 edge;
                    if(!edgeMap.TryGetValue(key,out edge))
                    {edge=new FeatureEdge95{A=a,B=b};edgeMap.Add(key,edge);edges.Add(edge);}
                    edge.Corners.Add(f*3+k);
                }
            }
            var neighbors=new List<int>[triangles.Count];
            var continuous=new HashSet<long>(EdgeKeyComparer.Instance);
            for(int i=0;i<neighbors.Length;i++)neighbors[i]=new List<int>();
            foreach(var edge in edges)
            {
                if(edge.Corners.Count!=2)continue;
                int a=edge.Corners[0],b=edge.Corners[1];
                if(topology[a/3][a%3]!=topology[b/3][(b%3+1)%3])continue;
                neighbors[a/3].Add(b/3);neighbors[b/3].Add(a/3);
                // Both endpoints must have identical encoded shading normals. This
                // respects existing hard-normal splits, source groups and sheet topology.
                if(smoothDomains&&triangles[a/3][a%3]==triangles[b/3][(b%3+1)%3]
                    &&triangles[a/3][(a%3+1)%3]==triangles[b/3][b%3])
                    continuous.Add(EdgeKey(a/3,b/3));
            }
            var owner=new int[triangles.Count];for(int i=0;i<owner.Length;i++)owner[i]=-1;
            var domains=new List<List<int>>();
            for(int seed=0;seed<triangles.Count;seed++)
            {
                if(owner[seed]>=0)continue;
                int id=domains.Count;var domain=new List<int>();var queue=new Queue<int>();
                domains.Add(domain);owner[seed]=id;queue.Enqueue(seed);
                while(queue.Count>0)
                {
                    int f=queue.Dequeue();domain.Add(f);
                    foreach(int other in neighbors[f])
                        if(owner[other]<0&&(continuous.Contains(EdgeKey(f,other))||SameFeaturePlane95(vs,triangles,normals,seed,other)))
                        {owner[other]=id;queue.Enqueue(other);}
                }
            }
            var boundary=new List<FeatureBoundary95>();
            foreach(var edge in edges)
            {
                if(edge.Corners.Count==2)
                {
                    int a=edge.Corners[0],b=edge.Corners[1];
                    if(topology[a/3][a%3]==topology[b/3][(b%3+1)%3])
                    {
                        if(owner[a/3]!=owner[b/3])
                            boundary.Add(new FeatureBoundary95{A=triangles[a/3][a%3],B=triangles[a/3][(a%3+1)%3],TopologyA=topology[a/3][a%3],TopologyB=topology[a/3][(a%3+1)%3],Face=owner[a/3],Other=owner[b/3]});
                        continue;
                    }
                }
                // Open/chunk boundaries and ambiguous nonmanifold joins have one record
                // per incident face. Never invent a neighbor or silently discard a face.
                foreach(int a in edge.Corners)
                    boundary.Add(new FeatureBoundary95{A=triangles[a/3][a%3],B=triangles[a/3][(a%3+1)%3],TopologyA=topology[a/3][a%3],TopologyB=topology[a/3][(a%3+1)%3],Face=owner[a/3]});
            }
            if(smoothDomains)
            {
                boundary=ChainFeatureEdges95(boundary,vs);
                ReorderFeatureDomains95(ref domains,boundary);
            }
            bool wide=vs.Count>255;
            var bounds=new List<double[]>();
            using(var ms=new MemoryStream())using(var w=new BinaryWriter(ms))
            {
                WriteFeatureVertices95(w,vs,ns);
                w.Write(.2f);w.Write(new byte[]{0,2,2,10});Compact(w,checked((uint)domains.Count));
                foreach(var domain in domains)
                {
                    var indices=new int[checked(domain.Count*3)];int cursor=0;
                    foreach(int f in domain)foreach(int i in triangles[f])indices[cursor++]=i;
                    bounds.Add(Bounds(vs,indices));
                    w.Write(CompressFeaturePacket95(triangles,domain,wide));
                }
                foreach(var domain in domains)w.Write(new byte[]{48,4,4,color.Opacity??255,255,color.B,color.G,color.R});
                w.Write(new byte[]{1,1});Compact(w,checked((uint)boundary.Count));
                foreach(var edge in boundary)
                {
                    w.Write((byte)2);Compact(w,(uint)(edge.Polyline==null?2:edge.Polyline.Count));
                    if(edge.Polyline==null){Index(w,edge.A,wide);Index(w,edge.B,wide);}
                    else foreach(int i in edge.Polyline)Index(w,i,wide);
                }
                w.Write(new byte[]{16,36,4,255,255,0,0,0});payload=ms.ToArray();
            }
            using(var ms=new MemoryStream())using(var w=new BinaryWriter(ms))
            {
                w.Write((byte)255);w.Write(0);w.Write(PacketPrefix);
                for(int i=0;i<domains.Count;i++)
                {
                    w.Write(checked(firstId+(uint)i));w.Write(FaceReserved);
                    foreach(double x in bounds[i])w.Write(Finite(x));w.Write(FaceSuffix);
                }
                for(int i=0;i<boundary.Count;i++)
                {
                    var edge=boundary[i];w.Write((byte)96);w.Write(checked(firstId+(uint)domains.Count+(uint)i));w.Write(EdgeReserved);
                    Compact(w,(uint)edge.Face);Compact(w,edge.Other<0?uint.MaxValue:(uint)edge.Other);
                }
                pick=ms.ToArray();LE(pick,1,pick.Length);
            }
            faceCount=domains.Count;edgeCount=boundary.Count;
        }

        private static void WriteFeatureVertices95(BinaryWriter w,List<float[]> vs,List<float[]> ns)
        {
            w.Write(new byte[]{255,255,2,0,1});Compact(w,(uint)vs.Count);Compact(w,(uint)ns.Count);w.Write(0);
            var bitmap=new byte[(vs.Count+3)/4];var values=new List<float>();float[] previous=null;
            for(int i=0;i<vs.Count;i++)
            {
                var v=vs[i];int code;
                if(previous!=null&&v[0]==previous[0]&&v[1]==previous[1]&&v[2]==previous[2])code=1;
                else if(previous!=null&&v[0]==previous[0]&&v[1]==previous[1]){code=2;values.Add(v[2]);}
                else if(previous!=null&&v[0]==previous[0]){code=3;values.Add(v[1]);values.Add(v[2]);}
                else{code=0;values.AddRange(v);}
                bitmap[i/4]|=(byte)(code<<(2*(i%4)));previous=v;
            }
            w.Write(bitmap);Compact(w,(uint)values.Count);foreach(float x in values)w.Write(x);
            var codes=new ulong[ns.Count];uint words=0;
            for(int i=0;i<ns.Count;i++){codes[i]=FeatureNormalCode95(ns[i]);if((codes[i]>>32)<6)words+=2;}
            Compact(w,words);var normalMap=new byte[(ns.Count+1)/2];
            for(int i=0;i<ns.Count;i++)
            {
                ulong value=codes[i];int code=(int)(value>>32);
                if(code<6){w.Write((ushort)value);w.Write((ushort)(value>>16));}
                normalMap[i/2]|=(byte)(code<<(4*(i%2)));
            }
            w.Write(normalMap);
        }

        private static byte[] WrapFeature95(List<float[]> vertices,byte[] payload,uint pickOffset)
        {
            byte[] child;
            using(var ms=new MemoryStream())using(var w=new BinaryWriter(ms))
            {
                w.Write(ScenePrefix,43,ScenePrefix.Length-43);w.Write(payload);Compact(w,pickOffset);
                w.Write(SceneSuffix,1,SceneSuffix.Length-1);child=ms.ToArray();
            }
            LE(child,2,child.Length-2);LE(child,148,payload.Length+40);
            var all=new int[vertices.Count];for(int i=0;i<all.Length;i++)all[i]=i;
            var sphere=Bounds(vertices,all);double radius=sphere[9]+Math.Max(.2,sphere[9]*1e-6);
            int[] radii={17,64,111,163},centers={25,72,119,171};
            for(int i=0;i<radii.Length;i++)
            {Float(child,radii[i],radius);for(int k=0;k<3;k++)Float(child,centers[i]+k*4,sphere[k]);}
            return child;
        }
    }
}
