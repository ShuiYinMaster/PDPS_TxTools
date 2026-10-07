---
key: default_number_objects
name: 对象批量编号
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

把绑定对象改为 **前缀 + 序号**，例如 `Part_001`、`Part_002`。

按“取当前选择”得到的对象顺序编号，只修改绑定对象，不遍历下属对象。重叠对象自动去重；不能重命名的对象会跳过，并输出新旧名称。

## 参数

```json
[
  {"Name":"targets","Label":"编号对象","Kind":"objects","Required":true},
  {"Name":"name_prefix","Label":"名称前缀","Kind":"text","Required":true,"Default":"Part_"},
  {"Name":"start_number","Label":"起始序号","Kind":"number","Required":true,"Default":"1"},
  {"Name":"number_digits","Label":"序号最少位数","Kind":"number","Required":true,"Default":"3","Help":"`3` 表示 001；超过三位的序号正常保留。"}
]
```

## 代码

```csharp
double startValue = Convert.ToDouble(start_number), digitsValue = Convert.ToDouble(number_digits);
if (double.IsNaN(startValue) || double.IsInfinity(startValue) || startValue < 0 || startValue > int.MaxValue || Math.Floor(startValue) != startValue)
    throw new Exception("起始序号必须为非负整数。");
if (double.IsNaN(digitsValue) || double.IsInfinity(digitsValue) || digitsValue < 1 || digitsValue > 9 || Math.Floor(digitsValue) != digitsValue)
    throw new Exception("序号最少位数必须为 1 到 9 的整数。");
var items = new List<ITxObject>();
var seen = new HashSet<string>(StringComparer.Ordinal);
foreach (ITxObject item in targets) if (seen.Add(Convert.ToString(item.Id))) items.Add(item);
if (startValue + items.Count - 1 > int.MaxValue) throw new Exception("编号超过整数范围，请减小起始序号。");
int changed = 0, skipped = 0, failed = 0;
int number = (int)startValue;
string prefix = Convert.ToString(name_prefix);
foreach (ITxObject item in items)
{
    string newName = prefix + (number++).ToString("D" + (int)digitsValue);
    try
    {
        if (!item.CanBeRenamed) { skipped++; log("[跳过] " + item.Name + "：不支持重命名。"); continue; }
        string oldName = item.Name;
        item.Name = newName;
        changed++; log("[已编号] " + oldName + " -> " + newName);
    }
    catch (Exception ex) { failed++; log("[失败] " + item.Name + "：" + ex.Message); }
}
if (changed == 0) throw new Exception("没有对象成功编号，详情见日志。");
return "## 编号结果\n\n- 成功：**" + changed + "** 个\n- 不支持：" + skipped + " 个\n- 失败：" + failed + " 个";
```
