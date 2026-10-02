---
key: default_set_color
name: 一键修改对象颜色
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

给绑定的零件、资源或几何体设置统一颜色。

### 用法

1. 在 PS 中选择目标，再点击 **取当前选择**。
2. 填写十六进制颜色，例如蓝色 `#4F83CC`、红色 `#E45B5B`、绿色 `#49A879`。
3. 默认同时修改目标及其下属可显示对象；取消勾选可只修改目标本身。

重叠范围自动去重，逐项报告结果。更改纳入配方的撤销记录。

## 参数

```json
[
  {"Name":"targets","Label":"修改对象","Kind":"objects","Required":true},
  {"Name":"color_hex","Label":"颜色（#RRGGBB）","Kind":"text","Required":true,"Default":"#4F83CC","Help":"例如蓝色 `#4F83CC`、红色 `#E45B5B`、绿色 `#49A879`。"},
  {"Name":"include_children","Label":"包含下属对象","Kind":"bool","Required":false,"Default":"true"}
]
```

## 代码

```csharp
string hex = Convert.ToString(color_hex).Trim();
if (!System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$"))
    throw new Exception("颜色格式应为 #RRGGBB，例如 #4F83CC。");
var color = new TxColor(Convert.ToByte(hex.Substring(1, 2), 16),
    Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
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
if (objects.Count == 0) throw new Exception("绑定范围内没有可以修改颜色的对象。");
int changed = 0, failed = 0;
foreach (ITxDisplayableObject item in objects)
{
    try { item.Color = color; changed++; }
    catch (Exception ex) { failed++; log("[失败] " + ((ITxObject)item).Name + "：" + ex.Message); }
}
if (changed == 0) throw new Exception("所有对象改色失败，详情见日志。");
return "## 改色结果\n\n颜色：`" + hex.ToUpperInvariant() + "`\n\n- 成功：**" + changed + "** 个\n- 失败：" + failed + " 个";
```
