param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$SdkDirectory,
    [string]$LocalRoot,
    [string]$MSBuildPath
)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
if (!$LocalRoot) { $LocalRoot = Join-Path $sourceRoot '..\TxTools.Local' }
$localDirectory = [IO.Path]::GetFullPath($LocalRoot)
if (!$SdkDirectory) { $SdkDirectory = Join-Path $sourceRoot '..\2402dll' }
if (!(Test-Path -LiteralPath (Join-Path $SdkDirectory 'Tecnomatix.Engineering.dll'))) {
    throw 'Set -SdkDirectory to a Process Simulate SDK directory.'
}
if (!$MSBuildPath) {
    $installed = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($installed) { $MSBuildPath = $installed.Source }
    else { $MSBuildPath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe' }
}
if (!(Test-Path -LiteralPath $MSBuildPath)) { throw 'MSBuild was not found; pass -MSBuildPath.' }
$logDirectory = Join-Path $localDirectory 'build\logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logFile = Join-Path $logDirectory ("TxTools-$Configuration-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
$buildArguments = @(
    (Join-Path $sourceRoot 'TxTools.csproj'), '/t:Build', '/nologo', '/nr:false',
    "/p:Configuration=$Configuration", "/p:ProcessSimulateDllDir=$SdkDirectory",
    "/p:TxToolsLocalRoot=$($localDirectory.TrimEnd('\'))/", '/v:minimal', '/clp:Summary',
    "/flp:LogFile=$logFile;Verbosity=normal"
)
& rtk proxy $MSBuildPath @buildArguments
if ($LASTEXITCODE -ne 0) { throw "Build failed. Log: $logFile" }
Write-Output "Output: $(Join-Path $localDirectory "build\bin\TxTools\$Configuration")"
Write-Output "Log: $logFile"
