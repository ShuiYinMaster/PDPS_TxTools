using System;using System.IO;using System.Linq;using System.Collections.Generic;using System.Reflection;using System.Globalization;using TxTools.ExportByColor;
class DirectHoleCgrRegression {
 static string Point(float[] p){return string.Join(",",p.Select(x=>BitConverter.ToInt32(BitConverter.GetBytes(x),0).ToString("X8")));}
 static string Key(CgrWriter.Face f,List<float[]> v){var p=f.Idx.Select(i=>Point(v[i])).ToArray();int smallest=0;for(int i=1;i<3;i++)if(string.CompareOrdinal(p[i],p[smallest])<0)smallest=i;return p[smallest]+p[(smallest+1)%3]+p[(smallest+2)%3]+BitConverter.ToString(new[]{f.R,f.G,f.B,f.Opacity??255});}
 static void Add(Dictionary<string,int> d,string key,int sign){int n;d.TryGetValue(key,out n);n+=sign;if(n==0)d.Remove(key);else d[key]=n;}
 static int Main(string[] args){string bin=args.Length>4?args[4]:AppDomain.CurrentDomain.BaseDirectory;AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string path=Path.Combine(bin,new AssemblyName(e.Name).Name+".dll");return System.IO.File.Exists(path)?Assembly.LoadFrom(path):null;};try{Run(args);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static void Run(string[] args){Directory.CreateDirectory(args[2]);var mesh=JtDirectBridge.Read(args[1],args[0]);var faces=mesh.Faces.Select(f=>new CgrWriter.Face{Idx=f.Indices,R=f.R,G=f.G,B=f.B,Opacity=f.Alpha,Surface=f.Surface,Nx=f.Normal[0],Ny=f.Normal[1],Nz=f.Normal[2]}).ToList();
 var holes=RoundHoleReconstruction.Find(mesh.Vertices,faces,new RoundHoleReconstruction.Options{IncludeExternalCylinders=true});if(args.Length>3&&args[3]!="analyze"&&args[3]!="all")holes=holes.Where(h=>h.Surface==int.Parse(args[3])).ToList();Console.WriteLine("SOURCE vertices="+mesh.Vertices.Count+" faces="+faces.Count+" fittedCandidates="+holes.Count);
 var remesh=RoundHoleCgrMesh.Replace(mesh.Vertices,faces,holes,Console.WriteLine);if(remesh.Cylinders.Count==0)throw new Exception("No safely replaced cylindrical walls");
 if(args.Length>3&&args[3]=="analyze")return;
 if(remesh.Faces.Count!=faces.Count-remesh.RemovedWallTriangles-remesh.ReplacedCapTriangles+remesh.AddedWallTriangles+remesh.AddedCapTriangles)throw new Exception("Triangle accounting mismatch");
 for(int i=0;i<mesh.Vertices.Count;i++)if(!object.ReferenceEquals(mesh.Vertices[i],remesh.Vertices[i]))throw new Exception("Source vertex array mutated");
 string path=Path.Combine(args[2],"fixture_holes.cgr");CgrWriter.BuildFileWithCylinders(remesh.Vertices,remesh.Faces,path,remesh.Cylinders,Console.WriteLine);
 RoundHoleReconstruction.WriteCsv(Path.Combine(args[2],"fitted_holes.csv"),holes);
 System.IO.File.WriteAllLines(Path.Combine(args[2],"replaced_cylinders.csv"),new[]{"source_shape,cylinder_shape,cx,cy,cz,ax,ay,az,radius,depth,direction"}.Concat(remesh.Cylinders.Values.Select(c=>c.SourceSurface+","+c.Surface+","+string.Join(",",c.Center.Concat(c.Axis).Concat(new[]{c.Radius,c.Depth}).Select(v=>v.ToString("R",CultureInfo.InvariantCulture)))+","+(c.ExternalCylinder?"outer":"inner"))));
 var delta=new Dictionary<string,int>();foreach(var f in remesh.Faces)Add(delta,Key(f,remesh.Vertices),1);
 var data=System.IO.File.ReadAllBytes(path);var actualVertices=new List<float[]>();var actualFaces=new List<CgrWriter.Face>();int leaves=0;
 for(int p=0;p+46<=data.Length;){if(data[p]!=0x95||data[p+1]!=255||data[p+41]!=255||data[p+42]!=255||data[p+43]!=2||data[p+44]!=0||data[p+45]!=1){p++;continue;}int end=checked(p+(int)BitConverter.ToUInt32(data,p+2)+1);typeof(FeatureTestDecoder).GetMethod("ReadLeaf",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{data,p+41,end,leaves++,actualVertices,actualFaces});p=end;}
 foreach(var f in actualFaces)Add(delta,Key(f,actualVertices),-1);Console.WriteLine("READBACK triangles="+actualFaces.Count+" orientedXYZ_RGBA_differences="+delta.Count);if(delta.Count!=0||actualFaces.Count!=remesh.Faces.Count)throw new Exception("CGR readback mismatch");
 int cylinderRecords=0,circleRecords=0;var cylinderCenters=new Dictionary<string,int>();
 Func<int,float> be=p=>{var b=new[]{data[p+3],data[p+2],data[p+1],data[p]};return BitConverter.ToSingle(b,0);};
 foreach(var c in remesh.Cylinders.Values){double n=Math.Sqrt(c.Axis.Sum(x=>x*x));Add(cylinderCenters,Point(c.Center.Select(x=>(float)x).ToArray())+Point(new[]{(float)(c.Axis[0]/n),(float)(c.Axis[1]/n),(float)(c.Axis[2]<0?-c.Radius:c.Radius)}),1);}
 for(int p=0;p+28<=data.Length;p++){if(data[p+1]==0&&data[p+2]==0x33){if(data[p]==26&&data[p+3]==0x33){cylinderRecords++;var values=Enumerable.Range(0,6).Select(i=>be(p+4+4*i)).ToArray();Add(cylinderCenters,Point(values.Take(3).ToArray())+Point(values.Skip(3).ToArray()),-1);}if(data[p]==18&&data[p+3]==0x37)circleRecords++;}}
 if(cylinderCenters.Count!=0)throw new Exception("Missing native cylinder parameters");
 Console.WriteLine("CANONICAL cylinderFaces="+cylinderRecords+" circularEdges="+circleRecords);if(cylinderRecords<remesh.Cylinders.Count||circleRecords<2*remesh.Cylinders.Count)throw new Exception("Missing native measurement attributes");
 var detailShapes=new[]{14,65,remesh.Cylinders.Values.Where(c=>c.ExternalCylinder).OrderByDescending(c=>c.Radius).Select(c=>c.SourceSurface).FirstOrDefault()}.Distinct();
 foreach(int shape in detailShapes){
 var detailCylinders=remesh.Cylinders.Where(c=>c.Value.SourceSurface==shape).ToDictionary(c=>c.Key,c=>c.Value);if(detailCylinders.Count==0)continue;var detail=remesh.Faces.Where(f=>f.Surface==shape||detailCylinders.ContainsKey(f.Surface)).ToList();var detailVertices=new List<float[]>();var map=new Dictionary<int,int>();for(int i=0;i<detail.Count;i++){var face=detail[i];var ids=new int[3];for(int k=0;k<3;k++){int mapped;if(!map.TryGetValue(face.Idx[k],out mapped)){mapped=detailVertices.Count;map.Add(face.Idx[k],mapped);detailVertices.Add(remesh.Vertices[face.Idx[k]]);}ids[k]=mapped;}face.Idx=ids;detail[i]=face;}CgrWriter.BuildFileWithCylinders(detailVertices,detail,Path.Combine(args[2],"cylinder_detail_"+shape+".cgr"),detailCylinders,Console.WriteLine);}
 Console.WriteLine("PASS direct full-fixture CGR with remeshed cylinder walls and joined circular cap rims; unmodified source mesh; no CATPart companion");
 }
}
