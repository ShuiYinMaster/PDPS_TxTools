---
key: default_object_coordinates
name: 查看对象世界坐标
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

以表格列出绑定对象的世界坐标，便于检查标记位置或对照焊点坐标。

默认读取对象的**绝对坐标原点**；勾选“读取几何中心”后使用 `GeometricCenter`。两者对偏置建模的几何可能不同。单位为 mm，只读取数据，不改变工程。

## 参数

```json
[
  {"Name":"targets","Label":"检查对象","Kind":"objects","Required":true},
  {"Name":"use_geometric_center","Label":"读取几何中心","Kind":"bool","Required":false,"Default":"false"}
]
```

## 代码

```csharp
var seen = new HashSet<string>(StringComparer.Ordinal);
var rows = new List<string>();
int failed = 0, skipped = 0;
foreach (ITxObject item in targets)
{
    if (!seen.Add(Convert.ToString(item.Id))) continue;
    var locatable = item as ITxLocatableObject;
    if (locatable == null) { skipped++; log("[跳过] " + item.Name + "：不是可定位对象。"); continue; }
    try
    {
        TxVector p = (use_geometric_center ? locatable.GeometricCenter : locatable.AbsoluteLocation).Translation;
        if (new double[] { p.X, p.Y, p.Z }.Any(v => double.IsNaN(v) || double.IsInfinity(v))) throw new Exception("坐标不是有限数字。");
        string name = (item.Name ?? "").Replace("|", "／").Replace("\r", " ").Replace("\n", " ");
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        rows.Add("| " + name + " | " + p.X.ToString("F3", culture) + " | " + p.Y.ToString("F3", culture) + " | " + p.Z.ToString("F3", culture) + " |");
    }
    catch (Exception ex) { failed++; log("[读取失败] " + item.Name + "：" + ex.Message); }
}
if (rows.Count == 0) throw new Exception("没有读取到有效坐标，详情见日志。");
return "## 世界坐标（mm）\n\n位置来源：" + (use_geometric_center ? "几何中心" : "对象绝对原点")
    + "\n\n| 对象 | X | Y | Z |\n| --- | ---: | ---: | ---: |\n" + string.Join("\n", rows.ToArray())
    + "\n\n读取 " + rows.Count + " 个，跳过 " + skipped + " 个，失败 " + failed + " 个。";
```
