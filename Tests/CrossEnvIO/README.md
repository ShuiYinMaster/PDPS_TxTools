# CrossEnvIO 回归验证

链接生产源文件，覆盖位姿、复制身份、junction 目标、编码/BOM、XML 实体、精确库映射、依赖清理、子树重建、焊点多零件绑定和远程预检。场景存储和创建接口由 `HostStubs.cs` 模拟；文件系统、ZIP、编码和 Windows junction 使用真实实现。

在项目根目录运行（需要 Visual Studio Build Tools 和 .NET Framework 4.8）：

```powershell
rtk proxy 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe' Tests\CrossEnvIO\CrossEnvIO.csproj /t:Build /p:Configuration=Release /nologo /v:minimal
rtk proxy Tests\CrossEnvIO\bin\Release\CrossEnvIORegression.exe
```

受限环境可能禁止创建 junction，需要允许测试进程在其独立测试目录内执行该项验证。测试只创建 `bin/Release/fixtures-<GUID>`，结束时删除本次夹具；不使用真实用户工程或库。

宿主验收重点：

1. 三个 PS 实例之间切换目标，确认下拉框、工程和库根一致；目标退出后操作应停止。
2. 有已有对象的目标容器中复制，确认只重命名和定位本次新增对象；中文 cojt 和同名不同路径组件应正确插入。
3. 对端重建后执行源端清理，确认模型文件被保护；未重建时由两端引用检查决定是否清理。
4. 拾取资源、焊点和坐标系，确认空选择范围提示、忙碌状态、原点重置与原生控件一致。
5. 显式 `save_reload=true` 后保存并重开工程，确认组件链接、XML 编码和模型位姿正确。
