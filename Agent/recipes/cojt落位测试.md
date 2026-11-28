---
key: cojt_savelibrary_test
name: cojt 落位测试
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

对一个处于建模中的 `TxLocalStorage` 组件调用 `TxLocalStorage.SaveToLibrary(TxLibraryData)`，验证它能否把 cojt **落到指定子目录**而不是库根。这是唯一带目录参数的入库 API。

## 参数

```json
[
  {"Name":"targets","Label":"待落位组件","Kind":"objects","Required":true},
  {"Name":"target_dir","Label":"目标子目录","Kind":"text","Required":true},
  {"Name":"file_name","Label":"cojt 名称（留空=组件名）","Kind":"text","Required":false,"Default":""},
  {"Name":"remove_source","Label":"成功后删除原组件","Kind":"bool","Required":false,"Default":"false"}
]
```

## 执行按钮

```json
[
  {
    "id": "try",
    "label": "尝试落位",
    "args": {
      "remove_source": "false"
    }
  }
]
```

## 代码

```csharp
// 目标：验证 SaveToLibrary 能否把本地建模组件落到指定子目录，
// 而不是像结束建模那样统一写回库根。
var sb = new StringBuilder();

Func<string, bool> isAscii = delegate(string s)
{
    if (s == null) return true;
    for (int i = 0; i < s.Length; i++) if (s[i] > 127) return false;
    return true;
};

Func<ITxObject, string> nameOf = delegate(ITxObject o)
{
    if (o == null) return "";
    try { return o.Name == null ? "" : o.Name; }
    catch (Exception) { return ""; }
};

string sysroot = "";
try { string r = TxApplication.SystemRootDirectory; sysroot = r == null ? "" : r; }
catch (Exception) { }

string dir = Convert.ToString(target_dir);
if (dir == null) dir = "";
dir = dir.Trim().Replace('/', '\\').TrimEnd('\\');
if (dir.Length == 0) throw new Exception("请填写目标子目录。");
if (!System.IO.Path.IsPathRooted(dir)) dir = System.IO.Path.Combine(sysroot, dir);

sb.AppendLine("## cojt 落位测试");
sb.AppendLine();
sb.AppendLine("| 项目 | 值 |");
sb.AppendLine("| --- | --- |");
sb.AppendLine("| 库根 sysroot | `" + sysroot + "` |");
sb.AppendLine("| 目标目录 | `" + dir + "` |");
sb.AppendLine("| 目标目录全 ASCII | " + (isAscii(dir) ? "是" : "**否**") + " |");

bool dirExists = false;
try { dirExists = System.IO.Directory.Exists(dir); } catch (Exception) { }
sb.AppendLine("| 目标目录存在 | " + (dirExists ? "是" : "**否**") + " |");
if (!dirExists) throw new Exception("目标目录不存在，请先创建：" + dir);

string sysRootNorm = sysroot.TrimEnd('\\');
if (sysRootNorm.Length > 0 && dir.StartsWith(sysRootNorm, StringComparison.OrdinalIgnoreCase))
    sb.AppendLine("| 位于库根内 | 是（相对路径：" + dir.Substring(sysRootNorm.Length).TrimStart('\\') + "） |");
else
    sb.AppendLine("| 位于库根内 | **否**（SaveToLibrary 可能拒绝库根之外的目录） |");
sb.AppendLine();

foreach (ITxObject raw in targets)
{
    ITxComponent comp = raw as ITxComponent;
    if (comp == null) { log("[跳过] " + nameOf(raw) + "：不是组件"); continue; }

    string compName = nameOf(comp);
    sb.AppendLine("### " + (compName.Length == 0 ? "(未命名)" : compName));
    sb.AppendLine();

    ITxStorable storable = comp as ITxStorable;
    object storage = storable == null ? null : storable.StorageObject;
    string storageKind = storage == null ? "无存储对象" : storage.GetType().Name;
    sb.AppendLine("- 当前存储类型：" + storageKind);

    TxLocalStorage local = storage as TxLocalStorage;
    if (local == null)
    {
        sb.AppendLine("- **跳过**：不是 TxLocalStorage，本配方只处理本地建模组件（已入库组件无需落位）。");
        sb.AppendLine();
        continue;
    }

    bool open = false;
    try { open = comp.IsOpenForModeling; } catch (Exception) { }
    sb.AppendLine("- 是否在建模中：" + (open ? "是" : "否"));

    string name = Convert.ToString(file_name);
    if (name == null) name = "";
    name = name.Trim();
    if (name.Length == 0) name = compName;
    if (!name.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase)) name = name + ".cojt";
    sb.AppendLine("- 目标 cojt 名称：`" + name + "`（" + (isAscii(name) ? "ASCII" : "**含非 ASCII**") + "）");
    sb.AppendLine();

    string expected = System.IO.Path.Combine(dir, name);
    bool existedBefore = false;
    try { existedBefore = System.IO.Directory.Exists(expected); } catch (Exception) { }
    sb.AppendLine("- 目标是否已存在：**" + (existedBefore ? "是（可能被覆盖或拒绝）" : "否") + "**");
    sb.AppendLine();

    TxLibraryStorage saved = null;
    string error = null;
    int beforeCount = 0;
    try { beforeCount = TxApplication.ActiveDocument.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxObject))).Count; }
    catch (Exception) { }

    // TxLibraryData 的目录参数实测必须是绝对路径（相对路径不被接受）。
    string libRel = dir;

    try
    {
        sb.AppendLine("- 传给 TxLibraryData 的目录（绝对路径）：`" + libRel + "`");
        sb.AppendLine();
        var data = new TxLibraryData(libRel, name);
        saved = local.SaveToLibrary(data);
    }
    catch (Exception ex)
    {
        error = ex.GetType().FullName + "：" + ex.Message;
        Exception inner = ex.InnerException;
        int depth = 0;
        while (inner != null && depth < 3)
        {
            error += " / inner: " + inner.GetType().Name + "：" + inner.Message;
            inner = inner.InnerException;
            depth++;
        }
    }

    if (saved == null)
    {
        sb.AppendLine("**失败**" + (error == null ? "：SaveToLibrary 返回 null" : ""));
        if (error != null) sb.AppendLine();
        if (error != null) sb.AppendLine("- 异常：" + error);
        sb.AppendLine();
        sb.AppendLine("判定：SaveToLibrary 这条路走不通，落位只能靠 GUI 另存为或文件级搬运 + psz 修正。");
        sb.AppendLine();
        continue;
    }

    // ── 路径回写探测 ──
    // TxLibraryStorage.LibraryData 是只读属性；若它返回内部活引用，
    // 修改其中的 Path 有可能把存储登记形式改成「绝对路径 + 反斜杠」。
    string ldPathBefore = null;
    string ldPathAfter = null;
    string fpAfterSet = null;
    string ldVerdict = "";
    try
    {
        TxLibraryData ld = saved.LibraryData;
        if (ld == null)
        {
            ldVerdict = "LibraryData 返回 null，无法回写。";
        }
        else
        {
            ldPathBefore = ld.Path;
            ld.Path = libRel;
            ldPathAfter = ld.Path;
            fpAfterSet = saved.FullPath;
            if (fpAfterSet != null) fpAfterSet = fpAfterSet.Replace('/', '\\');
            if (ldPathAfter == null || ldPathAfter.Length == 0)
                ldVerdict = "回写后 Path 为空：LibraryData 多半是副本，set 未作用到存储。";
            else if (!string.Equals(ldPathAfter, ldPathBefore, StringComparison.Ordinal))
                ldVerdict = "Path 已变化 —— LibraryData 可能是活引用，写入生效（还要看 FullPath 与保存结果）。";
            else
                ldVerdict = "Path 未变化（原值即目标值，或写入被忽略）。";
        }
    }
    catch (Exception ex)
    {
        ldVerdict = "回写异常：" + ex.GetType().Name + "：" + ex.Message;
    }

    string fullPath = null;
    try { fullPath = saved.FullPath; } catch (Exception) { }
    // SDK 返回的 FullPath 可能是正斜杠（D:/Sim Project/...），统一成反斜杠后再显示与比较，
    // 避免写进计划文件或与 expected 做字符串比对时因分隔符不同而误判。
    if (fullPath != null) fullPath = fullPath.Replace('/', '\\');

    // 关键：SaveToLibrary 之后，**原组件自身**的存储是否变了？
    // 变了 → 组件已指向目标，保存后若被重置就是 psz 落盘形式（正斜杠）的问题；
    // 没变 → 只是写了一份副本，场景实例仍无正式归属。
    string afterKind = "无存储对象";
    string afterPath = null;
    try
    {
        ITxStorable st2 = comp as ITxStorable;
        if (st2 != null && st2.StorageObject != null)
        {
            afterKind = st2.StorageObject.GetType().Name;
            TxLibraryStorage l2 = st2.StorageObject as TxLibraryStorage;
            if (l2 != null)
            {
                afterPath = l2.FullPath;
                if (afterPath != null) afterPath = afterPath.Replace('/', '\\');
            }
        }
    }
    catch (Exception ex) { afterKind = "读取失败：" + ex.GetType().Name; }

    bool landed = false;
    try { landed = System.IO.Directory.Exists(expected); } catch (Exception) { }

    // 关键：SaveToLibrary 返回的是「新组件」的存储，必须确认场景里是否真有对应的实例。
    int afterCount = 0;
    try { afterCount = TxApplication.ActiveDocument.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxObject))).Count; }
    catch (Exception) { }

    var matches = new List<string>();
    var stack = new Stack<ITxObject>();
    int visited = 0;
    try
    {
        TxPhysicalRoot probeRoot = TxApplication.ActiveDocument.PhysicalRoot;
        if (probeRoot != null) stack.Push(probeRoot);
    }
    catch (Exception) { }

    while (stack.Count > 0 && visited < 30000)
    {
        ITxObject node = stack.Pop();
        visited++;
        try
        {
            ITxStorable ns = node as ITxStorable;
            TxLibraryStorage nlib = ns == null ? null : ns.StorageObject as TxLibraryStorage;
            if (nlib != null && !string.IsNullOrEmpty(nlib.FullPath) && fullPath != null && fullPath.Length > 0)
            {
                bool same = false;
                try { same = string.Equals(System.IO.Path.GetFullPath(nlib.FullPath), System.IO.Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase); }
                catch (Exception) { }
                if (same) matches.Add(node.GetType().Name + " / " + nameOf(node));
            }
        }
        catch (Exception) { }

        ITxObjectCollection coll = node as ITxObjectCollection;
        if (coll == null) continue;
        try
        {
            TxObjectList kids = coll.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject)));
            if (kids == null) continue;
            foreach (ITxObject kid in kids)
                if (kid != null && stack.Count < 50000) stack.Push(kid);
        }
        catch (Exception) { }
    }

    sb.AppendLine("**成功**。");
    sb.AppendLine();
    sb.AppendLine("| 项目 | 值 |");
    sb.AppendLine("| --- | --- |");
    sb.AppendLine("| 新库引用 FullPath | " + (fullPath == null || fullPath.Length == 0 ? "（空）" : "`" + fullPath + "`") + " |");
    sb.AppendLine("| LibraryData.Path（落位后） | " + (ldPathBefore == null ? "（空）" : "`" + ldPathBefore + "`") + " |");
    sb.AppendLine("| 回写 Path 后 | " + (ldPathAfter == null ? "（空）" : "`" + ldPathAfter + "`") + " |");
    sb.AppendLine("| 回写后 FullPath | " + (fpAfterSet == null || fpAfterSet.Length == 0 ? "（空）" : "`" + fpAfterSet + "`") + " |");
    sb.AppendLine("| 回写判定 | " + ldVerdict + " |");
    sb.AppendLine("| 落位后原组件存储类型 | " + afterKind + " |");
    sb.AppendLine("| 落位后原组件 FullPath | " + (afterPath == null || afterPath.Length == 0 ? "（空）" : "`" + afterPath + "`") + " |");
    sb.AppendLine("| 目标目录已出现 cojt | " + (landed ? "**是**" : "**否**") + " |");
    sb.AppendLine("| 场景对象数变化 | " + beforeCount + " → " + afterCount + " |");
    sb.AppendLine("| 引用该路径的场景对象 | " + (matches.Count == 0 ? "**没有**" : matches.Count + " 个") + " |");
    sb.AppendLine();

    if (afterKind == "TxLibraryStorage" && afterPath != null)
    {
        bool sameAsTarget = false;
        try { sameAsTarget = string.Equals(System.IO.Path.GetFullPath(afterPath), System.IO.Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { }
        if (sameAsTarget)
            sb.AppendLine("★ **原组件已指向目标位置**。若保存/重启后又回到默认目录，说明问题在 psz 落盘形式（正斜杠绝对路径写不进去），而不是落位本身。");
        else
            sb.AppendLine("⚠ 原组件已变成库存储，但指向 `" + afterPath + "`，与目标不同。");
        sb.AppendLine();
    }
    else
    {
        sb.AppendLine("原组件仍是 " + afterKind + " —— 落位只写了副本，场景实例没有正式归属。");
        sb.AppendLine();
    }

    if (matches.Count > 0)
    {
        for (int mi = 0; mi < matches.Count; mi++) sb.AppendLine("- " + matches[mi]);
        sb.AppendLine();
        sb.AppendLine("✅ 场景里存在引用该正式 cojt 的实例 —— **落位链路完整**，可直接在重建流程中使用。");
    }
    else
    {
        sb.AppendLine("⚠ 库里已有正确 cojt，但**场景里没有引用它的实例**。");
        sb.AppendLine("说明 SaveToLibrary 只把数据写入库，没有创建场景实例。");
        sb.AppendLine();
        sb.AppendLine("后续可行做法：删除当前本地实例，改为插入这个已落位的 cojt；");
        sb.AppendLine("但插入走的是 `InsertComponent`——若中文路径限制依旧，这条路会再次卡住。");
        sb.AppendLine("这种情况下，落位只能作为「生成正确 cojt 文件」的手段，场景实例仍需其他方式建立。");
    }
    sb.AppendLine();

    if (fullPath != null && fullPath.Length > 0)
    {
        bool samePath = false;
        try { samePath = string.Equals(System.IO.Path.GetFullPath(fullPath), System.IO.Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { }
        if (samePath) sb.AppendLine("✅ 库引用与目标路径完全一致 —— **SaveToLibrary 能精确落位**。");
        else sb.AppendLine("⚠ 库引用与目标路径不一致，实际落在 `" + fullPath + "`。");
    }
    sb.AppendLine();

    if (Convert.ToBoolean(remove_source))
    {
        try
        {
            ((ITxObject)comp).Delete();
            sb.AppendLine("已删除原组件，仅保留新入库副本。");
        }
        catch (Exception ex) { sb.AppendLine("⚠ 删除原组件失败：" + ex.Message); }
    }
    else
    {
        sb.AppendLine("原组件已保留（它可能仍在建模中），请自行决定是否删除。");
    }
    sb.AppendLine();

    log("[落位] " + compName + " → " + (fullPath == null ? "(空)" : fullPath));
}

sb.AppendLine("---");
sb.AppendLine();
sb.AppendLine("说明：SaveToLibrary 的语义是「把组件副本存到库」，返回的是**新组件**的存储；");
sb.AppendLine("原组件不会被自动替换，需要确认新实例是否满足使用要求（名称、原型、引用）。");

return sb.ToString();
```
