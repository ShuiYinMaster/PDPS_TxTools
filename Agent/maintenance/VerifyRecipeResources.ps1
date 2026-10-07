param([Parameter(Mandatory = $true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
$assemblyFile = [IO.Path]::GetFullPath($AssemblyPath)
# Loading bytes permits inspection without holding a lock on the deployed DLL.
# Only manifest resources are read; no plugin or engineering code is invoked.
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($assemblyFile))
$resourceNames = $assembly.GetManifestResourceNames()
$groups = @(
    @{ Folder = (Join-Path $PSScriptRoot '..\recipes\defaults'); Prefix = 'TxTools.Agent.DefaultRecipes.' },
    @{ Folder = (Join-Path $PSScriptRoot 'recipe-migrations'); Prefix = 'TxTools.Agent.LegacyRecipes.' }
)
$checked = 0
foreach ($group in $groups) {
    foreach ($file in Get-ChildItem -LiteralPath $group.Folder -Filter '*.md' -File) {
        $name = $group.Prefix + $file.Name
        if ($resourceNames -cnotcontains $name) { throw "Missing recipe resource: $name. Reload the project in Visual Studio and rebuild." }
        $reader = [IO.StreamReader]::new($assembly.GetManifestResourceStream($name), [Text.Encoding]::UTF8, $true)
        try { $embedded = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $expected = [IO.File]::ReadAllText($file.FullName)
        if ($embedded.Replace("`r`n", "`n") -cne $expected.Replace("`r`n", "`n")) { throw "Stale recipe resource: $name. Reload the project in Visual Studio and rebuild." }
        $checked++
    }
}
Write-Output "PASS: $checked recipe resources match current source in $assemblyFile"
