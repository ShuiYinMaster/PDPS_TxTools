using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.CrossEnvIO
{
    public enum TransferPlacement { ByType, SingleDirectory, PerPackage, SourceRelative, Automatic }
    public enum TransferConflict { Block, Rename, ReuseVerified, Skip }

    public sealed class TransferPackage
    {
        public string Key = Guid.NewGuid().ToString("N");
        public string OriginalSource, Source, SourceRoot, Name, OverrideDirectory, TargetName;
        public string SourceHash, TargetHash;
        public DevicePrototypeData Prototype;
        public long Bytes;
        public int Instances;
        public TransferConflict Conflict;
        public Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class TransferPlan
    {
        public int Version = 2;
        public string JobId = Guid.NewGuid().ToString("N");
        public string Kind = "model", SourceStudy, SourceDocument, SourceStudyId;
        public string TargetStudy, TargetDocument, TargetStudyId, TargetRoot, TargetParentId;
        public string ReceiverBindingScope;
        public string DataFile, DataHash, PreparedStamp;
        public string Classification = "sdk", DefaultDirectory = "", SingleDirectory = "导入";
        public string SourceContentHash;
        public string TargetSceneStamp;
        public TransferPlacement Placement = TransferPlacement.Automatic;
        public bool CopyModels = true, BindOnly, Project;
        public bool AllowUnboundWelds=true,IncludeWeldParts;
        public string WeldModelsFile,WeldModelsHash;
        public double OriginX, OriginY, OriginZ;
        public double[] SourceFrame,TargetFrame;
        public int ObjectRows;
        public List<TransferPackage> Packages = new List<TransferPackage>();
        public List<string> LibraryDirectories = new List<string>();
        public Dictionary<string, string> TypeDirectories = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> ObjectMap = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, List<string>> WeldBindings = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public static TransferPlan Load(string path)
        {
            var value = JsonConvert.DeserializeObject<TransferPlan>(File.ReadAllText(path, Encoding.UTF8));
            if (value == null || value.Version < 1 || value.Version > 2) throw new InvalidDataException("不支持的传输计划版本");
            return value;
        }

        public void Save(string path) => TransferFile.Write(path, JsonConvert.SerializeObject(this, Formatting.Indented));

        public IEnumerable<KeyValuePair<string, string>> Categories()
        {
            return TransferPackages().SelectMany(p => p.Types).Where(p => MatchesBasis(p.Key))
                .GroupBy(p => p.Key, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(p => p.Value, StringComparer.CurrentCulture);
        }

        private bool MatchesBasis(string key) => Classification == "sdk"
            ? !key.StartsWith("planning:", StringComparison.Ordinal) && !key.StartsWith("prototype:", StringComparison.Ordinal)
                && !key.StartsWith("family:", StringComparison.Ordinal)
            : key.StartsWith(Classification + ":", StringComparison.Ordinal);

        public string DirectoryFor(TransferPackage item)
        {
            if (!string.IsNullOrWhiteSpace(item.OverrideDirectory)) return item.OverrideDirectory;
            if (Placement == TransferPlacement.Automatic)
            {
                string reason;var matched=TransferDirectoryMatcher.Match(item,LibraryDirectories,out reason);
                if(matched!=null)return matched;
                if(!string.IsNullOrWhiteSpace(DefaultDirectory))return DefaultDirectory;
                throw new InvalidOperationException(reason+"；请拖到接收库文件夹明确分配: "+item.Name);
            }
            if (Placement == TransferPlacement.SingleDirectory) return SingleDirectory;
            if (Placement == TransferPlacement.SourceRelative)
            {
                string relative = CojtTransfer.RelativeToRoot(item.OriginalSource, item.SourceRoot);
                if (relative == null) throw new InvalidOperationException("源组件不在原库根内，请逐项指定: " + item.Name);
                return Path.GetDirectoryName(relative) ?? ".";
            }
            if (Placement == TransferPlacement.PerPackage)
                throw new InvalidOperationException("尚未逐项指定存放目录: " + item.Name);
            var categories = item.Types.Keys.Where(MatchesBasis).ToList();
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in categories)
            {
                string directory;
                if (TypeDirectories.TryGetValue(key, out directory) && !string.IsNullOrWhiteSpace(directory))
                    directories.Add(TransferPaths.Destination(TargetRoot, directory));
                else if (!string.IsNullOrWhiteSpace(DefaultDirectory))
                    directories.Add(TransferPaths.Destination(TargetRoot, DefaultDirectory));
                else throw new InvalidOperationException("类型尚未分配目录: " + item.Types[key] + " / " + item.Name);
            }
            if (categories.Count == 0 && !string.IsNullOrWhiteSpace(DefaultDirectory)) return DefaultDirectory;
            if (directories.Count != 1)
                throw new InvalidOperationException(directories.Count == 0 ? "组件未分类且没有显式兜底目录: " + item.Name
                    : "共享组件命中多个不同存放目录，请逐项指定: " + item.Name);
            return directories.Single();
        }

        public string Destination(TransferPackage item)
        {
            if (item.Conflict == TransferConflict.Rename && string.IsNullOrWhiteSpace(item.TargetName))
                throw new InvalidOperationException("改名策略需要指定新的 cojt 名称: " + item.Name);
            string name = string.IsNullOrWhiteSpace(item.TargetName) ? Path.GetFileName(item.OriginalSource.TrimEnd('\\', '/')) : item.TargetName;
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !name.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目标必须是有效的 cojt 目录名: " + name);
            string result=TransferPaths.Destination(TargetRoot, Path.Combine(DirectoryFor(item), name));
            LibraryInsertion.CheckDestination(result,TargetRoot);
            return result;
        }

        public Dictionary<string, string> ModelPaths()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in TransferPackages().Where(p => p.Conflict != TransferConflict.Skip))
                result.Add(Path.GetFullPath(item.OriginalSource), Destination(item));
            return result;
        }

        public IEnumerable<TransferPackage> TransferPackages()=>Kind=="weld"&&!IncludeWeldParts?Enumerable.Empty<TransferPackage>():Packages;

        internal TransferPlan WeldModelsPlan()
        {
            if(!IncludeWeldParts||string.IsNullOrWhiteSpace(WeldModelsFile))return null;
            var models=JsonConvert.DeserializeObject<TransferPlan>(JsonConvert.SerializeObject(this));
            models.Kind="model";models.DataFile=WeldModelsFile;models.DataHash=WeldModelsHash;
            models.IncludeWeldParts=false;models.WeldModelsFile=models.WeldModelsHash=null;return models;
        }

        public string Stamp()
        {
            string previous = PreparedStamp;
            try { PreparedStamp = null; return PackageFiles.HashBytes(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this))); }
            finally { PreparedStamp = previous; }
        }

        public TransferFileReport InspectFiles(bool sourceRequired = true)
        {
            var report = new TransferFileReport();
            try
            {
                if (!Directory.Exists(TargetRoot)) throw new DirectoryNotFoundException("目标库根不存在: " + TargetRoot);
                TransferPaths.CheckParents(TargetRoot);
                if (!File.Exists(DataFile) || PackageFiles.HashFile(DataFile) != DataHash)
                    throw new InvalidDataException("数据文件缺失或在生成计划后发生变化，请重新捕获范围");
                if(Kind=="weld"&&IncludeWeldParts&&(!File.Exists(WeldModelsFile)||PackageFiles.HashFile(WeldModelsFile)!=WeldModelsHash))
                    throw new InvalidDataException("绑定零件的模型快照缺失或变化；请从源端重新捕获，或导入包含零件的离线包");
                if (string.IsNullOrWhiteSpace(JobId) || JobId.Length > 80 || JobId.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
                    throw new InvalidDataException("无效任务标识");
            }
            catch (Exception ex) { report.Errors.Add(ex.Message); return report; }
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in TransferPackages())
            {
                if (item.Conflict == TransferConflict.Skip) { report.Skipped++; continue; }
                try
                {
                    string destination = Destination(item);
                    if (!destinations.Add(destination)) throw new InvalidOperationException("不同组件包映射到同一目标: " + destination);
                    if (sourceRequired && CopyModels)
                    {
                        var snapshot = PackageFiles.Inspect(item.Source);
                        if (!string.IsNullOrEmpty(item.SourceHash) && item.SourceHash != snapshot.Hash
                            && !(TransferPaths.Same(item.Source,destination) && PrototypeReceipts.Matches(this,item,snapshot.Hash)))
                            throw new IOException("源组件复制前发生变化: " + item.Source);
                        if (string.IsNullOrEmpty(item.SourceHash)) item.SourceHash = snapshot.Hash;
                        item.Bytes = snapshot.Bytes;
                        if (item.Conflict != TransferConflict.ReuseVerified)
                        {
                            report.Errors.AddRange(snapshot.ExternalReferences.Select(p => item.Name + " 包外依赖未解决: " + p));
                            if (!TransferPaths.Same(item.Source, destination))
                                report.Errors.AddRange(snapshot.AbsoluteReferences.Select(p => item.Name + " 绝对引用需重新打包为相对引用，或核验复用已有目标: " + p));
                        }
                    }
                    bool exists = Directory.Exists(destination);
                    if (File.Exists(destination)) throw new IOException("目标路径已被文件占用: " + destination);
                    if (exists)
                    {
                        string current = PackageFiles.Inspect(destination).Hash;
                        if (item.Conflict == TransferConflict.ReuseVerified)
                        {
                            if (string.IsNullOrEmpty(item.TargetHash) || current != item.TargetHash)
                                throw new IOException("人工核验的目标模型已变化，请重新确认: " + destination);
                        }
                        else if ((string.IsNullOrEmpty(item.SourceHash) || current != item.SourceHash) && !PrototypeReceipts.Matches(this,item,current))
                            throw new IOException("目标同名但内容不同，请改名、核验复用或跳过: " + destination);
                        report.Reused++;
                    }
                    else
                    {
                        if (!CopyModels || !sourceRequired) throw new DirectoryNotFoundException("目标模型尚不存在: " + destination);
                        if (item.Conflict == TransferConflict.ReuseVerified) throw new IOException("要复用的目标模型不存在");
                        if (CojtTransfer.RelativeToRoot(destination, item.Source) != null)
                            throw new IOException("不能把组件复制到自身内部");
                        report.ToCopy++; report.BytesToCopy = checked(report.BytesToCopy + item.Bytes);
                    }
                    if (sourceRequired) TransferPaths.ProbeWrite(Path.GetDirectoryName(destination));
                }
                catch (Exception ex) { report.Errors.Add(item.Name + ": " + ex.Message); }
            }
            if (sourceRequired && report.BytesToCopy > 0)
            {
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(TargetRoot)));
                    if (drive.IsReady && drive.AvailableFreeSpace < report.BytesToCopy + 16L * 1024 * 1024)
                        report.Errors.Add("目标磁盘空间不足（含 16 MB 暂存余量）");
                }
                catch (Exception ex) { report.Errors.Add("无法核验磁盘空间: " + ex.Message); }
            }
            return report;
        }

        public CopyResult Copy(Action<string> log, System.Threading.CancellationToken cancellation)
        {
            log = log ?? delegate { };
            var report = InspectFiles();
            if (report.Errors.Count > 0) throw new InvalidOperationException(string.Join("\n", report.Errors));
            var result = new CopyResult();
            try
            {
                int index = 0;
                foreach (var item in TransferPackages())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (item.Conflict == TransferConflict.Skip) continue;
                    string destination = Destination(item);
                    log("[复制 " + (++index) + "/" + (TransferPackages().Count() - report.Skipped) + "] " + item.Name);
                    if (Directory.Exists(destination))
                    {
                        string expected = item.Conflict == TransferConflict.ReuseVerified ? item.TargetHash : item.SourceHash;
                        string current = PackageFiles.Inspect(destination).Hash;
                        if (current != expected && (item.Conflict == TransferConflict.ReuseVerified || !PrototypeReceipts.Matches(this,item,current))) throw new IOException("目标模型预检后发生变化");
                        result.Skipped++;
                    }
                    else
                    {
                        // 使用单层暂存，核验后发布，避免双层 GUID 目录放大 Windows 路径长度。
                        string parent = Path.GetDirectoryName(destination);
                        TransferPaths.CheckParents(parent);
                        Directory.CreateDirectory(parent);
                        if (!CojtTransfer.CopyCojtDirectory(item.Source, parent, log, result, Path.GetFileName(destination),
                            staged => { TransferPaths.CheckParents(parent); return PackageFiles.Inspect(staged).Hash == item.SourceHash; }, cancellation))
                            throw new IOException("复制失败: " + item.Source);
                        result.Ok++;
                    }
                    result.TargetPaths[Path.GetFullPath(item.OriginalSource)] = destination;
                }
                return result;
            }
            catch (Exception ex)
            {
                result.Fail++;
                foreach (string directory in result.CopiedDirs)
                    if (Directory.Exists(directory)) result.Capture(directory, log);
                throw new TransferCopyException(ex.Message, result, ex);
            }
        }
    }

    public sealed class TransferCopyException : IOException
    {
        public CopyResult PartialResult { get; }
        public TransferCopyException(string message, CopyResult result, Exception inner) : base(message, inner) { PartialResult = result; }
    }
    public sealed class TransferFileReport
    {
        public List<string> Errors = new List<string>();
        public int ToCopy, Reused, Skipped;
        public long BytesToCopy;
        public bool Ok => Errors.Count == 0;
        public override string ToString() => $"待复制 {ToCopy} · 复用 {Reused} · 跳过 {Skipped} · 约 {BytesToCopy / 1048576.0:0.0} MB · 阻断 {Errors.Count}";
    }

    internal static class TransferDirectoryMatcher
    {
        private static string Normalize(string value)=>new string((value??"").Trim().SkipWhile(c=>char.IsDigit(c)||char.IsPunctuation(c)||char.IsWhiteSpace(c))
            .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        private static string Category(string value)
        {
            string key=Normalize(value);
            switch(key)
            {
                case "robot":case "robots":case "机器人":return "robot";
                case "gun":case "weldgun":case "servogun":case "焊枪":case "伺服焊枪":return "gun";
                case "fixture":case "fixtures":case "夹具":case "工装":case "工装夹具":return "fixture";
                case "layout":case "布局":case "周边":case "周边设备":return "layout";
                case "product":case "products":case "产品":return "product";
                case "part":case "parts":case "零件":return "part";
                case "tool":case "tools":case "工具":case "工艺工具":return "tool";
                case "equipment":case "device":case "devices":case "设备":return "equipment";
                case "resource":case "resources":case "资源":return "resource";
                case "import":case "imports":case "incoming":case "导入":return "import";
                case "other":case "others":case "其他":case "其它":return "other";
                default:return key;
            }
        }
        internal static string Match(TransferPackage item,IEnumerable<string> folders,out string reason)
        {
            var matches = Candidates(item, folders, out reason);
            return matches.Count == 1 ? matches[0] : null;
        }

        internal static List<string> Candidates(TransferPackage item,IEnumerable<string> folders,out string reason)
        {
            string relative=CojtTransfer.RelativeToRoot(item.OriginalSource,item.SourceRoot);
            string sourceDirectory=relative==null?"":Path.GetDirectoryName(relative)??"";
            string[] source=sourceDirectory.Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries).Select(Category).ToArray();
            var categories=new HashSet<string>(StringComparer.Ordinal);
            foreach(string type in item.Types.Keys.Concat(new[]{item.Prototype?.RuntimeType,item.Prototype?.Type,item.Prototype?.Family}).Where(t=>!string.IsNullOrWhiteSpace(t)))
            {
                string key=type.ToLowerInvariant();
                if(key.Contains("gun"))categories.Add("gun");
                else if(key.Contains("robot"))categories.Add("robot");
                else if(key.Contains("fixture")||key.Contains("gripper"))categories.Add("fixture");
                else if(key.Contains("layout"))categories.Add("layout");
                else if(key.Contains("part"))categories.Add("part");
                else if(key.Contains("tool"))categories.Add("tool");
                else if(key.Contains("device"))categories.Add("equipment");
                if(type.StartsWith("family:",StringComparison.Ordinal))categories.Add(Category(type.Substring(7)));
                else categories.Add(Category(type));
            }
            // Source directory relationships take priority over broad SDK types.
            int best=0;var matches=new List<string>();string basis=null;
            foreach(string folder in (folders??Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var parts=folder.Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries).Select(Category).ToArray();
                if(parts.Length==0)continue;
                string leaf=parts[parts.Length-1];int score=0;string why=null;
                int suffix=0;
                while(suffix<Math.Min(source.Length,parts.Length)&&source[source.Length-1-suffix]==parts[parts.Length-1-suffix])suffix++;
                bool generic=leaf=="resource"||leaf=="equipment"||leaf=="import"||leaf=="other";
                if(suffix>0&&!generic){score=300+suffix*100;why="源目录对应";}
                else if(source.Contains(leaf)&&leaf!="resource"&&leaf!="equipment")
                {if(!generic){score=200;why="源目录上级分类对应";}}
                if(categories.Contains(leaf)&&leaf!="import"&&leaf!="other")
                {
                    int typeScore=leaf=="gun"||leaf=="robot"?130:leaf=="fixture"?120:generic?50:100;
                    if(typeScore>score){score=typeScore;why="资源类型对应";}
                }
                if(score==0)continue;
                if(score>best){best=score;matches.Clear();basis=why;}
                if(score==best)matches.Add(folder);
            }
            reason=matches.Count==1?basis:matches.Count>1?"匹配到多个接收目录："+string.Join("、",matches):"未找到对应的接收目录";
            return matches;
        }
        internal static string Association(TransferPackage item)
        {
            string relative=CojtTransfer.RelativeToRoot(item.OriginalSource,item.SourceRoot);
            string directory=relative==null?null:Path.GetDirectoryName(relative);
            if(!string.IsNullOrWhiteSpace(directory))return "source:"+directory.Replace('/','\\').ToUpperInvariant();
            if(!string.IsNullOrWhiteSpace(item.Prototype?.Family))return "family:"+item.Prototype.Family;
            // A bare TxComponent or generic tool prototype is too broad for mass assignment.
            return item.Types.Keys.FirstOrDefault(k=>k.EndsWith("TxRobot",StringComparison.Ordinal)||k.EndsWith("TxServoGun",StringComparison.Ordinal)
                ||k.EndsWith("TxGun",StringComparison.Ordinal)||k.EndsWith("TxGripper",StringComparison.Ordinal));
        }
    }

    internal static class TransferPaths
    {
        public static string Destination(string root, string value)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(value)) throw new IOException("存放目录尚未指定");
            if (value.Split('\\', '/').Any(s => s == "..")) throw new IOException("目录不能包含 ..");
            string full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
            if (!Same(full, root) && CojtTransfer.RelativeToRoot(full, root) == null) throw new IOException("目标目录越过接收端库根: " + full);
            CheckParents(full);
            return full;
        }
        public static bool Same(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        public static void CheckParents(string path)
        {
            for (var node = new DirectoryInfo(Path.GetFullPath(path)); node != null; node = node.Parent)
                if (node.Exists && (node.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("目录路径包含联接或符号链接，需使用真实路径: " + node.FullName);
        }
        public static void ProbeWrite(string directory)
        {
            var node = new DirectoryInfo(Path.GetFullPath(directory));
            while (!node.Exists) node = node.Parent ?? throw new DirectoryNotFoundException(directory);
            CheckParents(node.FullName);
            string probe = Path.Combine(node.FullName, ".crossenv-probe-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
    }
}
