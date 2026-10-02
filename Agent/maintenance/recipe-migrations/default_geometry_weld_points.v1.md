---
key: default_geometry_weld_points
name: 从标记几何中心生成焊点
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

把球体、圆柱等**独立焊点标记**的几何中心转换为焊点，加入指定的焊接操作。

### 用法

1. 绑定球体／圆柱几何，或只包含这些标记的组件。组件内每个实体分别生成一个点，重叠选择自动去重。
2. 单独选择目标 **焊接操作**，再为“目标焊接操作”取当前选择。
3. 默认沿用标记名称，添加 `WP_` 前缀。同名会追加序号。
4. 默认跳过与目标操作已有焊点或本次新点距离不超过 **0.1 mm** 的标记；重复执行不会再次生成这些点。

### 坐标与姿态

- 使用 SDK 的 `GeometricCenter`，不是几何体的坐标原点。圆柱取中心位置，不取端面。
- 默认姿态为世界坐标方向；勾选“保留标记姿态”后使用标记的绝对姿态，平移仍取几何中心。
- 只适用于独立标记。普通车身零件的几何中心不能代表焊接位置。
- 不识别表面法向，不自动绑定零件、焊枪或工艺参数；生成后按工艺要求设置。保留原始几何。

## 参数

```json
[
  {"Name":"markers","Label":"球体／圆柱标记或标记组件","Kind":"objects","Required":true,"Help":"组件下按每个 `TxSolid` 提取一个中心，请只绑定焊点标记。"},
  {"Name":"target_operation","Label":"目标焊接操作","Kind":"object","Required":true,"TypeHint":"TxWeldOperation"},
  {"Name":"name_prefix","Label":"焊点名称前缀","Kind":"text","Required":false,"Default":"WP_"},
  {"Name":"use_marker_names","Label":"沿用标记名称","Kind":"bool","Required":false,"Default":"true"},
  {"Name":"duplicate_tolerance","Label":"重复点距离容差（mm）","Kind":"number","Required":true,"Default":"0.1","Help":"目标操作内及本次生成的点都参与去重；`0` 表示仅排除完全相同的坐标。"},
  {"Name":"copy_orientation","Label":"保留标记姿态","Kind":"bool","Required":false,"Default":"false"}
]
```

## 代码

```csharp
double tolerance = Convert.ToDouble(duplicate_tolerance);
if (double.IsNaN(tolerance) || double.IsInfinity(tolerance) || tolerance < 0 || tolerance > 1000000)
    throw new Exception("重复点距离容差必须是 0 到 1000000 mm 的有限数字。");
var creator = target_operation as ITxWeldLocationOperationCreation;
if (creator == null) throw new Exception("目标对象不是可创建焊点的焊接操作。");
var solids = new List<TxSolid>();
var seen = new HashSet<string>(StringComparer.Ordinal);
Action<ITxObject> add = delegate(ITxObject item)
{
    var solid = item as TxSolid;
    if (solid != null && seen.Add(Convert.ToString(item.Id))) solids.Add(solid);
};
foreach (ITxObject root in markers)
{
    add(root);
    var group = root as ITxObjectCollection;
    if (group != null)
        foreach (ITxObject item in group.GetAllDescendants(new TxTypeFilter(typeof(TxSolid)))) add(item);
}
solids = solids.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => Convert.ToString(s.Id)).ToList();
if (solids.Count == 0) throw new Exception("绑定范围内没有实体标记。请加载详细几何并选择球体、圆柱或其组件。");
var points = new List<TxVector>();
var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (ITxObject item in target_operation.GetAllDescendants(new TxTypeFilter(typeof(ITxObject)))) names.Add(item.Name);
foreach (TxWeldLocationOperation location in target_operation.GetAllDescendants(new TxTypeFilter(typeof(TxWeldLocationOperation))))
    points.Add(location.AbsoluteLocation.Translation);
// 先读取所有几何中心，任何坐标错误都在创建点位之前报告。
var poses = new List<TxTransformation>();
foreach (TxSolid solid in solids)
{
    TxVector center = solid.GeometricCenter.Translation;
    if (new double[] { center.X, center.Y, center.Z }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
        throw new Exception("标记 " + solid.Name + " 的几何中心无效。");
    var pose = copy_orientation ? new TxTransformation(solid.AbsoluteLocation) : new TxTransformation();
    pose.Translation = new TxVector(center.X, center.Y, center.Z);
    if (!pose.IsValid || !pose.IsRigidBodyTransformation || pose.IsMirrored)
        throw new Exception("标记 " + solid.Name + " 的姿态不是有效的非镜像刚体变换。");
    poses.Add(pose);
}
int created = 0, duplicate = 0, failed = 0;
string prefix = Convert.ToString(name_prefix);
for (int i = 0; i < solids.Count; i++)
{
    var solid = solids[i];
    var pose = poses[i];
    TxVector p = pose.Translation;
    bool exists = points.Any(q =>
    {
        double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
        return dx * dx + dy * dy + dz * dz <= tolerance * tolerance;
    });
    if (exists) { duplicate++; log("[重复跳过] " + solid.Name); continue; }
    string baseName = prefix + (use_marker_names && !string.IsNullOrWhiteSpace(solid.Name)
        ? solid.Name : (i + 1).ToString("D3"));
    if (string.IsNullOrWhiteSpace(baseName)) baseName = "WP_" + (i + 1).ToString("D3");
    string name = baseName;
    int suffix = 2;
    while (names.Contains(name)) name = baseName + "_" + suffix++;
    TxWeldLocationOperation location = null;
    TxWeldPoint feature = null;
    try
    {
        var data = new TxWeldLocationOperationCreationData();
        data.Name = name;
        data.ProjectedLocation = pose;
        data.WeldPointCreationData = Tecnomatix.Engineering.DataTypes.TxMfgCreationDataFactory.CreateWeldPointCreationData(name, pose);
        try
        {
            if (!creator.CanCreateWeldLocationOperation(data)) throw new Exception("目标操作拒绝创建该焊点。");
            location = creator.CreateWeldLocationOperation(data);
        }
        finally { data.Dispose(); }
        if (location == null || location.WeldPoint == null) throw new Exception("创建接口没有返回完整焊点。");
        feature = location.WeldPoint;
        feature.AbsoluteLocation = pose;
        location.AbsoluteLocation = pose;
        names.Add(name); points.Add(p); created++;
        log("[已创建] " + name + "，中心 mm：" + p.X.ToString("F3") + ", " + p.Y.ToString("F3") + ", " + p.Z.ToString("F3"));
    }
    catch (Exception ex)
    {
        failed++;
        log("[失败] " + solid.Name + "：" + ex.Message);
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
return "## 焊点提取结果\n\n- 新建：**" + created + "** 个\n- 重复跳过：" + duplicate
    + " 个\n- 失败：" + failed + " 个\n\n目标操作：" + target_operation.Name
    + "\n\n请检查点位、姿态及工艺设置；原始几何已保留。";
```
