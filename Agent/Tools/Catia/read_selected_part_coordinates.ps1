$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'ReadSelectedPartCoordinates.cs'
$output = Join-Path $env:TEMP 'ReadSelectedPartCoordinates.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe "/out:$output" $source
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
& $output
exit $LASTEXITCODE
