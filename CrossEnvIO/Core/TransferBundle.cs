using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.CrossEnvIO
{
    internal static class TransferBundle
    {
        internal static void Export(TransferPlan original, string output, Action<string> log)
        {
            var plan = JsonConvert.DeserializeObject<TransferPlan>(JsonConvert.SerializeObject(original));
            plan.Packages=plan.TransferPackages().ToList();
            output = Path.GetFullPath(output);
            foreach (var package in plan.Packages)
                if (CojtTransfer.RelativeToRoot(output, package.Source) != null)
                    throw new IOException("导出包不能位于源 cojt 内部");
            if (PackageFiles.HashFile(plan.DataFile) != plan.DataHash) throw new IOException("数据快照已变化");
            if(plan.IncludeWeldParts&&(!File.Exists(plan.WeldModelsFile)||PackageFiles.HashFile(plan.WeldModelsFile)!=plan.WeldModelsHash))
                throw new IOException("绑定零件的模型快照缺失或变化");
            foreach (var package in plan.Packages)
            {
                var content = PackageFiles.Inspect(package.Source);
                if (content.ExternalReferences.Count + content.AbsoluteReferences.Count > 0)
                    throw new IOException(package.Name + " 含无法随包迁移的依赖:\n" + string.Join("\n", content.ExternalReferences.Concat(content.AbsoluteReferences)));
                if (!string.IsNullOrEmpty(package.SourceHash) && package.SourceHash != content.Hash) throw new IOException("源模型已变化，请重新捕获范围");
                package.SourceHash = content.Hash; package.Bytes = content.Bytes;
                package.TargetHash = null; package.Conflict = TransferConflict.Block; package.TargetName = null;
                package.OverrideDirectory = null;
            }
            plan.PreparedStamp = null; plan.TargetSceneStamp = null; plan.ObjectMap.Clear(); plan.WeldBindings.Clear();
            plan.ReceiverBindingScope=null;
            plan.TargetRoot = null; plan.TargetStudy = null; plan.TargetDocument = null; plan.TargetStudyId = null; plan.TargetParentId = null;
            plan.TypeDirectories.Clear(); plan.DefaultDirectory = "";
            plan.LibraryDirectories.Clear();plan.TargetFrame=null;
            if(!plan.IncludeWeldParts)plan.WeldModelsFile=plan.WeldModelsHash=null;
            string staging = output + ".bundle-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var zip = ZipFile.Open(staging, ZipArchiveMode.Create))
                {
                    WriteText(zip, "manifest.json", JsonConvert.SerializeObject(plan, Formatting.Indented));
                    zip.CreateEntryFromFile(plan.DataFile, "data.tsv", CompressionLevel.Optimal);
                    if(plan.IncludeWeldParts)zip.CreateEntryFromFile(plan.WeldModelsFile,"bound-models.tsv",CompressionLevel.Optimal);
                    foreach (var package in plan.Packages)
                    {
                        string prefix = "packages/" + SafeKey(package.Key) + "/";
                        zip.CreateEntry(prefix);
                        foreach (string file in Directory.GetFiles(package.Source, "*", SearchOption.AllDirectories))
                        {
                            string relative = file.Substring(package.Source.TrimEnd('\\', '/').Length).TrimStart('\\', '/');
                            zip.CreateEntryFromFile(file, prefix + relative.Replace('\\', '/'), CompressionLevel.Optimal);
                        }
                        // 包内空目录也影响组件身份，必须显式保留。
                        foreach (string directory in Directory.GetDirectories(package.Source, "*", SearchOption.AllDirectories))
                            zip.CreateEntry(prefix + directory.Substring(package.Source.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/') + "/");
                        if (PackageFiles.Inspect(package.Source).Hash != package.SourceHash) throw new IOException("打包期间源组件发生变化");
                        log("[打包] " + package.Name);
                    }
                }
                if (File.Exists(output)) File.Replace(staging, output, null); else File.Move(staging, output);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        internal static TransferPlan Import(string file, string sessionDirectory, Action<string> log)
        {
            string extracted = Path.Combine(Path.GetFullPath(sessionDirectory), "bundle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extracted);
            try
            {
                using (var zip = ZipFile.OpenRead(file))
                {
                    if (zip.Entries.Count > 100000) throw new InvalidDataException("包内条目过多");
                    long total = 0;
                    foreach (var entry in zip.Entries)
                    {
                        total = checked(total + entry.Length);
                        if (total > 64L * 1024 * 1024 * 1024) throw new InvalidDataException("包解压后超过 64 GB 限制");
                        if (entry.FullName == "manifest.json" && entry.Length > 8L * 1024 * 1024)
                            throw new InvalidDataException("传输清单过大");
                        if ((entry.FullName == "data.tsv"||entry.FullName=="bound-models.tsv") && entry.Length > 128L * 1024 * 1024)
                            throw new InvalidDataException("结构数据超过 128 MB 限制");
                    }
                    var drive = new DriveInfo(Path.GetPathRoot(extracted));
                    if (drive.IsReady && drive.AvailableFreeSpace < total + 16L * 1024 * 1024) throw new IOException("临时目录空间不足以解包");
                    foreach (var entry in zip.Entries)
                    {
                        string relative = entry.FullName.Replace('/', '\\');
                        if (Path.IsPathRooted(relative) || relative.Split('\\').Any(p => p == "..") || relative.IndexOf(':') >= 0)
                            throw new InvalidDataException("包包含不安全路径");
                        string target = Path.GetFullPath(Path.Combine(extracted, relative));
                        if (CojtTransfer.RelativeToRoot(target, extracted) == null) throw new InvalidDataException("包路径越界");
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) Directory.CreateDirectory(target);
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            using (var input = entry.Open())
                            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                        }
                    }
                }
                var plan = TransferPlan.Load(Path.Combine(extracted, "manifest.json"));
                plan.Version = 2;
                if (plan.Kind != "model" && plan.Kind != "weld") throw new InvalidDataException("不支持的传输数据类型");
                plan.DataFile = Path.Combine(extracted, "data.tsv");
                if (PackageFiles.HashFile(plan.DataFile) != plan.DataHash) throw new InvalidDataException("包内结构数据校验失败");
                if(plan.IncludeWeldParts)
                {
                    plan.WeldModelsFile=Path.Combine(extracted,"bound-models.tsv");
                    if(!File.Exists(plan.WeldModelsFile)||PackageFiles.HashFile(plan.WeldModelsFile)!=plan.WeldModelsHash)
                        throw new InvalidDataException("包内绑定零件结构数据校验失败");
                }
                foreach (var package in plan.Packages)
                {
                    package.Source = Path.Combine(extracted, "packages", SafeKey(package.Key));
                    if (PackageFiles.Inspect(package.Source).Hash != package.SourceHash) throw new InvalidDataException("包内组件校验失败: " + package.Name);
                    log("[载入] " + package.Name);
                }
                plan.JobId = Guid.NewGuid().ToString("N"); plan.PreparedStamp = null; plan.TargetSceneStamp = null;
                plan.TargetStudy = null; plan.TargetDocument = null; plan.TargetStudyId = null; plan.TargetRoot = null; plan.TargetParentId = null;
                plan.ObjectMap.Clear(); plan.WeldBindings.Clear();
                return plan;
            }
            catch
            {
                // 此 GUID 目录由本次解包独占创建，条目不会被解压成联接或符号链接。
                try { Directory.Delete(extracted, true); } catch { }
                throw;
            }
        }

        private static string SafeKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 128 || key.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
                throw new InvalidDataException("无效组件包标识");
            return key;
        }
        private static void WriteText(ZipArchive zip, string name, string content)
        { using (var output = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) output.Write(content); }
    }
}
