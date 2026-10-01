using System;
using System.Collections.Generic;
using System.IO;

namespace TxTools.ExportByColor
{
    public static partial class CgrWriter
    {
        private static void ReorderFeatureDomains95(ref List<List<int>> domains,List<FeatureBoundary95> boundary)
        {
            // Native compact integers use one byte for 0..254, five for >=255.
            // Assign those 255 cheap slots to the most referenced face domains.
            // This is a permutation only: triangle membership and edge incidence stay intact.
            if(domains.Count<=255)return;
            var frequency=new int[domains.Count];
            foreach(var edge in boundary){frequency[edge.Face]++;if(edge.Other>=0)frequency[edge.Other]++;}
            var order=new List<int>();for(int i=0;i<domains.Count;i++)order.Add(i);
            order.Sort((a,b)=>{int c=frequency[b].CompareTo(frequency[a]);return c!=0?c:a.CompareTo(b);});
            long before=0,after=0;
            for(int i=0;i<255;i++){before+=frequency[i];after+=frequency[order[i]];}
            if(after<=before)return;
            var remap=new int[domains.Count];var sorted=new List<List<int>>(domains.Count);
            for(int i=0;i<order.Count;i++){remap[order[i]]=i;sorted.Add(domains[order[i]]);}
            foreach(var edge in boundary){edge.Face=remap[edge.Face];if(edge.Other>=0)edge.Other=remap[edge.Other];}
            domains=sorted;
        }

        private static List<FeatureBoundary95> ChainFeatureEdges95(List<FeatureBoundary95> input,List<float[]> vs)
        {
            var groups=new Dictionary<Tuple<int,int>,List<FeatureBoundary95>>();var order=new List<List<FeatureBoundary95>>();
            foreach(var edge in input)
            {
                if(edge.Other>=0&&edge.Other<edge.Face){int f=edge.Face;edge.Face=edge.Other;edge.Other=f;}
                var key=Tuple.Create(edge.Face,edge.Other);List<FeatureBoundary95> group;
                if(!groups.TryGetValue(key,out group)){group=new List<FeatureBoundary95>();groups.Add(key,group);order.Add(group);}
                group.Add(edge);
            }
            var result=new List<FeatureBoundary95>();
            foreach(var group in order)
            {
                var endpoints=new Dictionary<int,List<int>>();var links=new int[group.Count][];
                for(int i=0;i<group.Count;i++)
                {
                    links[i]=new[]{-1,-1};var e=group[i];
                    for(int k=0;k<2;k++)
                    {
                        int vertex=k==0?e.TopologyA:e.TopologyB;List<int> incidents;
                        if(!endpoints.TryGetValue(vertex,out incidents)){incidents=new List<int>();endpoints.Add(vertex,incidents);}
                        incidents.Add(i);
                    }
                }
                foreach(var pair in endpoints)
                {
                    // A branch or nonmanifold junction must remain separately selectable.
                    var incidents=pair.Value;if(incidents.Count!=2||incidents[0]==incidents[1])continue;
                    int a=incidents[0],b=incidents[1];var ea=group[a];var eb=group[b];
                    int ka=ea.TopologyA==pair.Key?0:1,kb=eb.TopologyA==pair.Key?0:1;
                    var origin=vs[ka==0?ea.A:ea.B];var va=vs[ka==0?ea.B:ea.A];var vb=vs[kb==0?eb.B:eb.A];
                    double dot=0,aa=0,bb=0;
                    for(int k=0;k<3;k++){double u=(double)va[k]-origin[k],v=(double)vb[k]-origin[k];dot+=u*v;aa+=u*u;bb+=v*v;}
                    // Join gentle polyline continuation, never a sharp corner. Every
                    // original boundary segment and intermediate position is retained.
                    if(aa==0||bb==0||dot/Math.Sqrt(aa*bb)>-Math.Cos(Math.PI/10))continue;
                    links[a][ka]=b;links[b][kb]=a;
                }
                var used=new bool[group.Count];
                // Open chains first; otherwise starting in the middle would split them.
                for(int pass=0;pass<2;pass++)for(int seed=0;seed<group.Count;seed++)
                {
                    if(used[seed]||(pass==0&&links[seed][0]>=0&&links[seed][1]>=0))continue;
                    var first=group[seed];int side=links[seed][0]<0?0:links[seed][1]<0?1:0;
                    int vertex=side==0?first.TopologyA:first.TopologyB,current=seed;
                    var polyline=new List<int>{side==0?first.A:first.B};
                    while(current>=0&&!used[current])
                    {
                        used[current]=true;var edge=group[current];bool forward=vertex==edge.TopologyA;
                        polyline.Add(forward?edge.B:edge.A);
                        vertex=forward?edge.TopologyB:edge.TopologyA;current=links[current][forward?1:0];
                    }
                    result.Add(new FeatureBoundary95{A=polyline[0],B=polyline[polyline.Count-1],Face=first.Face,Other=first.Other,Polyline=polyline});
                }
            }
            return result;
        }

        // Native direct32 encoding. Axis codes 6..11 mean the same unit normal
        // as codes 0..5 with two zero words; no precision is discarded here.
        private static ulong FeatureNormalCode95(float[] n)
        {
            int axis=0;for(int k=1;k<3;k++)if(Math.Abs(n[k])>Math.Abs(n[axis]))axis=k;
            double length=Math.Sqrt((double)n[0]*n[0]+(double)n[1]*n[1]+(double)n[2]*n[2]);
            if(length==0||double.IsNaN(length)||double.IsInfinity(length))throw new InvalidDataException("Invalid feature normal");
            ulong words=0;int shift=0;
            for(int k=0;k<3;k++)if(k!=axis)
            {
                short value=(short)Math.Round(Math.Max(-1,Math.Min(1,n[k]/length))*32767);
                words|=(ulong)(ushort)value<<shift;shift+=16;
            }
            int code=axis*2+(n[axis]<0?1:0)+(words==0?6:0);
            return words|((ulong)code<<32);
        }

        private static void CompactFeatureVertices95(ref List<float[]> vs,ref List<float[]> ns,ref List<int[]> triangles)
        {
            // Welding is for rendering only. Original topology indices remain separate,
            // so disconnected sheets and hard edges keep their face/edge identities.
            var vertices=vs;var order=new List<int>();
            for(int i=0;i<vs.Count;i++)order.Add(i);
            order.Sort((a,b)=>{for(int k=0;k<3;k++){int c=vertices[a][k].CompareTo(vertices[b][k]);if(c!=0)return c;}return a.CompareTo(b);});
            var map=new Dictionary<Tuple<float,float,float,ulong>,int>();
            var compactV=new List<float[]>();var compactN=new List<float[]>();var remap=new int[vs.Count];
            foreach(int old in order)
            {
                var v=vs[old];var key=Tuple.Create(v[0],v[1],v[2],FeatureNormalCode95(ns[old]));int index;
                if(!map.TryGetValue(key,out index))
                {index=compactV.Count;map.Add(key,index);compactV.Add(v);compactN.Add(ns[old]);}
                remap[old]=index;
            }
            var result=new List<int[]>(triangles.Count);
            foreach(var t in triangles)result.Add(new[]{remap[t[0]],remap[t[1]],remap[t[2]]});
            vs=compactV;ns=compactN;triangles=result;
        }

        private struct FeatureNext95
        {
            internal int Triangle,Vertex;
        }
        private static long DirectedFeatureEdge95(int a,int b){return ((long)a<<32)|(uint)b;}

        private static byte[] FeaturePacketBytes95(List<int> singles,List<List<int>> strips,List<List<int>> fans,bool wide)
        {
            using(var ms=new MemoryStream())using(var w=new BinaryWriter(ms))
            {
                int flags=(singles.Count>0?1:0)|(strips.Count>0?2:0)|(fans.Count>0?4:0);
                w.Write((byte)1);w.Write((byte)(64|flags));
                if(singles.Count>0)Compact(w,(uint)(singles.Count/3));
                if(strips.Count>0)Compact(w,(uint)strips.Count);
                if(fans.Count>0)Compact(w,(uint)fans.Count);
                int total=singles.Count;
                foreach(var seq in strips)total=checked(total+seq.Count);
                foreach(var seq in fans)total=checked(total+seq.Count);
                Compact(w,(uint)total);
                foreach(var seq in strips)Compact(w,(uint)seq.Count);
                foreach(var seq in fans)Compact(w,(uint)seq.Count);
                foreach(int i in singles)Index(w,i,wide);
                foreach(var seq in strips)foreach(int i in seq)Index(w,i,wide);
                foreach(var seq in fans)foreach(int i in seq)Index(w,i,wide);
                return ms.ToArray();
            }
        }

        private static byte[] CompressFeaturePacket95(List<int[]> triangles,List<int> domain,bool wide)
        {
            var plain=new List<int>(checked(domain.Count*3));
            foreach(int f in domain)plain.AddRange(triangles[f]);
            var empty=new List<List<int>>();var fallback=FeaturePacketBytes95(plain,empty,empty,wide);
            if(domain.Count<2)return fallback;
            // Adjacent IDs often have the same a^b. Default Int64 hashing turns
            // regular grids into huge collision chains; mix both endpoints instead.
            var edges=new Dictionary<long,List<FeatureNext95>>(EdgeKeyComparer.Instance);
            for(int f=0;f<domain.Count;f++)
            {
                var t=triangles[domain[f]];
                if(t[0]==t[1]||t[1]==t[2]||t[2]==t[0])continue;
                for(int k=0;k<3;k++)
                {
                    long key=DirectedFeatureEdge95(t[k],t[(k+1)%3]);List<FeatureNext95> next;
                    if(!edges.TryGetValue(key,out next)){next=new List<FeatureNext95>();edges.Add(key,next);}
                    next.Add(new FeatureNext95{Triangle=f,Vertex=t[(k+2)%3]});
                }
            }
            var consumed=new bool[domain.Count];var singles=new List<int>();
            var strips=new List<List<int>>();var fans=new List<List<int>>();
            for(int seed=0;seed<domain.Count;seed++)
            {
                if(consumed[seed])continue;
                var t=triangles[domain[seed]];List<int> best=null;HashSet<int> bestUsed=null;bool bestFan=false;
                for(int kind=0;kind<2;kind++)for(int rotation=0;rotation<3;rotation++)
                {
                    var seq=new List<int>{t[rotation],t[(rotation+1)%3],t[(rotation+2)%3]};
                    var used=new HashSet<int>{seed};
                    while(used.Count<1024)
                    {
                        int n=seq.Count;
                        int a=kind==1?seq[0]:((n&1)!=0?seq[n-1]:seq[n-2]);
                        int b=kind==1?seq[n-1]:((n&1)!=0?seq[n-2]:seq[n-1]);
                        List<FeatureNext95> next;if(!edges.TryGetValue(DirectedFeatureEdge95(a,b),out next))break;
                        int chosen=-1;
                        for(int j=0;j<Math.Min(64,next.Count);j++)
                            if(!consumed[next[j].Triangle]&&!used.Contains(next[j].Triangle)){chosen=j;break;}
                        if(chosen<0)break;
                        used.Add(next[chosen].Triangle);seq.Add(next[chosen].Vertex);
                    }
                    if(bestUsed==null||used.Count>bestUsed.Count){best=seq;bestUsed=used;bestFan=kind==1;}
                }
                foreach(int f in bestUsed)consumed[f]=true;
                if(bestUsed.Count==1)singles.AddRange(t);
                else if(bestFan)fans.Add(best);else strips.Add(best);
            }
            var packed=FeaturePacketBytes95(singles,strips,fans,wide);
            // For small/disconnected domains packet overhead can exceed index savings.
            return packed.Length<fallback.Length?packed:fallback;
        }
    }
}
