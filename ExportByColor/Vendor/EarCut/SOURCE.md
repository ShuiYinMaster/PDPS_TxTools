# Vendored EarCut

Source: https://github.com/MadWorldNL/EarCut

Pinned commit: `b32c264a59cf8a294920ed63061425cc99c66446`.
MIT license is preserved in LICENSE. EarCutDouble.cs, NodeDouble.cs and
DoubleExtensions.cs were adapted to .NET Framework 4.8 / C# 7.3: explicit
imports, private namespace, nullable syntax removed, ordinary list construction,
and ordinary null assignment. The triangulation algorithm is unchanged.

This source is compiled into TxTools. No runtime package or Python is required.
The caller independently checks oriented area, manifold edges and the complete
cap boundary, restoring collinear boundary points omitted by triangulation.
