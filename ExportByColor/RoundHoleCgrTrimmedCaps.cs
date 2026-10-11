using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.ExportByColor
{
    public static partial class RoundHoleCgrMesh
    {
        // Cut a curved, triangulated cap along the cylinder. Projecting along
        // the proven cylinder axis gives a height field; each retained cap
        // triangle stays on its own source plane and keeps its own material.
        sealed class TrimEnd
        {
            internal readonly HashSet<int> Faces=new HashSet<int>();
            internal bool Open,Inside;internal int[] Ring;
        }
        static double Cross2(double[] a,double[] b,double[] p){return (b[0]-a[0])*(p[1]-a[1])-(b[1]-a[1])*(p[0]-a[0]);}
        static double TrimCoordinateResolution(float value)
        {
            float magnitude=Math.Abs(value);int bits=BitConverter.ToInt32(BitConverter.GetBytes(magnitude),0);
            return (double)BitConverter.ToSingle(BitConverter.GetBytes(bits+1),0)-magnitude;
        }
        static bool ClipSegment(double[] a,double[] b,List<double[]> polygon,double sign,out double lo,out double hi)
        {
            lo=0;hi=1;
            for(int i=0;i<polygon.Count;i++)
            {
                double da=sign*Cross2(polygon[i],polygon[(i+1)%polygon.Count],a),db=sign*Cross2(polygon[i],polygon[(i+1)%polygon.Count],b);
                if(da<-1e-10&&db<-1e-10)return false;
                if((da<0)!=(db<0)){double t=da/(da-db);if(da<0)lo=Math.Max(lo,t);else hi=Math.Min(hi,t);}
                if(hi<lo)return false;
            }
            return hi-lo>1e-10;
        }
        static bool TryClipTrimmedHole(List<float[]> originalVertices,List<CgrWriter.Face> originalFaces,Result result,RoundHoleReconstruction.Hole h,out string failure)
        {
            failure="source-wall";
            var references=new Dictionary<int[],int>();for(int i=0;i<result.Faces.Count;i++)if(result.Faces[i].Surface==h.Surface)references[result.Faces[i].Idx]=i;
            var walls=new HashSet<int>();foreach(int fi in h.WallTriangles){int current;if(!references.TryGetValue(originalFaces[fi].Idx,out current))return false;walls.Add(current);}
            var material=result.Faces[walls.First()];
            if((material.Opacity??255)==0||walls.Any(fi=>result.Faces[fi].R!=material.R||result.Faces[fi].G!=material.G||result.Faces[fi].B!=material.B||(result.Faces[fi].Opacity??255)!=(material.Opacity??255))){failure="wall-material";return false;}
            var source=result.Vertices;var faces=result.Faces;var output=new List<float[]>(source);
            var p=new Plan{Hole=h};p.U=Unit(Cross(h.Axis,Math.Abs(h.Axis[0])<.8?new[]{1d,0d,0d}:new[]{0d,1d,0d}));p.V=Cross(h.Axis,p.U);
            Func<double[],double[]> xy=point=>{var d=Sub(point,h.Center);return new[]{Dot(d,p.U),Dot(d,p.V)};};
            var coordinates=new Dictionary<Tuple<float,float,float>,int>();var points=new List<double[]>();
            Func<double[],int> coordinate=point=>{var key=Tuple.Create((float)point[0],(float)point[1],(float)point[2]);int id;if(!coordinates.TryGetValue(key,out id)){id=points.Count;coordinates.Add(key,id);points.Add(point);}return id;};
            // Index only cap Shapes touching the original exact rim. Indexing
            // millions of already refined cylinder triangles for each failed
            // candidate would waste memory in multi-resource production runs.
            var rawRims=h.RimPointsA.Concat(h.RimPointsB).ToList();var rawKeys=new HashSet<Tuple<float,float,float>>(rawRims.Select(v=>Tuple.Create((float)v[0],(float)v[1],(float)v[2])));
            var minimum=Enumerable.Range(0,3).Select(k=>rawRims.Min(v=>v[k])-.0001).ToArray();var maximum=Enumerable.Range(0,3).Select(k=>rawRims.Max(v=>v[k])+.0001).ToArray();
            var rawIds=new HashSet<int>();for(int i=0;i<originalVertices.Count;i++){var v=originalVertices[i];if(v[0]>=minimum[0]&&v[0]<=maximum[0]&&v[1]>=minimum[1]&&v[1]<=maximum[1]&&v[2]>=minimum[2]&&v[2]<=maximum[2]&&rawKeys.Contains(Tuple.Create(v[0],v[1],v[2])))rawIds.Add(i);}
            var originalWalls=new HashSet<int>(h.WallTriangles);var capShapes=new HashSet<int>();for(int fi=0;fi<originalFaces.Count;fi++)if(!originalWalls.Contains(fi)&&originalFaces[fi].Idx.Count(v=>rawIds.Contains(v))>=2)capShapes.Add(originalFaces[fi].Surface);
            var incidence=new Dictionary<long,List<int>>();var local=new Dictionary<int,int[]>();
            for(int fi=0;fi<faces.Count;fi++)if(capShapes.Contains(faces[fi].Surface)&&!walls.Contains(fi))
            {
                local[fi]=faces[fi].Idx.Select(v=>coordinate(D(source[v]))).ToArray();
                for(int k=0;k<3;k++){long key=Edge(local[fi][k],local[fi][(k+1)%3]);List<int> list;if(!incidence.TryGetValue(key,out list))incidence.Add(key,list=new List<int>());list.Add(fi);}
            }
            var ends=new[]{new TrimEnd(),new TrimEnd()};var rims=new[]{h.RimPointsA,h.RimPointsB};
            for(int ring=0;ring<2;ring++)
            {
                var seeds=new HashSet<int>();int absent=0;int shape=-1;
                foreach(var i in Enumerable.Range(0,rims[ring].Length))
                {
                    int a=coordinate(rims[ring][i]),b=coordinate(rims[ring][(i+1)%rims[ring].Length]);List<int> matches;
                    if(!incidence.TryGetValue(Edge(a,b),out matches)){absent++;continue;}
                    if(matches.Count!=1){failure="ambiguous-cap-edge";return false;}
                    int fi=matches[0];if(shape<0)shape=faces[fi].Surface;else if(faces[fi].Surface!=shape){failure="split-cap-shapes";return false;}seeds.Add(fi);
                }
                if(absent!=0)
                {
                    if(absent==rims[ring].Length&&(ring==0?h.OpenRimA:h.OpenRimB)){ends[ring].Open=true;continue;}
                    failure="partial-cap-rim";return false;
                }
                double sign=Dot(Unit(Normal(source,faces[seeds.First()].Idx)),h.Axis)>0?1:-1;
                var pending=new Queue<int>(seeds);
                while(pending.Count>0)
                {
                    int fi=pending.Dequeue();if(ends[ring].Faces.Contains(fi))continue;
                    var n=Normal(source,faces[fi].Idx);double length=Math.Sqrt(Dot(n,n));
                    if(faces[fi].Surface!=shape||length==0||sign*Dot(n,h.Axis)/length<=.15)continue;
                    ends[ring].Faces.Add(fi);
                    for(int k=0;k<3;k++)foreach(int next in incidence[Edge(local[fi][k],local[fi][(k+1)%3])])if(!ends[ring].Faces.Contains(next))pending.Enqueue(next);
                }
                if(!seeds.All(fi=>ends[ring].Faces.Contains(fi))){failure="cap-height-field";return false;}
                // A counterbore step fills the inside of its outer rim; a hole
                // cap fills the outside. Determine the filled side from each
                // source rim edge and its adjacent triangle, before choosing
                // the cap algorithm. A nearly planar end may still cross several
                // facet planes and must not be flattened as an annular step.
                bool? inside=null;
                for(int i=0;i<rims[ring].Length;i++)
                {
                    int a=coordinate(rims[ring][i]),b=coordinate(rims[ring][(i+1)%rims[ring].Length]);int fi=incidence[Edge(a,b)][0];
                    int third=faces[fi].Idx.First(v=>{int id=coordinate(D(source[v]));return id!=a&&id!=b;});
                    var first=xy(rims[ring][i]);var last=xy(rims[ring][(i+1)%rims[ring].Length]);double centerSide=Cross2(first,last,new[]{0d,0d});double filledSide=Cross2(first,last,xy(D(source[third])));
                    if(centerSide==0||filledSide==0){failure="cap-side-degenerate";return false;}
                    bool value=centerSide*filledSide>0;if(inside.HasValue&&inside.Value!=value){failure="mixed-cap-side";return false;}inside=value;
                }
                ends[ring].Inside=inside.Value;
                if(ends[ring].Inside&&(ring==0?h.TrimmedRimA:h.TrimmedRimB)){failure="trimmed-inner-step";return false;}
            }
            if(ends[0].Faces.Overlaps(ends[1].Faces)){failure="same-cap-on-both-ends";return false;}
            var angles=rims.SelectMany(r=>r).Select(v=>Angle(p,v)).ToList();
            var originalRimIds=new HashSet<int>(rims.SelectMany(r=>r).Select(coordinate));
            Func<double[],double[]> edgeXY=point=>{if(!originalRimIds.Contains(coordinate(point)))return xy(point);double angle=Angle(p,point);return new[]{h.Radius*Math.Cos(angle),h.Radius*Math.Sin(angle)};};
            // Include each original cap-facet/circle intersection. Wall and cap
            // then share vertices exactly where the curved cap changes plane.
            foreach(int fi in ends.SelectMany(e=>e.Faces))for(int k=0;k<3;k++)
            {
                var a=edgeXY(D(source[faces[fi].Idx[k]]));var b=edgeXY(D(source[faces[fi].Idx[(k+1)%3]]));double dx=b[0]-a[0],dy=b[1]-a[1],aa=dx*dx+dy*dy,bb=2*(a[0]*dx+a[1]*dy),cc=a[0]*a[0]+a[1]*a[1]-h.Radius*h.Radius;
                double discriminant=bb*bb-4*aa*cc;if(aa==0||discriminant<0)continue;
                foreach(double t in new[]{(-bb-Math.Sqrt(discriminant))/(2*aa),(-bb+Math.Sqrt(discriminant))/(2*aa)})if(t>1e-7&&t<1-1e-7){double angle=Math.Atan2(a[1]+t*dy,a[0]+t*dx);angles.Add(angle<0?angle+2*Math.PI:angle);}
            }
            angles.Sort();var unique=new List<double>();double merge=Math.Max(1e-7,.0001/h.Radius);
            foreach(double a in angles)if(unique.Count==0||a-unique[unique.Count-1]>merge)unique.Add(a);
            if(unique.Count>1&&2*Math.PI+unique[0]-unique[unique.Count-1]<=merge)unique.RemoveAt(unique.Count-1);
            double step=Math.Min(2*Math.PI/96,2*Math.Acos(Math.Max(-1,1-.0019/h.Radius)));if(step<2*Math.PI/2048){failure="display-resolution";return false;}
            p.Angles=new List<double>();for(int i=0;i<unique.Count;i++){double a=unique[i],b=i+1<unique.Count?unique[i+1]:unique[0]+2*Math.PI;int count=(int)Math.Ceiling((b-a)/step);for(int k=0;k<count;k++)p.Angles.Add((a+(b-a)*k/count)%(2*Math.PI));}p.Angles.Sort();
            var snaps=new Dictionary<int,int>();
            for(int ring=0;ring<2;ring++)
            {
                ends[ring].Ring=new int[p.Angles.Count];
                for(int i=0;i<p.Angles.Count;i++)
                {
                    double angle=p.Angles[i];var radial=h.Center.Select((c,k)=>c+h.Radius*(Math.Cos(angle)*p.U[k]+Math.Sin(angle)*p.V[k])).ToArray();double height;
                    if(ends[ring].Open||ends[ring].Inside)height=RimHeight(p,ring,angle);
                    else if(!CapHeight(radial,xy(radial),ends[ring].Faces,source,faces,h.Axis,xy,out height)){failure="cap-height-ambiguous-end-"+ring+"-angle-"+angle.ToString("R",System.Globalization.CultureInfo.InvariantCulture);return false;}
                    ends[ring].Ring[i]=output.Count;output.Add(radial.Select((c,k)=>(float)(c+height*h.Axis[k])).ToArray());
                }
            }
            // Float32 rounding can make extremely close facet-intersection
            // samples form a tiny reflex corner. Remove such intermediate
            // samples in both rings, keeping one shared convex display contour.
            var keep=Enumerable.Range(0,p.Angles.Count).ToList();bool removed;
            do{removed=false;for(int i=0;i<keep.Count;i++)if(Enumerable.Range(0,2).Any(ring=>Cross2(xy(D(output[ends[ring].Ring[keep[(i+keep.Count-1)%keep.Count]]])),xy(D(output[ends[ring].Ring[keep[i]]])),xy(D(output[ends[ring].Ring[keep[(i+1)%keep.Count]]])))<=0)){keep.RemoveAt(i);removed=true;break;}}while(removed&&keep.Count>=96);
            if(keep.Count<96){failure="display-convexity";return false;}
            p.Angles=keep.Select(i=>p.Angles[i]).ToList();for(int ring=0;ring<2;ring++){ends[ring].Ring=keep.Select(i=>ends[ring].Ring[i]).ToArray();foreach(var point in rims[ring])snaps[coordinate(point)]=ends[ring].Ring[Closest(p.Angles,Angle(p,point))];}
            for(int i=0;i<p.Angles.Count;i++){double gap=(i+1<p.Angles.Count?p.Angles[i+1]:p.Angles[0]+2*Math.PI)-p.Angles[i];if(h.Radius*(1-Math.Cos(gap/2))>.00200001){failure="display-sag";return false;}}
            double direction=Dot(Sub(h.RimB,h.RimA),h.Axis)>0?1:-1;
            for(int i=0;i<p.Angles.Count;i++)if(direction*Dot(Sub(D(output[ends[1].Ring[i]]),D(output[ends[0].Ring[i]])),h.Axis)<=.02){failure="crossing-ends";return false;}
            var replacement=new Dictionary<int,List<CgrWriter.Face>>();
            p.Rings=new[]{ends[0].Ring,ends[1].Ring};
            for(int ring=0;ring<2;ring++)
            {
                if(ends[ring].Open)continue;
                if(ends[ring].Inside)
                {
                    // A planar counterbore end is an annular step: its rim can
                    // be the cap's outer boundary. The existing whole-patch
                    // triangulator supports both inner and outer circular rims.
                    var rimEdges=new Dictionary<long,RimEdge>();var capEdges=new Dictionary<int,List<RimEdge>>();
                    for(int i=0;i<rims[ring].Length;i++)
                    {
                        int a=coordinate(rims[ring][i]),b=coordinate(rims[ring][(i+1)%rims[ring].Length]);int cap=incidence[Edge(a,b)][0];
                        var edge=new RimEdge{Plan=p,Ring=ring,A=a,B=b,Cap=cap,Arc=Arc(p,ring,Angle(p,rims[ring][i]),Angle(p,rims[ring][(i+1)%rims[ring].Length]))};rimEdges.Add(Edge(a,b),edge);
                        List<RimEdge> list;if(!capEdges.TryGetValue(cap,out list))capEdges.Add(cap,list=new List<RimEdge>());list.Add(edge);
                    }
                    var wallPlans=walls.ToDictionary(fi=>fi,fi=>p);var patches=BuildCapPatches(source,faces,wallPlans,capEdges,(s,point)=>coordinate(point),points,new HashSet<int>());
                    var mapped=new Dictionary<Tuple<int,float,float,float>,int>();foreach(int fi in ends[ring].Faces)foreach(int v in faces[fi].Idx){int next;if(snaps.TryGetValue(coordinate(D(source[v])),out next))mapped[Key(faces[fi].Surface,D(source[v]))]=next;}
                    foreach(var patch in patches)
                    {
                        string reason;var triangles=TriangulateCapPatch(patch,source,output,faces,rimEdges,points,mapped,out reason);if(triangles==null){failure="planar-step-"+reason;return false;}
                        foreach(int fi in patch.Faces)replacement[fi]=new List<CgrWriter.Face>();ends[ring].Faces.UnionWith(patch.Faces);
                        var materialFace=faces[patch.Faces[0]];replacement[patch.Faces[0]]=triangles.Select(t=>{var copy=Triangle(materialFace,t[0],t[1],t[2]);var normal=Unit(Normal(output,t));copy.Nx=(float)normal[0];copy.Ny=(float)normal[1];copy.Nz=(float)normal[2];return copy;}).ToList();
                    }
                    continue;
                }
                var hull=ends[ring].Ring.Select(v=>xy(D(output[v]))).ToList();
                // The cap-edge root can move along a nearly tangent edge when
                // Float32 rounding removes a close circle sample. Associate it
                // with that same edge, within two coordinate ULPs, rather than
                // accepting an arbitrary nearby circle point. Full seam and
                // source-plane checks below still have to pass atomically.
                double coordinateBudget=Math.Max(1e-7,2*ends[ring].Faces.SelectMany(fi=>faces[fi].Idx).SelectMany(v=>source[v]).Max(TrimCoordinateResolution));
                foreach(int fi in ends[ring].Faces)
                {
                    var f=faces[fi];var ids=f.Idx.Select(v=>{int mapped;return snaps.TryGetValue(coordinate(D(source[v])),out mapped)?mapped:v;}).ToArray();
                    var triangle=ids.Select(v=>xy(D(output[v]))).ToList();double sign=Cross2(triangle[0],triangle[1],triangle[2])>0?1:-1;
                    var boundary=new List<int[]>();bool touched=false;
                    Func<double[],int> ringPoint=point=>
                    {
                        int closest=Closest(p.Angles,Math.Atan2(point[1],point[0]));int id=ends[ring].Ring[closest];var q=hull[closest];
                        double distance=Math.Sqrt((q[0]-point[0])*(q[0]-point[0])+(q[1]-point[1])*(q[1]-point[1]));if(distance<=.001)return id;if(distance>.01)return -1;
                        for(int edge=0;edge<3;edge++)
                        {
                            var a=triangle[edge];var b=triangle[(edge+1)%3];double dx=b[0]-a[0],dy=b[1]-a[1],length=Math.Sqrt(dx*dx+dy*dy);if(length==0)continue;
                            double parameter=((q[0]-a[0])*dx+(q[1]-a[1])*dy)/(length*length);
                            if(parameter>=0&&parameter<=1&&Math.Abs(Cross2(a,b,point))/length<=coordinateBudget&&Math.Abs(Cross2(a,b,q))/length<=coordinateBudget)return id;
                        }
                        return -1;
                    };
                    for(int k=0;k<3;k++)
                    {
                        int next=(k+1)%3;double lo,hi;
                        if(!ClipSegment(triangle[k],triangle[next],hull,1,out lo,out hi)){boundary.Add(new[]{ids[k],ids[next]});continue;}
                        touched=true;
                        if(lo>1e-8){var point=new[]{triangle[k][0]+lo*(triangle[next][0]-triangle[k][0]),triangle[k][1]+lo*(triangle[next][1]-triangle[k][1])};int end=ringPoint(point);if(end<0){failure="lost-facet-intersection";return false;}if(end!=ids[k])boundary.Add(new[]{ids[k],end});}
                        if(hi<1-1e-8){var point=new[]{triangle[k][0]+hi*(triangle[next][0]-triangle[k][0]),triangle[k][1]+hi*(triangle[next][1]-triangle[k][1])};int start=ringPoint(point);if(start<0){failure="lost-facet-intersection";return false;}if(start!=ids[next])boundary.Add(new[]{start,ids[next]});}
                    }
                    for(int k=0;k<hull.Count;k++)
                    {
                        int next=(k+1)%hull.Count;double lo,hi;if(!ClipSegment(hull[k],hull[next],triangle,sign,out lo,out hi))continue;touched=true;
                        var a=new[]{hull[k][0]+lo*(hull[next][0]-hull[k][0]),hull[k][1]+lo*(hull[next][1]-hull[k][1])};var b=new[]{hull[k][0]+hi*(hull[next][0]-hull[k][0]),hull[k][1]+hi*(hull[next][1]-hull[k][1])};int first=ringPoint(a),last=ringPoint(b);
                        if(first<0||last<0){failure="lost-arc-intersection";return false;}if(first!=last)boundary.Add(sign>0?new[]{last,first}:new[]{first,last});
                    }
                    if(!touched&&ids.SequenceEqual(f.Idx))continue;
                    List<int[]> triangles;var n=Normal(source,f.Idx);
                    if(!TriangulateTrimBoundary(boundary,output,n,xy,sign,out triangles)){failure="cap-cut-topology";return false;}
                    double normalLength=Math.Sqrt(Dot(n,n));var planeOrigin=D(source[f.Idx[0]]);
                    if(normalLength==0||triangles.SelectMany(t=>t).Any(v=>Math.Abs(Dot(Sub(D(output[v]),planeOrigin),n))/normalLength>.001)){failure="cap-source-plane-drift";return false;}
                    replacement[fi]=triangles.Select(t=>{var copy=Triangle(f,t[0],t[1],t[2]);var normal=Unit(Normal(output,t));copy.Nx=(float)normal[0];copy.Ny=(float)normal[1];copy.Nz=(float)normal[2];return copy;}).ToList();
                }
            }
            int surface=checked(faces.Max(f=>f.Surface)+1);var newWall=new List<CgrWriter.Face>();material.Surface=surface;
            for(int i=0;i<p.Angles.Count;i++){int j=(i+1)%p.Angles.Count;foreach(var t in new[]{new[]{ends[0].Ring[i],ends[0].Ring[j],ends[1].Ring[j]},new[]{ends[0].Ring[i],ends[1].Ring[j],ends[1].Ring[i]}}){var radial=Sub(D(output[t[0]]),h.Center);double axial=Dot(radial,h.Axis);for(int k=0;k<3;k++)radial[k]-=axial*h.Axis[k];if(Dot(Normal(output,t),radial)>0){int swap=t[1];t[1]=t[2];t[2]=swap;}var n=Unit(Normal(output,t));material.Nx=(float)n[0];material.Ny=(float)n[1];material.Nz=(float)n[2];newWall.Add(Triangle(material,t[0],t[1],t[2]));}}
            // Check the complete affected cap region plus wall, including all
            // previously rebuilt neighboring holes. Only original outer region
            // boundaries and genuinely source-open ends may remain one-sided.
            var beforeRegion=ends.SelectMany(e=>e.Faces).Concat(walls).Distinct().Select(fi=>faces[fi]).ToList();
            var afterRegion=ends.SelectMany(e=>e.Faces).Distinct().SelectMany(fi=>replacement.ContainsKey(fi)?replacement[fi]:new List<CgrWriter.Face>{faces[fi]}).Concat(newWall).ToList();
            var beforeEdges=TrimEdgeCounts(beforeRegion,source);var afterEdges=TrimEdgeCounts(afterRegion,output);var allowed=new HashSet<string>(beforeEdges.Where(e=>e.Value==1).Select(e=>e.Key));
            for(int ring=0;ring<2;ring++)if(ends[ring].Open)for(int i=0;i<p.Angles.Count;i++)allowed.Add(TrimEdgeKey(output[ends[ring].Ring[i]],output[ends[ring].Ring[(i+1)%p.Angles.Count]]));
            if(afterEdges.Any(e=>e.Value>2||(e.Value==1&&!allowed.Contains(e.Key)))){failure="cap-seam";return false;}
            var all=new List<CgrWriter.Face>();for(int fi=0;fi<faces.Count;fi++){if(walls.Contains(fi))continue;List<CgrWriter.Face> patch;if(replacement.TryGetValue(fi,out patch))all.AddRange(patch);else all.Add(faces[fi]);}all.AddRange(newWall);
            result.Vertices=output;result.Faces=all;result.RemovedWallTriangles+=walls.Count;result.ReplacedCapTriangles+=replacement.Count;result.AddedWallTriangles+=newWall.Count;result.AddedCapTriangles+=replacement.Values.Sum(list=>list.Count);
            result.Cylinders.Add(surface,new Cylinder{Surface=surface,SourceSurface=h.Surface,Center=h.Center,Axis=h.Axis,RimA=h.RimA,RimB=h.RimB,Radius=h.Radius,Depth=h.Depth,TrimmedRimA=h.TrimmedRimA,TrimmedRimB=h.TrimmedRimB});
            foreach(int fi in h.WallTriangles)result.ChangedSourceTriangles.Add(fi);
            var originals=new Dictionary<int[],int>();for(int fi=0;fi<originalFaces.Count;fi++)if(capShapes.Contains(originalFaces[fi].Surface))originals[originalFaces[fi].Idx]=fi;foreach(int fi in replacement.Keys){int original;if(originals.TryGetValue(faces[fi].Idx,out original))result.ChangedSourceTriangles.Add(original);}
            foreach(var patch in replacement){int original;if(!result.SourceFaceIndices.TryGetValue(faces[patch.Key].Idx,out original)&&!originals.TryGetValue(faces[patch.Key].Idx,out original))continue;foreach(var triangle in patch.Value)result.SourceFaceIndices[triangle.Idx]=original;}
            result.Diagnostics.Add("shape="+h.Surface+" radius="+h.Radius+" center="+string.Join(",",h.Center)+" trim-clip=accepted source cap facets retained");return true;
        }
        static bool CapHeight(double[] radial,double[] point,HashSet<int> region,List<float[]> source,List<CgrWriter.Face> faces,double[] axis,Func<double[],double[]> xy,out double height)
        {
            height=0;double best=double.NegativeInfinity;bool found=false;
            foreach(int fi in region)
            {
                var triangle=faces[fi].Idx.Select(v=>xy(D(source[v]))).ToList();double area=Cross2(triangle[0],triangle[1],triangle[2]);if(Math.Abs(area)<1e-16)continue;
                double score=double.PositiveInfinity;for(int k=0;k<3;k++){var a=triangle[k];var b=triangle[(k+1)%3];double length=Math.Sqrt((b[0]-a[0])*(b[0]-a[0])+(b[1]-a[1])*(b[1]-a[1]));if(length==0){score=double.NegativeInfinity;break;}score=Math.Min(score,(area>0?1:-1)*Cross2(a,b,point)/length);}
                if(score<-.001)continue;var n=Normal(source,faces[fi].Idx);double value=Dot(Sub(D(source[faces[fi].Idx[0]]),radial),n)/Dot(axis,n);
                if(found&&best>1e-5&&score>1e-5&&Math.Abs(value-height)>.01)return false;
                if(score>best){best=score;height=value;found=true;}
            }
            return found;
        }
        static bool TriangulateTrimBoundary(List<int[]> boundary,List<float[]> vertices,double[] normal,Func<double[],double[]> xy,double sign,out List<int[]> triangles)
        {
            triangles=new List<int[]>();if(boundary.Count==0)return true;
            var unique=boundary.GroupBy(e=>Edge(e[0],e[1])).ToList();if(unique.Any(g=>g.Count()>2||(g.Count()==2&&g.First()[0]==g.Last()[0])))return false;boundary=unique.Where(g=>g.Count()==1).Select(g=>g.First()).ToList();if(boundary.Count==0)return true;
            var outgoing=new Dictionary<int,int>();foreach(var e in boundary){if(outgoing.ContainsKey(e[0]))return false;outgoing.Add(e[0],e[1]);}
            var left=new HashSet<int>(outgoing.Keys);var loops=new List<List<int>>();
            while(left.Count>0){int start=left.First(),current=start;var loop=new List<int>();do{if(!left.Remove(current))return false;loop.Add(current);if(!outgoing.TryGetValue(current,out current))return false;}while(current!=start);if(loop.Count<3)return false;loops.Add(loop);}
            Func<List<int>,double> area=loop=>{double sum=0;for(int i=0;i<loop.Count;i++){var a=xy(D(vertices[loop[i]]));var b=xy(D(vertices[loop[(i+1)%loop.Count]]));sum+=a[0]*b[1]-b[0]*a[1];}return sign*sum/2;};
            var outers=loops.Where(l=>area(l)>1e-12).ToList();var holes=loops.Where(l=>area(l)<-1e-12).ToList();if(outers.Count+holes.Count!=loops.Count)return false;
            foreach(var outer in outers)
            {
                var polygon=new List<List<int>>{outer};foreach(var hole in holes)if(hole.All(v=>InsideCapLoop(xy(D(vertices[v])),outer,id=>xy(D(vertices[id])))))polygon.Add(hole);
                var ids=new List<int>();var starts=new List<int>();var coordinates=new List<double>();foreach(var loop in polygon){if(ids.Count>0)starts.Add(ids.Count);foreach(int id in loop){ids.Add(id);var q=xy(D(vertices[id]));coordinates.Add(q[0]);coordinates.Add(q[1]);}}
                var mesh=Vendor.EarCut.EarCut.Calculate(coordinates.ToArray(),starts.ToArray());if(mesh.Count==0||mesh.Count%3!=0)return false;
                for(int i=0;i<mesh.Count;i+=3){var t=new[]{ids[mesh[i]],ids[mesh[i+1]],ids[mesh[i+2]]};if(Dot(Normal(vertices,t),normal)<0){int swap=t[1];t[1]=t[2];t[2]=swap;}triangles.Add(t);}
            }
            string failure;triangles.RemoveAll(t=>Dot(Normal(vertices,t),normal)==0);if(!RestoreCapBoundary(triangles,loops,vertices,Unit(normal),out failure))return false;
            double expected=loops.Sum(area),actual=triangles.Sum(t=>Math.Abs(Cross2(xy(D(vertices[t[0]])),xy(D(vertices[t[1]])),xy(D(vertices[t[2]]))))/2);
            return triangles.All(t=>Dot(Normal(vertices,t),normal)>0)&&Math.Abs(expected-actual)<=Math.Max(1e-7,expected*1e-7);
        }
        static string TrimEdgeKey(float[] a,float[] b){Func<float[],string> key=p=>string.Join(",",p.Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));string first=key(a),last=key(b);return string.CompareOrdinal(first,last)<0?first+"|"+last:last+"|"+first;}
        static Dictionary<string,int> TrimEdgeCounts(List<CgrWriter.Face> faces,List<float[]> vertices){var result=new Dictionary<string,int>();foreach(var f in faces)for(int k=0;k<3;k++){string key=TrimEdgeKey(vertices[f.Idx[k]],vertices[f.Idx[(k+1)%3]]);int n;result.TryGetValue(key,out n);result[key]=n+1;}return result;}
    }
}
