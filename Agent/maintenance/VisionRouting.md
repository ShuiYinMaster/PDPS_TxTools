# 视觉路由更新（2026-09-04）

依据 [DeepSeek Vision 官方文档](https://api-docs.deepseek.com/guides/vision/)，支持图像的型号是 `deepseek-v4-flash-vision-exp`。普通 `deepseek-v4-flash` / `deepseek-v4-pro` 不应接收图片。本项目为视觉实验型号暂用保守的 128,000 上下文估算，不宣称这是其服务端限制。

## 当前行为

- `analyze_image` / `analyze_viewport` 按需读取图片或截图，调用一次视觉模型，返回带 provider/model 来源的文字结果。
- 显式传 `provider` 时严格限定该平台；不可用就报错，不静默传图给其他平台。
- 自动路由依次优先当前视觉模型、当前 provider 的视觉型号、回退偏好和其他有密钥的目录候选。不再固定优先千问。
- DeepSeek 视觉型号已加入内置列表和能力目录；即使主对话仍选普通 Flash/Pro，视觉工具也可使用同账号的 vision-exp。模型实际授权仍以 API 为准，配置密钥不代表每个型号都有权限。
- 显式 provider 选择不再临时改写共享的 `PreferredVisionProvider`，减少并发路由串用的风险。
- 官方 DeepSeek 视觉子调用使用低思考强度和 8,192 token 输出上限，保留现有 low/high 图像精度；auto/original 也可传入，其他值会提前报错。
- BMP 依据文件签名转为 PNG；读文件前先检查大小，转换后再检查 Base64 上限。
- 视觉 API 错误、空结果、输出截断统一标记 `Error:`，不把未完成内容视为成功。鉴权、限流、网络或兼容错误不会触发自动跨平台重试。
- 多模态 `ChatMessage` JSON 反序列化保留图片块；改回文本/null 时清除旧图，避免重放丢图或残留。

## 边界

这次没有把附件自动注入主对话的多轮上下文。即使使用当前选中的视觉型号，也是经现有视觉工具进行独立子调用，主对话接收文字结果；不是把完整聊天历史传给视觉端点。后续如要原生多模态主循环，需要单独完善 Harness 消息转换、图片留存和跨模型切换策略。

精确数量、坐标、层级、碰撞判断仍应优先 SDK 工具；视觉用于图像可见信息，不替代工程数据校验。没有新增 Files API 上传或额外云端图片持久化。

## 验证

完整宿主隔离编译通过。`VisionRegression.cs` 的 18 项离线检查，以及已有 `StorageRegression.cs` 的 24 项检查通过。覆盖模型区分、选择顺序、显式平台隔离、无密钥、共享状态不变、JSON 图像 round-trip、BMP 转换及参数校验。

未进行真实付费视觉调用或 PS/CATIA 场景变更测试。测试夹具只在隔离构建目录产生。
