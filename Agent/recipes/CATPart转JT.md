---
key: catia_part_to_jt
name: CATPart 转 JT（转换器直调）
lang: csharp
recipe_format: 1
run_count: 0
fail_count: 0
---

不经过「零件树整理」窗口，直接调用插件部署目录里的 `CatiaPartTree/TxTools.CatiaConverter.exe` 把 CATPart 转成 JT，并可顺手封装 COJT、插入当前工程，用于快速验证。

- 输入可以是**单个 CATPart**，也可以是**一个文件夹**（只处理该层 `*.CATPart`，不递归）。
- 输出留空时写在与源同目录：`<源名>.jt`；单文件时可指定完整路径。
- `目标体积 KB` 大于 0 时由转换器自动定三角形数：先按体积估算，再实际写出并测量，逐步收敛到目标以内（边界与硬边仍不塌陷）；填 0 表示保持原样。低于该零件可减到的下限时停在几何允许的最小值并提示。
- 勾选 `封装 COJT 并插入` 一步完成：在 COJT 目录（默认系统库根）生成 `<名>.cojt`（JT + TuneData 原型记录）并立即插入当前工程，可选目标容器。
- `平铺条带` 用来复现旧版"每个三角形独立条带"的输出，仅供 PS 侧对照实验；正常转换保持默认。

## 参数

```json
[
  {"Name":"source","Label":"CATPart 文件或文件夹","Kind":"text","Required":true,"Default":""},
  {"Name":"output","Label":"输出 JT（仅单文件有效）","Kind":"text","Required":false,"Default":""},
  {"Name":"mode","Label":"条带组织","Kind":"text","Required":false,"Default":"chained","Choices":[{"label":"链式条带（默认）","value":"chained"},{"label":"平铺条带（旧行为）","value":"flat"}]},
  {"Name":"target_kb","Label":"目标体积 KB（0=不简化，自动定三角形数）","Kind":"number","Required":false,"Default":"1024"},
  {"Name":"timeout","Label":"单件超时（秒）","Kind":"number","Required":false,"Default":"600"},
  {"Name":"skip_fresh","Label":"跳过已是最新的 JT","Kind":"bool","Required":false,"Default":"true"},
  {"Name":"build_cojt","Label":"封装 COJT 并插入（库根）","Kind":"bool","Required":false,"Default":"false"},
  {"Name":"cojt_root","Label":"COJT 目录（留空=系统库根）","Kind":"text","Required":false,"Default":""},
  {"Name":"parent","Label":"导入目标容器（留空=物理根）","Kind":"object","Required":false},
  {"Name":"dry_run","Label":"只检查环境不转换","Kind":"bool","Required":false,"Default":"false"}
]
```

## 执行按钮

```json
[
  {
    "id": "convert",
    "label": "转换为 JT",
    "args": {
      "dry_run": "false"
    }
  },
  {
    "id": "probe",
    "label": "只检查环境",
    "args": {
      "dry_run": "true"
    }
  }
]
```

## 代码

```csharp
// CATPart → JT：直调转换器；可选简化、封装 COJT、插入场景。
var sb = new StringBuilder();
bool dryRun = Convert.ToBoolean(dry_run);
bool skipFresh = Convert.ToBoolean(skip_fresh);
bool buildCojt = Convert.ToBoolean(build_cojt);
bool flat = string.Equals(Convert.ToString(mode), "flat", StringComparison.OrdinalIgnoreCase);
int targetKb = 0;
try { targetKb = (int)Math.Max(0.0, Convert.ToDouble(target_kb)); } catch (Exception) { }
int timeoutMs = -1;
try
{
    double seconds = Convert.ToDouble(timeout);
    if (seconds > 0) timeoutMs = (int)Math.Min(seconds * 1000.0, 3600000.0);
}
catch (Exception) { }

string input = source == null ? "" : Convert.ToString(source).Trim().Trim('"');
if (input.Length == 0) throw new Exception("请填写 CATPart 文件或文件夹路径。");
if (!System.IO.File.Exists(input) && !System.IO.Directory.Exists(input)) throw new Exception("路径不存在：" + input);
ITxObject target = parent as ITxObject;

// ── 定位转换器：环境变量 → 已加载的 TxTools.dll 同级 CatiaPartTree → 当前目录 ──
string converter = null;
string fromEnv = Environment.GetEnvironmentVariable("TXTOOLS_CATIA_CONVERTER");
if (!string.IsNullOrEmpty(fromEnv) && System.IO.File.Exists(fromEnv)) converter = fromEnv;
if (converter == null)
{
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
        try
        {
            if (asm.GetName().Name != "TxTools") continue;
            string dir = System.IO.Path.GetDirectoryName(asm.Location);
            if (string.IsNullOrEmpty(dir)) continue;
            string candidate = System.IO.Path.Combine(System.IO.Path.Combine(dir, "CatiaPartTree"), "TxTools.CatiaConverter.exe");
            if (System.IO.File.Exists(candidate)) { converter = candidate; break; }
        }
        catch (Exception) { }
    }
}
if (converter == null)
{
    string here = System.IO.Path.Combine(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CatiaPartTree"), "TxTools.CatiaConverter.exe");
    if (System.IO.File.Exists(here)) converter = here;
}
if (converter == null) throw new Exception("没找到 TxTools.CatiaConverter.exe。请确认插件部署目录下存在 CatiaPartTree 子目录，或设置环境变量 TXTOOLS_CATIA_CONVERTER 指向该程序。");

var info = new System.IO.FileInfo(converter);
sb.AppendLine("**转换器**：`" + converter + "`");
sb.AppendLine("大小 " + info.Length + " 字节，修改时间 " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
sb.AppendLine("条带：" + (flat ? "平铺（对照）" : "链式") + "；体积目标：" + (targetKb > 0 ? (targetKb + " KB（自动定三角形数）") : "不简化") + "；单件超时：" + (timeoutMs < 0 ? "不限" : (timeoutMs / 1000) + " 秒") + "。");
sb.AppendLine();

// ── 收集待转换文件 ──
string explicitOutput = output == null ? "" : Convert.ToString(output).Trim().Trim('"');
var files = new List<string>();
if (System.IO.File.Exists(input))
{
    files.Add(input);
}
else
{
    string[] found = System.IO.Directory.GetFiles(input);
    for (int i = 0; i < found.Length; i++)
    {
        if (string.Equals(System.IO.Path.GetExtension(found[i]), ".CATPart", StringComparison.OrdinalIgnoreCase)) files.Add(found[i]);
    }
    files.Sort(StringComparer.OrdinalIgnoreCase);
    if (files.Count == 0) throw new Exception("文件夹里没有 CATPart：" + input);
    if (explicitOutput.Length > 0) sb.AppendLine("提示：输入是文件夹，输出参数被忽略。").AppendLine();
}

if (dryRun)
{
    sb.AppendLine("**只检查环境，未执行转换。**");
    sb.AppendLine();
    sb.AppendLine("待处理 " + files.Count + " 个零件：");
    for (int i = 0; i < files.Count; i++) sb.AppendLine("- " + System.IO.Path.GetFileName(files[i]) + "（" + new System.IO.FileInfo(files[i]).Length + " 字节）");
    return sb.ToString();
}

string cojtRoot = cojt_root == null ? "" : Convert.ToString(cojt_root).Trim().Trim('"');
if (cojtRoot.Length == 0) cojtRoot = TxApplication.SystemRootDirectory;

Func<string, string> hashOf = delegate(string path)
{
    using (var stream = System.IO.File.OpenRead(path))
    using (var sha = System.Security.Cryptography.SHA256.Create())
    {
        byte[] digest = sha.ComputeHash(stream);
        var text = new StringBuilder(digest.Length * 2);
        for (int i = 0; i < digest.Length; i++) text.Append(digest[i].ToString("x2"));
        return text.ToString();
    }
};

int done = 0, failed = 0, skipped = 0, imported = 0;
var errors = new List<string>();
var clock = new System.Diagnostics.Stopwatch();
for (int i = 0; i < files.Count; i++)
{
    string part = files[i];
    string partName = System.IO.Path.GetFileNameWithoutExtension(part);
    string jt = explicitOutput.Length > 0 && files.Count == 1
        ? explicitOutput
        : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(part), partName + ".jt");
    string folder = System.IO.Path.GetDirectoryName(jt);
    if (!string.IsNullOrEmpty(folder) && !System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);

    bool fresh = System.IO.File.Exists(jt) && System.IO.File.GetLastWriteTimeUtc(jt) >= System.IO.File.GetLastWriteTimeUtc(part);
    bool produced = System.IO.File.Exists(jt);
    if (skipFresh && fresh)
    {
        skipped++;
        sb.AppendLine("- " + System.IO.Path.GetFileName(part) + "：JT 已比源新，跳过转换");
    }
    else
    {
        string arguments = "\"" + part + "\" \"" + jt + "\" " + hashOf(part) + (flat ? " flat" : "") + (targetKb > 0 ? " targetkb=" + targetKb : "");
        var start = new System.Diagnostics.ProcessStartInfo(converter, arguments);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardError = true;
        start.RedirectStandardOutput = true;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.StandardOutputEncoding = Encoding.UTF8;

        clock.Restart();
        string error = "";
        bool ok = false;
        using (var process = System.Diagnostics.Process.Start(start))
        {
            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            bool exited = true;
            if (timeoutMs > 0) exited = process.WaitForExit(timeoutMs);
            else process.WaitForExit();
            if (!exited)
            {
                try { process.Kill(); } catch (Exception) { }
                try { process.WaitForExit(); } catch (Exception) { }
                error = "超时 " + (timeoutMs / 1000) + " 秒，已终止转换程序";
            }
            else
            {
                error = (errorTask.Result + " " + outputTask.Result).Trim();
                ok = process.ExitCode == 0 && System.IO.File.Exists(jt) && new System.IO.FileInfo(jt).Length > 104;
                if (!ok && error.Length == 0) error = "退出码 " + process.ExitCode + "，未产出有效 JT";
            }
        }
        clock.Stop();
        produced = ok;
        if (ok)
        {
            done++;
            long bytes = new System.IO.FileInfo(jt).Length;
            string triangles = "";
            string report = error;   // 转换器把实际三角形数打在 stdout 上
            int at = report.IndexOf("simplify=", StringComparison.Ordinal);
            if (at >= 0)
            {
                int end = report.IndexOf(' ', at);
                if (end < 0) end = report.Length;
                triangles = "，" + report.Substring(at + 9, end - at - 9) + " 三角形";
            }
            sb.AppendLine("- " + partName + "：JT " + (bytes / 1024) + " KB" + triangles + "，转换 " + clock.ElapsedMilliseconds + " ms" + (targetKb > 0 && bytes > targetKb * 1024L ? "（已达几何下限，仍超出目标）" : ""));
        }
        else
        {
            failed++;
            errors.Add(System.IO.Path.GetFileName(part) + "：" + error);
            sb.AppendLine("- " + partName + "：**转换失败** " + error);
            continue;
        }
    }
    if (!produced) continue;

    // ── 可选：封装 COJT 并插入（一步） ──
    if (buildCojt)
    {
        string cojt = System.IO.Path.Combine(cojtRoot, partName + ".cojt");
        try
        {
            string summary;
            var inserted = TxTools.Agent.Core.RecipeCatiaBridge.BuildAndInsert(jt, cojt, partName, target, out summary);
            imported++;
            sb.AppendLine("  · COJT：`" + cojt + "` → 已插入 " + inserted.Name + (target == null ? "（物理根）" : "（指定容器）"));
        }
        catch (Exception ex)
        {
            failed++;
            errors.Add(partName + "（COJT/插入）：" + ex.Message);
            sb.AppendLine("  · **COJT/插入失败** " + ex.Message);
        }
    }
}

sb.AppendLine();
sb.AppendLine("---");
sb.AppendLine();
sb.AppendLine("转换成功 " + done + " 个，失败 " + failed + " 个，跳过 " + skipped + " 个（共 " + files.Count + " 个）" + (buildCojt ? "；COJT/插入 " + imported + " 个" : "") + "。");
if (buildCojt) sb.AppendLine("COJT 目录：`" + cojtRoot + "`（必须在当前 PS 系统库内，允许直接放库根）。");
if (failed > 0)
{
    sb.AppendLine();
    sb.AppendLine("失败原因：");
    for (int i = 0; i < errors.Count; i++) sb.AppendLine("- " + errors[i]);
}
if (failed == files.Count) throw new Exception("全部零件失败，详见输出。");

return sb.ToString();
```
