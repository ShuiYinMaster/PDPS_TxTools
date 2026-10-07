$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$dotnetPath = (Get-Command dotnet).Source
$sdkRoot = Join-Path (Split-Path $dotnetPath) 'sdk'
$compiler = Get-ChildItem -LiteralPath $sdkRoot -Directory |
    Sort-Object LastWriteTime -Descending |
    ForEach-Object { Join-Path $_.FullName 'Roslyn\bincore\csc.dll' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$compiler) { throw 'A .NET SDK with Roslyn is required.' }
$frameworkRefs = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$outputDir = Join-Path $PSScriptRoot 'bin\Regression'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$outputExe = Join-Path $outputDir 'UndoSafetyRegression.exe'
$compilerArgs = @(
    $compiler, '/nologo', '/langversion:7.3', '/warnaserror', '/target:exe', '/nostdlib',
    "/out:$outputExe", "/reference:$frameworkRefs\mscorlib.dll", "/reference:$frameworkRefs\System.dll",
    "/reference:$frameworkRefs\System.Core.dll", "/reference:$frameworkRefs\Microsoft.CSharp.dll",
    (Join-Path $projectRoot 'SRC\SceneUndoScope.cs'), (Join-Path $projectRoot 'SRC\SceneCreationBatch.cs'),
    (Join-Path $projectRoot 'SRC\StudySnapshot.cs'), (Join-Path $projectRoot 'CrossEnvIO\Core\CojtTransfer.cs'),
    (Join-Path $PSScriptRoot 'SdkStubs.cs'), (Join-Path $PSScriptRoot 'Program.cs')
)
& $dotnetPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Undo safety regression compilation failed.' }
& $outputExe
if ($LASTEXITCODE -ne 0) { throw 'Undo safety regression failed.' }
