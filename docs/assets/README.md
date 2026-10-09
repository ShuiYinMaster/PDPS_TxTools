# 主 README 图像资源

这些图片由仓库主 [README](../../README.md) 引用，使用相对路径，可随仓库离线查看。

| 文件 | 来源与含义 |
|---|---|
| `tx-tools-banner.svg` | 可编辑的 SVG 首屏图；机器人和路径为示意，不是 PS 仿真截图 |
| `feature-map.svg` | 根据当前模块说明整理的任务地图；完整模块索引以主 README 为准 |
| `recipe-controls-preview.png` | 当前 `Agent/UI` 实际界面代码与默认配方渲染的本地浏览器预览；使用示例对象和模拟执行，不连接 PS |

SVG 不包含脚本、外部字体或远程图片，字体由查看端的系统提供。

重新生成配方预览：

```powershell
node Agent/maintenance/BuildRecipeSidebarPreview.js
```

打开输出的 `Agent/artifacts/recipe-preview/controls.html`，在左侧选取示例对象，然后截取标题、示例数据提示和两个界面。当前图片使用 720 × 814 像素区域。预览源码、默认配方或样式发生变化时应同步更新图片，并检查是否仍能读清参数和执行按钮。
