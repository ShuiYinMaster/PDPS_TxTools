# R45: JT to line/face CGR

The exporter adds two explicit JT modes: continuous line/face compression and planar line/face compatibility. The existing JT mode continues to use compact CGR. A detached backend value travels with each conversion task; workers do not change process-wide backend settings. Resource snapshots remain on PS and CATIA insertion remains on its STA coordinator.

Line/face grouping now includes opacity. Face appearance records retain JT alpha, including zero and partial alpha. JT conversion preserves source winding, does not invent reverse triangles, and skips coordinate welding before generating feature adjacency. Coincident sheets with independent JT indices remain independent. Existing PS line/face welding behavior is unchanged. Legacy and non-source-winding RGBA remain rejected; the opaque CFV3/3DXML adapter is unchanged.

These are mesh-derived selectable face domains and boundary polylines. They do not recover analytic CAD surfaces or exact original B-rep face identities. Source indices and chunk/material boundaries can create additional selectable domains and lines. In particular JT8 strips may have duplicated coordinate vertices. This conservative behavior increases size; it does not assert optimal compression or native analytic measurement.

Validation on the formal Release DLL (2026-10-10):

- All seven T13J fixtures: continuous line/face CGR, 5,160,976 triangles, complete XYZ/oriented triangle/RGBA multiset equality. All seven inserted into CATIA and saved in separate test products.
- T1E24MY-9156 transparent JT10.6 fixture: continuous and planar output, 654,789 triangles, all alpha groups retained and full geometry/color roundtrip equality.
- T13J-5156 JT10.0: planar output, 928,384 triangles, full geometry/color equality and successful CATIA insertion.
- Synthetic tests for both modes: alpha 0/128/255 separation, nonzero slender triangle, two coincident independently indexed sheets retain two domains and eight boundary edges.
- CATIA alpha probe: opaque red foreground remains red; alpha128 red foreground over opaque blue becomes purple in both feature modes.
- Existing compression self-tests: 105 oriented packet cases, six edge-chain cases, three domain-index permutations, axis normals passed.

Direct tests do not use PS geometry. Interactive face/edge picking and measuring on representative production assemblies still need user acceptance; successful insertion and byte-level geometry verification do not establish every measurement workflow.

Tests use C# and .NET Framework; no Python runtime is needed. Compile FeatureRegression.cs with FeatureTestDecoder.cs, or FeatureTopologyRegression.cs with FeatureTestDecoder.cs, referencing the production TxTools.dll. FeatureTestDecoder is a test-only adaptation of the existing feature decoder; the production opaque 3DXML bridge is not relaxed. FeatureBatch runs seven fixtures through two bounded regression processes.

```powershell
rtk .\FeatureRegression.exe sample.jt new-output-directory production-bin LineFace
rtk .\FeatureRegression.exe sample.jt new-output-directory production-bin LineFacePlanar
rtk .\FeatureTopologyRegression.exe production-bin new-output-directory
rtk .\FeatureBatch.exe fixture-parent new-output-directory production-bin
```

The test runner reads per-domain RGBA and compares every decoded triangle against the JT worker mesh. Customer JT files, generated CGRs, screenshots and binary outputs are excluded from Git.
