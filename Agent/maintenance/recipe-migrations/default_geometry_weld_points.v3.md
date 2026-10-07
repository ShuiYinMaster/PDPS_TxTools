---
key: default_geometry_weld_points
name: 从标记几何中心生成焊点
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

从球体、圆柱等**独立焊点标记**的几何中心创建制造焊点及焊点位置操作。依据提供的两份脚本，直接使用当前研究的 **OperationRoot** 创建，无需绑定目标焊接操作。

### 用法

1. 绑定标记几何或包含标记的零件／组件；支持多个组件，重叠选择自动去重。
2. 输入“几何体名称关键字”（例如 `RSW`），按**包含匹配、不区分大小写**筛选；留空匹配全部。只按输入关键字筛选，不再固定排除 CO2，也不要求名称符合 DPUB 格式。
3. 符合 `DPUB-{零件号}-..._{工位}-{编号}-{类型}` 时，生成 `{工位}_{零件号}_{编号}`，编号去掉前导零。例如：
   `DPUB-501021339-XWS_00.001_T1EJ-0000116179-RSW` → **T1EJ_501021339_116179**。
4. 其他格式沿用几何体原名；无名称时生成 `WP_001` 等编号。同名异位的焊点报告冲突，不自动追加后缀。
5. 默认跳过操作根节点下已有焊点及本次新点附近 **0.1 mm** 内的标记，重复执行不会再次生成这些点。

### 坐标与姿态

- 遍历 `Tx2Or3DimensionalGeometry`，每个符合条件的几何体取 `GeometricCenter`。圆柱取中心，不取端面或对象原点。
- 制造焊点位置传入 `GeometricCenter.Translation`，位置操作的 `ProjectedLocation` 使用完整的 `GeometricCenter`，与上传脚本一致。
- 不自动识别球体／圆柱形状。只绑定独立焊点标记；普通车身几何中心不能代表焊点。
- 原始几何保留；法向、零件绑定、焊枪与工艺参数生成后自行检查设置。

## 参数

```json
[
  {"Name":"markers","Label":"焊点标记几何或零件／组件","Kind":"objects","Required":true,"Help":"遍历下属 `Tx2Or3DimensionalGeometry`，按输入的关键字匹配名称。"},
  {"Name":"name_keyword","Label":"几何体名称关键字","Kind":"text","Required":false,"Default":"","Help":"例如 RSW、焊点、CO2；名称包含此关键字即匹配，不区分大小写。留空匹配全部，不使用正则或通配符。"},
  {"Name":"duplicate_tolerance","Label":"重复点距离容差（mm）","Kind":"number","Required":true,"Default":"0.1","Help":"在操作根节点下及本次新点之间按距离去重；0 表示仅排除完全相同的坐标。"}
]
```

## 代码

```csharp
double tolerance = Convert.ToDouble(duplicate_tolerance);
if (double.IsNaN(tolerance) || double.IsInfinity(tolerance) || tolerance < 0 || tolerance > 1000000)
    throw new Exception("重复点距离容差必须是 0 到 1000000 mm 的有限数字。");
var doc = TxApplication.ActiveDocument;
if (doc == null) throw new Exception("没有打开的研究。");
var opRoot = doc.OperationRoot;
if (opRoot == null) throw new Exception("当前研究没有操作根节点。");
var geometries = new List<Tx2Or3DimensionalGeometry>();
var seen = new HashSet<string>(StringComparer.Ordinal);
Action<ITxObject> add = delegate(ITxObject item)
{
    var geometry = item as Tx2Or3DimensionalGeometry;
    if (geometry != null && seen.Add(Convert.ToString(item.Id))) geometries.Add(geometry);
};
foreach (ITxObject root in markers)
{
    add(root);
    var group = root as ITxObjectCollection;
    if (group != null)
        foreach (ITxObject item in group.GetAllDescendants(new TxTypeFilter(typeof(Tx2Or3DimensionalGeometry)))) add(item);
}
geometries = geometries.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => Convert.ToString(g.Id)).ToList();
if (geometries.Count == 0) throw new Exception("绑定范围内没有二维／三维标记几何。请加载详细几何并选择标记或其组件。");
var points = new List<TxVector>();
var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (TxWeldLocationOperation existing in opRoot.GetAllDescendants(new TxTypeFilter(typeof(TxWeldLocationOperation))))
{
    names.Add(existing.Name);
    points.Add(existing.AbsoluteLocation.Translation);
}
int unmatched = 0, conflict = 0, created = 0, duplicate = 0, failed = 0;
string keyword = (Convert.ToString(name_keyword) ?? "").Trim();
var sources = new List<Tx2Or3DimensionalGeometry>();
var poses = new List<TxTransformation>();
var weldNames = new List<string>();
// 在写入场景之前完成候选名称、中心和姿态校验。
foreach (var geometry in geometries)
{
    string geomName = geometry.Name ?? "";
    if (keyword.Length > 0 && geomName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
    { unmatched++; log("[关键字不匹配跳过] " + geomName); continue; }
    var match = System.Text.RegularExpressions.Regex.Match(geomName,
        @"^DPUB-(?<part>[^-\s]+)-.*?_(?<station>T[^-\s]+)-(?<id>[0-9]+)-[^-\r\n]+$");
    string weldName = string.IsNullOrWhiteSpace(geomName) ? "WP_" + (sources.Count + 1).ToString("D3") : geomName;
    if (match.Success)
    {
        string id = match.Groups["id"].Value.TrimStart('0');
        if (id.Length == 0) id = "0";
        weldName = match.Groups["station"].Value + "_" + match.Groups["part"].Value + "_" + id;
    }
    var pose = new TxTransformation(geometry.GeometricCenter);
    TxVector center = pose.Translation;
    if (new double[] { center.X, center.Y, center.Z }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
        throw new Exception("标记 " + geomName + " 的几何中心无效。");
    if (!pose.IsValid || !pose.IsRigidBodyTransformation || pose.IsMirrored)
        throw new Exception("标记 " + geomName + " 的几何中心姿态不是有效的非镜像刚体变换。");
    sources.Add(geometry); poses.Add(pose); weldNames.Add(weldName);
}
for (int i = 0; i < sources.Count; i++)
{
    var geometry = sources[i];
    var pose = poses[i];
    TxVector p = pose.Translation;
    string name = weldNames[i];
    bool exists = points.Any(q =>
    {
        double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
        return dx * dx + dy * dy + dz * dz <= tolerance * tolerance;
    });
    if (exists) { duplicate++; log("[重复跳过] " + geometry.Name); continue; }
    if (names.Contains(name)) { conflict++; log("[同名异位跳过] " + name + "，请核对标记编号与已有焊点。"); continue; }
    TxWeldLocationOperation location = null;
    TxWeldPoint feature = null;
    try
    {
        var data = new TxWeldLocationOperationCreationData();
        try
        {
            data.Name = name;
            data.WeldPointCreationData = Tecnomatix.Engineering.DataTypes.TxMfgCreationDataFactory.CreateWeldPointCreationData(name, p);
            data.ProjectedLocation = pose;
            // 上传的已验证脚本使用 OperationRoot，避免焊接操作创建接口的 NotImplemented。
            location = opRoot.CreateWeldLocationOperation(data);
            if (location != null) feature = location.WeldPoint;
        }
        finally { data.Dispose(); }
        if (location == null || feature == null) throw new Exception("创建接口没有返回完整焊点。");
        names.Add(name); points.Add(p); created++;
        log("[已创建] " + name + "，中心 mm：" + p.X.ToString("F3") + ", " + p.Y.ToString("F3") + ", " + p.Z.ToString("F3"));
    }
    catch (Exception ex)
    {
        failed++; log("[失败] " + geometry.Name + "：" + ex.Message);
        if (location != null) { try { location.Delete(); } catch (Exception cleanup) { throw new Exception("新点清理失败，请撤销本次操作：" + cleanup.Message); } }
        if (feature != null)
        {
            try
            {
                if (!feature.CanBeDeleted) throw new Exception("新建制造焊点仍存在且不能删除。");
                feature.Delete();
            }
            catch (TxInvalidObjectException) { }
            catch (Exception cleanup) { throw new Exception("新焊点特征清理失败，请撤销本次操作：" + cleanup.Message); }
        }
    }
}
if (created == 0 && failed > 0) throw new Exception("没有成功创建焊点，失败 " + failed + " 个，详情见日志。");
return "## 焊点提取结果\n\n- 新建：**" + created + "** 个\n- 关键字不匹配跳过：" + unmatched
    + " 个\n- 重复跳过：" + duplicate
    + " 个\n- 同名异位跳过：" + conflict + " 个\n- 失败：" + failed
    + " 个\n\n创建位置：操作根节点（OperationRoot）。\n\n请检查点位、姿态及工艺设置；原始几何已保留。";
```
