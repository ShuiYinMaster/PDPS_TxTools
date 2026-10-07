---
key: default_visibility
name: 一键显示或隐藏对象
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

选择对象后点击“显示”或“隐藏”。勾选“包含下属对象”可同时处理子对象。

## 参数

```json
[
  {"Name":"targets","Label":"显示／隐藏对象","Kind":"objects","Required":true},
  {"Name":"show_objects","Label":"显示对象","Kind":"bool","Required":false,"Default":"true"},
  {"Name":"include_children","Label":"包含下属对象","Kind":"bool","Required":false,"Default":"false"}
]
```

## 执行按钮

```json
[
  {
    "id": "show",
    "label": "显示",
    "args": {
      "show_objects": "true"
    }
  },
  {
    "id": "hide",
    "label": "隐藏",
    "args": {
      "show_objects": "false"
    }
  }
]
```

## 代码

```csharp
var objects = new List<ITxDisplayableObject>();
var seen = new HashSet<string>(StringComparer.Ordinal);
Action<ITxObject> add = delegate(ITxObject item)
{
    var display = item as ITxDisplayableObject;
    if (display != null && seen.Add(Convert.ToString(item.Id))) objects.Add(display);
};
foreach (ITxObject root in targets)
{
    add(root);
    var collection = root as ITxObjectCollection;
    if (include_children && collection != null)
        foreach (ITxObject item in collection.GetAllDescendants(new TxTypeFilter(typeof(ITxDisplayableObject)))) add(item);
}
if (objects.Count == 0) throw new Exception("绑定范围内没有可显示对象。");
int changed = 0, failed = 0;
foreach (ITxDisplayableObject item in objects)
{
    try { if (show_objects) item.Display(); else item.Blank(); changed++; }
    catch (Exception ex) { failed++; log("[失败] " + ((ITxObject)item).Name + "：" + ex.Message); }
}
if (changed == 0) throw new Exception("所有对象可见性修改失败，详情见日志。");
return "## 可见性结果\n\n操作：**" + (show_objects ? "显示" : "隐藏") + "**\n\n- 成功：" + changed + " 个\n- 失败：" + failed + " 个";
```
