using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Forms;
using TxTools.Common;

class Program
{
    static IEnumerable<Control> All(Control c) { foreach(Control x in c.Controls) {yield return x;foreach(var y in All(x))yield return y;} }
    [STAThread] static int Main(string[] args)
    {
        try { return Run(args); }
        catch(Exception error)
        {
            while(error.InnerException!=null)error=error.InnerException;
            Console.Error.WriteLine(error.GetType().Name+": "+error.Message);
            Console.Error.WriteLine(error.StackTrace);
            return 1;
        }
    }
    static int Run(string[] args)
    {
        Console.OutputEncoding=System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();
        bool hidden=args.Contains("--no-logs");PluginLog.Enabled=!hidden;
        string only=args.FirstOrDefault(a=>a.StartsWith("--only="));
        int failures=0,cases=0;
        foreach(float scale in new[]{1f,1.75f,2f})
        foreach(Type fixture in new[]{typeof(FenceFixture),typeof(GunFixture),typeof(AllocatorFixture),typeof(CatiaFixture),typeof(LineFixture),typeof(BaseFixture),typeof(MatrixFixture),typeof(CatiaFullFixture),typeof(ThemeFixture),typeof(MechFixture),typeof(FenceFullFixture),typeof(PathFixture),typeof(GrouperFixture),typeof(ColorFixture),typeof(CrossFixture),typeof(RecorderFixture),typeof(AnnotatorFixture),typeof(WizardFixture),typeof(SnakeFixture)})
        {
            if(only!=null && fixture.Name!=only.Substring(7))continue;
            if(only!=null)Console.WriteLine("Layout "+fixture.Name+" "+scale);
            using(Form form=(Form)Activator.CreateInstance(fixture))
            {
            form.ShowInTaskbar=false;form.Opacity=0;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);
            PluginUiPolicy.Prepare(form);
            FormUiKit.TestScale=scale;
            var fonts=All(form).Select(c=>new {Control=c,Font=c.Font}).ToArray();
            if(scale>1){form.Scale(new SizeF(scale,scale));foreach(var x in fonts)x.Control.Font=new Font(x.Font.FontFamily,x.Font.Size*scale,x.Font.Style);
                form.ClientSize=new Size(form.ClientSize.Width,Math.Min(form.ClientSize.Height,(int)(560*scale)));}
            PluginUiPolicy.Finish(form);form.Show();Application.DoEvents();form.PerformLayout();
            var gunFixture=form as GunFixture;
            if(gunFixture!=null)
            {
                int exportTop=gunFixture.ExportButton.Top, summaryHeight=gunFixture.GunSummary.Height;
                gunFixture.GunSummary.Text="焊钳（30 把）："+string.Join(", ",Enumerable.Range(1,30).Select(i=>"长名称焊钳_"+i));
                Application.DoEvents();form.PerformLayout();
                if(gunFixture.GunSummary.Height!=summaryHeight || gunFixture.ExportButton.Top!=exportTop)
                {Console.WriteLine("Gun summary displaced export button");failures++;}
                int windowHeight=form.Height;
                gunFixture.WriteBackendLog("[Catia] 插枪结果（共享几何：2 份，成功定位 30/30，失败 0）");
                gunFixture.WriteBackendLog("[Catia] 点球完成：成功 30，失败 0");
                if(gunFixture.LogExpanded || form.Height!=windowHeight)
                {Console.WriteLine("Normal export expanded the log");failures++;}
            }
            var tabs=All(form).OfType<TabControl>().FirstOrDefault();
            int pageCount=form is WizardFixture?4:(tabs==null?1:tabs.TabCount);
            for(int pageIndex=0;pageIndex<pageCount;pageIndex++)
            {
            if(tabs!=null){tabs.SelectedIndex=pageIndex;Application.DoEvents();form.PerformLayout();}
            if(form is WizardFixture){((WizardFixture)form).SelectStep(pageIndex);Application.DoEvents();form.PerformLayout();}
            cases++;
            foreach(var c in All(form).Where(c=>c.Visible && (c is GroupBox || c is Button || c is NumericUpDown || c is TextBox || c is RichTextBox || c is ComboBox || c is CheckBox)))
            {
                var bounds=c.RectangleToScreen(c.ClientRectangle);
                for(Control p=c.Parent;p!=null;p=p.Parent)
                {
                    var clip=p.RectangleToScreen(p.ClientRectangle);clip.Inflate(2,2);
                    if(!clip.Contains(bounds)){Console.WriteLine("CLIPPED "+form.GetType().Name+" "+scale+" "+c.Text+" in "+p.GetType().Name+": "+bounds+" / "+clip);failures++;break;}
                }
            }
            var allocator=form as AllocatorFixture;
            if(allocator!=null){Console.WriteLine("Selection viewport "+scale+": "+allocator.Selection.Height/scale);if(allocator.Selection.Height<120*scale)failures++;
                var preview=All(form).OfType<DataGridView>().Single();Console.WriteLine("Preview viewport "+scale+": "+preview.Height/scale);if(preview.Height<60*scale)failures++;}
            if(form is GunFixture)
            {
                var points=All(form).OfType<ListView>().Single();
                var columns=(TableLayoutPanel)points.Parent.Parent.Parent;
                var widths=columns.GetColumnWidths();
                if(widths.Max()-widths.Min()>2 || points.Height<350*scale){Console.WriteLine("Gun columns/list: "+string.Join(",",widths)+" height="+points.Height/scale);failures++;}
                var left=columns.GetControlFromPosition(0,0);
                var leftStack=left.Controls.OfType<TableLayoutPanel>().Single();
                var opsCard=All(left).OfType<GroupBox>().Single(g=>g.Text=="操作选择");
                var opsList=All(opsCard).OfType<Panel>().Single(p=>p.BorderStyle==BorderStyle.FixedSingle);
                Console.WriteLine("Gun operation selection height: "+opsList.Height/scale);
                if(opsList.Height<150*scale){Console.WriteLine("Gun operation selection is too short");failures++;}
                int unused=left.ClientSize.Height-left.Padding.Bottom-leftStack.Bottom;
                if(unused>8*scale){Console.WriteLine("Unused left-column height: "+unused/scale);failures++;}
                var footer=All(form).OfType<Button>().Where(b=>new[]{"复位","导出Excel","关闭","?"}.Contains(b.Text)).ToArray();
                if(footer.Length!=4 || footer.Select(b=>b.PointToScreen(Point.Empty).Y).Distinct().Count()!=1){Console.WriteLine("Gun footer count/alignment: "+footer.Length);failures++;}
                var ball=All(form).OfType<GroupBox>().Single(g=>g.Text=="导点球");
                var names=All(ball).OfType<TextBox>().Where(t=>!(t.Parent is UpDownBase)).ToArray();
                if(names.Length!=1){Console.WriteLine("Ball name field count: "+names.Length);failures++;}
            }
            Directory.CreateDirectory("docs/ui-preview");
            string suffix=(pageIndex==0?"":"-tab-"+pageIndex)+(hidden?"-logs-hidden":"");
            using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));bmp.Save("docs/ui-preview/"+form.GetType().Name+"-"+(int)(scale*100)+suffix+".png");}
            }
            FormUiKit.TestScale=1;
            }
        }
        Console.WriteLine("Layout cases: "+cases+"; clipping failures: "+failures);return failures==0?0:1;
    }
}
