param(
    [Parameter(Mandatory = $true)][string]$SdkDirectory,
    [Parameter(Mandatory = $true)][string]$InteropDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$dotnetPath = (Get-Command dotnet).Source
$compiler = Get-ChildItem -LiteralPath (Join-Path (Split-Path $dotnetPath) 'sdk') -Directory |
    Sort-Object LastWriteTime -Descending |
    ForEach-Object { Join-Path $_.FullName 'Roslyn\bincore\csc.dll' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$compiler) { throw 'A .NET SDK with Roslyn is required.' }
$sdkDll = Join-Path $SdkDirectory 'Tecnomatix.Engineering.dll'
foreach ($required in @($sdkDll, (Join-Path $InteropDirectory 'Interop.INFITF.dll'),
    (Join-Path $InteropDirectory 'Interop.ProductStructureTypeLib.dll'))) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing build dependency: $required" }
}
$outputDir = Join-Path $PSScriptRoot ('bin\Regression_' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($outputDir) | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('mscorlib', 'System', 'System.Core', 'System.Xml', 'System.Xml.Linq',
    'System.Drawing', 'System.IO.Compression', 'System.IO.Compression.FileSystem', 'Microsoft.CSharp') |
    ForEach-Object { '/r:' + (Join-Path $framework ($_ + '.dll')) }
$interopReferences = Get-ChildItem -LiteralPath $InteropDirectory -Filter 'Interop.*.dll' |
    ForEach-Object { '/r:' + $_.FullName }
# Follow the production compile list; historical files in this directory may be
# intentionally excluded (for example CgrTemplate.cs replaced by scene records).
[xml]$mainProject = Get-Content -LiteralPath (Join-Path $projectRoot 'TxTools.csproj') -Raw
$projectNamespaces = [Xml.XmlNamespaceManager]::new($mainProject.NameTable)
$projectNamespaces.AddNamespace('msb', 'http://schemas.microsoft.com/developer/msbuild/2003')
$sources = @($mainProject.SelectNodes('//msb:Compile', $projectNamespaces) |
    Where-Object { [IO.Path]::GetDirectoryName($_.Include) -eq 'ExportByColor' -and
        [IO.Path]::GetFileName($_.Include) -notmatch 'Cmd|Form' } |
    ForEach-Object { Join-Path $projectRoot $_.Include })
if ($sources.Count -eq 0) { throw 'The main project has no ExportByColor production sources.' }
$earcut = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'ExportByColor\Vendor\EarCut') -Filter '*.cs' |
    ForEach-Object { $_.FullName }
$library = Join-Path $outputDir 'ExportByColor.dll'
$libraryArgs = @($compiler, '/nologo', '/nostdlib', '/target:library', '/platform:x64', '/langversion:8',
    '/warnaserror', "/out:$library", "/r:$sdkDll") + $references + $interopReferences + $sources + $earcut +
    (Join-Path $projectRoot 'Agent\Core\TxToolsTemp.cs')
& rtk proxy $dotnetPath @libraryArgs
if ($LASTEXITCODE -ne 0) { throw 'ExportByColor production compilation failed.' }
$executable = Join-Path $outputDir 'AppearanceRegression.exe'
$testArgs = @($compiler, '/nologo', '/nostdlib', '/target:exe', '/platform:x64', '/warnaserror',
    "/out:$executable", "/r:$sdkDll", "/r:$library") + $references + (Join-Path $PSScriptRoot 'Program.cs')
& rtk proxy $dotnetPath @testArgs
if ($LASTEXITCODE -ne 0) { throw 'Appearance regression compilation failed.' }
[IO.File]::Copy($sdkDll, (Join-Path $outputDir 'Tecnomatix.Engineering.dll'))
# The standalone test executable does not inherit PS's COM wrapper search path.
Get-ChildItem -LiteralPath $InteropDirectory -Filter 'Interop.*.dll' |
    ForEach-Object { [IO.File]::Copy($_.FullName, (Join-Path $outputDir $_.Name)) }
& rtk proxy $executable (Join-Path $outputDir 'outputs')
if ($LASTEXITCODE -ne 0) { throw 'Appearance regression failed.' }
$placementExecutable = Join-Path $outputDir 'PlacementRegression.exe'
$placementArgs = @($compiler, '/nologo', '/nostdlib', '/target:exe', '/platform:x64', '/warnaserror',
    "/out:$placementExecutable") + $references + @(
    (Join-Path $projectRoot 'ExportByColor\AppearanceGeometrySource.cs'),
    (Join-Path $projectRoot 'ExportByColor\GeometryEntityLoader.cs'),
    (Join-Path $PSScriptRoot 'PlacementSdkStubs.cs'), (Join-Path $PSScriptRoot 'PlacementRegression.cs'))
& rtk proxy $dotnetPath @placementArgs
if ($LASTEXITCODE -ne 0) { throw 'Placement regression compilation failed.' }
Push-Location -LiteralPath $outputDir
try {
    & rtk proxy ./PlacementRegression.exe
    if ($LASTEXITCODE -ne 0) { throw 'Placement regression failed.' }
} finally { Pop-Location }
$entityExecutable = Join-Path $outputDir 'EntityLevelRegression.exe'
$entityArgs = @($compiler, '/nologo', '/nostdlib', '/target:exe', '/platform:x64', '/warnaserror',
    "/out:$entityExecutable") + $references + @(
    (Join-Path $projectRoot 'ExportByColor\AppearanceGeometrySource.cs'),
    (Join-Path $projectRoot 'ExportByColor\GeometryEntityLoader.cs'),
    (Join-Path $PSScriptRoot 'PlacementSdkStubs.cs'), (Join-Path $PSScriptRoot 'EntityLevelRegression.cs'))
& rtk proxy $dotnetPath @entityArgs
if ($LASTEXITCODE -ne 0) { throw 'Entity level regression compilation failed.' }
Push-Location -LiteralPath $outputDir
try {
    & rtk proxy ./EntityLevelRegression.exe
    if ($LASTEXITCODE -ne 0) { throw 'Entity level regression failed.' }
} finally { Pop-Location }
Write-Output "Artifacts: $outputDir"
