---
key: default_number_objects
name: 对象批量编号
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

按选择顺序将对象命名为“前缀＋序号”，例如 Part_001；不包含下属对象。

## 参数

```json
[
  {"Name":"targets","Label":"编号对象","Kind":"objects","Required":true},
  {"Name":"name_prefix","Label":"名称前缀","Kind":"text","Required":true,"Default":"Part_"},
  {"Name":"start_number","Label":"起始序号","Kind":"number","Required":true,"Default":"1"},
  {"Name":"number_digits","Label":"序号最少位数","Kind":"number","Required":true,"Default":"3","Help":"例如 3 位显示为 001。","Choices":[{"label": "1 位", "value": "1"}, {"label": "2 位", "value": "2"}, {"label": "3 位", "value": "3"}, {"label": "4 位", "value": "4"}, {"label": "5 位", "value": "5"}, {"label": "6 位", "value": "6"}, {"label": "7 位", "value": "7"}, {"label": "8 位", "value": "8"}, {"label": "9 位", "value": "9"}]}
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
