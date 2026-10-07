param([string]$PluginRoot, [string]$SourcePath)
$ErrorActionPreference = 'Stop'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $PluginRoot 'Newtonsoft.Json.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PluginRoot 'TxTools.dll'))
$type = $assembly.GetType('TxTools.Agent.Core.RecipeUiActions', $true)
$map = $type.GetField('ObjectTypeLabels', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
$source = Get-Content -LiteralPath $SourcePath -Raw -Encoding UTF8
$entries = [regex]::Matches($source, '\["(Tx[^"\r\n]+)"\] = "([^"\r\n]+)"')
if ($map.Count -ne $entries.Count) { throw '实际 DLL 中文映射数量与源码不同。' }
foreach ($entry in $entries) {
    if ($map[$entry.Groups[1].Value] -ne $entry.Groups[2].Value) { throw ('实际 DLL 中文映射未更新：' + $entry.Groups[1].Value) }
}
if (@($map.Values | Select-Object -Unique).Count -ne $map.Count) { throw '中文映射存在重复标签。' }
if ($map['TxWeldPoint'] -eq $map['TxWeldLocationOperation'] -or $map['TxCompoundPart'] -eq $map['TxCompoundResource']) { throw '不同类型共用模糊中文名称。' }
Write-Output ('PASS: actual .NET Framework DLL contains all ' + $map.Count + ' current Chinese mappings without duplicate labels')
Write-Output 'PASS: weld features, weld locations, compound parts and compound resources have distinct labels'
