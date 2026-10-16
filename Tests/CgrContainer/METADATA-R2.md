# R2：身份关联、原生差分与 CATIA COM 验收

R4 更正：以下早期记录中的 `D` 是根描述符名称长度低半字的误读，真实名称以 `.` 开始；4 个整数片段会省略前导零，不保证总长 32 个十六进制字符。PID、时间和计数器的数值证据仍有效。详见 [名称边界与变长编码](IDENTITY-R4.md)。

日期：2026-10-15。容器版本：`CFV2-20261015-R2-linked-identity`。当前源码仍使用已接受的默认身份配置；新身份参数只在内部研究接口开放，没有部署新 DLL。

## 新确认的身份字段

本轮样本的真实顶层存储名为 `.` 后连接 4 个整数片段；以下例子恰好每段 8 字符，可解成 **4 个小端 uint32**。例如本轮原生导出（已按 R4 修正名称起点）：

`.4eb51e588c240000e581d06a398f0000`

| 字段 | 数值/解释 | 证据与边界 |
| --- | --- | --- |
| word0 | `0x581EB54E` | 在已检查原生样本中相同；真实分配/命名空间规则未知 |
| word1 | `9356`，生产 CATIA 的进程号 | 本轮独立读取 CNEXT.exe PID 为 9356，4 次新原生 ExportData 全部匹配；C# 已命名为 `ProcessId` |
| word2 | Unix 秒数 | 与目录顶层 FILETIME 的整秒、Header +5 的小端 uint32 三方一致 |
| word3 | 递增分量 | 重复导出增加 1；改 X、恢复分别增加 6。只说明本轮观测到的分配行为，尚未还原完整分配器 |

146 份可处理的历史文件满足三方时间关联。该集合混有原生文件、历史生成文件和实验文件，不能称为 146 份独立原生证据；另外 206 份不符合严格容器解析范围、5 份身份形式不受支持，均保留具体原因，没有混入通过计数。

不能把所有节点的首个 FILETIME 都强制设成同一值：新原生导出中，顶层和子节点可以相差 1 ms，两个后续时间槽也可比首槽早 1 ms。重复导出而不改变 Part 时，身份和目录时间会变化，但在本轮同一整秒的两次导出中，所有逻辑流完全相同。因此这些时间不应直接解释为零件建模时间；具体生命周期仍待进一步控制实验。

## 字段生成继续替代固定载荷

`CgrStorageIdentity` 通过 4 个整数序列化存储名；Header 时间从同一个身份对象取得。研究接口改变 Unix 秒数时，同时平移非零 FILETIME，保留原有相对时间和零值槽，避免只改字符串、留下不一致的 Header。

Header 的 63 字节布局已经有独立解析/序列化模型：封套、时间、标志槽、Float32 槽、6 个包围盒 Float32、4 字节外观槽和尾部两个 Float32。除时间、包围盒外，部分字段的物理含义仍不明，代码保留接受过的默认值，不能据此宣称 Header 语义已完全解码。

9 字节控制记录和 19 字节 Describe 记录的共有小端标记、整数字段也改为结构化写出；Describe 最后的 7 字节仍保留为不透明数据。几何场景前后缀仍有未解码的配置。

## 通过 COM 创建的独立原生样本

脚本 `catia_identity_fixtures.py` 创建一个新的研究 CATPart，依次导出：

1. 点 `(11,22,33)`。
2. 不修改几何，重复导出。
3. X 改为 21。
4. X 恢复为 11。

每次核对实际运行的 CNEXT PID、身份时间关联、Header 包围盒和完整容器重建。重复导出的逻辑流相同；修改坐标只改变 Header 和几何流，恢复后的逻辑流恢复原值。代码测试允许重复实验跨越整秒时 Header 时间正常变化。

样本和原生 CATPart 位于工作区 `analysis_cgr/container_20261015_r2/native_identity/`，原始记录为 `native_identity_fixtures.json`。脚本仅创建、保存、关闭自己的研究 Part，并恢复原活动文档。

## CATIA COM 验收结果

五种盒子变体通过 `Selection.VisProperties.SetShow` 分别隔离，并由 `ActiveViewer.CaptureToFile` 捕获显示；不依赖多模型重叠时看到的外观：

- 默认身份。
- word3 增加 4096。
- Unix 秒数增加 3600，并关联修改目录时间和 Header。
- word1 改为另一个值。
- 时间关联修改，同时把 26/25 分段改为 1/1。

五组模型的 `Product.Analyze.Volume` 均为 `256000.00000000003 mm³`；单分段变体另在只包含一个模型的 Product 中验证。身份修改样本均保留原始几何和拾取流字节。

| FFM170 | 分段数：几何/拾取 | 目录字节 | CATIA COM 体积 |
| --- | --- | --- | --- |
| 原生文件 | 26/25 | 3684 | `28359252.01632797 mm³` |
| 字段重建、合并分段 | 1/1 | 2704 | `28359252.01632797 mm³` |

两者分别放在只有一个组件的新 Product 中，均完成加载、显示捕获、测量和 SaveAs。对比图显示完整模型，逻辑流此前已逐字节验证相同。目录减少 980 字节是分段表缩短，不是几何压缩收益。结论限于此样本/当前 CATIA 环境，生产默认分段数尚未改变。

保存的验收文件：

- `analysis_cgr/container_20261015_r2/com_acceptance/identity_variants.CATProduct`
- `analysis_cgr/container_20261015_r2/com_acceptance/identity_single.CATProduct`
- `analysis_cgr/container_20261015_r2/com_ffm170/ffm170_native.CATProduct`
- `analysis_cgr/container_20261015_r2/com_ffm170/ffm170_single_extent.CATProduct`

报告分别位于上述两个目录的 `catia_com_acceptance.json`；盒子接触表为 `com_acceptance/contact_sheet.png`，FFM170 对比图为 `com_ffm170/comparison.png`。两个旧测试 Product 已保存；本轮创建的 FFM170 测试 Product 保存后关闭。原有非研究文档的保存状态标志检查保持一致。

首次 COM 大模型插入因 Python 重复包装 SAFEARRAY 失败；修正为直接传 tuple 后，补跑只执行失败的 FFM170 部分。失败原始记录仍保留在首份报告中，最终 FFM170 报告无错误。

## 尚未通过的能力

`Topology.Face/Edge` 搜索对这些 CGR 引用失败，`SPAWorkbench.GetMeasurable(ProductReference).GeometryName` 还出现不合理枚举返回。本轮不将它们算作面/边拾取或精确解析曲面测量通过；体积验证使用实际成功的 `Product.Analyze.Volume`，没有用源网格数值替代。

完整 PS 插件未构建/部署。默认输出仍保留历史配置，未知 word0、完整计数器分配规则、Describe 尾部、部分控制字段和几何前后缀是后续解码重点。

## 自动化验证

- Python 18 项测试通过，含原生控制导出、字段损坏、身份关联和严格目录验证。
- R2 完整编码器的 38 组新旧输出逐字节一致；报告为 `analysis_cgr/container_20261015_r2/verification/verification.json`。
- 7 组 C# 身份参数验证通过，包括时间边界和单分段配置。1970/2106 边界组只做离线结构验证，没有送入 CATIA。
- 既有几何压缩自检通过；新容器代码可使用 Windows .NET Framework 编译器编译。

COM 操作需要访问正在运行的桌面 CATIA。当前沙箱内无法取得运行对象，因此通过正常审批后在沙箱外执行；没有使用鼠标键盘自动化。后续同类研究默认使用 COM。
