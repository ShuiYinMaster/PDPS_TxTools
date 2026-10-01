param(
    [switch]$Apply,
    [switch]$Toggle,
    [switch]$ToggleTop,
    [switch]$SetRequestedToggleResult,
    [switch]$InvertNestedResult,
    [switch]$ProbeNestedStates,
    [switch]$TestShowOne,
    [switch]$InvertMatchedResult,
    [switch]$RestoreProductOnlyHidden,
    [string]$Target = 'DPUB-501038693-XWS_01',
    [int]$MaxDepth = 100
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'HideGraphicsSetsForHiddenParts.cs'
$output = Join-Path $env:TEMP 'HideGraphicsSetsForHiddenParts.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $source)) {
    throw "Source file not found: $source"
}
if (-not (Test-Path -LiteralPath $csc)) {
    throw "C# compiler not found: $csc"
}

& $csc /nologo /target:exe "/out:$output" $source
if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE."
}

$arguments = @("--max-depth=$MaxDepth")
$arguments += "--target=$Target"
if ($Apply) {
    $arguments += '--apply'
}
if ($Toggle) {
    $arguments += '--toggle'
}
if ($ToggleTop) {
    $arguments += '--toggle-top'
}
if ($SetRequestedToggleResult) {
    $arguments += '--set-requested-toggle-result'
}
if ($InvertNestedResult) {
    $arguments += '--invert-nested-result'
}
if ($ProbeNestedStates) {
    $arguments += '--probe-nested-states'
}
if ($TestShowOne) {
    $arguments += '--test-show-one'
}
if ($InvertMatchedResult) {
    $arguments += '--invert-matched-result'
}
if ($RestoreProductOnlyHidden) {
    $arguments += '--restore-product-only-hidden'
}

& $output @arguments
exit $LASTEXITCODE
