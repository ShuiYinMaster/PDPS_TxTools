---
key: default_geometry_weld_points
name: 从标记几何中心生成焊点
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

从标记中心批量生成焊点，支持实体、曲面、几何对象和 Block。

1. 选择标记或包含标记的组件；选择组件时会自动查找其内部标记。
2. 按需填写名称关键字。默认只跳过坐标完全相同的点，同名且位置不同的点会分别创建。
3. 点击“执行”生成焊点。

原始几何保留。创建后请检查焊点位置和姿态。

## 参数

```json
[
  {
    "Name": "markers",
    "Label": "标记或包含标记的对象",
    "Kind": "objects",
    "Required": true,
    "ObjectFilter": true,
    "Help": "选择标记几何，或包含标记的零件、组件。"
  },
  {
    "Name": "name_keyword",
    "Label": "标记名称包含",
    "Kind": "text",
    "Required": false,
    "Default": "",
    "Help": "不区分大小写；留空匹配全部，按字面匹配，不使用通配符。"
  },
  {
    "Name": "duplicate_tolerance",
    "Label": "重复点距离容差（mm）",
    "Kind": "number",
    "Required": true,
    "Default": "0",
    "Help": "距离不超过此值的焊点视为重复；0 表示只跳过坐标完全相同的点。"
  }
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
string keyword = (Convert.ToString(name_keyword) ?? "").Trim();
var children = new Dictionary<string, List<ITxObject>>(StringComparer.Ordinal);
Func<ITxObject, List<ITxObject>> getChildren = delegate(ITxObject item)
{
    string id = Convert.ToString(item.Id);
    List<ITxObject> result;
    if (children.TryGetValue(id, out result)) return result;
    result = new List<ITxObject>();
    var collection = item as ITxObjectCollection;
    if (collection != null)
        foreach (ITxObject child in collection.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject)))) result.Add(child);
    children.Add(id, result);
    return result;
};
var geometryState = new Dictionary<string, bool>(StringComparer.Ordinal);
Func<ITxObject, bool> hasGeometry = null;
hasGeometry = delegate(ITxObject item)
{
    if (item is ITxGeometry) return true;
    string id = Convert.ToString(item.Id);
    bool result;
    if (geometryState.TryGetValue(id, out result)) return result;
    geometryState[id] = false;
    var group = item as ITxGroup;
    if (group != null)
    {
        try { if (group.CreateAsGeometry() != null) { geometryState[id] = true; return true; } }
        catch (TxNotSupportedInTCPlatformException) { }
    }
    foreach (var child in getChildren(item))
        if (hasGeometry(child)) { geometryState[id] = true; return true; }
    return false;
};
var roots = new List<ITxObject>();
var rootIds = new HashSet<string>(StringComparer.Ordinal);
foreach (ITxObject root in markers)
    if (root != null && rootIds.Add(Convert.ToString(root.Id))) roots.Add(root);
if (roots.Count == 0) throw new Exception("请先绑定标记或组件。");
var geometries = new List<ITxObject>();
var seen = new HashSet<string>(StringComparer.Ordinal);
int unmatched = 0, unsupported = 0;
Action<ITxObject> visit = null;
visit = delegate(ITxObject item)
{
    if (!seen.Add(Convert.ToString(item.Id))) return;
    // 组件始终作为范围，不能成为焊点标记。
    bool component = item is ITxComponent;
    bool marker = !component && (item is ITxGeometry || item is ITxGroup);
    bool matchName = keyword.Length == 0 || (item.Name ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    if (marker)
    {
        if (matchName && item is ITxLocatableObject && hasGeometry(item))
        { geometries.Add(item); return; }
        if (!matchName) { unmatched++; log("[名称不匹配] " + item.Name); }
        else { unsupported++; log("[跳过] " + item.Name + "：没有可用的几何。"); }
    }
    var descendants = getChildren(item);
    if (descendants.Count == 0 && !marker)
    { unsupported++; log("[跳过] " + item.Name + "：没有可用的子级标记。"); }
    foreach (var child in descendants) visit(child);
};
// 先处理最外层绑定，避免父标记和内部几何重复生成。
foreach (var root in roots)
{
    bool covered = false;
    var ancestors = new HashSet<string>(StringComparer.Ordinal);
    ITxObject parent = root.Collection as ITxObject;
    while (parent != null && ancestors.Add(Convert.ToString(parent.Id)))
    {
        if (rootIds.Contains(Convert.ToString(parent.Id))) { covered = true; break; }
        parent = parent.Collection as ITxObject;
    }
    if (!covered) visit(root);
}
geometries = geometries.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
    .ThenBy(g => Convert.ToString(g.Id), StringComparer.Ordinal).ToList();
if (geometries.Count == 0) throw new Exception("绑定范围没有匹配的标记。请检查名称及详细几何是否加载；跳过原因见日志。");
var points = new List<TxVector>();
foreach (TxWeldLocationOperation existing in opRoot.GetAllDescendants(new TxTypeFilter(typeof(TxWeldLocationOperation))))
{
    points.Add(existing.AbsoluteLocation.Translation);
}
int created = 0, duplicate = 0, failed = 0;
var sources = new List<ITxObject>();
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
    var pose = new TxTransformation(((ITxLocatableObject)geometry).GeometricCenter);
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
    log("[候选] " + geometry.Name + " (" + geometry.GetType().Name + ") -> " + name
        + "，中心 mm：" + p.X.ToString("F3") + ", " + p.Y.ToString("F3") + ", " + p.Z.ToString("F3"));
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
            location = opRoot.CreateWeldLocationOperation(data);
            if (location != null) feature = location.WeldPoint;
        }
        finally { data.Dispose(); }
        if (location == null || feature == null) throw new Exception("创建接口没有返回完整焊点。");
        points.Add(p); created++;
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
return "## 焊点生成结果\n\n- 新建：**" + created
    + "** 个\n- 位置重复跳过：" + duplicate
    + " 个\n- 名称不匹配：" + unmatched + " 个\n- 不支持或无几何：" + unsupported
    + " 个\n- 创建失败：" + failed
    + " 个\n\n原始几何已保留，请检查点位和姿态。";
```
