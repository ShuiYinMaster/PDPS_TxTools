# Agent 记忆与对话优化（2026-09-04）

## 状态

源码已修改并通过隔离编译、24 项回归测试和 JavaScript 语法检查。2026-09-04 14:39（中国标准时间）确认 Tune 进程数为 0 后，已应用清理并原子替换部署 DLL/PDB；新版本在下次启动 Process Simulate 后生效。

恢复目录：`D:\Program Files\Tecnomatix_2402\eMPower\DotNetCommands\TxTools\maintenance-backup\20260904_063919_846`。包含 76 个原始记忆文件、SHA-256 清单、原 prefs/recipes JSON 和原 DLL/PDB。归档逐文件哈希验证通过，部署产物与隔离构建哈希一致，再次预览待处理文件为 0。

部署目录：`D:\Program Files\Tecnomatix_2402\eMPower\DotNetCommands\TxTools`。

## 行为调整

- 官方 DeepSeek V4 默认 `reasoning_effort=low`；`prefs.json` 可设 `ReasoningEffort` 为 `low`、`high` 或 `max`，重新打开智能体生效。其他端点不发送专有参数。
- 保留现有输出 token 上限，不靠截断回答降低思考时间。低强度不是固定 token 数或耗时保证。
- 工程任务优先检索相关经验、只读查询、脚本对比/差集筛查、小范围变更及读回复核；禁止靠手算矩阵或猜测 API 反复试错。
- 常驻记忆由最多 10 条事实 + 15 条避坑，缩为最多 6 条偏好/API事实 + 5 条有正解的避坑；工作流仍可按需检索。
- 自动片段晋升默认关闭。重复执行不代表结果正确，待定项读取不再顺手删除过期文件。

参数和工具回传依据：[DeepSeek Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)。官方 V4 带 tools 时需要回传完整 `reasoning_content`，不是所有 API 都禁止回传。

## 对话存储

- 存储专用 JSON resolver 保存模型实际返回的 `reasoning_content`，与网络序列化隔离。
- 完成、取消、调用失败、仅返回思考的响应均有保存路径；不生成或补写模型没有返回的思考。
- 历史 UI 默认折叠显示思考记录。思考是过程记录，不当作已验证事实进入知识库。
- 紧凑 JSON，临时文件落盘并 flush 后原子替换，保留一个 `.bak`；损坏时可回退旧版。列表优先读取元数据，不反序列化全部消息。
- 修复工具完成事件早于结果入库，以及上下文裁剪导致数值归档游标错位的遗漏风险。
- 旧对话保持可读取，不批量重写。旧版本从未存下来的思考不能恢复。进程强杀时尚未返回的流式片段仍可能丢失；本次不是逐 token WAL。
- 磁盘记录包含用户数据和模型返回内容；备份应按对话本身的敏感程度保护。

## 清理计划

2026-09-04 已归档 76 个文件（可恢复，非永久删除）。

| 类别 | 当前 | 归档 | 保留 |
|---|---:|---:|---:|
| facts | 512 | 51 个 scene_constant | 461 |
| snippets | 352 | 23 个 auto-promoted 且 success_count=0 | 329 |
| Markdown recipes | 4 | 2 个未记录运行且与综合导出重叠的配方 | 2 |

保留按关节拆分配方，因为输出结构与按颜色拆分不同。保留 180 条 gotchas、157 条 pending、114 份对话、全部知识库。旧 `recipes.json` 的 21 条配方也保留：当前源码 `RecipeStore.All()` 读取 Markdown，不把旧 JSON 当作已迁移、可直接运行的配方。

不做模糊相似度批量删除：文字相似的 API 经验可能涉及不同对象、参考系或前置条件。脚本仅自动归档明确类别和未经验证的自动条目；完全重复事实保留一份（本次没有命中）。

## 应用和恢复

1. 保存工程并关闭所有 Process Simulate（Tune）进程。
2. 先运行 `Optimize-DeployedMemory.ps1` 预览，审核统计。
3. 获得部署目录写权限后，运行 `Optimize-DeployedMemory.ps1 -Apply`。
4. 脚本先复制备份并校验 SHA-256，再逐文件移出活动目录；清单位于部署目录 `maintenance-backup/<UTC时间>/manifest.json`。没有永久销毁。
5. DLL 替换须单独备份原 `TxTools.dll`/`TxTools.pdb`，再部署 `artifacts/review-build` 中编译产物。UI 为内嵌资源，不能仅修改 HTML 文件就让部署生效。编译基于当前工作树，保留并包含用户原有改动。
6. 如需恢复，关闭 Tune，按 manifest 把备份中的对应文件复制回原路径；遇到同名新文件先比较，不覆盖新内容。`prefs.json` 也有原始备份。程序不负责自动回滚场景或 CATIA 文档。

本次不更改 `auto_all` 审批选择、不读取/复制 API key、不执行任何场景配方。

## 测试

`StorageRegression.cs` 覆盖网络字段隔离、low/max/非法配置、历史思考 round-trip、旧数据兼容、原子备份和恢复、上下文 token 计数、正常/取消/失败/仅思考响应保存，以及工具完成时结果已配对入库。当前 24 项通过。HTML 内联 JavaScript 语法检查通过。

未进行付费实时模型调用，也未在 PS/CATIA 中执行工程变更验证。编译中的原有警告（重复 Compile 项、COM 封送、未使用字段等）未顺手修改。
