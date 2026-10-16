using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal static class SourceCylinderParameterRegression
{
    private static Dictionary<string,int> Rows(string path)
    {
        string[] lines=File.ReadAllLines(path), fields=lines[0].Split(',');
        int[] indices=new[]{"shape","kind","cx","cy","cz","ax","ay","az","radius","depth"}.Select(f=>Array.IndexOf(fields,f)).ToArray();
        if(indices.Any(i=>i<0))throw new InvalidDataException("Missing source cylinder field");
        var result=new Dictionary<string,int>();
        foreach(string line in lines.Skip(1).Where(l=>l.Length>0))
        {
            string[] cells=line.Split(','); string key=string.Join("|",indices.Select(i=>cells[i]));
            int count;result.TryGetValue(key,out count);result[key]=count+1;
        }
        return result;
    }
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2)throw new ArgumentException("Expected two source-generated cylinder CSVs");
            var before=Rows(args[0]);var after=Rows(args[1]);
            if(before.Count!=after.Count || before.Any(p=>!after.ContainsKey(p.Key)||after[p.Key]!=p.Value))
                throw new Exception("Source shape/kind/center/axis/radius/depth changed");
            Console.WriteLine("PASS source cylinder CSV parameters identical; count="+after.Values.Sum()+"; no CGR readback"); return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
