using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using DLAT.JTReader;

partial class Program
{
    static Node Root9()
    {
        // DTK files can have a Group root without a Partition node. Choose
        // only parentless scene nodes with a supported triangle descendant.
        var children=new HashSet<int>(nodes.Values.SelectMany(n=>n.Children));
        var sceneTypes=new HashSet<string>{"10dd103e","10dd101b","10dd102c","10dd104c","10dd10f3","ce357245","ce357244","10dd102a","10dd1077"};
        var roots=nodes.Values.Where(n=>sceneTypes.Contains(n.Type)&&!children.Contains(n.Id)&&HasTriangle(n.Id,true,new HashSet<int>())).ToArray();
        if(roots.Length!=1)throw new InvalidDataException("Expected one JT9 scene root with supported triangles; found="+roots.Length);
        Console.WriteLine("JT9_ROOT id="+roots[0].Id+" type="+roots[0].Type);
        return roots[0];
    }
    static void Decode9(Binding binding,int surface)
    {
        var lod=(Tri_StripSetShapeLODData)binding.Segment.Legacy.elementData;
        var topological=lod.vertexShapeLodData.topoMeshTopologicallyCompressedLODData;
        if(topological==null||topological.repData==null)
            throw new NotSupportedException("Unsupported JT9 topological LOD at shape "+surface);
        var rep=topological.repData;var records=rep.vertexRecords;
        if(records==null||records.compressedVertexCoordinateArray==null)
            throw new InvalidDataException("Missing JT9 vertex coordinates at shape "+surface);
        if(records.compressedVertexColorArray!=null)
            throw new NotSupportedException("JT9 per-vertex colors require explicit material mapping at shape "+surface);
        var xyz=records.compressedVertexCoordinateArray.vertexCoordinates;
        var normals=records.compressedVertexNormalArray==null?new List<float>():records.compressedVertexNormalArray.normalCoordinates;
        if(xyz.Count==0||xyz.Count%3!=0||normals.Count%3!=0)
            throw new InvalidDataException("Invalid JT9 coordinate/normal arrays at shape "+surface);
        var driver=new MeshCoderDriver();
        driver.setInputData(rep.vertexValences,rep.faceDegress,rep.vertexGroups,rep.vertexFlags,
            rep.faceAttributeMasks30LSBs,rep.faceAttributeMasks8_30nextMSBs,rep.faceAttributeMask8_4MSBs,
            rep.highDegreeFaceAttributeMasks,rep.splitFaceSyms,rep.splitFacePositions);
        driver.decode();var mesh=driver.DecodedMesh;
        var local=new List<double[]>();int offset=vertices.Count;
        for(int i=0;i<xyz.Count;i+=3)local.Add(Transform(new[]{(double)xyz[i],xyz[i+1],xyz[i+2]},binding.Matrix,true));
        vertices.AddRange(local);
        var rgb=binding.RGBA.Take(3).Select(v=>(byte)Math.Round(v*255)).ToArray();
        int corrected=0,hidden=0,degenerate=0;
        for(int f=0;f<mesh.numFaces();f++)
        {
            int nv=mesh.faceNumVts(f);
            if(mesh.faceGrp(f)<0){hidden+=Math.Max(0,nv-2);continue;}
            for(int k=1;k<nv-1;k++)
            {
                int[] ids={mesh.faceVtx(f,0),mesh.faceVtx(f,k),mesh.faceVtx(f,k+1)};
                if(ids.Any(id=>id<0||id>=local.Count))throw new InvalidDataException("Invalid JT9 vertex index");
                var original=ids.Select(id=>new[]{(double)xyz[id*3],xyz[id*3+1],xyz[id*3+2]}).ToArray();
                var sourceNormal=Cross(original[0],original[1],original[2]);
                if(Math.Sqrt(sourceNormal.Sum(v=>v*v))<=1e-12){degenerate++;continue;}
                var geo=Cross(local[ids[0]],local[ids[1]],local[ids[2]]);var sum=new double[3];
                foreach(int id in ids)
                {
                    int ni=mesh.faceVtxAttr(f,id);if(ni<0||normals.Count==0)continue;
                    if(ni*3+2>=normals.Count)throw new InvalidDataException("Invalid JT9 normal index");
                    var n=Normalize(Transform(new[]{(double)normals[ni*3],normals[ni*3+1],normals[ni*3+2]},binding.Matrix,false));
                    for(int j=0;j<3;j++)sum[j]+=n[j];
                }
                sum=Normalize(sum);
                if(Enumerable.Range(0,3).Sum(j=>sum[j]*geo[j])<0){int swap=ids[1];ids[1]=ids[2];ids[2]=swap;corrected++;}
                if(sum.Sum(v=>v*v)<.5)sum=Normalize(geo);
                faces.Add(new Face{Ids=ids.Select(id=>id+offset).ToArray(),RGB=rgb,Alpha=(byte)Math.Round(binding.RGBA[3]*255),Surface=surface,Normal=sum});
            }
        }
        if(vertices.Count>5000000||faces.Count>5000000)throw new InvalidDataException("Mesh size limit");
        Console.WriteLine("JT9_TOPOLOGY shape="+surface+" vertices="+local.Count+" totalFaces="+faces.Count+" windingCorrections="+corrected+" hidden="+hidden+" degenerate="+degenerate);
    }
}
