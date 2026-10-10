# R42 JT 透明材质兼容（2026-10-10）

`Missing/transparent shape material` 原因是旧 worker 在场景遍历阶段要求所有 Shape 的 Alpha 必须等于 1；有颜色的半透明/全透明材质也因此使整个 JT 转换失败。

R42 保留 JT 材质的 RGB 和 Alpha 继承关系，缺失 RGB 仍明确报错。worker 输出 `JTMESH02` version 2，每个三角面增加一字节 Alpha。桥接读取器同时接受旧 `JTMESH01`（默认不透明）和新协议，并按协议检查长度、源文件 SHA-256、顶点索引和有限坐标。DLL 与 worker 须一起更新。

`CgrWriter.Face.Opacity` 为可空 byte，旧调用未赋值时仍为 255。compact CGR 按源 Shape、RGB、Opacity 分组；颜色记录为 `20 04 04 Alpha FF B G R`。CATIA 红色前面/蓝色后面的对照测试确认 Alpha=128 时前面半透明。Alpha=0 的几何仍编码，但不再强制显示为不透明。

透明度仅支持 compact CGR 且保留源绕序的直接路线。其他后端明确拒绝，避免静默丢失透明度；本次没有扩大 CFV3 的支持范围。CGR 使用 8 位 Alpha 精度。

同时修复有效极细长三角形的零法向：当角度余弦舍入到 1，原 `acos` 得到 0，导致角度权重为零。仅在这个分支用 `atan2(三角面积的两倍, 边向量点积)` 恢复角度；不删除三角面、不改几何或颜色。

## 夹具样本验证

BP-60-T1E24MY-9156-20240701：JT 10.6、小端，文件 10,843,939 字节。所有 2,495 个三角 Shape 都有有效 RGB，82 个 Shape 非完全不透明。

| Alpha | Shape 数 | 三角面数 |
|---:|---:|---:|
| 0 | 50 | 8,088 |
| 63 | 8 | 2,068 |
| 127 | 12 | 6,268 |
| 190 | 12 | 7,734 |
| 255 | 2,413 | 630,631 |

输出 353,301 个源顶点、654,789 个三角面、24 种 RGB / 37 种 RGBA，CGR 11,809,410 字节。全部 654,789 个三角面独立回读，坐标、绕序、RGBA 多重集合差异为 0。测试读取历史 compact 解析器时，仅将读取副本中的 Alpha 改为 255 以复用旧几何语法；比较使用原 CGR 的真实 Alpha，未修改生产 CGR。

正式输出通过 JT→CGR→CATIA 新建 Product 插入与 CATProduct 保存；不经过 PS。该夹具不再触发材质或零法向错误。正式 Release 构建成功。

## C# 回归

- `FixtureRegression.cs` 参数：源 JT、全新输出目录、正式 bin。验证完整夹具几何、绕序和 RGBA 回读。
- `RgbaCompatibilityRegression.cs` 参数：正式 bin、全新输出目录、旧 JTMESH01 文件、对应 JT、旧版四资源 CGR 目录、新版四资源 CGR 目录。验证相同 RGB 不同 Alpha 分组、Alpha=0、极细长三角形、不支持后端拒绝、旧协议读取、历史不透明产物 SHA-256。

两个控制台程序使用 .NET Framework 4.8 C#，引用正式 `TxTools.dll` 编译，不依赖 Python。合成回归通过；KR210 和 FFM130 共四个实例的新产物与此前生产版本逐字节一致。双路并行和单个文件失败隔离继续通过。

现场使用前需重新加载 PS 插件，使 `TxTools.dll` 与 `JtDirectCs/TxTools.JtDecoder.exe` 同时采用 R42。启动日志分别包含 `CGR-20261010-R42-jt-rgba` 和 `visible-faces-rgba-v6`；解码日志新增 `MATERIAL_ALPHA` 汇总。
