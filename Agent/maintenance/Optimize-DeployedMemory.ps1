param(
    [string]$Root = 'D:\Program Files\Tecnomatix_2402\eMPower\DotNetCommands\TxTools',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
$expected = 'D:\Program Files\Tecnomatix_2402\eMPower\DotNetCommands\TxTools'
if ($rootPath -ne $expected) { throw 'Unexpected deployment root; no changes made.' }
function Read-Memory($file) {
    $raw = [IO.File]::ReadAllText($file.FullName)
    $match = [regex]::Match($raw, '(?s)\A---\r?\n(.*?)\r?\n---\r?\n(.*)\z')
    $meta = @{}
    if ($match.Success) {
        foreach ($line in ($match.Groups[1].Value -split '\r?\n')) {
            if ($line -match '^([^:]+):\s*(.*)$') { $meta[$matches[1]] = $matches[2] }
        }
    }
    [pscustomobject]@{ File=$file; Meta=$meta; Body=$match.Groups[2].Value.Trim(); Hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
$plan = [Collections.Generic.List[object]]::new()
$selected = @{}
function Archive-Candidate($item, $reason) {
    if ($selected.ContainsKey($item.File.FullName)) { return }
    $relative = $item.File.FullName.Substring($rootPath.Length + 1)
    if (-not $relative.StartsWith('memory\') -or $relative.Contains('..')) { throw 'Invalid archive target' }
    $selected[$item.File.FullName] = $true
    $plan.Add([pscustomobject]@{ RelativePath=$relative; Reason=$reason; SHA256=$item.Hash })
}
$facts = @(Get-ChildItem -LiteralPath "$rootPath\memory\facts" -Filter '*.md' | ForEach-Object { Read-Memory $_ })
foreach ($fact in $facts) {
    if ($fact.Meta.category -eq 'scene_constant') { Archive-Candidate $fact 'Scene-specific snapshot: preserve in archive, query current scene instead.' }
}
$groups = $facts | Where-Object {$_.Body} | Group-Object { [regex]::Replace($_.Body, '\s+', ' ').Trim() }
foreach ($group in $groups) {
    $ordered = @($group.Group | Sort-Object @{e={ [int]$_.Meta.used_count };Descending=$true}, @{e={$_.Meta.last_confirmed};Descending=$true}, @{e={$_.File.Name}})
    foreach ($duplicate in ($ordered | Select-Object -Skip 1)) { Archive-Candidate $duplicate 'Exact normalized fact duplicate; another copy retained.' }
}
$snippets = @(Get-ChildItem -LiteralPath "$rootPath\memory\snippets" -Filter '*.md' | ForEach-Object { Read-Memory $_ })
foreach ($snippet in $snippets) {
    if ($snippet.Meta.origin -eq 'auto-promoted' -and [int]$snippet.Meta.success_count -eq 0) {
        Archive-Candidate $snippet 'Auto-promoted without verified reuse; available in archive for review.'
    }
}
$cutoff = [DateTime]::UtcNow.AddDays(-30)
foreach ($file in (Get-ChildItem -LiteralPath "$rootPath\memory\pending" -Filter '*.md')) {
    $item = Read-Memory $file
    $last = [DateTime]::MinValue
    if ([DateTime]::TryParse($item.Meta.last_seen, [ref]$last) -and $last -lt $cutoff) {
        Archive-Candidate $item 'Pending experiment unused for over 30 days.'
    }
}
# Keep joint-specific export: its output topology differs from color-grouped export.
$integrated = "$rootPath\memory\recipes\export_devices_by_color_stl_to_catia_auto.md"
if (Test-Path -LiteralPath $integrated) {
    foreach ($name in @('export_resource_stl_to_catia','robot_split_by_colors_stl_and_color_catia')) {
        $path = "$rootPath\memory\recipes\$name.md"
        if (Test-Path -LiteralPath $path) {
            $item = Read-Memory (Get-Item -LiteralPath $path)
            if ([int]$item.Meta.run_count -eq 0) { Archive-Candidate $item 'Unrun overlapping recipe; integrated color export/import recipe retained. Restore if joint-specific output is needed.' }
        }
    }
}
$plan | Group-Object Reason | Select-Object Count,Name | Format-Table -Wrap
Write-Output "Candidate files: $($plan.Count). Conversations, knowledge, corrected gotchas and legacy recipes.json are retained."
if (-not $Apply) { Write-Output 'Dry run only. Use -Apply after Process Simulate is closed.'; exit }
if (Get-Process -Name Tune -ErrorAction SilentlyContinue) { throw 'Close Process Simulate before applying: live caches could overwrite cleanup.' }
$backup = Join-Path $rootPath ('maintenance-backup\' + [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fff'))
[IO.Directory]::CreateDirectory($backup) | Out-Null
$plan | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'manifest.json') -Encoding UTF8
# Snapshot before mutation. No keys, binaries, or huge vector cache are copied here.
foreach ($name in @('prefs.json','recipes.json')) {
    $source = Join-Path $rootPath $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $backup $name) }
}
foreach ($entry in $plan) {
    $source = [IO.Path]::GetFullPath((Join-Path $rootPath $entry.RelativePath))
    $dest = [IO.Path]::GetFullPath((Join-Path $backup $entry.RelativePath))
    if (-not $source.StartsWith($rootPath + '\memory\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $dest.StartsWith($backup + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Path boundary violation' }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.SHA256) { throw "Source changed: $source" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $dest
    if ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ne $entry.SHA256) { throw 'Backup verification failed' }
}
foreach ($entry in $plan) {
    $source = [IO.Path]::GetFullPath((Join-Path $rootPath $entry.RelativePath))
    if (-not $source.StartsWith($rootPath + '\memory\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid removal path' }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.SHA256) { throw 'Source changed after snapshot' }
    Remove-Item -LiteralPath $source # Single verified file; recoverable copy in manifest location.
}
$prefsPath = Join-Path $rootPath 'prefs.json'
$prefs = Get-Content -LiteralPath $prefsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$prefs | Add-Member -NotePropertyName ReasoningEffort -NotePropertyValue 'low' -Force
$temp = $prefsPath + '.maintenance.tmp'
[IO.File]::WriteAllText($temp, ($prefs | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
[IO.File]::Replace($temp, $prefsPath, (Join-Path $backup 'prefs.previous.json'))
Write-Output "Applied. Recoverable archive and manifest: $backup"
