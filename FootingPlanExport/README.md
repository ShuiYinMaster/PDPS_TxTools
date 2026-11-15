# 导出地脚图

TxTools 主程序集中的 PS 2402 按钮：`TxTools.FootingPlanExport.FootingPlanExportCommand`。

界面仅包含 Z 距离输入、导出按钮和进度条。距离单位 mm，以全局 Z=0 为基准，正值向上、负值向下，默认值 +3；截平面平行于全局 XY。无需选择设备，也不读取当前选择集。自动收集场景中的可见顶层组件，嵌套组件通过祖先只处理一次。

创建截平面之前先读取场景可见包围盒，Z 距离落在包围盒之外时直接报错并给出实际范围，不再进入原生命令。

点击导出后创建并激活临时截平面，关闭原来激活的截平面，把临时截平面放入当前选择集（结束后恢复原选择），调用图形视图的 ShowContours / UpdateContours 和 `TxApplication.RefreshDisplay()`，然后执行原生菜单 “Save Section Contour as Component” 对应的准确命令 `DnSectionCommands.CUiSecSaveContourAsComponentCmd`。

原生命令的 enabler 依赖图形视图已经生成并发布的截面轮廓，而同步 SDK 调用链不会让 PS 走完一次自身的刷新周期，因此不能只查询一次 enabler。实现上先按 Clip 开、Clip 关两种显示状态分别刷新轮廓，再泵消息并轮询 enabler（首次 3 s、第二次 1.5 s），就绪后才执行命令。轮询期间响应取消。

enabler 始终未启用时，错误信息给出该高度的场景可见 Z 范围、穿过该高度的组件数量以及对应处置建议，便于区分“高度处没有零件”和“轮廓未生成”两种情况。

该命令是交互式的半模态命令：它不在场景里留下组件，而是弹出 “Save Component As” 另存为对话框，由用户挑选 `.cojt` 路径。该对话框会校验文件名，带路径分隔符的名字会被 PS 拒绝（`The following characters are illegal for use in filenames`），因此只能填写纯文件名。导出必须全自动，所以 `SaveComponentDialog` 在命令运行期间由一个后台线程监视本进程内的另存为窗口：按“文件名编辑框默认以 `.cojt` 结尾”定位文件名，写入本次唯一的 `Contour_<GUID>.cojt`，先用 `CDM_GETFOLDERPATH` 读回对话框当前文件夹（失败时退回“文本是绝对路径的组合框”），再点击确认，并等对话框真正关闭后才认定保存成功。命令同步阻塞和异步弹出两种情况都能处理，两者都按“cojt 落盘”而不是按组件差集判定成功。

命令返回后最多等待 12 s 让 cojt 落盘。PS 的另存为不保证采用插件写入的文件名与目录：实测产物会以它自己的 GUID 命名、落在“上次保存目录”（可能正是缓存根 `TxToolsCache/FootingPlanExport`），因此成功判定不依赖文件名，而是在命令前后对缓存根和本次运行目录做 cojt 快照，只认本次新出现的那个；找不到时再依次退回对话框确认路径、本次运行目录、按唯一文件名在常见根目录的受限搜索。确定 cojt 后递归取最浅层的 JT（cojt 内部层级不固定），复制到 `FootingPlan_<GUID>.cojt/FootingPlan.jt` 再交给转换器。产物落在缓存目录以外时，JT 抽取完成后立即删除这次生成的临时 cojt 目录；落在缓存目录内则随缓存一起清理。cojt 未出现时，若场景中出现了一个新的顶层组件，则退回 `TxLocalStorage.SaveComponent()` 取 JT（兼容在场景里生成组件的版本）；两种情况都不成立时报错，并打印目标文件名、对话框目录、是否已点保存、确认路径等诊断。这个临时 COJT 目录只用于承载可视化 JT 的中转，不生成正式 PS 资源库元数据；不调用 SaveToLibrary 或 EndModeling，也不改变当前工程库路径。最后恢复原截平面、轮廓显示状态和选择集，并只清理本次创建的对象。

场景对象与导出文件的名称一律使用纯 ASCII（`FootingPlane_<GUID>`、`FootingPlan_<GUID>`、`Contour_<GUID>.cojt`、`FootingPlan.dxf`、`FootingPlan_Z+3.dxf`），避免原生截面命令与 JT 存储在中文名称下走 Unicode→ANSI 转换后退化为问号。

转换时显示进度；完成后自动弹出标准 DXF 保存对话框。成功保存、校验并确认临时场景引用已移除后，清理本次 COJT 缓存，并显示最终 DXF 路径。取消保存时保留转换结果，再点同一导出按钮可重新选择保存位置。错误详情写入缓存中的 error.log；保存阶段错误优先显示，清理错误追加显示。

SDK 调用只在 PS UI 线程执行，使用主项目 SceneUndoScope 管理临时场景变更；独立转换器异步执行。关闭正在工作的窗体会请求取消，当前 SDK 调用结束后响应。

主项目通过 tools/Build.ps1 编译。转换器源码在 Converter，运行依赖在同级 TxTools.Local/dependencies/footing-plan-export/converter。重建转换器使用本模块 BuildConverter.ps1。主项目构建目标将完整运行目录复制到输出的 FootingPlanExport/converter，部署时须与 TxTools.dll 一同更新。需要 PS 已连接且启用原生 Save Section Contour as Component 命令；不可用时明确报错。

JT 转换支持小端 JT 10.6、非量化 XYZ Polyline、毫米单位及单 LOD，其他表示明确报错。输出 DXF 为毫米制 1:1，回读核对顶点、数量、单位与开闭状态。

闭合多段线如果整体构成一个完整圆，转换器输出 CIRCLE 实体。判定同时满足四个条件：顶点数不少于 `--circle-min-points`（默认 16）；所有顶点到拟合圆的径向偏差不超过 `--circle-tolerance`（默认 0.1 mm）；顶点确实绕圆心一周（无 ≥180° 的角度缺口）；弦长不超过 `2r·sin(π/点数)·1.25` —— 最后一条用来否决六边形、八边形、方形这类「顶点同样落在圆上」的真实多边形。拟合用 Kåsa 代数最小二乘加径向距离 Gauss-Newton 精修。`--no-circles` 关闭该行为。回读阶段对每个 CIRCLE 重新计算原始顶点到圆心距，偏差超过该圆自己的容差即判失败。

拟合出的直径落在整数附近时取整，判定窗口为 `max(--snap-tolerance, --snap-relative × 直径)`，默认 0.2 mm 与 0.02%。超出窗口时保持原值。取整后按新半径重新计算顶点偏差，并用于回读校验；`--no-snap` 可关闭取整。

圆的径向判定在绝对容差之上再给一个相对窗口：`min(max(--circle-tolerance, 0.01 × 直径), 5 × --circle-tolerance)`。细分后的孔不是理想圆，14 mm 的孔常有 0.12 mm 形状误差，固定 0.1 mm 会漏判；上限保证椭圆仍然通不过。`--circle-relative` 调整该比例。

非圆的闭合多段线保持为**一条**闭合 LWPOLYLINE，并优先把直线段与圆弧段表达成 bulge：圆角变成「顶点 + 凸度」，一块 40×40、R5 圆角的地脚板从 27 个顶点降到 8 个，仍然是一个可编辑的实体。拆分不出直线/圆弧时退回纯顶点简化（Douglas-Peucker 去掉直线边上的冗余过渡点，密排矩形 80 点收敛到 4 个角点）。两条路径都用原始顶点回验：每个原始点到折线（含 bulge 圆弧）的最短距离必须不超过 `--shape-tolerance`。开口多段线才按 `LINE / ARC` 拆分：预简化用一半容差，种子生长到覆盖 `--arc-min-angle` 后再扩张，DXF 的 ARC 恒为逆时针（顺时针链自动交换起止角）。拆分只在「实体数 ≤ 原始段数的一半」时采用，否则保留原多段线。

正式测试位于同级 TxTools.Local/tests/footing-plan-export；本地输出和回归结果位于 TxTools.Local/build。
