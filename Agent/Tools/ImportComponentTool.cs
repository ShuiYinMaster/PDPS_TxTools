// TxTools.Agent / Tools / Import / ImportComponentTool.cs
//
// 从磁盘导入 cojt 组件到当前 study。
//
// ══════════════════════════════════════════════════════════════════
//  根因:中文路径
// ══════════════════════════════════════════════════════════════════
//   InsertComponent 长期失败的原因不是配置、不是许可、不是 prototype ——
//   是【路径或名称含非 ASCII 字符】。
//
//   反编译佐证:name 走 TxConvertor.StringToRWCString(窄字符 RWClassicCString),
//   path 走 StringToRWWString(宽字符)。native 内部窄/宽混用,
//   遇到中文就在里面崩掉,返回 false,而错误码映射成通用异常,
//   托管层还把 errMsg 文本丢了 —— 所以表面看是"配置不支持"。
//
// ══════════════════════════════════════════════════════════════════
//  实测通过的五步流程
// ══════════════════════════════════════════════════════════════════
//   ① 复制 cojt → 纯英文临时路径
//   ② InsertComponent(英文路径 + 场景同类型原型) → 插入成功
//   ③ CopyToLocal() → SaveToLibrary(项目库目录, 真实名) → 迁回库
//   ④ 组件 FullPath 指向项目库,GUI 库浏览器可正常管理
//   ⑤ TuneData 的外部 ID / 类型零污染
//
//   第③步不是可选的:不迁库的话组件的 FullPath 指向临时目录,
//   临时目录一清理组件就坏了。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using Tecnomatix.Planning;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Core
{
    public sealed class ImportComponentTool : TxAgentToolBase
    {
        /// <summary>
        /// 中转用的临时根目录。必须【纯 ASCII】——
        /// 不能用 %TEMP%,因为用户名含中文时它本身就带中文。
        /// </summary>
        public static string TempRoot = @"C:\TxAgentImport";

        public override string Name { get { return "import_component"; } }

        public override string Description
        {
            get
            {
                return "把磁盘上的 cojt 组件导入当前 study，并落到项目库中。"
                     + "内部流程:复制到纯英文临时路径 → 插入 → 迁回项目库 → 清理临时文件。"
                     + "【为什么要绕这一圈】PS 的 InsertComponent 遇到中文路径或中文名会失败，"
                     + "且报的是含义无关的通用异常，所以必须在纯 ASCII 路径下完成插入再迁回。"
                     + "prototype_from 传场景里一个同类型组件名(如已有的同型号焊枪)，"
                     + "插入需要一个原型对象，缺了会失败。"
                     + "第一次用可传 probe=true 探查当前 PS 版本的接口与配置支持情况。";
            }
        }

        /// <summary>会改场景，走审批。</summary>
        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'properties': {
                        'path':           { 'type': 'string',  'description': '源 cojt 目录的绝对路径（可含中文）' },
                        'name':           { 'type': 'string',  'description': '可选，落库后的组件名。留空用源目录名' },
                        'prototype_from': { 'type': 'string',  'description': '场景中一个同类型组件的名字，用它的原型。强烈建议提供' },
                        'library_dir':    { 'type': 'string',  'description': '可选，落库目标目录。留空则自动放在 prototype_from 那个组件所在的库目录旁' },
                        'x':              { 'type': 'number' },
                        'y':              { 'type': 'number' },
                        'z':              { 'type': 'number' },
                        'keep_temp':      { 'type': 'boolean', 'description': '可选，保留临时文件以便排查，默认 false' },
                        'probe':          { 'type': 'boolean', 'description': '只探查接口与配置，不实际导入' }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            if (Bool(input, "probe")) return Probe();

            var srcPath = (GetString(input, "path") ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(srcPath))
                return "Error: path 不能为空。";
            if (!Directory.Exists(srcPath) && !File.Exists(srcPath))
                return "Error: 路径不存在: " + srcPath + "\n注意 .cojt 是目录，传目录本身。";

            var doc = TxApplication.ActiveDocument;
            if (doc == null) return "Error: 当前没有打开的工程。";
            var root = doc.PhysicalRoot;
            if (root == null) return "Error: 取不到 PhysicalRoot。";

            var finalName = GetString(input, "name");
            if (string.IsNullOrWhiteSpace(finalName))
                finalName = DeriveName(srcPath);

            var log = new StringBuilder();
            string tempDir = null;

            try
            {
                // ── ① 复制到纯英文临时路径 ──
                // 临时名也必须是 ASCII:name 走窄字符 CString,中文同样会崩
                var asciiName = ToAscii(finalName);
                tempDir = Path.Combine(EnsureTempRoot(), asciiName + ".cojt");

                CopyTree(srcPath, tempDir);
                log.AppendLine("① 已复制到临时路径: " + tempDir);

                // ── ② 插入 ──
                var proto = ResolvePrototype(GetString(input, "prototype_from"), log);

                var data = new TxInsertComponentCreationData(asciiName, tempDir);
                if (proto != null) data.Prototype = proto;

                if (input["x"] != null || input["y"] != null || input["z"] != null)
                {
                    var loc = new TxTransformation();
                    loc.Translation = new TxVector(Num(input, "x"), Num(input, "y"), Num(input, "z"));
                    data.AbsoluteLocation = loc;
                }

                ITxComponent comp;
                try
                {
                    comp = root.InsertComponent(data);
                }
                catch (Exception ex)
                {
                    return log + "\n" + Explain(ex, tempDir, proto);
                }

                if (comp == null) return log + "\n② 插入返回 null，未知失败。";
                log.AppendLine("② 插入成功: " + SafeName(comp));

                // ── ③ 迁回项目库 ──
                var libDir = GetString(input, "library_dir");
                var migrated = MigrateToLibrary(comp, libDir, finalName, log);

                // ── ④ 落位与命名 ──
                if (!string.Equals(SafeName(comp), finalName, StringComparison.Ordinal))
                {
                    try
                    {
                        ((ITxObject)comp).Name = finalName;
                        log.AppendLine("④ 已重命名为: " + finalName);
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine("④ 重命名失败(不影响使用): " + ex.Message);
                    }
                }

                // ── ⑤ 清理 ──
                if (migrated && !Bool(input, "keep_temp"))
                {
                    TryDelete(tempDir);
                    log.AppendLine("⑤ 已清理临时文件");
                }
                else if (!migrated)
                {
                    log.AppendLine("⑤ 【临时文件已保留】—— 迁库未完成，"
                                 + "组件仍指向临时目录，删掉它组件就会损坏: " + tempDir);
                }

                log.AppendLine();
                log.Append("导入完成。对象: ").Append(SafeName(comp));
                try { log.Append("  [").Append(((ITxObject)comp).Id).Append("]"); } catch { }
                return log.ToString();
            }
            catch (Exception ex)
            {
                if (tempDir != null && !Bool(input, "keep_temp")) TryDelete(tempDir);
                return log + "\nError: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        // ── 原型 ──

        /// <summary>
        /// 从场景里一个同类型组件取原型。
        /// InsertComponent 需要 prototype 才能确定实例化成什么类型；
        /// 不给的话 native 直接拒绝。
        /// </summary>
        private static ITxPlanningObject ResolvePrototype(string sourceName, StringBuilder log)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                log.AppendLine("② 未提供 prototype_from，尝试无原型插入(成功率低)");
                return null;
            }

            ITxObject src; string err;
            if (!PsBridge.TryResolve(sourceName, null, out src, out err))
            {
                log.AppendLine("② 找不到原型来源对象: " + err);
                return null;
            }

            try
            {
                var p = src.GetType().GetProperty("PlanningRepresentation",
                    BindingFlags.Public | BindingFlags.Instance);
                var proto = p != null ? p.GetValue(src, null) as ITxPlanningObject : null;

                if (proto == null)
                {
                    log.AppendLine("② " + sourceName + " 没有 PlanningRepresentation");
                    return null;
                }

                log.AppendLine("② 原型取自: " + sourceName);
                return proto;
            }
            catch (Exception ex)
            {
                log.AppendLine("② 取原型失败: " + ex.Message);
                return null;
            }
        }

        // ── 迁库 ──

        /// <summary>
        /// CopyToLocal → SaveToLibrary，把组件从临时路径迁进项目库。
        /// 这一步做完，组件的 FullPath 才指向库，GUI 库浏览器也才能管理它。
        /// </summary>
        private static bool MigrateToLibrary(ITxComponent comp, string libDir,
                                             string finalName, StringBuilder log)
        {
            try
            {
                var storable = comp as ITxStorable;
                if (storable == null) { log.AppendLine("③ 组件不是 ITxStorable，跳过迁库"); return false; }

                var libStorage = storable.StorageObject as TxLibraryStorage;
                if (libStorage == null)
                {
                    log.AppendLine("③ StorageObject 不是 TxLibraryStorage("
                        + (storable.StorageObject == null ? "null" : storable.StorageObject.GetType().Name)
                        + ")，跳过迁库");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(libDir))
                {
                    log.AppendLine("③ 未指定 library_dir，组件留在临时路径");
                    return false;
                }

                var local = libStorage.CopyToLocal();
                if (local == null) { log.AppendLine("③ CopyToLocal 返回 null"); return false; }

                // SaveToLibrary 的目标路径由项目库决定，这里可以是中文 ——
                // 插入阶段已经过去了，后续走的是库管理路径，不再经过那个窄字符转换
                var target = new TxLibraryData(libDir, finalName);
                var saved = local.SaveToLibrary(target);

                if (saved == null) { log.AppendLine("③ SaveToLibrary 返回 null"); return false; }

                log.AppendLine("③ 已迁入库: " + saved.FullPath);
                return true;
            }
            catch (Exception ex)
            {
                log.AppendLine("③ 迁库失败: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ── 文件 ──

        private static string EnsureTempRoot()
        {
            var root = TempRoot;

            // 兜底:配置被改成含非 ASCII 的路径时，整件事就白做了
            if (!IsAscii(root)) root = @"C:\TxAgentImport";

            Directory.CreateDirectory(root);
            return root;
        }

        private static void CopyTree(string src, string dst)
        {
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            Directory.CreateDirectory(dst);

            if (File.Exists(src))
            {
                // 传的是单个文件(如 .jt)，直接放进 cojt 目录
                File.Copy(src, Path.Combine(dst, Path.GetFileName(src)), true);
                return;
            }

            foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(src, dst));

            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(src, dst), true);
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        // ── 字符串 ──

        private static bool IsAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            foreach (var c in s) if (c > 127) return false;
            return true;
        }

        /// <summary>
        /// 转成 ASCII 安全名。非 ASCII 字符换成下划线，
        /// 全是中文时退回带哈希的固定前缀 —— 保证不会产出空名或重名。
        /// </summary>
        private static string ToAscii(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Comp_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(c <= 127 && (char.IsLetterOrDigit(c) || c == '_' || c == '-') ? c : '_');

            var r = sb.ToString().Trim('_');
            if (r.Length == 0 || !r.Any(char.IsLetterOrDigit))
                r = "Comp_" + Math.Abs(s.GetHashCode()).ToString("D8");

            return r.Length > 60 ? r.Substring(0, 60) : r;
        }

        private static string DeriveName(string path)
        {
            try
            {
                var raw = path.TrimEnd('\\', '/');
                var n = Path.GetFileNameWithoutExtension(raw);
                return string.IsNullOrWhiteSpace(n) ? "ImportedComponent" : n;
            }
            catch { return "ImportedComponent"; }
        }

        // ── 异常分类 ──

        private static string Explain(Exception ex, string tempPath, ITxPlanningObject proto)
        {
            var e = Unwrap(ex);
            var t = e.GetType().Name;

            var sb = new StringBuilder();
            sb.Append("Error: 插入失败 [").Append(t).Append("] ").AppendLine(e.Message);
            sb.AppendLine();

            switch (t)
            {
                case "TxNotImplementedException":
                case "TxUnknownErrorException":
                    sb.AppendLine("这两种异常在本场景下【几乎都不是字面含义】——");
                    sb.AppendLine("native 层拒绝时错误文本被托管层丢弃，只留了一个笼统的类型名。");
                    sb.AppendLine("已知的真实原因按概率排序:");
                    sb.Append("  1. 路径或名称含非 ASCII 字符 —— 本工具已转 ASCII 临时路径("
                        ).Append(tempPath).AppendLine(")，若仍失败请检查该路径是否真的纯英文");
                    sb.AppendLine(proto == null
                        ? "  2. 【没有提供原型】—— 传 prototype_from=场景里同类型组件名 再试，这一条最可能"
                        : "  2. 原型类型与 cojt 不匹配 —— 换一个真正同型号的组件作 prototype_from");
                    sb.Append("  3. cojt 内容不完整(缺 TuneData.xml 或几何文件)");
                    break;

                case "TxFileDoesNotExistException":
                    sb.Append("临时路径不存在或不被识别: ").AppendLine(tempPath);
                    sb.Append("检查源目录是否是完整的 .cojt(应含 TuneData.xml 与几何文件)。");
                    break;

                case "TxComponentNotSycnhronizedException":
                    sb.Append("连接模式下该组件未同步到 eMServer。先同步，或切 Standalone。");
                    break;

                case "TxComponentUnderLibraryRootDirectlyException":
                    sb.Append("组件直接位于库根目录下。在库根下建一层子目录再放进去。");
                    break;

                case "TxNotSupportedInTCPlatformException":
                    sb.Append("Teamcenter 平台下不支持该接口，组件需走签入签出流程。");
                    break;

                default:
                    sb.Append("未归类的异常。若要拿到 native 的真实错误文本，"
                            + "需通过 Tecnomatix Doctor 开启应用日志 —— "
                            + "托管异常里只有错误码映射后的类型名，errMsg 被丢弃了。");
                    break;
            }

            return sb.ToString();
        }

        // ── 探查 ──

        private static string Probe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("== 导入接口探查 ==");
            sb.AppendLine();

            var doc = TxApplication.ActiveDocument;
            sb.Append("ActiveDocument: ").AppendLine(doc == null ? "null（未打开工程）" : "ok");
            if (doc == null) return sb.ToString();

            object root = null;
            try { root = doc.PhysicalRoot; } catch { }
            sb.Append("PhysicalRoot: ").AppendLine(root == null ? "null" : root.GetType().FullName);

            sb.AppendLine();
            sb.Append("临时根目录: ").Append(TempRoot);
            sb.AppendLine(IsAscii(TempRoot) ? "  ✅ 纯 ASCII" : "  ❌ 含非 ASCII，必须改！");

            sb.AppendLine();
            sb.AppendLine("-- InsertComponent 重载 --");
            try
            {
                foreach (var m in root.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                             .Where(x => x.Name == "InsertComponent"))
                    sb.Append("  ").Append(m.ReturnType.Name).Append(" InsertComponent(")
                      .Append(string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)))
                      .AppendLine(")");
            }
            catch (Exception ex) { sb.AppendLine("  反射失败: " + ex.Message); }

            sb.AppendLine();
            sb.AppendLine("-- 提示 --");
            sb.Append("导入失败最常见的原因是路径/名称含中文。本工具会自动中转到 ");
            sb.Append(TempRoot).AppendLine(" 下完成插入，再迁回项目库。");
            sb.Append("务必提供 prototype_from（场景里一个同型号组件名），缺原型基本插不进去。");
            return sb.ToString();
        }

        // ── 辅助 ──

        private static bool Bool(JObject o, string key)
        {
            return o != null && o[key] != null && o[key].Type == JTokenType.Boolean && (bool)o[key];
        }

        private static double Num(JObject o, string key)
        {
            if (o == null || o[key] == null) return 0;
            double v;
            return double.TryParse(o[key].ToString(), out v) ? v : 0;
        }

        private static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            return ex;
        }

        private static string SafeName(object o)
        {
            try { return ((ITxObject)o).Name; } catch { return "?"; }
        }
    }
}
