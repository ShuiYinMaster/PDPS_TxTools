<##
.SYNOPSIS
    Read the active CATIA V5 Product tree and count part instances.

.DESCRIPTION
    This wrapper compiles and runs ReadCatiaTreeCount.cs. The C# utility uses
    the same late-bound reflection calls as TxAgent's CatiaBridge and
    CatiaTreeReader, including InvokeMethod(Item, i) for CATIA collections.

    The default summary counts PartNumber prefixes FQ114, Q114 and FQ199,
    and prints their combined total. The operation is read-only.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\read_catia_tree_count.ps1

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\read_catia_tree_count.ps1 -ShowTree
##>

[CmdletBinding()]
param(
    [int]$MaxDepth = 100,
    [switch]$ShowTree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'ReadCatiaTreeCount.cs'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Missing source file: $source"
}

$compiler = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($compiler)) {
    throw 'No .NET Framework C# compiler (csc.exe) was found.'
}

$tempExe = Join-Path ([System.IO.Path]::GetTempPath()) 'TxTools.ReadCatiaTreeCount.exe'
$sourceInfo = Get-Item -LiteralPath $source
$needsBuild = -not (Test-Path -LiteralPath $tempExe -PathType Leaf)
if (-not $needsBuild) {
    $needsBuild = (Get-Item -LiteralPath $tempExe).LastWriteTimeUtc -lt $sourceInfo.LastWriteTimeUtc
}

if ($needsBuild) {
    & $compiler /nologo /target:exe ("/out:{0}" -f $tempExe) $source
    if ($LASTEXITCODE -ne 0) {
        throw "C# compilation failed with exit code $LASTEXITCODE."
    }
}

$arguments = @('--max-depth={0}' -f $MaxDepth)
if ($ShowTree) { $arguments += '--show-tree' }

& $tempExe @arguments
exit $LASTEXITCODE
