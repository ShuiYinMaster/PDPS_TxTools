using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TxTools.ExportByColor;

internal static class CompressionSelfTests
{
    private static object Call(string name,params object[] args)
    {return typeof(CgrWriter).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);}
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
    private static uint Count(BinaryReader r){byte b=r.ReadByte();return b==255?r.ReadUInt32():b;}
    private static int CountBytes(int count){return count<255?1:5;}
    private static string Key(int a,int b,int c)
    {
        // Cyclic rotations preserve winding; reversing it must fail this test.
        var t=new[]{a,b,c};int best=0;
        for(int i=1;i<3;i++)
            if(t[i]<t[best]||(t[i]==t[best]&&t[(i+1)%3]<t[(best+1)%3]))best=i;
        return t[best]+","+t[(best+1)%3]+","+t[(best+2)%3];
    }
    private static void Add(Dictionary<string,int> values,int a,int b,int c)
    {string key=Key(a,b,c);int n;values.TryGetValue(key,out n);values[key]=n+1;}
    private static void Packet(List<int[]> triangles,bool wide)
    {
        var domain=new List<int>();for(int i=0;i<triangles.Count;i++)domain.Add(i);
        byte[] data=(byte[])Call("CompressFeaturePacket95",triangles,domain,wide);
        int plain=2+CountBytes(triangles.Count)+CountBytes(triangles.Count*3)+triangles.Count*3*(wide?2:1);
        Require(data.Length<=plain,"packet expanded");
        var expected=new Dictionary<string,int>();foreach(var t in triangles)Add(expected,t[0],t[1],t[2]);
        var actual=new Dictionary<string,int>();
        using(var r=new BinaryReader(new MemoryStream(data)))
        {
            Require(r.ReadByte()==1,"packet prefix");int flags=r.ReadByte()&15;
            int lists=(flags&1)!=0?(int)Count(r):0,strips=(flags&2)!=0?(int)Count(r):0,fans=(flags&4)!=0?(int)Count(r):0;
            int ni=(int)Count(r);var lengths=new int[strips+fans];
            for(int i=0;i<lengths.Length;i++)lengths[i]=(int)Count(r);
            var indices=new int[ni];for(int i=0;i<ni;i++)indices[i]=wide?(r.ReadByte()<<8)|r.ReadByte():r.ReadByte();
            Require(r.BaseStream.Position==data.Length,"unconsumed packet");
            int cursor=0;
            for(int i=0;i<lists;i++){Add(actual,indices[cursor],indices[cursor+1],indices[cursor+2]);cursor+=3;}
            for(int group=0;group<lengths.Length;group++)
            {
                for(int j=2;j<lengths[group];j++)
                {
                    int a=group>=strips?indices[cursor]:(j%2==0?indices[cursor+j-2]:indices[cursor+j-1]);
                    int b=group>=strips?indices[cursor+j-1]:(j%2==0?indices[cursor+j-1]:indices[cursor+j-2]);
                    Add(actual,a,b,indices[cursor+j]);
                }
                cursor+=lengths[group];
            }
            Require(cursor==ni,"index accounting");
        }
        Require(actual.Count==expected.Count,"triangle set");
        foreach(var pair in expected){int n;Require(actual.TryGetValue(pair.Key,out n)&&n==pair.Value,"winding or multiplicity changed");}
    }
    private static readonly Type EdgeType=typeof(CgrWriter).GetNestedType("FeatureBoundary95",BindingFlags.NonPublic);
    private static FieldInfo Field(string name){return EdgeType.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);}
    private static string Segment(int a,int b,int face,int other){return Math.Min(a,b)+","+Math.Max(a,b)+":"+face+","+other;}
    private static void Chain(List<float[]> vertices,int[][] edges,int expectedCount)
    {
        var input=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(EdgeType));
        var expected=new Dictionary<string,int>();
        foreach(var edge in edges)
        {
            object e=Activator.CreateInstance(EdgeType,true);
            string[] fields={"A","B","TopologyA","TopologyB","Face","Other"};
            for(int i=0;i<fields.Length;i++)Field(fields[i]).SetValue(e,edge[i]);
            input.Add(e);string key=Segment(edge[0],edge[1],edge[4],edge[5]);int n;expected.TryGetValue(key,out n);expected[key]=n+1;
        }
        var chainMethod=typeof(CgrWriter).GetMethod("ChainFeatureEdges95",BindingFlags.NonPublic|BindingFlags.Static);
        var output=(IList)(chainMethod.GetParameters().Length==2?Call("ChainFeatureEdges95",input,vertices):Call("ChainFeatureEdges95",input,vertices,false));Require(output.Count==expectedCount,"edge chain count");
        var actual=new Dictionary<string,int>();
        foreach(object e in output)
        {
            var line=(List<int>)Field("Polyline").GetValue(e);int face=(int)Field("Face").GetValue(e),other=(int)Field("Other").GetValue(e);
            for(int i=1;i<line.Count;i++){string key=Segment(line[i-1],line[i],face,other);int n;actual.TryGetValue(key,out n);actual[key]=n+1;}
        }
        Require(expected.Count==actual.Count,"boundary set changed");
        foreach(var pair in expected){int n;Require(actual.TryGetValue(pair.Key,out n)&&n==pair.Value,"boundary segment dropped or duplicated");}
    }
    private static void DomainOrder(int count)
    {
        var domains=new List<List<int>>();for(int i=0;i<count;i++)domains.Add(new List<int>{i});
        var edges=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(EdgeType));
        var original=new List<int[]>();
        for(int i=0;i<count*3;i++)
        {
            // Make a face just beyond the one-byte threshold the hottest endpoint.
            int face=count-1,other=i%5==0?-1:i%count;
            object e=Activator.CreateInstance(EdgeType,true);Field("Face").SetValue(e,face);Field("Other").SetValue(e,other);
            edges.Add(e);original.Add(new[]{face,other});
        }
        object[] args={domains,edges};Call("ReorderFeatureDomains95",args);
        var reordered=(List<List<int>>)args[0];var seen=new HashSet<int>();
        foreach(var d in reordered)Require(seen.Add(d[0]),"duplicated face domain");
        Require(seen.Count==count,"missing face domain");
        for(int i=0;i<edges.Count;i++)
        {
            int face=(int)Field("Face").GetValue(edges[i]),other=(int)Field("Other").GetValue(edges[i]);
            Require(reordered[face][0]==original[i][0],"face adjacency remap");
            Require(other<0?original[i][1]<0:reordered[other][0]==original[i][1],"other adjacency remap");
        }
        Require(count<=255?ReferenceEquals(domains,reordered):reordered[0][0]==count-1,"short-index assignment");
    }
    internal static void Run()
    {
        int packets=0;
        foreach(int side in new[]{2,15,16,32})
        {
            var triangles=new List<int[]>();
            for(int y=0;y<side;y++)for(int x=0;x<side;x++)
            {int a=y*(side+1)+x,b=a+1,c=a+side+1,d=c+1;triangles.Add(new[]{a,b,d});triangles.Add(new[]{a,d,c});}
            Packet(triangles,(side+1)*(side+1)>255);packets++;
        }
        var fan=new List<int[]>();for(int i=1;i<=400;i++)fan.Add(new[]{0,i,i==400?1:i+1});Packet(fan,true);packets++;
        // Duplicate and degenerate packets exercise index accounting independent of
        // the writer's earlier zero-area filter, including the compact-count threshold.
        var random=new Random(9127);
        for(int test=0;test<100;test++)
        {
            var triangles=new List<int[]>();int count=1+random.Next(600),vertices=test%2==0?255:40960;
            for(int i=0;i<count;i++)triangles.Add(new[]{random.Next(vertices),random.Next(vertices),random.Next(vertices)});
            triangles.Add(triangles[0]);triangles.Add(new[]{1,1,1});Packet(triangles,vertices>255);packets++;
        }
        var square=new List<float[]>{new float[]{0,0,0},new float[]{10,0,0},new float[]{10,10,0},new float[]{0,10,0}};
        Chain(square,new[]{new[]{0,1,0,1,0,1},new[]{1,2,1,2,0,1},new[]{2,3,2,3,0,1},new[]{3,0,3,0,0,1}},4);
        var straight=new List<float[]>{new float[]{0,0,0},new float[]{10,0,0},new float[]{20,0,0},new float[]{10,10,0}};
        Chain(straight,new[]{new[]{1,2,1,2,0,1},new[]{0,1,0,1,0,1}},1);
        Chain(straight,new[]{new[]{0,1,0,1,0,1},new[]{1,2,1,2,0,1},new[]{1,3,1,3,0,1}},3);
        Chain(straight,new[]{new[]{0,1,0,1,0,1},new[]{1,2,1,2,0,2}},2);
        Chain(straight,new[]{new[]{0,1,0,1,0,1},new[]{1,2,5,6,0,1}},2);
        var circle=new List<float[]>();var ring=new int[48][];
        for(int i=0;i<48;i++){circle.Add(new[]{(float)Math.Cos(i*Math.PI/24),(float)Math.Sin(i*Math.PI/24),0f});ring[i]=new[]{i,(i+1)%48,i,(i+1)%48,0,1};}
        Chain(circle,ring,1);
        Require(((ulong)Call("FeatureNormalCode95",new float[]{0,0,1})>>32)==10,"axis-normal encoding");
        Require(((ulong)Call("FeatureNormalCode95",new float[]{0,0,-1})>>32)==11,"negative axis-normal encoding");
        foreach(int count in new[]{255,256,1024})DomainOrder(count);
        Console.WriteLine("PASS compression: "+packets+" oriented packet cases; 6 edge-chain cases; 3 face-index permutations; axis normals");
    }
}
