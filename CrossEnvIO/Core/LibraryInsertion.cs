using System;
using System.IO;

namespace TxTools.CrossEnvIO
{
    internal static class LibraryInsertion
    {
        private static readonly object InsertionGate=new object();
        internal static bool NeedsCompatibility(string path)
        {
            if(string.IsNullOrEmpty(path))return false;
            foreach(char character in path)if(character>127)return true;
            return false;
        }
        internal static void CheckDestination(string path,string root)
        {
            CheckDestination(path,root,false);
        }
        // allowRoot: 配方里的快速验证允许把 COJT 直接放在库根。
        internal static void CheckDestination(string path,string root,bool allowRoot)
        {
            string relative=CojtTransfer.RelativeToRoot(path,root);
            if(relative==null||!path.TrimEnd('\\','/').EndsWith(".cojt",StringComparison.OrdinalIgnoreCase))
                throw new IOException("模型文件必须位于接收库内："+path);
            if(!allowRoot&&string.IsNullOrEmpty(Path.GetDirectoryName(relative)))
                throw new IOException("模型不能直接存放在库根，请在第二栏指定库内子文件夹："+path);
            TransferPaths.CheckParents(path);
        }
        internal static string SupportIssue(string path)
        {
            try
            {
                if(NeedsCompatibility(path))return AppFuncCompatibility.Current.SupportIssue();
                AppFuncCompatibility.Current.EnsureIdle();return null;
            }
            catch(Exception ex){return ex.Message;}
        }
        internal static T AtDestination<T>(string path,string root,Func<string,T> insert,Action<string> log)
        {
            return AtDestination(path,root,insert,log,false);
        }
        internal static T AtDestination<T>(string path,string root,Func<string,T> insert,Action<string> log,bool allowRoot)
        {
            path=Path.GetFullPath(path).TrimEnd('\\','/');root=Path.GetFullPath(root);
            CheckDestination(path,root,allowRoot);
            if(!Directory.Exists(path))throw new DirectoryNotFoundException("接收模型文件不存在："+path);
            lock(InsertionGate)
            {
                // Every SDK call receives the real final path. Never alter the library
                // root, working directory, filenames or model-package contents here.
                if(NeedsCompatibility(path))return AppFuncCompatibility.Current.Run(()=>insert(path),log);
                AppFuncCompatibility.Current.EnsureIdle();return insert(path);
            }
        }
    }
}
