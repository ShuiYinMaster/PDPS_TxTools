using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;

namespace TxTools.CrossEnvIO
{
    public sealed class DevicePrototypeData
    {
        public string Type, Family, ExternalId, RuntimeType, Error;
        public double Weight;
    }

    // SDK prototype/storage changes are reusable only for this exact project, source and output.
    internal static class PrototypeReceipts
    {
        private static string FileFor(TransferPlan plan, TransferPackage package) => TxToolsTemp.FileFor("CrossEnvIO", "pr",
            PackageFiles.HashBytes(Encoding.UTF8.GetBytes(Path.GetFullPath(plan.Destination(package)).ToUpperInvariant())).Substring(0,32) + ".json");
        private static string Context(TransferPlan plan, TransferPackage package) => JsonConvert.SerializeObject(new
        { plan.TargetStudy, plan.TargetStudyId, plan.TargetDocument, plan.TargetRoot, Destination=plan.Destination(package), package.SourceHash, package.Prototype });
        internal static bool Matches(TransferPlan plan, TransferPackage package, string hash)
        {
            try { var value = JObject.Parse(File.ReadAllText(FileFor(plan, package))); return (string)value["context"] == Context(plan, package) && (string)value["hash"] == hash; }
            catch { return false; }
        }
        internal static void Save(TransferPlan plan, TransferPackage package, string hash) => TransferFile.Write(FileFor(plan, package),
            new JObject { ["context"] = Context(plan, package), ["hash"] = hash }.ToHostJson(Formatting.None));
    }
}
