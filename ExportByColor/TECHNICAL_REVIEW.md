# ExportByColor 技术审查报告

审查日期：2026-09-12  
审查范围：`ExportByColor/` 当前实现及其在 `TxTools.csproj` 中的编译配置。  
审查目标：确认 PS 几何读取、按颜色分组、坐标变换、CGR/CAD 导入和通用网格导出的正确性、稳定性与可维护性。

## 1. 结论摘要

ExportByColor 已形成比较完整的导出链路：PS 主线程读取对象和几何，后台线程编码文件，CGR 结果可自动导入 CATIA，通用网格支持 STL、OBJ、PLY 和 FBX。大网格还有分批写入、临时文件、取消请求和失败恢复缓存等设计，整体方向是正确的。

当前版本仍不建议直接作为“颜色保真”和“长时间批量导出”的生产基线，主要原因是：

1. `GetColors()` 返回多个颜色时，代码只选择一个颜色并应用到该几何的全部三角面，存在颜色丢失。
2. 几何读取期间加载到 `Detailed` 的 `TxLibraryStorage` 没有被可靠恢复到 `United`，且当前 `st` 参数实际上始终为 `null`。
3. `RunAsync` 在线程主体进入 `try` 前就执行 PS 调用，异常时可能留下永久的 `_running=1` 状态且不触发完成回调。

上述三项应优先修复。CGR 私有编码、输出发布事务和自动化回归测试属于第二优先级。

## 2. 当前架构与数据流

```text
PS 选择 / 全部可见资源
        │
        ▼
ExpandPicked：容器展开、设备去重、可见性过滤
        │
        ▼  PsContext / SynchronizationContext.Send
CollectDeviceGroups（PS 主线程）
  ├─ StorageObject.Reload(Detailed)
  ├─ EnumDeviceGeometries
  ├─ GetColors + Approximation.Primitives
  ├─ AbsoluteLocation + 原点逆变换
  └─ 生成 DeviceData / ColorGroup / float[9] 三角面
        │
        ├─ CGR：CgrWriter → .cgr → CATIA Product.AddComponentsFromFiles
        └─ STL / OBJ / PLY / FBX：MeshExport / FbxExport → 输出目录
```

入口是 `ExportByColorCmd`，界面由 `ExportByColorForm` 提供资源选择、原点、格式、合并和边线选项；核心业务集中在 `ExportByColorService`。`CgrWriter` 使用内置模板和自定义二进制编码，默认走 compact95 路径；打开边线或设置 legacy 环境变量时走兼容编码路径。

## 3. 功能覆盖

| 能力 | 当前实现 | 备注 |
|---|---|---|
| 资源枚举 | `PhysicalRoot`、`ResourceRoot`、`ComponentRoot` 展开 | 保留可见性过滤，运行时按对象 ID 去重 |
| 几何读取 | `TxLibraryStorage.Reload(Detailed)` + `Approximation` | 支持 `TxGeometry`、`TxSolid` 等几何接口 |
| 颜色 | 每个几何选一个 `TxColor` | 不能表达同一几何内的多色三角面 |
| 坐标 | 几何绝对变换，可选原点逆变换 | 原点按名称查找，当前取第一个匹配对象 |
| CGR | 单设备或合并 CGR，支持 CATIA 自动添加 | 私有/实验性编码，需目标 CATIA 版本验证 |
| STL | 二进制 STL + `.rgb.json` | STL 本身不含颜色，颜色依赖旁车清单 |
| OBJ | OBJ + MTL | 每个颜色分组生成材质 |
| PLY | ASCII PLY，面颜色 | 文件较大，写入较慢 |
| FBX | 7500 二进制 / 7400 ASCII | 单 Part 超过 1,000,000 三角面时分块 |
| 取消与恢复 | 取消标记、partial 文件、mesh spool | 取消不会中断正在执行的单次 PS/CATIA 原生调用 |

## 4. 重点问题

### P1-1：多颜色几何被压成单色

位置：`ExportByColorService.cs:674-700`、`ExportByColorService.cs:719-739`

实现通过 `displayable.GetColors()` 得到颜色集合，但只跳过黑色后选取第一个颜色；随后所有 `Approximation.Primitives` 都使用同一组 `r/gg2/bb2`。`HashSet<TxColor>` 的迭代顺序也不是稳定的。

影响：如果 PS 几何内部包含多个面颜色，CGR、OBJ、PLY、FBX 都会把这些面输出成同一种颜色。即使 PS 当前大多数设备“一几何一颜色”，该实现也无法保证颜色保真。

建议：

- 优先确认 PS SDK 是否能从 primitive/face 级别取得颜色，并把颜色作为三角面属性传入编码器。
- 若 SDK 只能提供几何级颜色，则在发现颜色集合数量大于 1 时明确记录并阻止静默降级，或将几何拆分为可验证的颜色块。
- 单颜色场景也应先排序/规范化候选颜色，避免 `HashSet` 顺序造成结果漂移。

### P1-2：Detailed 表示状态没有可靠恢复

位置：`ExportByColorService.cs:645-646`、`ExportByColorService.cs:789-819`、`ExportByColorService.cs:782`

`EnumDeviceGeometries` 创建局部 `HashSet<TxLibraryStorage>` 并在遍历节点时调用 `Reload(Detailed)`，但没有把加载过的 storage 写回传入的 `ref TxLibraryStorage st`。调用方最后只执行 `if (st != null) st.Reload(United)`，因此该恢复分支实际上不会执行。几何读取异常时也没有 `finally` 恢复逻辑。

影响：导出结束后，设备可能继续占用 Detailed 表示，导致 PS 内存增长、后续场景操作变慢，异常导出还可能留下不一致的表示状态。

建议：让枚举函数返回本次实际加载的全部 storage，并在 `CollectDeviceGroups` 的 `finally` 中逐个恢复 `United`；恢复失败必须写入日志。应覆盖成功、设备失败、取消和线程异常四条路径。

### P1-3：线程主体的前置异常可能卡死运行状态

位置：`ExportByColorService.cs:122-140`

`RunAsync` 设置 `_running=1` 后创建线程，但线程内的 `selectionName = OnPs(...)` 位于主 `try` 之前。如果 `SynchronizationContext.Send`、对象引用或 `Name` 读取抛出异常，后续 `catch/finally` 不会执行。

影响：`_running` 可能永久保持 1，界面没有 `onComplete` 回调，之后每次启动都会收到“上一次导出尚未结束”。

建议：把线程主体的所有初始化都放入同一个最外层 `try/finally`；输入参数和选择对象应在设置 `_running` 前完成基本校验。完成回调应保证最多调用一次。

### P2-1：CGR 依赖私有模板和未经自动验证的编码路径

位置：`CgrWriter.cs:8`、`CgrWriter.cs:73-81`、`CgrTemplate.cs`

代码明确将 CGR 编码标记为 experimental，并依赖硬编码的 R7-R12 模板、目录偏移和 compact95 结构。默认路径还会做法线平滑、背面复制和拓扑重排相关处理。

影响：换 CATIA 版本、不同 CGR 表示类型、非流形网格、超大坐标或异常拓扑时，可能出现导入失败、显示缺面、法线错误或颜色异常。当前仓库没有发现自动调用 CATIA 校验生成文件的测试。

建议：

- 建立按 CATIA 版本和输入拓扑分类的 golden fixtures，至少覆盖闭合实体、开放薄片、非流形边、退化面、多个颜色组和超过单块上限的网格。
- 生成后对 CGR 文件执行最小结构校验，并保留 CATIA 实机导入结果矩阵。
- 将模板版本、环境变量和兼容性边界写入发布说明；默认关闭未经验证的实验开关。

### P2-2：OBJ/STL 发布不是完整的原子事务

位置：`MeshExport.cs:75-77`、`MeshExport.cs:81-99`

OBJ 的 `.mtl` 直接写入最终目录，STL 的 `.rgb.json` 也在主 STL `.partial` 移动前直接写入最终路径。若 OBJ、MTL 或清单写入中途失败，清理逻辑只删除主 `.partial` 文件，不会回滚旁车文件。

影响：输出目录可能留下不完整的 `.mtl` 或 `.rgb.json`；下一次重试时 `FileMode.CreateNew` 又可能因为残留文件直接失败。

建议：所有伴随文件都写到唯一临时文件，完成后按固定顺序发布；失败时删除整组临时文件。最终成功标志应在主文件和旁车文件都完成后再产生。

### P2-3：原点按名称取第一个匹配对象

位置：`ExportByColorService.cs:626-632`

原点通过 `GetObjectsByName(originName)` 查找，并直接使用第一个结果。PS 场景允许不同容器中存在同名对象，因此该选择不一定是用户在界面中拾取的对象。

影响：导出的模型可能整体落在错误的坐标系中，且日志只显示名称，难以复现。

建议：界面和服务层传递对象 ID/对象引用，而不是只传名称；至少在多个匹配时记录候选 ID 并终止或要求用户明确选择。

### P2-4：输入 primitive 必须恰好是三角形

位置：`ExportByColorService.cs:738-739`

代码允许少于 3 个索引时跳过，但对不等于 3 的 primitive 直接抛错并使整个设备失败。该行为假设 `Approximation` 永远已经三角化，接口契约和错误恢复没有在代码中显式保证。

影响：某些 PS 表示或版本返回 quad、strip 或其他索引布局时，设备会整体导出失败，而不是被安全三角化。

建议：在采集层明确支持的 primitive 类型；无法可靠三角化时至少报告 primitive 类型、数量和设备名，并在设备级别保留失败原因。不要静默截断索引。

### P2-5：PS 文档/对象生命周期没有绑定校验

位置：`ExportByColorService.cs:122-266`

导出在后台线程异步执行，持有从 UI 选择得到的 PS COM/SDK 对象，但开始导出后没有记录并校验活动文档或 Study 是否仍未切换。用户可以在导出期间关闭文档、切换 Study 或删除对象。

影响：对象引用失效时会出现部分导出；当前代码能记录设备失败，但不能明确区分“源数据变化”和“读取逻辑错误”。

建议：开始时保存文档/Study 标识，在每个设备批次前通过 PS 主线程校验；检测到上下文变化时停止发布合并文件，并在日志中明确标记为源场景变化。

## 5. 代码质量观察

- `OnPs` 的封送方式适合当前“后台编码、PS 主线程读取”的模型，但构造函数允许传入普通 `SynchronizationContext`。普通上下文的 `Send` 会在调用线程执行，服务层应拒绝未绑定 PS UI 的上下文，避免误把 PS SDK 调用放到后台线程。
- `ReloadRecursive` 与 `CgrWriter.ReorderForStrips` 当前未被生产路径调用，建议删除、转为测试辅助，或补充调用约束，避免维护者误以为它们已经参与导出。
- `CgrTestData` 是有价值的回放包生成器，但当前仓库检索不到自动消费它的测试入口；建议接入独立测试项目或 CI 资产校验。
- 通用网格导出对退化三角形的处理与 CGR 不一致：CGR 会过滤零面积面，STL/OBJ/PLY/FBX 主要依赖 `Validate`，可能保留零面积三角形。建议统一策略并在报告中统计过滤数量。
- 颜色、坐标、单位和格式信息目前主要依赖日志或旁车文件；建议为每次导出生成统一 manifest，记录源对象 ID、Study、原点 ID、格式、三角面数、颜色组数、编码器版本和 SHA-256。

## 6. 建议修复顺序

### 第一阶段：发布阻断项

1. 修复 Detailed storage 的收集与 `finally` 恢复。
2. 把 `RunAsync` 的全部初始化放入最外层异常边界，保证 `_running` 和完成回调一致。
3. 明确多颜色几何的支持策略，禁止静默把多色面压成单色。

### 第二阶段：可靠发布

1. OBJ/MTL、STL/RGB 清单统一使用临时文件和原子发布。
2. 原点改用对象 ID/引用，增加文档/Study 生命周期校验。
3. 为 primitive 类型、零面积面、非流形边和超大网格增加可诊断统计。

### 第三阶段：兼容性与维护

1. 建立 CGR golden fixtures 和 CATIA 版本导入矩阵。
2. 将 `CgrTestData` 接入自动化回归，覆盖颜色、坐标和三角面数量。
3. 清理未使用的生产代码，补充统一 manifest 与发布校验。

## 7. 回归测试清单

- 一个设备、多几何、多个颜色；确认每个颜色组的面数和最终材质一致。
- 共享边、闭合实体、开放薄片、非流形边、零面积面；分别验证 CGR 显示和通用网格结果。
- 0、255、256、40960 顶点边界，以及单 Part 超过 1,000,000 三角面的 FBX 分块。
- 原点不存在、原点重名、原点非可定位对象、坐标含大数值和负数。
- 成功、读取异常、编码异常、CATIA 添加异常、用户取消、关闭窗体；确认 storage 恢复、临时文件清理和完成回调只发生一次。
- CATIA 目标版本至少覆盖项目声明的 R7-R12 兼容范围，并分别测试边线开关和 compact95/legacy 路径。

## 8. 审查执行说明

本报告基于当前源码静态审查、调用关系检索和项目配置核对生成。已检查 `ExportByColorService`、`ExportByColorForm`、`ExportByColorCmd`、`CgrWriter`、`CgrCompact95`、`CgrSurfaceOrientation`、`CgrTemplate`、`MeshExport`、`FbxExport` 与 `CgrTestData`。

本机未找到 `msbuild`/Visual Studio 构建工具，因此本次未能完成依赖真实 PS SDK 和 COM 环境的 Release 编译及 CATIA 实机导入验证；文中“未验证”项应在具备该环境后补测。
