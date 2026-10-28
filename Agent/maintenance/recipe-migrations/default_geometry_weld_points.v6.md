---
key: default_geometry_weld_points
name: 从标记几何中心生成焊点
lang: csharp
recipe_format: 1
migration_description_sha256: 22ff2055335473b0956f90a81c04b778d04228ea93563ed4a53dd7c28ac6cdc6
migration_code_sha256: d00dc3b4aaa528c266bf46515c23f27d03b807c5b62a188ab54673be2c608387
run_count: 0
fail_count: 0
---

旧版配方兼容定义。

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
    "Name": "scope_mode",
    "Label": "处理方式",
    "Kind": "text",
    "Required": true,
    "Default": "auto",
    "Choices": [
      {
        "label": "自动：几何/Block 取整体，组件展开",
        "value": "auto"
      },
      {
        "label": "整体：每个所选对象生成一个点",
        "value": "selected"
      },
      {
        "label": "展开：查找内部标记",
        "value": "descendants"
      }
    ],
    "Help": "不确定时选“自动”；需要查找对象内部的标记时选“展开”。"
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
    "Default": "0.1",
    "Help": "距离不超过此值的焊点视为重复；0 表示只跳过坐标完全相同的点。"
  },
  {
    "Name": "run_mode",
    "Label": "执行方式",
    "Kind": "text",
    "Required": true,
    "Default": "preview",
    "Choices": [
      {
        "label": "预览候选与跳过原因",
        "value": "preview"
      },
      {
        "label": "创建焊点",
        "value": "create"
      }
    ]
  }
]
```

## 代码

```csharp
// Exact legacy code is identified by migration_code_sha256.
```
