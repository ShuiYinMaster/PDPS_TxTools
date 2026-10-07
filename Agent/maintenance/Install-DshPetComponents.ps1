#Requires -Version 7.0
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\UI\pet\dsh-pet'),
    [string]$Cache = (Join-Path $PSScriptRoot '..\artifacts\pet-components'),
    [switch]$SkipDownload
)
$ErrorActionPreference = 'Stop'
$electronVersion = '43.3.0'
$upstreamCommit = '972f1cb9437dc256bdcb0707f7a6812069dd93db' # dsh-pet v0.3.0
$cacheRoot = [IO.Path]::GetFullPath($Cache)
$targetRoot = [IO.Path]::GetFullPath($Destination)
$stageRoot = Join-Path $cacheRoot 'staged'
$headers = @{ 'User-Agent' = 'TxTools-Pet-Component-Setup' }
New-Item -ItemType Directory -Path $cacheRoot, $stageRoot -Force | Out-Null

function Get-GitBlobHash([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $prefix = [Text.Encoding]::UTF8.GetBytes("blob $($bytes.Length)`0")
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA1)
    try {
        $hash.AppendData($prefix); $hash.AppendData($bytes)
        return [BitConverter]::ToString($hash.GetHashAndReset()).Replace('-', '').ToLowerInvariant()
    } finally { $hash.Dispose() }
}

if (!$SkipDownload) {
    $archive = Join-Path $cacheRoot "electron-v$electronVersion-win32-x64.zip"
    $checksums = Join-Path $cacheRoot 'SHASUMS256.txt'
    $releaseUrl = "https://github.com/electron/electron/releases/download/v$electronVersion"
    Invoke-WebRequest -Uri "$releaseUrl/SHASUMS256.txt" -Headers $headers -OutFile $checksums
    $checksumLine = Get-Content -LiteralPath $checksums | Where-Object { $_ -match "\s+\*?electron-v$([regex]::Escape($electronVersion))-win32-x64\.zip$" }
    if (@($checksumLine).Count -ne 1) { throw '官方校验清单缺少所需 Electron 版本。' }
    $expected = ($checksumLine.Trim() -split '\s+')[0].ToLowerInvariant()
    if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
        Write-Output "下载 Electron $electronVersion Windows x64…"
        Invoke-WebRequest -Uri "$releaseUrl/electron-v$electronVersion-win32-x64.zip" -Headers $headers -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Electron 压缩包 SHA-256 不匹配。' }
    $electronStage = Join-Path $stageRoot 'electron'
    # All archive paths must resolve inside our staging directory before extraction.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $resolved = [IO.Path]::GetFullPath((Join-Path $electronStage $entry.FullName))
            if (!$resolved.StartsWith($electronStage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw "压缩包包含越界路径：$($entry.FullName)"
            }
        }
    } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $archive -DestinationPath $electronStage -Force
    Write-Output 'Electron 官方 SHA-256 校验通过。'

    $tree = Invoke-RestMethod -Uri "https://api.github.com/repos/PC2005-cloud/dsh-pet/git/trees/${upstreamCommit}?recursive=1" -Headers $headers
    if ($tree.truncated) { throw '上游文件清单被截断。' }
    $assets = @($tree.tree | Where-Object { $_.type -eq 'blob' -and
        ($_.path.StartsWith('dsh-pet/assets/webm/') -or $_.path.StartsWith('dsh-pet/assets/pic/')) })
    if ($assets.Count -eq 0) { throw '上游清单缺少动画和图片。' }
    $assets | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $cacheRoot 'upstream-assets.json') -Encoding utf8
    $done = 0
    foreach ($asset in $assets) {
        $relative = $asset.path.Substring('dsh-pet/'.Length)
        $file = [IO.Path]::GetFullPath((Join-Path $stageRoot $relative))
        if (!$file.StartsWith($stageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '上游资源路径越界。' }
        New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
        if (!(Test-Path -LiteralPath $file) -or (Get-GitBlobHash $file) -ne $asset.sha) {
            $encoded = (($asset.path -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
            Invoke-WebRequest -Uri "https://raw.githubusercontent.com/PC2005-cloud/dsh-pet/$upstreamCommit/$encoded" -Headers $headers -OutFile $file
        }
        if ((Get-Item -LiteralPath $file).Length -ne $asset.size -or (Get-GitBlobHash $file) -ne $asset.sha) { throw "上游文件校验失败：$relative" }
        $done++
        if ($done % 20 -eq 0 -or $done -eq $assets.Count) { Write-Output "动画/图片下载并校验：$done/$($assets.Count)" }
    }
    $files = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -File | ForEach-Object {
        [pscustomobject]@{ path = [IO.Path]::GetRelativePath($stageRoot, $_.FullName); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [pscustomobject]@{ electronVersion = $electronVersion; upstreamCommit = $upstreamCommit; electronArchiveSha256 = $expected; files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $cacheRoot 'manifest.json') -Encoding utf8
}

$manifest = Get-Content -LiteralPath (Join-Path $cacheRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.electronVersion -ne $electronVersion -or $manifest.upstreamCommit -ne $upstreamCommit) { throw '缓存版本不匹配。' }
foreach ($file in $manifest.files) {
    $source = [IO.Path]::GetFullPath((Join-Path $stageRoot $file.path))
    if (!$source.StartsWith($stageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '缓存文件路径越界。' }
    if (!(Test-Path -LiteralPath $source) -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) { throw "缓存校验失败：$($file.path)" }
}
foreach ($required in @('entry.js', 'config.json', 'host-routes.js', 'startup-monitor.js', 'quick-launch.js', 'upstream/main.js', 'upstream/index.html', 'upstream/preload.js', 'upstream/shared-core.js')) {
    if (!(Test-Path -LiteralPath (Join-Path $targetRoot $required))) { throw "目标不是完整的 TxAgent 桌宠目录，缺少 $required" }
}
$active = @(Get-Process -Name 'Tune*', 'ProcessSimulate*' -ErrorAction SilentlyContinue)
$replacements = @($manifest.files | Where-Object {
    $target = Join-Path $targetRoot $_.path
    (Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $_.sha256
})
if ($active.Count -and $replacements.Count) { throw '需替换已有组件，请保存工程并关闭 Process Simulate 后部署。' }
$runningPets = @(Get-Process -Name electron -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($targetRoot + '\', [StringComparison]::OrdinalIgnoreCase) })
if ($runningPets.Count) { throw '目标桌宠正在运行，请关闭后部署。' }

$backupRoot = Join-Path $cacheRoot ('backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$copied = 0; $unchanged = 0
$installedFolders = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
# Publish absent component directories only after their complete contents are copied.
# Assets go first so exposing electron.exe cannot start a pet with missing clips.
foreach ($component in @('assets', 'electron')) {
    $componentTarget = [IO.Path]::GetFullPath((Join-Path $targetRoot $component))
    if (Test-Path -LiteralPath $componentTarget) { continue }
    $temporary = [IO.Path]::GetFullPath((Join-Path $targetRoot ('.install-' + $component + '-' + [Guid]::NewGuid().ToString('N'))))
    foreach ($path in @($componentTarget, $temporary)) {
        if (!$path.StartsWith($targetRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '组件目录路径越界。' }
    }
    Copy-Item -LiteralPath (Join-Path $stageRoot $component) -Destination $temporary -Recurse
    Move-Item -LiteralPath $temporary -Destination $componentTarget
    $null = $installedFolders.Add($component)
}
foreach ($file in $manifest.files) {
    $source = Join-Path $stageRoot $file.path
    $target = [IO.Path]::GetFullPath((Join-Path $targetRoot $file.path))
    if (!$target.StartsWith($targetRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '部署文件路径越界。' }
    if ($installedFolders.Contains(($file.path -split '[\\/]')[0])) { $copied++; continue }
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $file.sha256) { $unchanged++; continue }
        $backup = Join-Path $backupRoot $file.path
        New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $backup
    }
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
    $copied++
}
foreach ($file in $manifest.files) {
    if ((Get-FileHash -LiteralPath (Join-Path $targetRoot $file.path) -Algorithm SHA256).Hash -ne $file.sha256) { throw "部署校验失败：$($file.path)" }
}
$report = [pscustomobject]@{ destination = $targetRoot; copied = $copied; unchanged = $unchanged; sha256Match = $true; electronVersion = $electronVersion; upstreamCommit = $upstreamCommit; backup = if (Test-Path -LiteralPath $backupRoot) { $backupRoot } else { $null }; completedUtc = [DateTime]::UtcNow.ToString('o') }
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $cacheRoot 'deployment.json') -Encoding utf8
$report | ConvertTo-Json
