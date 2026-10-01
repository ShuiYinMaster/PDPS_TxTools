using System;
using System.Drawing;
using System.Reflection;

// Exercises pure layout geometry without constructing a PS window.
public static class RecipeWindowLayoutRegression
{
    static Type layout;
    static int checks;
    static Rectangle Expand(Rectangle bounds, Rectangle area, int panel = 300, int maximum = 0)
    {
        return (Rectangle)layout.GetMethod("Expand").Invoke(null, new object[] { bounds, area, panel, maximum });
    }
    static Rectangle Collapse(Rectangle bounds, Rectangle area, int added, int min, int left)
    {
        return (Rectangle)layout.GetMethod("Collapse").Invoke(null, new object[] { bounds, area, added, min, left });
    }
    static void Check(bool valid, string name)
    {
        if (!valid) throw new Exception("FAILED: " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    public static void Main(string[] args)
    {
        try
        {
            layout = Assembly.LoadFrom(args[0]).GetType("TxTools.Agent.UI.RecipeSidebarWindowLayout", true);
            var area = new Rectangle(0, 0, 1920, 1040);
            var original = new Rectangle(200, 100, 560, 720);
            var expanded = Expand(original, area);
            Check(expanded == new Rectangle(200, 100, 860, 720), "opening preserves chat width and height");
            Check(Collapse(expanded, area, 300, 420, original.Left) == original, "closing restores original bounds");
            var nearRight = new Rectangle(1340, 100, 560, 720);
            expanded = Expand(nearRight, area);
            Check(expanded.Left == 1060 && expanded.Right == area.Right && expanded.Width == 860, "right edge shifts left to fit the work area");
            Check(Collapse(expanded, area, 300, 420, nearRight.Left) == nearRight, "closing restores the original right-side position");
            var secondScreen = new Rectangle(-1920, 0, 1920, 1040);
            expanded = Expand(new Rectangle(-600, 80, 560, 720), secondScreen);
            Check(expanded.Left == -860 && expanded.Right == 0, "negative monitor coordinates supported");
            Check(Expand(original, area, 450).Width == 1010, "scaled panel width supported");
            Check(Expand(original, new Rectangle(0, 0, 800, 1040)) == original, "small screen keeps original bounds for overlay mode");
            Check(Expand(original, area, 300, 800) == original, "maximum form width respected");
            var resized = new Rectangle(400, 140, 1000, 800);
            Check(Collapse(resized, area, 300, 420, resized.Left) == new Rectangle(400, 140, 700, 800), "manual resize and move preserved on close");
            Check(Collapse(new Rectangle(400, 140, 600, 800), area, 300, 420, 400).Width == 420, "collapse never goes below minimum width");
            Check(Collapse(original, area, 0, 420, original.Left) == original, "overlay close never shrinks the window");
            var roundTrip = original;
            for (int i = 0; i < 20; i++) roundTrip = Collapse(Expand(roundTrip, area), area, 300, 420, original.Left);
            Check(roundTrip == original, "repeated toggles do not drift");
            Console.WriteLine("Recipe window layout regression passed: " + checks + " checks.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            Environment.ExitCode = 1;
        }
    }
}
