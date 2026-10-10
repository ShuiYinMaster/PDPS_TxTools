using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using INFITF;
using MECMOD;
using HybridShapeTypeLib;
using SPATypeLib;

namespace TxTools.ExportByColor
{
    // Separate analytic reference geometry. This is not a solid-hole feature or a replacement for source CGR.
    public static class RoundHoleCatiaWriter
    {
        public sealed class Report
        {
            public int Circles, Cylinders;
            public double MaximumRadiusDifference, MaximumAxisLengthDifference;
            public double MaximumCylinderAreaRelativeDifference;
            public double MaximumCenterDifference, MaximumCircleLengthRelativeDifference;
        }
        public static Report WritePart(INFITF.Application app,List<RoundHoleReconstruction.Hole> holes,
            string output,bool includeCylinders=true,Action<string> log=null)
        {
            if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("CATIA reconstruction requires STA");
            if(app==null||holes==null||holes.Count==0)throw new ArgumentException("No circular hole candidates");
            output=Path.GetFullPath(output);if(System.IO.File.Exists(output))throw new IOException("Reconstruction output already exists: "+output);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var doc=(PartDocument)app.Documents.Add("Part");
            try
            {
                doc.Product.set_PartNumber("JT_Holes_"+Path.GetFileNameWithoutExtension(output));
                var part=doc.Part;var factory=(HybridShapeFactory)part.HybridShapeFactory;
                var body=part.HybridBodies.Add();body.set_Name("JT_Reconstructed_Hole_References");
                var spa=(SPAWorkbench)doc.GetWorkbench("SPAWorkbench");var report=new Report();int ordinal=0;var constructionPoints=new List<object>();
                foreach(var h in holes)
                {
                    ordinal++;if(!Finite(h.Radius)||h.Radius<=0||!Finite(h.Depth)||h.Depth<0)throw new ArgumentException("Invalid hole dimensions");
                    var set=body.HybridBodies.Add();set.set_Name("Shape_"+h.Surface+"_Hole_"+ordinal+"_D"+h.Radius*2);
                    var mid=factory.AddNewPointCoord(h.Center[0],h.Center[1],h.Center[2]);set.AppendHybridShape(mid);constructionPoints.Add(mid);
                    var direction=factory.AddNewDirectionByCoord(h.Axis[0],h.Axis[1],h.Axis[2]);
                    var centerReference=part.CreateReferenceFromObject(mid);
                    double length=h.PairedRims?h.Depth:h.Radius*2;
                    var axis=factory.AddNewLinePtDir(centerReference,direction,-length/2,length/2,false);set.AppendHybridShape(axis);axis.set_Name(h.PairedRims?"Fitted_Hole_Axis":"Direction_Only_No_Depth_Evidence");
                    var axisReference=part.CreateReferenceFromObject(axis);
                    part.UpdateObject(axis);if(log!=null)log("[Hole CATIA] hole="+ordinal+" axis ready");
                    foreach(var center in h.PairedRims?new[]{h.RimA,h.RimB}:new[]{h.RimA})
                    {
                        var point=factory.AddNewPointCoord(center[0],center[1],center[2]);set.AppendHybridShape(point);constructionPoints.Add(point);
                        var circle=factory.AddNewCircleCenterAxis(axisReference,part.CreateReferenceFromObject(point),h.Radius,false);
                        circle.SetLimitation(1);set.AppendHybridShape(circle);circle.set_Name("Fitted_Circular_Rim_"+(report.Circles+1));
                        if(log!=null)log("[Hole CATIA] update circle "+(report.Circles+1));part.UpdateObject(circle);var measured=spa.GetMeasurable(part.CreateReferenceFromObject(circle));
                        report.MaximumRadiusDifference=Math.Max(report.MaximumRadiusDifference,Math.Abs(measured.Radius-h.Radius));report.Circles++;
                        var measuredCenter=new object[]{0d,0d,0d};measured.GetCenter(measuredCenter);double centerError=0;for(int k=0;k<3;k++){double delta=Convert.ToDouble(measuredCenter[k])-center[k];centerError+=delta*delta;}report.MaximumCenterDifference=Math.Max(report.MaximumCenterDifference,Math.Sqrt(centerError));report.MaximumCircleLengthRelativeDifference=Math.Max(report.MaximumCircleLengthRelativeDifference,Math.Abs(measured.Length/(2*Math.PI*h.Radius)-1));
                    }
                    part.UpdateObject(axis);report.MaximumAxisLengthDifference=Math.Max(report.MaximumAxisLengthDifference,Math.Abs(spa.GetMeasurable(axisReference).Length-length));
                    if(includeCylinders&&h.PairedRims)
                    {
                        var cylinder=factory.AddNewCylinder(centerReference,h.Radius,h.Depth/2,h.Depth/2,direction);set.AppendHybridShape(cylinder);cylinder.set_Name("Fitted_Cylinder_Surface_Not_Solid");if(log!=null)log("[Hole CATIA] update cylinder "+(report.Cylinders+1));part.UpdateObject(cylinder);
                        // SPA reports area in m^2 while these CATIA length values are mm.
                        var measured=spa.GetMeasurable(part.CreateReferenceFromObject(cylinder));report.MaximumRadiusDifference=Math.Max(report.MaximumRadiusDifference,Math.Abs(measured.Radius-h.Radius));double expectedArea=2*Math.PI*h.Radius*h.Depth;double areaMm2=measured.Area*1000000;double areaError=Math.Abs(areaMm2/expectedArea-1);report.MaximumCylinderAreaRelativeDifference=Math.Max(report.MaximumCylinderAreaRelativeDifference,areaError);if(areaError>1e-8&&log!=null)log("[Hole CATIA] cylinder area(mm2)="+areaMm2+" expected="+expectedArea);report.Cylinders++;
                    }
                }
                part.Update();if(report.MaximumRadiusDifference>1e-6||report.MaximumAxisLengthDifference>1e-6||report.MaximumCenterDifference>1e-6||report.MaximumCircleLengthRelativeDifference>1e-8||report.MaximumCylinderAreaRelativeDifference>1e-8)throw new InvalidDataException("CATIA analytic measurement differs from fitted parameters: radius="+report.MaximumRadiusDifference+" axisLength="+report.MaximumAxisLengthDifference+" center="+report.MaximumCenterDifference+" circumferenceRelative="+report.MaximumCircleLengthRelativeDifference+" cylinderAreaRelative="+report.MaximumCylinderAreaRelativeDifference);
                var selection=doc.Selection;selection.Clear();foreach(var point in constructionPoints)selection.Add((AnyObject)point);selection.VisProperties.SetShow(CatVisPropertyShow.catVisPropertyNoShowAttr);selection.Clear();
                doc.SaveAs(output);if(log!=null)log("[Hole CATIA] circles="+report.Circles+" cylinders="+report.Cylinders+" radiusDifference="+report.MaximumRadiusDifference+" axisLengthDifference="+report.MaximumAxisLengthDifference+" centerDifference="+report.MaximumCenterDifference+" circumferenceRelativeDifference="+report.MaximumCircleLengthRelativeDifference+" cylinderAreaRelativeDifference="+report.MaximumCylinderAreaRelativeDifference+" output="+output);
                return report;
            }
            finally { doc.Close(); }
        }
        private static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
    }
}
