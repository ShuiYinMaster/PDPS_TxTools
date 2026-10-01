param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'CreateFQ199OriginPoints.cs'
$output = Join-Path $env:TEMP 'CreateFQ199OriginPoints.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe "/out:$output" $source
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
if ($Apply) { & $output --apply } else { & $output }
exit $LASTEXITCODE
