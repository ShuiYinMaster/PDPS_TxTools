# R4：名称长度边界与变长身份编码

后续场景进展见 [R5](SCENE-R5.md)：已将本文当时保留的几何前后缀字节载荷改为字段生成，仍有控制字段语义和兼容树配置待研究。

日期：2026-10-16。容器版本：`CFV2-20261016-R4-counted-identity`。

## 关键更正：B/D 不是身份前缀

此前解析器把顶层描述符的名称长度低 16 位当作 UTF-16 字符读取，所以显示出 `D.` 或 `B.`。实际字段是 **4 字节大端名称字节数**，后接 UTF-16BE 名称及终止符：

| 名称字节数 | 旧解析器误读的字符 | 实际名称 |
| --- | --- | --- |
| 68 = `0x0044` | `D` | `.` 加 32 个十六进制字符 |
| 66 = `0x0042` | `B` | `.` 加 31 个十六进制字符 |
| 64 = `0x0040` | `@` | `.` 加 30 个十六进制字符 |

因此没有本轮曾假设的“B 身份命名空间”，也不能把 B/D 差异归因于保存或重新打开 Part。

顶层存储描述符从其起点计算：

| 偏移 | 大小 | 字段 |
| --- | --- | --- |
| +0 | 12 | 零前缀 |
| +12 | 4 | class |
| +16 | 4 | 逻辑数据长度 |
| +20 | 64 | 元数据 |
| +84 | 4 | 名称字节数，大端，包含 UTF-16 终止符 |
| +88 | 可变 | UTF-16BE 名称及终止符 |

Python 读写器已按字段解析名称长度，拒绝截断、过长或不一致的计数；C# 根据实际名称计算该长度。默认输出中原来的 `00 00 00 44 00 2e ...` 字节不变，改变的是对其边界的正确解释。

## 身份文本规则

真实名称为 `.` 加 4 个整数片段的连接。每段取对应 uint32 的小端字节，视为大端整数，输出**不补前导零**的十六进制；零输出一个 `0`。

例如原生短名称：

```text
.4eb51e588c24000088bd06a4e920000
 [4eb51e58][8c240000][88bd06a][4e920000]
```

第三段还原为 8 个十六进制位时是 `088bd06a`，再按小端解释为 Unix 秒。每段并不固定为 8 字符，因此不能直接每 8 字符切分所有文件。

未补零的连接也可能存在分界歧义。解析器枚举每段 1..8 位的规范拼写：若仅有一个候选直接采用；否则用 Header/目录的独立时间上下文筛选，仍不能唯一确定时明确拒绝。没有根据固定 word0 或猜测 PID 强行选择。旧探针的 `D.` 加 32 位输入作为兼容别名保留；真实目录名称始终按正确边界读取和生成。

Header/目录时间不一致时，能独立还原身份的情况会报告 mismatch；不会仅为了通过时间关联而改写身份。测试覆盖了短名称 Header 时间损坏的情况。

## 新原生 COM 证据

脚本 `catia_identity_boundaries.py` 新建研究点 `(11,22,33)`，通过 CATIA COM 在 6 秒左右完成导出、保存和重开流程：

- 连续 272 次导出覆盖计数器低字节全部 256 个值，观察到名称字节数 64、66、68。
- 再保存后导出、关闭后重新打开并导出两次，总共 **275 份原生导出**。
- 每份均核对实际运行的 CNEXT PID、Header 包围盒、身份/时间三方关联、名称计数和完整容器逐字节重建。
- 连续导出的 271 个相邻计数器差值全部为 +1。保存后下一次导出 +1；重开后的下一次导出 +8；随后重复导出 +1。这只是本次操作序列的观测，不等同于已还原完整全局分配器。
- 没有修改全局可视化设置；仅关闭本轮创建或打开的研究 Part，恢复原活动文档，文档数量恢复。

275 是同一个研究点的受控导出状态，不是 275 个独立几何模型。身份 word0 的来源仍未确认。

## 生成器和验收

- C# `CgrStorageIdentity` 按原生规则生成变长拼写，`CgrContainer` 根据名称计算描述符计数；不再保留假定的 D 前缀。
- 选取 7 个原生身份，覆盖历史短时间/短计数器及 64/66/68 字节名称，由 C# 重新生成容器；.NET 8 和 .NET Framework 输出全部逐字节一致。
- 这些身份用于同一原生盒子的几何/拾取流，7 个候选与 1 个原生对照分别放入独立 Product，通过 COM 加载、显示更新、体积对照和 SaveAs。体积均为 `256000.00000000003 mm³`，创建的验收 Product 保存后关闭。
- **33 项 Python 测试通过**；包含根名称长度损坏、短身份歧义、时间损坏和 275 份原生状态的回归验证。
- **38 组既有完整输出与 R56 基线逐字节一致**，24 份旧样本重建与 4 种分段布局通过。
- 7 个既有身份参数配置继续通过，包括离线的 1970/2106 时间边界；时间边界组未作 CATIA 运行时验收。
- 原历史混合集合的关联成功数从 146 增至 **151**，原来的 5 份不支持身份全部可还原；另有 206 份容器结构不在该读取器范围。151 集合混有原生、历史生成及实验产物，不能称为 151 个独立原生模型。

R3 的 48 份字段/选项样本重新核对后均通过时间关联。旧 `R3-summary.json` 曾错误地把最终 14 份选项状态归为 B 身份；已保留为 `R3-summary.original.json` 并重算修正。原始 CGR、COM 报告和失败记录均保留，没有重写成“当时已通过”。

## 产物及限制

工作区：`analysis_cgr/container_20261016_r4/`。

```text
native_identity/native_identity_boundaries.json
native_identity/counted_identity_point.CATPart
identity_verification/identity_verification.json
com_identity/catia_accuracy_acceptance.json
metadata/metadata_verification.json
verification/verification.json
R4-summary.json
```

本轮的主要突破是纠正身份字段边界并还原变长编码，几何前后缀没有做替换。word0、完整计数器分配规则、Header 的其余控制槽、Describe 其余位/尾部及几何内部配置仍待解码。默认身份数值仍沿用已接受配置；没有部署 PS DLL，也没有据此宣称整个 CGR 已脱离固定配置。

## 重跑

仓库目录下，使用新输出目录：

```powershell
rtk python -X utf8 -m unittest discover -s Tests/CgrContainer -p test_*.py
rtk python -X utf8 Tests/CgrContainer/catia_identity_boundaries.py ../analysis_cgr/r4_identity_new
rtk python -X utf8 Tests/CgrContainer/verify_identity_r4.py .. ../analysis_cgr/r4_generated_new --probe ../analysis_cgr/container_20261016_r4/probe/CgrContainer.dll --net48-probe ../analysis_cgr/container_20261016_r4/probe/ContainerProbe.net48.exe
```

COM 仍通过正常审批在可连接桌面 CATIA 的环境执行，没有使用 UI 自动化。
