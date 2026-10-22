# 导插枪 / 点球导出回归

直接链接生产 `ExportService.cs`；PS 读取器与 CATIA 连接使用边界替身。
检查两条导出通道的 PS 主线程路由、CATIA STA 线程、连接生命周期、无点提前退出、连接失败、COM 异常传递及 HRESULT 保留。
同时链接生产 `GunExportNaming.cs`，检查同日重复命名、跨日、长名称、非法字符和序号超过 999 的处理。
链接生产 `CgrOutputPaths.cs`，覆盖 400 字符工具名、过深 JT 目录、缓存回退、同名已有文件与 Windows 保留名称；按编码器的 `.32位GUID.partial` 后缀执行实际文件写入及移动，检查最终路径、临时路径和文件名长度。这里不校验 CGR 几何编码。
分别使用 null、基类上下文、WinForms 上下文构造服务，防止后备上下文把 PS API 留在后台线程。

```powershell
rtk proxy MSBuild.exe Tests/ExportGun/ExportGunRegression.csproj /t:Build
rtk proxy Tests/ExportGun/bin/ExportGunRegression.exe
```

这些用例不启动或修改真实 CATIA 文档；实际 CGR 插入、定位、几何更新和保存仍需在 PS + CATIA 宿主复测。
