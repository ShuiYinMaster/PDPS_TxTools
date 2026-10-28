---
key: default_geometry_weld_points
name: 从标记几何中心生成焊点
lang: csharp
recipe_format: 1
migration_description_sha256: abe8818940093241e98a927a124c001da40aef23846ec13857caef2034c5d837
migration_code_sha256: cf122ba6cefe1aa7f9887c2d6edcca0d9f0994bf7d943e976d9c443434a439d4
run_count: 0
fail_count: 0
---

早期默认配方兼容定义。

## 参数

```json
[
  {"Name":"markers","Label":"焊点标记几何或零件／组件","Kind":"objects","Required":true,"Help":"只选择独立焊点标记或包含标记的组件。"},
  {"Name":"name_keyword","Label":"几何体名称关键字","Kind":"text","Required":false,"Default":"","Help":"例如 RSW、焊点、CO2；名称包含此关键字即匹配，不区分大小写。留空匹配全部，不使用正则或通配符。"},
  {"Name":"duplicate_tolerance","Label":"重复点距离容差（mm）","Kind":"number","Required":true,"Default":"0.1","Help":"在操作根节点下及本次新点之间按距离去重；0 表示仅排除完全相同的坐标。"}
]
```

## 代码

```csharp
// Exact legacy code is identified by migration_code_sha256.
```
