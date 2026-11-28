using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TxTools.CatiaPartTree.Conversion
{
    // Packed edges store two nearby vertex IDs in the high/low 32-bit words.
    // Mix both words before hashing; the default Int64 XOR hash puts many CAD
    // grid edges in the same bucket (for example, every (2n, 2n+1) hashes to 1).
    internal sealed class MeshEdgeComparer : IEqualityComparer<long>
    {
        internal static readonly MeshEdgeComparer Instance = new MeshEdgeComparer();
        private MeshEdgeComparer() { }
        public bool Equals(long a, long b) { return a == b; }
        public int GetHashCode(long value)
        {
            unchecked
            {
                ulong key = (ulong)value;
                key ^= key >> 33; key *= 0xff51afd7ed558ccdUL;
                key ^= key >> 33; key *= 0xc4ceb9fe1a85ec53UL;
                key ^= key >> 33;
                return (int)(key ^ (key >> 32));
            }
        }
    }

    public sealed class MeshShape
    {
        public string name;
        public string instanceId;
        public string sourceFile;
        public string sourceSha256;
        public double[][] vertices;
        public int[][] triangles;
        public byte[][] rgb;
        public double[][] normals;
        public bool nativeNormalBindings;
        // Geometry fallback for leaves that contain no usable native normals.
        // NativeCadReader resolves domain/vertex bindings before reaching this path.
        // Averaging is clustered by angle around each welded position: a flat face never
        // picks up the normal of the hole wall it touches, which is what made coarse
        // meshes show big wedge shaped shading bands.
        const double ClusterCos=0.8660254037844386; // 30 degrees
        public void Smooth()
        {
            int count=vertices.Length;var order=Enumerable.Range(0,count).ToArray();
            Array.Sort(order,(l,r)=>Compare(vertices[l],vertices[r]));
            var weld=new int[count];int groups=0;
            for(int i=0;i<count;i++)
            {
                if(i>0&&Compare(vertices[order[i]],vertices[order[i-1]])==0)weld[order[i]]=weld[order[i-1]];
                else weld[order[i]]=groups++;
            }
            var raw=new double[triangles.Length][];
            var buckets=new List<int>[groups];
            for(int g=0;g<groups;g++)buckets[g]=new List<int>(6);
            for(int f=0;f<triangles.Length;f++)
            {
                var t=triangles[f];var a=vertices[t[0]];var b=vertices[t[1]];var c=vertices[t[2]];
                raw[f]=new[]{
                    (b[1]-a[1])*(c[2]-a[2])-(b[2]-a[2])*(c[1]-a[1]),
                    (b[2]-a[2])*(c[0]-a[0])-(b[0]-a[0])*(c[2]-a[2]),
                    (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])};
                for(int k=0;k<3;k++)buckets[weld[t[k]]].Add(f*3+k);
            }
            var result=new double[count][];
            var bestArea=new double[count];
            var assigned=new bool[8];
            foreach(var bucket in buckets)
            {
                if(bucket.Count==0)continue;
                if(assigned.Length<bucket.Count)assigned=new bool[bucket.Count];
                for(int i=0;i<bucket.Count;i++)assigned[i]=false;
                for(int seedIndex=0;seedIndex<bucket.Count;seedIndex++)
                {
                    if(assigned[seedIndex])continue;
                    var seed=Normalize(raw[bucket[seedIndex]/3]);
                    if(seed==null){assigned[seedIndex]=true;continue;}
                    var members=new List<int>(bucket.Count);
                    double sx=0,sy=0,sz=0,area=0;
                    for(int j=seedIndex;j<bucket.Count;j++)
                    {
                        if(assigned[j])continue;
                        var n=raw[bucket[j]/3];
                        var unit=Normalize(n);
                        if(unit==null)continue;
                        if(seed[0]*unit[0]+seed[1]*unit[1]+seed[2]*unit[2]<ClusterCos)continue;
                        assigned[j]=true;members.Add(bucket[j]);
                        sx+=n[0];sy+=n[1];sz+=n[2];area+=Math.Sqrt(n[0]*n[0]+n[1]*n[1]+n[2]*n[2]);
                    }
                    var cluster=Normalize(new[]{sx,sy,sz});
                    if(cluster==null)continue;
                    foreach(int corner in members)
                    {
                        int index=triangles[corner/3][corner%3];
                        if(area<=bestArea[index])continue;   // 同一顶点属于两个曲面时取面积占优的一侧
                        bestArea[index]=area;result[index]=cluster;
                    }
                }
            }
            normals=result;
        }
        // 缓存自带法向时只补空缺（少量顶点可能缺），缺失处沿用几何近似。
        public void FillMissingNormals()
        {
            if(normals==null)return;
            bool missing=false;
            for(int i=0;i<normals.Length;i++)if(normals[i]==null){missing=true;break;}
            if(!missing)return;
            var fallback=new MeshShape{name=name,instanceId=instanceId,sourceFile=sourceFile,sourceSha256=sourceSha256,
                vertices=vertices,triangles=triangles,rgb=rgb};
            fallback.Smooth();
            for(int i=0;i<normals.Length;i++)if(normals[i]==null&&fallback.normals!=null)normals[i]=fallback.normals[i];
        }
        static double[] Normalize(double[] value)
        {
            double length=Math.Sqrt(value[0]*value[0]+value[1]*value[1]+value[2]*value[2]);
            return length>0?new[]{value[0]/length,value[1]/length,value[2]/length}:null;
        }
        static int Compare(double[] left,double[] right)
        {
            for(int k=0;k<3;k++){int c=Math.Round(left[k],3).CompareTo(Math.Round(right[k],3));if(c!=0)return c;}
            return 0;
        }
        public void Validate()
        {
            if(string.IsNullOrWhiteSpace(instanceId)||vertices==null||triangles==null||rgb==null||triangles.Length==0||rgb.Length!=triangles.Length)
                throw new InvalidDataException("Incomplete mesh instance");
            foreach(var p in vertices)if(p==null||p.Length!=3||p.Any(v=>double.IsNaN(v)||double.IsInfinity(v)))throw new InvalidDataException("Invalid mesh point");
            for(int i=0;i<triangles.Length;i++)
            {
                var t=triangles[i];if(t==null||t.Length!=3||t.Distinct().Count()!=3||t.Any(v=>v<0||v>=vertices.Length)||rgb[i]==null||rgb[i].Length!=3)
                    throw new InvalidDataException("Invalid triangle/color");
                if (Degenerate(vertices, t)) throw new InvalidDataException("Degenerate triangle");
            }
        }

        private static bool Degenerate(double[][] points, int[] triangle)
        {
            var a = points[triangle[0]];
            var b = points[triangle[1]];
            var c = points[triangle[2]];
            double x = (b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]);
            double y = (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]);
            double z = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
            return x * x + y * y + z * z == 0;
        }

        // Source CGR leaves occasionally contain zero-area triangles. They carry
        // no surface, so drop them before the combined mesh is validated.
        internal MeshShape RemoveDegenerateTriangles()
        {
            var keep = new List<int[]>(triangles.Length);
            var colors = new List<byte[]>(triangles.Length);
            for (int i = 0; i < triangles.Length; i++)
            {
                if (Degenerate(vertices, triangles[i])) continue;
                keep.Add(triangles[i]);
                colors.Add(rgb[i]);
            }
            if (keep.Count == triangles.Length) return this;
            if (keep.Count == 0) return null;
            return new MeshShape
            {
                name = name, instanceId = instanceId, sourceFile = sourceFile, sourceSha256 = sourceSha256,
                vertices = vertices, triangles = keep.ToArray(), rgb = colors.ToArray(), normals = normals,
                nativeNormalBindings = nativeNormalBindings
            };
        }

        public MeshShape Bake(Frame12 vehicleFromPart)
        {
            var result = BakeGeometry(vehicleFromPart);
            result.Validate();
            return result;
        }

        // Assembly transforms and JT float32 storage can collapse small faces.
        // Drop only those triangles so one bad instance cannot fail the whole file.
        internal MeshShape BakeInstanceForJt(Frame12 frame, out int removed)
        {
            var result = BakeGeometry(frame);
            // Assembly transforms can collapse small faces at the precision used by JT.
            var rounded = result.vertices.Select(point => point.Select(value => (double)(float)value).ToArray()).ToArray();
            var triangles = new List<int[]>(result.triangles.Length);
            var colors = new List<byte[]>(result.rgb.Length);
            for (int i = 0; i < result.triangles.Length; i++)
            {
                var triangle = result.triangles[i];
                if (Degenerate(result.vertices, triangle) || Degenerate(rounded, triangle)) continue;
                triangles.Add(triangle);
                colors.Add(result.rgb[i]);
            }
            removed = result.triangles.Length - triangles.Count;
            if (triangles.Count == 0) return null;
            if (removed != 0)
            {
                result.triangles = triangles.ToArray();
                result.rgb = colors.ToArray();
            }
            result.Validate();
            return result;
        }

        private MeshShape BakeGeometry(Frame12 vehicleFromPart)
        {
            Validate();var result=new MeshShape{name=name,instanceId=instanceId,sourceFile=sourceFile,sourceSha256=sourceSha256,
                vertices=vertices.Select(vehicleFromPart.Point).ToArray(),triangles=triangles.Select(t=>(int[])t.Clone()).ToArray(),rgb=rgb.Select(c=>(byte[])c.Clone()).ToArray(),nativeNormalBindings=nativeNormalBindings};
            if(vehicleFromPart.Determinant<0)foreach(var t in result.triangles){int old=t[1];t[1]=t[2];t[2]=old;}
            if(normals!=null)
            {
                var inverse=vehicleFromPart.Inverse().Components;
                result.normals=normals.Select(n=>
                {
                    // Normals are covectors. Use inverse-transpose for scale/shear;
                    // reflected triangle winding was already corrected above.
                    if(n==null)return null;
                    var d=new[]{inverse[0]*n[0]+inverse[1]*n[1]+inverse[2]*n[2],
                        inverse[3]*n[0]+inverse[4]*n[1]+inverse[5]*n[2],
                        inverse[6]*n[0]+inverse[7]*n[1]+inverse[8]*n[2]};
                    double length=Math.Sqrt(d[0]*d[0]+d[1]*d[1]+d[2]*d[2]);if(length==0)return null;
                    return new[]{d[0]/length,d[1]/length,d[2]/length};
                }).ToArray();
            }
            return result;
        }
    }
    public sealed class VehicleMeshScene
    {
        public string schema;
        public string vehicleFrameId;
        public string unit;
        public string coordinates;
        public string sourceAssembly;
        public string sourceAssemblySha256;
        public MeshShape[] shapes;
        public void Validate()
        {
            if(schema!="cad-drop.vehicle-mesh.v1"||unit!="mm"||coordinates!="vehicle"||string.IsNullOrWhiteSpace(vehicleFrameId)
                ||string.IsNullOrWhiteSpace(sourceAssembly)||string.IsNullOrWhiteSpace(sourceAssemblySha256)||shapes==null||shapes.Length==0)
                throw new InvalidDataException("Associated CATProduct vehicle frame is required");
            if(shapes.Select(s=>s.instanceId).Distinct().Count()!=shapes.Length)throw new InvalidDataException("Duplicate instance IDs");
            foreach(var s in shapes)s.Validate();
        }
    }

    // One placement of a shared source definition. The definition mesh is
    // written once in source coordinates; the frame places it in the scene.
    public sealed class AssemblyInstance
    {
        public int Definition;
        public string Name;
        public double[] Frame;
    }

    // Format-only output explicitly retains source coordinates. Vehicle scene
    // validation remains strict and is not bypassed by a fabricated assembly.
    public sealed class SourceMeshScene
    {
        public string name;
        public string sourceFile;
        public string sourceSha256;
        public string unit = "mm";
        public MeshShape[] shapes;
        // Source product structure for assembly imports. Null keeps the flat
        // single-level output used by part conversion and older manifests.
        public AssemblyStructureNode structure;
        // Baked shapes per flat manifest record; assembly part nodes index it.
        public MeshShape[][] partShapes;
        // Instanced assembly output: definitions are local meshes written once,
        // instances carry the placement frame and refer to a definition.
        public MeshShape[][] definitions;
        public AssemblyInstance[] instances;
        public void Validate()
        {
            if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(sourceFile)||string.IsNullOrWhiteSpace(sourceSha256)
                ||unit!="mm"||shapes==null||shapes.Length==0)throw new InvalidDataException("Incomplete source-coordinate scene");
            if(shapes.Select(s=>s.instanceId).Distinct().Count()!=shapes.Length)throw new InvalidDataException("Duplicate source instance IDs");
            foreach(var shape in shapes)shape.Validate();
            if(instances!=null)ValidateInstances();
            else if(structure!=null)ValidateStructure();
        }
        private void ValidateInstances()
        {
            if(definitions==null||definitions.Length==0)throw new InvalidDataException("Instanced assembly has no definitions");
            if(structure==null)throw new InvalidDataException("Instanced assembly has no hierarchy");
            if(instances==null||instances.Length==0)throw new InvalidDataException("Instanced assembly has no placements");
            var usedInstances=new bool[instances.Length];
            var usedDefinitions=new bool[definitions.Length];
            int nodes=0;
            void Check(AssemblyStructureNode node)
            {
                if(++nodes>200000)throw new InvalidDataException("Assembly hierarchy is too large");
                if(node.IsPart)
                {
                    if(node.PartIndex<0||node.PartIndex>=instances.Length)
                        throw new InvalidDataException("Assembly hierarchy references an unknown placement");
                    if(usedInstances[node.PartIndex])throw new InvalidDataException("Assembly hierarchy reuses a placement");
                    usedInstances[node.PartIndex]=true;
                    var instance=instances[node.PartIndex];
                    if(instance==null||instance.Definition<0||instance.Definition>=definitions.Length)
                        throw new InvalidDataException("Assembly placement references an unknown definition");
                    if(instance.Frame==null||instance.Frame.Length!=12||instance.Frame.Any(v=>double.IsNaN(v)||double.IsInfinity(v)))
                        throw new InvalidDataException("Assembly placement frame is invalid");
                    if(node.Children.Count!=0)throw new InvalidDataException("Assembly part cannot have children");
                    if(definitions[instance.Definition].Length!=0)usedDefinitions[instance.Definition]=true;
                    return;
                }
                foreach(var child in node.Children)Check(child);
            }
            Check(structure);
            for(int i=0;i<definitions.Length;i++)
            {
                if(definitions[i]==null)throw new InvalidDataException("Assembly definition is missing");
                foreach(var shape in definitions[i])shape.Validate();
                if(definitions[i].Length!=0&&!usedDefinitions[i])
                    throw new InvalidDataException("Assembly hierarchy omits a geometry definition");
            }
            for(int i=0;i<shapes.Length;i++)
                if(shapes[i]==null)throw new InvalidDataException("Assembly definition shape is missing");
        }
        private void ValidateStructure()
        {
            if(partShapes==null||partShapes.Length==0)throw new InvalidDataException("Assembly hierarchy has no geometry records");
            var used=new bool[partShapes.Length];
            int nodes=0;
            void Check(AssemblyStructureNode node, bool root)
            {
                if(++nodes>200000)throw new InvalidDataException("Assembly hierarchy is too large");
                if(node==null||node.IsPart)
                {
                    if(node==null||node.PartIndex<0||node.PartIndex>=partShapes.Length)
                        throw new InvalidDataException("Assembly hierarchy references an unknown part");
                    if(used[node.PartIndex])throw new InvalidDataException("Assembly hierarchy reuses a part record");
                    used[node.PartIndex]=true;
                    if(node.Children.Count!=0)throw new InvalidDataException("Assembly part cannot have children");
                    return;
                }
                if(root&&node.Children.Count==0)throw new InvalidDataException("Assembly hierarchy is empty");
                foreach(var child in node.Children)Check(child,false);
            }
            Check(structure,true);
            for(int i=0;i<partShapes.Length;i++)
                if(partShapes[i]!=null&&partShapes[i].Length!=0&&!used[i])
                    throw new InvalidDataException("Assembly hierarchy omits a geometry record");
        }
        public static SourceMeshScene ReadPart(string path)
        {
            path=Path.GetFullPath(path);
            if(!Path.GetExtension(path).Equals(".CATPart",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("Direct part conversion requires CATPart");
            byte[] data=File.ReadAllBytes(path);string hash;
            using(var sha=System.Security.Cryptography.SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            string name=Path.GetFileNameWithoutExtension(path);
            var mesh=NativeCadReader.Mesh(NativeCadReader.EmbeddedCgr(data),name,"source/1",path,hash);
            // Geometry and digest come from the same immutable byte snapshot.
            return new SourceMeshScene{name=name,sourceFile=path,sourceSha256=hash,shapes=new[]{mesh}};
        }
    }
}
