# 外观三角面获取与导出

## 2402 SDK 反编译结论

接口目标为 Process Simulate 2402 SDK，需随宿主版本核对类型和成员。

| SDK 类型/成员 | 核对结果 |
| --- | --- |
| TxPartAppearance、TxPartAppearanceDevice | 实现 ITxPartAppearance、ITxObjectCollection、ITxLocatableObject；没有直接的 Approximation，也未实现 ITxStorable。 |
| ITxPartAppearance.OriginPart | 返回源零件的 ITxPlanningObject。 |
| TxSkidAppearance、TxSkidAppearanceDevice | 实现 ITxSkidAppearance；其 OriginSkid 返回关联滑橇的 ITxPlanningObject。 |
| ITxPlanningObject.EngineeringRepresentation | 返回关联零件或滑橇的实体表示 ITxObject。这是规划对象到可采集几何对象的连接。 |
| ITxDisplayableObject.Visibility | 集合状态可能是 All、Partial、None、CannotBeDisplayed；Display()/Blank() 会影响后代。 |
| TxLibraryStorage.Reload(TxRepresentationLevel.Detailed) | 与普通零件/资源读取一致的实体级加载入口。必须先加载，再枚举新实体及读取 Approximation；Display() 不能替代这一步。 |
| ITxGeometry.Approximation | 获取子几何近似网格；TxGeometry 的实现调用 TxRose.GetApproximationData。 |
| TxApproximationData | 暴露 Points、Normals、Primitives；TxPrimitiveData.Indices 指向该近似数据的顶点数组。 |
| TxPrimitiveData.PrimitiveType | 内部只读枚举属性；2402 值为 Unknown、Face、Line、Strip。只读反射用于识别三角带和线图元。 |

外观不直接提供三角面。采集链路为：**外观 → OriginPart/OriginSkid → EngineeringRepresentation → 按需显示 → Load Entity Level（Detailed）→ 子几何 Approximation**。关联零件通常可能隐藏，采集前须按需显示，外观与关联零件也不能共用同一个世界矩阵。

## 显示与采集作用域

- 零件外观、滑橇外观及设备形式作为独立导出单元。
- 在 PS 同步上下文中解析关联实体，检查显示状态；隐藏时临时 Display()，再通过 GeometryEntityLoader 加载 Detailed。外观关联零件、嵌套可存储子集与普通资源共用此加载函数。每次重载之后再次检查显示状态，再枚举新子对象并读取 Approximation、Points、Primitives。
- 原状态为 None 时，采集结束恢复 Blank()；原状态为 All 时不改显示状态。原状态为 Partial 时，在 Display() 前保存各后代状态，结束后恢复可见与隐藏子项，避免把原本隐藏的子零件永久显示出来。
- ReadScope 一直存活到三角面和坐标变换完成；同一作用域内每个 Storage 只加载一次。采集前为 United 的关联组件在结束时恢复 United，原本为 Detailed 的保持 Detailed；嵌套组件先于父组件恢复。完成加载级别恢复后，通过规划关联重新获取源根对象，并按身份重新定位原可见/隐藏子项，避免使用 Reload 前已失效的实体包装对象。
- 成功、空网格、枚举异常、近似数据读取异常、Display() 部分失败以及实体级加载部分失败时，都尝试恢复加载级别和显示状态。恢复一项失败仍继续恢复其余项，最后报告恢复失败。
- 只临时修改显示状态，不移动源零件、修改几何、分解对象或改色。关联对象未加载或没有有效实体表示时明确失败，不按名称猜测其他零件。
- 源几何按对象身份去重并防止循环。多个外观共享同一源零件时，导出身份包含外观身份，不会把不同放置的实例合并。

## 坐标变换

设 A 为外观世界矩阵，S 为关联零件根对象世界矩阵，G 为子几何世界矩阵，p 为 Approximation 中的子几何局部顶点：

~~~text
p_world = A × inverse(S) × G × p
p_export = extraInverse × originInverse × A × inverse(S) × G × p
~~~

先用 inverse(S) 消除源零件根对象的世界放置，保留子几何在零件内的相对位置，再应用外观自身的完整世界变换。外观、零件和子几何均可包含旋转及平移；不能仅减原点平移，也不能把外观矩阵直接乘到源几何的世界坐标上。

普通资源继续使用 G × p。颜色读取关联子几何的设计色；外观整体姿态与源零件子几何相对布局分别保留。三角带按奇偶顺序展开，跳过退化连接和线图元；坏索引与其他暂不支持的多索引面明确失败。

## 导出路径

| 入口 | 外观策略 |
| --- | --- |
| CGR → CATIA | 关联零件三角面 → 外观位置变换 → 既有 CGR 编码器 → CATIA。 |
| 3DXML | 同一采集数据 → CGR/CFV3 → 装配封装，保留外观所在目录层级。 |
| STL / OBJ / PLY / FBX | 复用网格写出、颜色及合并缓存流程；STL 颜色仍通过 .rgb.json 保存。 |
| JT → CGR → CATIA | 外观在解析 JT 路径前使用关联零件采集；只把脱离 SDK 的三角面提交有界编码队列。 |
| JT 内外圆柱重建模式 | 普通 JT 资源继续重建；外观保留已变换的 PS 三角网格并在日志说明，不套用 JT 圆柱识别。 |

## 验证

运行 外观回归脚本，显式传入真实 SDK 和已有 CATIA Interop 目录：

~~~powershell
rtk proxy powershell -NoProfile -ExecutionPolicy Bypass -File Tests/AppearanceExport/Run-Regression.ps1 -SdkDirectory "<PS SDK目录>" -InteropDirectory "<CATIA Interop目录>"
~~~

脚本使用 Roslyn 编译完整 ExportByColor 非 UI 生产代码、几何编码器与真实 2402 SDK，启用警告即错误；不部署到正式插件目录。

当前结果：48 项真实 SDK 接口代理及导出检查通过，覆盖关联路径、隐藏零件需先显示、部分显示的还原、多个外观共享源、生产递归入口、源子树枚举回退与循环、显示/采集/恢复异常，以及 CGR、完整 3DXML、STL、OBJ、PLY、ASCII/Binary FBX 写出。

另有 8 项位置回归通过，覆盖带旋转和平移的源零件与外观、子几何偏移、共享源的不同外观、旋转导出原点、额外工具逆矩阵、外观自身坐标导出、普通资源位置及缺失矩阵。位置测试将同一生产采集类编译到独立测试程序，使用托管矩阵替身执行；离线环境缺少 SDK 原生 Implementation.dll，不能运行真实 SDK 矩阵运算。

新增 16 项实体级加载回归通过：源根及嵌套组件均在枚举前加载 Detailed；重载重置隐藏状态时重新显示；三角面读取前不卸载；共享源去重；嵌套加载级别逆序恢复；重新绑定实体包装对象并恢复部分显示；原本 Detailed 的组件保持 Detailed；加载部分失败后仍恢复。此组使用 SDK Storage 替身验证调用顺序和状态，真实 SDK 签名由生产编译核对。

尚未在真实 PS 场景和 CATIA 中验收。现场应使用隐藏关联零件且外观与源零件均有旋转/平移的模型，对照导出位置、面数和颜色；再用部分显示的源零件及失败用例确认显示状态恢复。
