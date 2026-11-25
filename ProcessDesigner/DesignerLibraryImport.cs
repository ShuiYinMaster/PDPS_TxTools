using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Xml;
using System.Xml.Linq;
using Tecnomatix.Engineering;
using Tecnomatix.Planning;

namespace TxTools.ProcessDesigner
{
    internal static class DesignerLibraryImport
    {
        internal static string LibraryName(DesignerTree tree)
        {
            switch (tree)
            {
                case DesignerTree.Part: return "PartLibrary";
                case DesignerTree.Resource: return "ResourceLibrary";
                default: return "MfgLibrary";
            }
        }

        internal static ITxEmsGlobalServicesProvider Services(TxDocument document)
        {
            return document?.PlatformGlobalServicesProvider as ITxEmsGlobalServicesProvider
                ?? throw new InvalidOperationException("请在 Process Designer 中打开 eMServer 工程。");
        }

        internal static (ITxPlanningObject Project, int ProjectId, ITxPlanningObject Library, int LibraryId) Resolve(
            ITxEmsGlobalServicesProvider services,
            int sessionId, DesignerTree? libraryTree)
        {
            var context = new EmpContext { SessionId = sessionId };
            var manager = Create(ProjectManagerClass);
            EmpObjectKey projectKey;
            try
            {
                ((IEmpProjectManager)manager).GetCurrentProject(ref context, out projectKey);
            }
            finally { Marshal.ReleaseComObject(manager); }
            if (projectKey.ObjectId <= 0)
                throw new InvalidOperationException("请在 Process Designer 中打开 eMServer 工程。");

            var project = services.GetObjectByInternalId(projectKey.ObjectId)
                ?? throw new InvalidOperationException("无法读取当前 eMServer 工程的导航树。");
            if (!libraryTree.HasValue) return (project, projectKey.ObjectId, null, 0);
            var library = FindLibrary(project, LibraryName(libraryTree.Value));
            if (!(library.PlatformServicesProvider is ITxEmsServicesProvider provider) || provider.InternalId <= 0)
                throw new InvalidOperationException("无法读取导入库的 eMServer 对象 ID：" + library.Name);
            return (project, projectKey.ObjectId, library, provider.InternalId);
        }

        internal static IEnumerable<ITxPlanningObject> Children(ITxPlanningObject parent)
        {
            var objects = parent.GetField("children") as TxObjectList;
            return objects == null ? Enumerable.Empty<ITxPlanningObject>() : objects.OfType<ITxPlanningObject>();
        }

        private static ITxPlanningObject FindLibrary(ITxPlanningObject project, string name)
        {
            string type = "Pm" + name;
            var visited = new HashSet<ITxPlanningObject> { project };
            var level = Children(project).Where(visited.Add).ToArray();
            while (level.Length != 0)
            {
                var libraries = level.Where(node => IsType(node, type)
                    || (IsType(node, "PmCollection") && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))).ToArray();
                var named = libraries.Where(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (named.Length == 1) return named[0];
                if (libraries.Length == 1) return libraries[0];
                if (libraries.Length > 1)
                    throw new InvalidOperationException("当前工程中有多个匹配的 " + name + "，无法确定导入库。");

                // Project customizations can place libraries below a navigation folder.
                level = level.Where(node => IsType(node, "PmCollection")).SelectMany(Children).Where(visited.Add).ToArray();
            }
            throw new InvalidOperationException("当前工程中没有找到 " + name + "。");
        }

        private static bool IsType(ITxPlanningObject node, string type)
        {
            return string.Equals(node.PlanningType, type, StringComparison.Ordinal) || node.IsDerivedFromPlanningType(type);
        }

        internal static void Import(int sessionId, int targetId, string path)
        {
            var context = new EmpContext { SessionId = sessionId };
            var target = new EmpObjectKey { ObjectId = targetId };
            var instance = Create(ImportCollectionClass);
            IStream xml = null;
            IntPtr imported = IntPtr.Zero;
            try
            {
                xml = OpenXmlStream(path);
                var collection = (IEmpImportCollection)instance;
                collection.SetFormat(0); // EmpXML
                collection.SetFullLog(true);
                collection.SetTarget(ref target);
                collection.ImportFromStream(ref context, xml, Path.GetFileName(path), out imported);
            }
            finally
            {
                if (imported != IntPtr.Zero) SafeArrayDestroy(imported);
                if (xml != null) Marshal.ReleaseComObject(xml);
                Marshal.ReleaseComObject(instance);
            }
        }

        private static IStream OpenXmlStream(string path)
        {
            // Feed eMServer the original XML bytes and encoding declaration. The
            // BSTR-based importer can misinterpret non-ASCII data.
            const uint readDenyWrite = 0x20; // STGM_READ | STGM_SHARE_DENY_WRITE
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, readDenyWrite, 0, false, IntPtr.Zero, out var stream));
            return stream;
        }

        internal static string ImportErrorMessage(Exception error)
        {
            string message = error.GetBaseException().Message.TrimStart('\ufeff').Trim();
            if (!message.StartsWith("<", StringComparison.Ordinal)) return message;

            try
            {
                var info = XElement.Parse(message);
                if (info.Name.LocalName != "ExecutionInfo") return message;
                var errors = info.Elements().FirstOrDefault(element => element.Name.LocalName == "ErrorsInformation");
                var details = errors?.Descendants()
                    .Where(element => element.Name.LocalName == "param")
                    .Select(element => element.Value.Trim().Trim('"'))
                    .Where(value => value.Length > 0)
                    .Distinct()
                    .ToArray();
                if (details?.Length > 0) return string.Join(Environment.NewLine, details);

                string result = info.Elements().FirstOrDefault(element => element.Name.LocalName == "Result")?.Value;
                return string.IsNullOrWhiteSpace(result) ? message : "eMServer 返回代码 " + result + "。";
            }
            catch (XmlException) { return message; }
        }

        private static object Create(Guid classId) => Activator.CreateInstance(Type.GetTypeFromCLSID(classId, throwOnError: true));

        private static readonly Guid ProjectManagerClass = new Guid("BA6EBE11-B62C-11D4-8E03-00D0B717BC1E");
        private static readonly Guid ImportCollectionClass = new Guid("F83B5CC5-DFD3-11D4-8DE7-0060080B4115");

        [StructLayout(LayoutKind.Sequential)]
        private struct EmpContext { internal int SessionId; }

        [StructLayout(LayoutKind.Sequential)]
        private struct EmpObjectKey { internal int ObjectId; }

        // EmpCore.tlb and EmpIntegration.tlb expose dual COM interfaces. Preserve the
        // vtable positions of the imported methods, including the inherited IDispatch slots.
        [ComImport, Guid("BA6EBE10-B62C-11D4-8E03-00D0B717BC1E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEmpProjectManager
        {
            void _VtblGap1_5();
            void GetCurrentProject([In] ref EmpContext context, out EmpObjectKey project);
        }

        [ComImport, Guid("F83B5CC6-DFD3-11D4-8DE7-0060080B4115"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEmpImportCollection
        {
            void _VtblGap1_5();
            void SetFormat(int format);
            void _VtblGap2_1();
            void SetFullLog([MarshalAs(UnmanagedType.VariantBool)] bool fullLog);
            void _VtblGap3_1();
            void ImportFromStream([In] ref EmpContext context, [MarshalAs(UnmanagedType.Interface)] IStream xml,
                [MarshalAs(UnmanagedType.BStr)] string sourceName, out IntPtr imported);
            void _VtblGap4_9();
            void SetTarget([In] ref EmpObjectKey target);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
            [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template, [MarshalAs(UnmanagedType.Interface)] out IStream stream);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        private static extern int SafeArrayDestroy(IntPtr array);
    }
}
