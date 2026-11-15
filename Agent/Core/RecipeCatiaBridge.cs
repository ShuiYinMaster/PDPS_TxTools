using System;
using System.IO;
using Tecnomatix.Engineering;
using TxTools.CrossEnvIO;

namespace TxTools.Agent.Core
{
    /// <summary>
    /// 配方可调用的 CATIA 导入桥：把已转换的 JT 封装成 COJT 并插入场景。
    /// 内部复用 CrossEnvIO 的 DevicePrototypes / ComponentIO，不另立一套导入路径。
    /// </summary>
    public static class RecipeCatiaBridge
    {
        /// <summary>在指定目录生成 COJT：复制 JT 并写入 PmPartPrototype 原型记录。</summary>
        public static string CreateCojt(string jtFile, string cojtDirectory, string type, string family)
        {
            if (string.IsNullOrWhiteSpace(jtFile) || !File.Exists(jtFile))
                throw new IOException("JT 不存在：" + jtFile);
            if (string.IsNullOrWhiteSpace(cojtDirectory))
                throw new IOException("缺少 COJT 目录。");
            string directory = Path.GetFullPath(cojtDirectory);
            if (!directory.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase))
                throw new IOException("COJT 目录必须以 .cojt 结尾：" + directory);
            Directory.CreateDirectory(directory);
            string name = Path.GetFileNameWithoutExtension(directory);
            string target = Path.Combine(directory, name + ".jt");
            if (!string.Equals(Path.GetFullPath(jtFile), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(jtFile, target, true);

            var prototype = DevicePrototypes.CreateTargetData(new TransferPackage
            {
                Name = name,
                Prototype = new DevicePrototypeData
                {
                    Type = string.IsNullOrWhiteSpace(type) ? "PmPartPrototype" : type,
                    Family = string.IsNullOrWhiteSpace(family) ? "PartPrototype" : family,
                    Weight = 0
                }
            });
            var manager = new Tecnomatix.Engineering.Utilities.TxTunePmDocumentManager();
            manager.SavePrototypeDataToDocument(directory, prototype);
            var saved = manager.LoadPrototypeDataFromDocument(directory);
            if (saved == null || saved.Type != prototype.Type || saved.Family != prototype.Family || saved.ExternalId != prototype.ExternalId)
                throw new IOException("COJT 原型记录回读不一致：" + directory);
            return directory;
        }

        /// <summary>一步完成封装与插入：COJT 允许位于系统库根，parent 为 null 时落在 PhysicalRoot。</summary>
        public static ITxObject BuildAndInsert(string jtFile, string cojtDirectory, string name, ITxObject parent, out string summary)
        {
            string cojt = CreateCojt(jtFile, cojtDirectory, "PmPartPrototype", "PartPrototype");
            return ImportCojt(cojt, name, parent, out summary);
        }

        /// <summary>把 COJT 插入当前工程；允许 COJT 直接位于系统库根。</summary>
        public static ITxObject ImportCojt(string cojtDirectory, string name, ITxObject parent, out string summary)
        {
            string library = TxApplication.SystemRootDirectory;
            string directory = Path.GetFullPath(cojtDirectory);
            if (CojtTransfer.RelativeToRoot(directory, Path.GetFullPath(library)) == null)
                throw new IOException("COJT 必须位于当前 PS 系统库内：" + directory);
            string finalName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(directory) : name;
            ITxObject inserted;
            if (!ComponentIO.InsertCojt(directory, finalName, directory, library, 0, 0, 0, null, out summary, out inserted, true))
                throw new IOException("插入 COJT 失败：" + summary);
            if (inserted == null || !inserted.IsValid()) throw new IOException("插入后未取得有效实例：" + finalName);
            inserted.Name = finalName;
            if (parent != null)
            {
                dynamic container = parent;
                container.AddObject(inserted);
            }
            return inserted;
        }
    }
}
