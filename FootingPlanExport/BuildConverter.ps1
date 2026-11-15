param([string]$LocalRoot, [string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
if (!$LocalRoot) { $LocalRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..\TxTools.Local')) }
$buildRoot = Join-Path $LocalRoot 'build\footing-plan-converter'
$runtimeRoot = Join-Path $LocalRoot 'dependencies\footing-plan-export'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
& rtk proxy $Python -m PyInstaller --noconfirm --onedir --console --name JtToDxf --distpath (Join-Path $buildRoot 'dist') --workpath (Join-Path $buildRoot 'work') --specpath $buildRoot --paths (Join-Path $PSScriptRoot 'Converter') --exclude-module matplotlib --exclude-module scipy --exclude-module pandas --exclude-module tkinter --exclude-module PyQt5 --exclude-module PySide6 (Join-Path $PSScriptRoot 'Converter\convert.py')
if ($LASTEXITCODE -ne 0) { throw 'Converter build failed.' }
$destination = Join-Path $runtimeRoot 'converter'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $buildRoot 'dist\JtToDxf') | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force }
Write-Output "Converter: $destination"
