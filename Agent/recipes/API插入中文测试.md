---
key: api_insert_chinese_test
name: API 插入中文测试
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

用 SDK 的 `TxInsertComponentCreationData` + `PhysicalRoot.InsertComponent` 插入指定 cojt，判断失败点是「中文路径」「中文名称」还是「插入成功但名称被退化」。每次运行只插入一个实例，可选自动删除。

GUI 的 Insert Component 支持中文，所以本配方只看 API 这一侧的行为差异。

## 参数

```json
[
  {"Name":"cojt_path","Label":"要插入的 cojt 路径","Kind":"text","Required":true},
  {"Name":"insert_name","Label":"插入名称（留空=用文档内名称）","Kind":"text","Required":false,"Default":""},
  {"Name":"cleanup","Label":"插入后删除测试实例","Kind":"bool","Required":false,"Default":"true"}
]
```

## 执行按钮

```json
[
  {
    "id": "insert_clean",
    "label": "插入并清理",
    "args": {
      "cleanup": "true"
    }
  },
  {
    "id": "insert_keep",
    "label": "插入并保留",
    "args": {
      "cleanup": "false"
    }
  }
]
```

## 代码

```csharp
var sb = new StringBuilder();

Func<string, bool> isAscii = delegate(string s)
{
    if (s == null) return true;
    for (int i = 0; i < s.Length; i++) if (s[i] > 127) return false;
    return true;
};

Func<string, string> nameOf = delegate(string s)
{
    if (s == null) return "";
    return s;
};

string path = Convert.ToString(cojt_path);
if (path == null) path = "";
path = path.Trim().Replace('/', '\\');
if (path.EndsWith("\\", StringComparison.Ordinal)) path = path.Substring(0, path.Length - 1);
if (path.Length == 0) throw new Exception("请填写要插入的 cojt 路径。");

bool exists = false;
try { exists = System.IO.Directory.Exists(path) || System.IO.File.Exists(path); }
catch (Exception) { }

sb.AppendLine("## API 插入中文测试");
sb.AppendLine();
sb.AppendLine("| 项目 | 值 |");
sb.AppendLine("| --- | --- |");
sb.AppendLine("| 源 cojt | `" + path + "` |");
sb.AppendLine("| 路径存在 | " + (exists ? "是" : "**否**") + " |");
sb.AppendLine("| 路径全 ASCII | " + (isAscii(path) ? "是" : "**否**") + " |");

string fileName = "";
try { fileName = System.IO.Path.GetFileName(path); } catch (Exception) { }
sb.AppendLine("| cojt 文件名 | `" + fileName + "`（" + (isAscii(fileName) ? "ASCII" : "**含非 ASCII**") + "） |");

string sysroot = "";
try { string r = TxApplication.SystemRootDirectory; sysroot = r == null ? "" : r; }
catch (Exception) { }
bool insideSysroot = false;
try { insideSysroot = sysroot.Length > 0 && path.StartsWith(sysroot, StringComparison.OrdinalIgnoreCase); }
catch (Exception) { }
sb.AppendLine("| 位于库根内 | " + (insideSysroot ? "是" : "**否**") + " |");
sb.AppendLine();

if (!exists) throw new Exception("路径不存在，请先确认 cojt 目录：" + path);

var segs = new List<string>();
foreach (string seg in path.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
    segs.Add((isAscii(seg) ? "ASCII" : "**非ASCII**") + " " + seg);
sb.AppendLine("路径分段：" + string.Join(" / ", segs.ToArray()));
sb.AppendLine();

string name = Convert.ToString(insert_name);
if (name == null) name = "";
name = name.Trim();
string nameLabel = name.Length == 0 ? "（空：使用模型文档内名称）" : name;
sb.AppendLine("插入参数：name=`" + nameLabel + "`，位置 (0,0,0)。");
if (name.Length > 0 && !isAscii(name))
    sb.AppendLine("注意：本次显式传入了**含非 ASCII 的名称**，用于观察 API 是否退化。");
sb.AppendLine();

// 记录插入前场景规模，用于判断是否产生了实例
int before = 0;
TxDocument doc = TxApplication.ActiveDocument;
TxPhysicalRoot root = doc.PhysicalRoot;
try { before = root.GetAllDescendants(new TxTypeFilter(typeof(ITxObject))).Count; }
catch (Exception) { }

ITxComponent comp = null;
string errorType = null;
string errorText = null;
string firstError = null;
string usedForm = "绝对路径（反斜杠）";
string relativeForm = "";

Func<string, ITxComponent> insertWith = delegate(string p)
{
    try
    {
        var data = new TxInsertComponentCreationData(name, p);
        var loc = new TxTransformation();
        loc.Translation = new TxVector(0, 0, 0);
        data.AbsoluteLocation = loc;
        return root.InsertComponent(data);
    }
    catch (Exception ex)
    {
        errorType = ex.GetType().FullName;
        errorText = ex.Message;
        Exception inner = ex.InnerException;
        int depth = 0;
        while (inner != null && depth < 3)
        {
            errorText += " / inner: " + inner.GetType().Name + ": " + inner.Message;
            inner = inner.InnerException;
            depth++;
        }
        return null;
    }
};

// 第 1 轮：绝对路径（反斜杠）。
comp = insertWith(path);

// 第 2 轮：绝对路径失败时，改用 #\ 相对路径 —— PS 在 psz 里存的就是这种格式。
// 目的：让插入产生的库引用天然是「反斜杠 + 相对路径」，保存时不会被重置到默认目录。
if (comp == null && insideSysroot)
{
    firstError = errorType + "：" + errorText;
    string normRoot = sysroot.TrimEnd('\\');
    string rel = path.Substring(normRoot.Length).TrimStart('\\');
    relativeForm = "#\\" + rel;
    errorType = null;
    errorText = null;
    comp = insertWith(relativeForm);
    if (comp != null) usedForm = "相对路径 `" + relativeForm + "`";
}

int after = before;
try { after = root.GetAllDescendants(new TxTypeFilter(typeof(ITxObject))).Count; }
catch (Exception) { }

sb.AppendLine("### 结果");
sb.AppendLine();

if (comp == null)
{
    sb.AppendLine("**插入失败**（未返回组件）。");
    sb.AppendLine();
    if (errorText != null)
    {
        sb.AppendLine("- 异常类型：`" + errorType + "`");
        sb.AppendLine("- 异常消息：" + errorText);
    }
    else
    {
        sb.AppendLine("- `InsertComponent` 返回 null，但未抛异常。");
    }
    if (relativeForm.Length > 0)
    {
        sb.AppendLine();
        sb.AppendLine("两轮都失败：");
        sb.AppendLine();
        sb.AppendLine("- 绝对路径那一轮：" + firstError);
        sb.AppendLine("- 相对路径 `" + relativeForm + "` 那一轮：" + errorText);
    }
    if (after > before)
    {
        sb.AppendLine();
        sb.AppendLine("⚠ 场景对象数从 " + before + " 变为 " + after + "，说明**已经产生实例**，");
        sb.AppendLine("请到场景里核对这个残留，避免重复插入。");
    }
    sb.AppendLine();
    sb.AppendLine("对照建议：把同一个 cojt 放到纯 ASCII 路径再跑一次；两次结果不同即可确认中文路径是变量。");
    return sb.ToString();
}

string actualName = "";
try { actualName = comp.Name == null ? "" : comp.Name; }
catch (Exception) { }

string storageKind = "无存储对象";
string fullPath = null;
try
{
    ITxStorable storable = comp as ITxStorable;
    if (storable != null && storable.StorageObject != null)
    {
        storageKind = storable.StorageObject.GetType().Name;
        TxLibraryStorage lib = storable.StorageObject as TxLibraryStorage;
        if (lib != null) fullPath = lib.FullPath;
    }
}
catch (Exception ex) { storageKind = "读取失败：" + ex.GetType().Name; }

bool open = false;
try { open = comp.IsOpenForModeling; } catch (Exception) { }

sb.AppendLine("**插入成功**。");
sb.AppendLine();
sb.AppendLine("| 项目 | 值 |");
sb.AppendLine("| --- | --- |");
sb.AppendLine("| 使用形式 | " + usedForm + " |");
sb.AppendLine("| 场景名称 | `" + actualName + "` |");
sb.AppendLine("| 场景对象数变化 | " + before + " → " + after + " |");
sb.AppendLine("| SDK 类型 | " + comp.GetType().Name + " |");
sb.AppendLine("| 存储类型 | " + storageKind + " |");
sb.AppendLine("| 库引用 | " + (fullPath == null || fullPath.Length == 0 ? "（空）" : "`" + fullPath + "`") + " |");
sb.AppendLine("| 是否在建模中 | " + (open ? "是" : "否") + " |");
sb.AppendLine();

// 名称退化检测：传入了非 ASCII 名称，实际拿到的却含 '?' 或与预期不符
string expected = name.Length > 0 ? name : fileName;
if (name.Length > 0 && !isAscii(name))
{
    if (actualName != name)
    {
        sb.AppendLine("⚠ **名称不一致**：请求 `" + name + "`，实际 `" + actualName + "`。");
        if (actualName.IndexOf('?') >= 0)
            sb.AppendLine("实际名称里出现 `?`，符合「Unicode → 窄字符转换丢失」的特征。");
        sb.AppendLine();
    }
    else
    {
        sb.AppendLine("名称被完整保留，API 这一侧没有退化。");
        sb.AppendLine();
    }
}

if (storageKind == "TxLocalStorage")
{
    sb.AppendLine("⚠ 插入后是 `TxLocalStorage`：组件没有取得正式库引用。");
    sb.AppendLine("若源 cojt 位于库根内，这说明插入时被当作外部组件复制到了本地 EngData，");
    sb.AppendLine("后续结束建模会缺少「转正」这一步。");
    sb.AppendLine();
}
else if (storageKind == "TxLibraryStorage")
{
    sb.AppendLine("插入后取得正式库引用，存储路径与源位置一致。");
    sb.AppendLine();
}

if (Convert.ToBoolean(cleanup))
{
    try
    {
        ((ITxObject)comp).Delete();
        sb.AppendLine("已删除本次插入的测试实例，场景恢复原状。");
        log("[清理] 已删除测试实例");
    }
    catch (Exception ex)
    {
        sb.AppendLine("⚠ 删除失败：" + ex.Message + "；请手工清理场景中的测试实例。");
    }
}
else
{
    sb.AppendLine("已保留本次插入的实例，便于在场景中查看名称与引用。");
}
sb.AppendLine();
sb.AppendLine("### 判定要点");
sb.AppendLine();
sb.AppendLine("- **抛异常** → API 插入不接受该路径/名称，记录异常原文与是否产生残留实例。");
sb.AppendLine("- **成功但名称为 `?`** → 字符串在 `.NET → native` 编组时被降级。");
sb.AppendLine("- **成功且名称完整、库引用正确** → 该组合下 API 可用，问题只出现在特定环节。");
sb.AppendLine("- 四组对照：ASCII 路径 + ASCII 名 / 中文路径 + ASCII 名 / ASCII 路径 + 中文名 / 中文路径 + 中文名。");

log("[插入] " + fileName + " → " + (comp == null ? "失败" : "成功"));

return sb.ToString();
```
