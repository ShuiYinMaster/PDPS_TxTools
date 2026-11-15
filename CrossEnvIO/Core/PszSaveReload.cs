using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TxTools.CrossEnvIO
{
    internal sealed class PszSaveReloadResult
    {
        internal string BackupFile;
        internal int Replacements;
    }
    internal static class PszSaveReload
    {
        internal static void Validate(string file)
        {
            if(string.IsNullOrWhiteSpace(file)||!file.EndsWith(".psz",StringComparison.OrdinalIgnoreCase)||!File.Exists(file))
                throw new InvalidOperationException("中文路径传输需保存并重载接收工程，请先将接收工程保存为 PSZ 再重新检查。");
            TransferPaths.CheckParents(Path.GetDirectoryName(Path.GetFullPath(file)));
            if((File.GetAttributes(file)&(FileAttributes.ReadOnly|FileAttributes.ReparsePoint))!=0)
                throw new IOException("接收 PSZ 只读或是链接文件，不能保存修补: "+file);
            PszPathFixer.ReadState(file);
        }
        internal static PszSaveReloadResult Run(string file,string root,IEnumerable<string> paths,Action save,Action load,Action<string> log,Action<string> beforeReload=null)
        {
            Validate(file); log=log??delegate{};
            var expected=paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(string path in expected)
                if(CojtTransfer.RelativeToRoot(path,root)==null||!Directory.Exists(path))throw new IOException("目标模型不在接收库内或不存在: "+path);
            string directory=Path.GetDirectoryName(Path.GetFullPath(file));
            string backup=Path.Combine(directory,".crossenv-psz-"+Guid.NewGuid().ToString("N").Substring(0,16)+".bak");
            File.Copy(file,backup,false); log("[PSZ] 原工程备份: "+backup);
            try
            {
                log("[PSZ] 保存接收工程…"); save();
                int count=PszPathFixer.FixPszPaths(file,root,log,expected,backup,true);
                if(count<0)throw new IOException("PSZ 路径或原编码修补失败");
                PszPathFixer.VerifyReferences(file,root,expected);
                beforeReload?.Invoke(backup);
                log("[PSZ] 路径与原编码已核验，重载接收工程…"); load();
                log("[PSZ] ✓ 已保存、修补并重载（"+count+" 条路径）");
                return new PszSaveReloadResult{BackupFile=backup,Replacements=count};
            }
            catch(Exception ex)
            {
                string staging=Path.Combine(directory,".crossenv-restore-"+Guid.NewGuid().ToString("N").Substring(0,16)+".tmp");
                try
                {
                    File.Copy(backup,staging,false);
                    if(File.Exists(file))File.Replace(staging,file,null);else File.Move(staging,file);
                    log("[PSZ] 未完成，原工程文件已恢复；请核对当前内存场景。");
                }
                catch(Exception restore){throw new IOException("PSZ 操作失败且自动恢复未完成；原备份: "+backup+" / "+restore.Message,ex);}
                finally{if(File.Exists(staging))File.Delete(staging);}
                throw new IOException("PSZ 保存/修补/重载失败: "+ex.Message+"；原工程已恢复，备份: "+backup,ex);
            }
        }
    }
}
