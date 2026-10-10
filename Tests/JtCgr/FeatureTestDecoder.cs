using System;
using System.Collections.Generic;
using System.IO;

namespace TxTools.ExportByColor
{
    // CFV3 has one fixed pick record per mesh leaf. R38's type12/type13 records
    // are variable-length and cannot be represented by that legacy CFV3 shell.
    // This adapter deliberately preserves mesh coordinates, winding and RGB,
    // keeping each R38 leaf as a separate compact source group.  Compact-95
    // can still subdivide that group at its own capacity limit. It does not
    // claim to carry CATIA line/face selection data.
    internal static class FeatureTestDecoder
    {
        public static int DomainCount,EdgeCount;
        private static readonly byte[] FeatureSignature={255,255,2,0,1};

        internal static bool HasFeatureLeaf(byte[] data)
        {
            for(int p=0;p+46<=data.Length;p++)
                if(data[p]==0x95&&data[p+1]==0xff&&Matches(data,p+41,FeatureSignature))return true;
            return false;
        }

        internal static void WriteCompactBridge(byte[] data,string path)
        {
            var vertices=new List<float[]>();var faces=new List<CgrWriter.Face>();int leaf=0;
            for(int p=0;p+46<=data.Length;)
            {
                if(data[p]!=0x95||data[p+1]!=0xff||!Matches(data,p+41,FeatureSignature)){p++;continue;}
                uint declared=U32(data,p+2);long endLong=(long)p+declared+1;
                if(endLong>data.Length||endLong<=p+41)throw new InvalidDataException("Invalid feature leaf envelope");
                ReadLeaf(data,p+41,(int)endLong,leaf++,vertices,faces);
                p=(int)endLong;
            }
            if(faces.Count==0)throw new InvalidDataException("CGR contains no R38 feature leaves");
            throw new NotSupportedException();
        }

        private static void ReadLeaf(byte[] data,int p,int end,int surface,List<float[]> allVertices,List<CgrWriter.Face> allFaces)
        {
            Require(data,p,FeatureSignature);p+=FeatureSignature.Length;
            int vertexCount=Compact(data,ref p);int normalVertices=Compact(data,ref p);
            if(vertexCount<3||vertexCount>65536||normalVertices!=vertexCount)throw new InvalidDataException("Invalid feature vertex counts");
            RequireRange(data,p,4,end);if(data[p]!=0||data[p+1]!=0||data[p+2]!=0||data[p+3]!=0)throw new InvalidDataException("Invalid feature coordinate prefix");p+=4;
            int bitmapBytes=(vertexCount+3)/4;RequireRange(data,p,bitmapBytes,end);byte[] bitmap=new byte[bitmapBytes];Buffer.BlockCopy(data,p,bitmap,0,bitmapBytes);p+=bitmapBytes;
            int values=Compact(data,ref p);int valuesEnd=checked(p+values*4);RequireRange(data,p,values*4,end);
            var local=new List<float[]>(vertexCount);int value=p;
            for(int i=0;i<vertexCount;i++)
            {
                int code=(bitmap[i/4]>>(2*(i%4)))&3;var current=new float[3];
                if(code==0){RequireRange(data,value,12,valuesEnd);Buffer.BlockCopy(data,value,current,0,12);value+=12;}
                else
                {
                    if(i==0)throw new InvalidDataException("Feature coordinate reuse at first vertex");
                    var previous=local[i-1];current[0]=previous[0];current[1]=previous[1];current[2]=previous[2];
                    if(code==2){RequireRange(data,value,4,valuesEnd);current[2]=BitConverter.ToSingle(data,value);value+=4;}
                    else if(code==3){RequireRange(data,value,8,valuesEnd);current[1]=BitConverter.ToSingle(data,value);current[2]=BitConverter.ToSingle(data,value+4);value+=8;}
                    else if(code!=1)throw new InvalidDataException("Invalid feature coordinate code");
                }
                local.Add(current);
            }
            if(value!=valuesEnd)throw new InvalidDataException("Feature coordinate value count mismatch");p=valuesEnd;
            int normalWords=Compact(data,ref p);RequireRange(data,p,checked(normalWords*2),end);p+=normalWords*2;
            int normalCodes=(vertexCount+1)/2;RequireRange(data,p,normalCodes,end);p+=normalCodes;
            RequireRange(data,p,8,end);if(data[p+4]!=0||data[p+5]!=2||data[p+6]!=2||data[p+7]!=10)throw new InvalidDataException("Unsupported feature primitive marker");p+=8;
            int domains=Compact(data,ref p);DomainCount+=domains;if(domains<1)throw new InvalidDataException("Feature leaf has no domains");
            bool wide=vertexCount>255;var domainTriangles=new List<List<int[]>>(domains);
            for(int i=0;i<domains;i++)domainTriangles.Add(ReadPacket(data,ref p,end,wide,vertexCount));
            RequireRange(data,p,checked(domains*8),end);byte r=0,g=0,b=0,alpha=255;bool colorSet=false;
            for(int i=0;i<domains;i++)
            {
                if(data[p]!=48||data[p+1]!=4||data[p+2]!=4||data[p+4]!=255)throw new InvalidDataException("Unsupported feature color record");
                byte nextAlpha=data[p+3];byte nextB=data[p+5],nextG=data[p+6],nextR=data[p+7];p+=8;
                if(!colorSet){r=nextR;g=nextG;b=nextB;alpha=nextAlpha;colorSet=true;}
                else if(r!=nextR||g!=nextG||b!=nextB||alpha!=nextAlpha)throw new InvalidDataException("Feature leaf mixes color groups");
            }
            // Parse and discard type-12 edge geometry. Its type-13 associations
            // have no safe fixed-record representation in the existing CFV3 shell.
            while(p<end)
            {
                RequireRange(data,p,2,end);if(data[p]!=1||data[p+1]!=1)throw new InvalidDataException("Unsupported feature edge group");p+=2;
                int edges=Compact(data,ref p);EdgeCount+=edges;
                for(int e=0;e<edges;e++)
                {
                    RequireRange(data,p,1,end);if(data[p++]!=2)throw new InvalidDataException("Unsupported feature edge opcode");
                    int points=Compact(data,ref p);RequireRange(data,p,checked(points*(wide?2:1)),end);p+=points*(wide?2:1);
                }
                RequireRange(data,p,8,end);if(data[p+1]!=36||data[p+2]!=4||data[p+4]!=255)throw new InvalidDataException("Unsupported feature edge appearance");p+=8;
            }
            if(p!=end)throw new InvalidDataException("Feature leaf trailing data");
            int baseIndex=allVertices.Count;allVertices.AddRange(local);
            foreach(var domain in domainTriangles)foreach(var triangle in domain)
                allFaces.Add(new CgrWriter.Face{Idx=new[]{baseIndex+triangle[0],baseIndex+triangle[1],baseIndex+triangle[2]},R=r,G=g,B=b,Opacity=alpha,Surface=surface});
        }

        private static List<int[]> ReadPacket(byte[] data,ref int p,int end,bool wide,int vertexCount)
        {
            RequireRange(data,p,2,end);if(data[p++]!=1)throw new InvalidDataException("Unsupported feature packet");int descriptor=data[p++];
            if((descriptor&248)!=64)throw new InvalidDataException("Unsupported feature packet descriptor");int flags=descriptor&7;
            int singles=(flags&1)!=0?Compact(data,ref p):0;
            int strips=(flags&2)!=0?Compact(data,ref p):0;
            int fans=(flags&4)!=0?Compact(data,ref p):0;
            if(singles<0||strips<0||fans<0)throw new InvalidDataException("Invalid feature packet flags");
            int total=Compact(data,ref p);var stripSizes=new int[strips];var fanSizes=new int[fans];
            for(int i=0;i<strips;i++)stripSizes[i]=Compact(data,ref p);
            for(int i=0;i<fans;i++)fanSizes[i]=Compact(data,ref p);
            int expected=checked(singles*3);foreach(int n in stripSizes)expected=checked(expected+n);foreach(int n in fanSizes)expected=checked(expected+n);
            if(total!=expected)throw new InvalidDataException("Feature packet index count mismatch");
            var values=new int[total];for(int i=0;i<total;i++){RequireRange(data,p,wide?2:1,end);int v=wide?(data[p]<<8)|data[p+1]:data[p];p+=wide?2:1;if(v<0||v>=vertexCount)throw new InvalidDataException("Feature index out of range");values[i]=v;}
            int cursor=0;var result=new List<int[]>(checked(singles+total));
            for(int i=0;i<singles;i++)result.Add(new[]{values[cursor++],values[cursor++],values[cursor++]});
            foreach(int count in stripSizes)
            {
                if(count<3)throw new InvalidDataException("Feature strip too short");int start=cursor;cursor+=count;
                for(int i=0;i<count-2;i++)result.Add((i&1)==0?new[]{values[start+i],values[start+i+1],values[start+i+2]}:new[]{values[start+i+1],values[start+i],values[start+i+2]});
            }
            foreach(int count in fanSizes)
            {
                if(count<3)throw new InvalidDataException("Feature fan too short");int start=cursor;cursor+=count;
                for(int i=1;i<count-1;i++)result.Add(new[]{values[start],values[start+i],values[start+i+1]});
            }
            if(cursor!=values.Length)throw new InvalidDataException("Feature packet cursor mismatch");return result;
        }

        private static int Compact(byte[] data,ref int p)
        {
            RequireRange(data,p,1,data.Length);int value=data[p++];if(value!=255)return value;RequireRange(data,p,4,data.Length);value=checked((int)U32(data,p));p+=4;return value;
        }
        private static uint U32(byte[] data,int p){return (uint)(data[p]|data[p+1]<<8|data[p+2]<<16|data[p+3]<<24);}
        private static bool Matches(byte[] data,int p,byte[] expected){if(p<0||p+expected.Length>data.Length)return false;for(int i=0;i<expected.Length;i++)if(data[p+i]!=expected[i])return false;return true;}
        private static void Require(byte[] data,int p,byte[] expected){if(!Matches(data,p,expected))throw new InvalidDataException("Feature signature mismatch");}
        private static void RequireRange(byte[] data,int p,int count,int end){if(p<0||count<0||p> end-count||end>data.Length)throw new InvalidDataException("Feature data is truncated");}
    }

}