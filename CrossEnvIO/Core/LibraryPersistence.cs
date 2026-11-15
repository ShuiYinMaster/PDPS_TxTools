using System;
using System.IO;
using System.Linq;
using Tecnomatix.Engineering;

namespace TxTools.CrossEnvIO
{
    internal sealed class NativeLibraryException : IOException
    {
        internal int UncountedCreated { get; }
        internal NativeLibraryException(string message,Exception inner,int uncountedCreated=0):base(message,inner){UncountedCreated=uncountedCreated;}
    }
    internal static class LibraryPersistence
    {
        internal static event Action Reloading;
        internal static void Reset(){ }
        internal static void NotifyReloading()=>Reloading?.Invoke();
        internal static ITxObject FindReusable(string expected,string type)
        {
            if(string.IsNullOrWhiteSpace(expected))return null;
            return TransferRuntime.SceneObjects().FirstOrDefault(item=>item.GetType().Name==type
                &&ReferenceIssue(item,expected,TxApplication.SystemRootDirectory)==null);
        }
        internal static bool IsDurablePath(string raw,string root,string expected)
        {
            try
            {
                if(string.IsNullOrWhiteSpace(raw)||string.IsNullOrWhiteSpace(root))return false;
                string path=raw.Replace('/','\\');
                if(path.StartsWith("#\\",StringComparison.Ordinal))path=Path.Combine(root,path.Substring(2));
                else if(!Path.IsPathRooted(path))path=Path.Combine(root,path);
                path=Path.GetFullPath(path);
                if(!Directory.Exists(path))return false;
                // A readable insertion junction is not a formal receiving-library reference.
                TransferPaths.CheckParents(path);
                string resolved=DirectoryLinks.Resolve(path);
                return TransferPaths.Same(resolved,DirectoryLinks.Resolve(expected))
                    &&CojtTransfer.RelativeToRoot(resolved,DirectoryLinks.Resolve(root))!=null;
            }
            catch{return false;}
        }
        internal static string StorageDescription(ITxObject item)
        {
            try
            {
                var storage=(item as ITxStorable)?.StorageObject;
                if(storage==null)return "无存储对象";
                if(storage is TxLocalStorage)return "本地建模存储，未取得正式库引用";
                if(storage is TxLibraryStorage library)return string.IsNullOrWhiteSpace(library.FullPath)?"库存储路径为空":library.FullPath;
                return storage.GetType().Name;
            }
            catch(Exception ex){return "读取存储状态失败："+ex.Message;}
        }
        internal static string ReferenceIssue(ITxObject item,string expected,string root)
        {
            try
            {
                LibraryInsertion.CheckDestination(expected,root);
                if(item is ITxComponent component&&component.IsOpenForModeling)
                    return "组件仍在建模，尚未取得完成的正式库引用";
                var storage=(item as ITxStorable)?.StorageObject as TxLibraryStorage;
                if(storage==null||string.IsNullOrWhiteSpace(storage.FullPath))return "没有可读的正式库引用";
                if(!IsDurablePath(storage.FullPath,root,expected))return "场景引用未指向指定的正式库文件夹";
                return null;
            }
            catch(Exception ex){return "库引用核验未完成："+ex.Message;}
        }
        internal static void Ensure(ITxObject item,string expected,string source,Action<string> log)
        {
            string issue=ReferenceIssue(item,expected,TxApplication.SystemRootDirectory);
            if(issue!=null)
                throw new NativeLibraryException("插入后的正式库引用核验未通过："+item?.Name+"\n"+issue
                    +"\n实际引用："+StorageDescription(item)+"\n计划引用："+expected
                    +"\n本次使用原始库路径插入，未执行建模、另存为或路径迁移；请核对接收端实例，不要重复执行当前任务。",null);
            log?.Invoke("[引用] ✓ 场景名称："+item.Name+"；模型文件："+Path.GetFileName(expected)+"；正式库引用："+StorageDescription(item));
        }
    }
}
