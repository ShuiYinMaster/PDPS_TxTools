param(
    [string]$Source = (Join-Path $PSScriptRoot '..\artifacts\dsh-pet-build'),
    [string]$Destination = 'E:\ProcessSimulatePlugin\Process Simulate\bin',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$buildRoot = [IO.Path]::GetFullPath($Source)
$deployRoot = [IO.Path]::GetFullPath($Destination)
$relativePet = 'Agent\UI\pet\dsh-pet'
$petSource = Join-Path $buildRoot $relativePet
function Read-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '') }
    finally { $hash.Dispose(); $stream.Dispose() }
}
foreach ($required in @('TxTools.dll', "$relativePet\entry.js", "$relativePet\electron\electron.exe", "$relativePet\config.json")) {
    if (!(Test-Path -LiteralPath (Join-Path $buildRoot $required))) { throw "缺少部署文件：$required" }
}
if ($buildRoot -eq $deployRoot) { throw '源和目标目录不能相同。' }
if (!$Apply) {
    Write-Output "预览：将 TxTools.dll / PDB 与 dsh-pet 资源复制到 $deployRoot；覆盖前备份。"
    return
}
$active = @(Get-Process -Name 'Tune*','ProcessSimulate*' -ErrorAction SilentlyContinue)
if ($active.Count -gt 0) { throw '请先保存工程并关闭 Process Simulate，再部署。' }
New-Item -ItemType Directory -Path $deployRoot -Force | Out-Null
$backup = Join-Path $deployRoot ('maintenance-backup\dsh-pet-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$petDestination = Join-Path $deployRoot $relativePet
foreach ($name in @('TxTools.dll','TxTools.pdb')) {
    $current = Join-Path $deployRoot $name
    if (Test-Path -LiteralPath $current) { Copy-Item -LiteralPath $current -Destination (Join-Path $backup $name) }
}
if (Test-Path -LiteralPath $petDestination) {
    Copy-Item -LiteralPath $petDestination -Destination (Join-Path $backup 'dsh-pet') -Recurse
}
New-Item -ItemType Directory -Path (Split-Path $petDestination -Parent) -Force | Out-Null
if (!(Test-Path -LiteralPath $petDestination)) {
    Copy-Item -LiteralPath $petSource -Destination $petDestination -Recurse
} else {
    foreach ($child in Get-ChildItem -LiteralPath $petSource) {
        Copy-Item -LiteralPath $child.FullName -Destination $petDestination -Recurse -Force
    }
}
foreach ($name in @('TxTools.dll','TxTools.pdb')) {
    $file = Join-Path $buildRoot $name
    if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $deployRoot $name) -Force }
}
foreach ($name in @('TxTools.dll', "$relativePet\entry.js", "$relativePet\electron\electron.exe", "$relativePet\config.json")) {
    $expected = Read-Sha256 (Join-Path $buildRoot $name)
    $actual = Read-Sha256 (Join-Path $deployRoot $name)
    if ($expected -ne $actual) { throw "部署校验失败：$name；备份在 $backup" }
}
Write-Output "部署完成：$deployRoot"
Write-Output "备份：$backup"
