# Detached CGR regression runner

This .NET 8 runner links the production encoder without loading Process Simulate
or CATIA. JSON input contains `vertices`, `faces`, optional per-face `colors`
(RGB bytes), and optional per-face `surfaces` (source group IDs).

```powershell
rtk dotnet build Tests/CgrLineFace/CgrLineFace.csproj -c Release
rtk dotnet Tests/CgrLineFace/bin/Release/net8.0/CgrLineFace.dll --self-test
rtk dotnet Tests/CgrLineFace/bin/Release/net8.0/CgrLineFace.dll model.json output.cgr features
```

Modes: `compact` (original backend), `features` (continuous mesh domains and
polyline boundaries), `features-planar` (R35 face partition with improved native
encoding). An optional fourth argument sets the face limit per block. The optional
fifth argument `cfv3` is intended only for `compact` output. Output files must not
already exist.

`--self-test` checks 105 independent oriented triangle-packet roundtrips, count and
index width boundaries, duplicate/degenerate indices, six edge-chain cases, and
axis normal encoding, plus three face-index permutations around the 255-domain
threshold. It verifies packet size never exceeds the corresponding
triangle list and that chaining retains segment multiplicity and incident faces.

To replay archived encoder sources, build with `-p:EncoderDir=<absolute path>`
and a separate `-o <runner directory>`. Use `compact`/`features` as supported by
that version; compression self-tests require R36 methods.

CATIA measurement and display acceptance is a separate check; a successful binary roundtrip does not replace host validation.
