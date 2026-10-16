# CFV2 容器字段生成与解码（2026-10-15）

最新进展见 [R7：变长公共头与显示属性继承](SCENE-STYLES-R7.md)：公共头首字段是紧凑继承掩码，头长可以是 35/39 字节；已确认颜色/透明度/线宽/线型继承掩码、BGR 通道、透明度位及跨字节线样式打包。41 个原生状态和跨运行时配置通过，58 项测试通过，38 组默认输出保持一致。场景版本 `CGRSCENE-20261016-R7-variable-attributes`，未部署 PS。

此前 [R6：图层、禁止拾取位与体积贡献位](SCENE-FLAGS-R6.md) 已确认 62/A3 的定长图层字段，修正 R5 将其误读为扩展整数的问题，并定位叶节点 0x10 的体积贡献门控。Compact 默认体积位保持原值。

此前 [R5：场景记录字段生成](SCENE-R5.md) 已删除 `CgrTemplate.cs`，9 块几何框架载荷改为字段序列化；62/A3/95、矩阵、共享引用和拾取偏移可按边界解析。部分标志和树层次仍为兼容配置。

当前进展见 [R4：名称长度边界与变长身份编码](IDENTITY-R4.md)。根存储名的 B/D 前缀是旧解析器误读的长度字符，现已修正；275 份新 COM 导出覆盖计数器低字节全部取值，7 组身份重建通过 COM 验收。此前精度和附加数据进展见 [R3](FIELDS-R3.md)，PID/时间关联见 [R2](METADATA-R2.md)。尚未部署到 PS。

R1 阶段将 `CgrWriter` 的文件头、目录、存储节点索引表改为字段序列化，删除整块 Base64 容器模板和固定目录偏移。实现为 `ExportByColor/CgrContainer.cs`，当前容器版本 `CFV2-20261016-R4-counted-identity`。保留原子临时文件发布。几何编码器的版本标记仍为 R56。下文保留早期字段证据和验证记录，并修正 R4 确认的名称边界；R3 已修正附加存储没有索引表也可以包含目录子节点的情况。

默认沿用原有 26/25 个几何/拾取分段，以便先证明实现替换不改变输出。研究探针另测 1/1、2/7、26/25、127/129 四种布局，目录长度随分段数计算，不再依赖旧模板槽位。

## 解码结论与字段证据

以下限于已验证的、未压缩的 V5_CFV2 布局；不等同于任意 CGR/CFV3 容器规范。

文件头为 64 字节，`+8/+12` 两个大端 uint32 指向末尾目录及其长度。目录从 `CATIA_V5 CB0001\0` 开始，尾部为目录长度 uint32 和 `CB__END\0`。

| 目录头偏移 | 内容 | 本轮结论 |
| --- | --- | --- |
| +16 | byte `0x4E` | 固定控制值，含义待证 |
| +17 | uint16 `1000` | 保留观测值，含义待证 |
| +19 | uint16 `64` | 与目录头长度一致 |
| +21 | uint16 `12` | 含义待证；点对象也为 12，不能当作非根记录数 |
| +23 | uint16 | 非根分配记录数量，点对象 11、实体样本 12 |
| +25 | uint16 | 全部分配记录数量，点对象 12、实体样本 13 |
| +27 | uint32 | class=2 的逻辑流字节数之和，不含存储索引表 |
| +31 | uint32 | 当前文件总字节数 |

分配记录不是几何类型表。旧工具常称 `type12/type13` 的数字，实际是该布局中分配记录的 ID。根空记录为 ID=1，class=0；class=1 对应存储索引，class=2 对应数据流。点对象没有 `SurfacicReps`，所以只有 12 条记录。解析器逐条遍历，不扫描载荷中的“疑似记录签名”。

| 分配记录偏移 | 大小 | 内容 |
| --- | --- | --- |
| +0 | 4 | 记录长度 = `86 + 20 × 分段数` |
| +4 | 4 | 记录 ID |
| +8 | 2 | class，当前支持 0/1/2 |
| +10 | 4 | 重复的 class 值；并非目录树父 ID |
| +14 | 4 | 逻辑数据总长 |
| +18 | 64 | 元数据字段，见下文 |
| +82 | 4 | 分段数 |
| +86 | 每项 20 | 物理起点、存储长度、解码长度、逻辑偏移、零标志，均为大端 uint32 |

每条非根分配记录前还有描述符，包含真正的父 ID、class、逻辑长度、重复元数据，以及带长度的 UTF-16BE 名称。顶层存储描述符有 12 字节零前缀，随后是 class、逻辑长度、64 字节元数据、**uint32 大端名称字节数**和 UTF-16BE 名称（含终止符）。早期误将名称长度低 16 位读成了字符 D/B；实际名称以 `.` 开始，详见 R4。

存在索引载荷的 class=1 存储可以由目录树重建：12 字节表头（表长、本节点 ID、子节点数），后跟子节点项（项长、子 ID、class、名称字节数、UTF-16BE 名称及终止符）。正式写出保留已接受的子 ID 降序。顶层节点的子项依次为 SceneGraph、Header、Describe、MAIN；SceneGraph 下为 SurfacicReps、MainDataStream、SceneGraph。R3 另确认原生附加存储可以没有索引载荷而仍有目录子节点。

64 字节元数据中的 `+4/+12/+20` 各 8 字节，可按“低 32 位、高 32 位，各自大端”组合成 FILETIME 数值。FFM170 的首个值解出 `2026-09-02T12:17:27.283000+00:00`，与不同日期创建的原生样本变化相符。`+28` 为标志（已接受样本的顶层存储 `0x80000011`、其他非根 `0x11`）。时间槽的创建/修改/身份生命周期含义尚未独立证明，本轮保留原值。

## 已完成的验证

- 24 个样本按字段解析并重建容器后，**整个文件逐字节一致**：Part1、three3、gt_N128、原生 FFM170、2026-10-13 两批盒子/点对象，以及 R55 FFM170 生成文件。最后一项是历史编码器产物，并非独立原生导出。
- 11 个网格 × Compact / LineFace / LineFacePlanar 共 33 组完整输出，与修改前保存的编码器二进制逐字节一致。覆盖颜色、圆柱、远坐标、开放面、反向面、接触体、非流形、重复面和大网格。
- 另对 Legacy、公开 `Build` 接口、三种 RGBA 路径做 5 组同调用者的新旧比较。RGBA 包含 0、85、170、255 四档不透明度。
- 独立 C# 容器探针无需编译 `CgrTemplate.cs`，四种分段布局由 Python 顺序解析器验证流哈希、树/索引一致性、包围盒和完整覆盖。
- Python 10 项测试含 23 个样本子用例及损坏输入检查：越界、重叠、错误长度/计数、元数据不一致、父节点循环、索引表撒谎等。
- 既有几何压缩自检通过：105 组保绕序图元、6 组边链、3 组面索引映射及轴法向。
- 新容器源文件以 Windows .NET Framework 编译器编译运行，输出与 .NET 8 探针完全一致。
- 用仓库已有的 `catia_to_jt/tools/native_cfv.py` 作第二读取器，对最终目录下的 81 个产物全部通过结构读取；清单位于 `independent_reader.json`。

完整 PS 插件未构建：工作区默认引用的 `2402dll` 目录不存在。已完成独立 CGR 编码器的 .NET 8 构建，以及新容器代码的 Windows .NET Framework 编译/运行。PS 加载仍未验证；CATIA 运行时已在 R2 补做 COM 验收。构建中的 NU1900 警告来自离线环境无法取得 NuGet 漏洞索引，编译无错误。

工作区验证报告：`analysis_cgr/container_20261015/verification_final/verification.json`。修改前的运行器及相关源码快照位于同级 `baseline/` 和 `*.before.cs`，对比基线包含本轮开始前已有的 R56 工作区改动。

## 实验文件与限制

`FFM170_structured_single_extent_experimental.cgr` 是把原生 FFM170 的 26/25 个流分段合成 1/1，并从字段重新生成容器的实验产物。目录减少 980 字节，所有逻辑流逐字节保持不变；**这不是几何压缩收益**。R2 已补做当前 CATIA 环境下的 COM 插入、显示、体积和保存验收，原版/合并版体积一致。面边拾取尚未通过，不外推到任意 CGR。

默认路径保持历史物理布局并已证明测试输出逐字节一致；任意分段数仅在研究探针开放，没有增加生产环境变量或部署新 DLL。

仍未完全解码的内容包括存储身份 word0 和完整分配规则、时间字段的生命周期语义、部分控制字/标志、Describe 的其余位及尾部、Header 的其余槽，以及场景公共头部分语义和树层次选择。R5 已将几何前后缀改为字段生成，但还依赖已接受的配置值；不能据此宣称整个 CGR 格式已完全破解。

后续未解码字段及最新实验边界见 R4 文档。1/1 样本已在 R2 通过当前环境的 CATIA COM 验收。CFV3 的多段控制块有额外引用约束，应另行研究，不能直接移植 CFV2 的合并结论。

## 重跑

在仓库目录执行，先准备修改前/后的独立运行器目录：

```powershell
rtk python -m unittest discover -s Tests/CgrContainer -p test_cfv2.py -v
rtk dotnet build Tests/CgrContainer/CgrContainer.csproj -p:MSBuildEnableWorkloadResolver=false -p:NuGetAudit=false --ignore-failed-sources -o ../analysis_cgr/container_probe
rtk dotnet build Tests/CgrLineFace/CgrLineFace.csproj -p:MSBuildEnableWorkloadResolver=false -p:NuGetAudit=false --ignore-failed-sources -o ../analysis_cgr/container_encoder
rtk python Tests/CgrContainer/verify.py .. ../analysis_cgr/container_new_run --probe ../analysis_cgr/container_probe/CgrContainer.dll --baseline ../analysis_cgr/container_20261015/baseline/CgrLineFace.dll --current ../analysis_cgr/container_encoder/CgrLineFace.dll
```

输出目录必须是新目录。验证不启动 PS/CATIA，也不覆盖原始 CGR。
