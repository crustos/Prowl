# Prowl.Runtime/Destruction2D: the shared core of destructible terrain and shattering sprites

Plain C# in the subset that CCSharp translates to C, with no dependency on the engine, the scene or the native library. **One copy, compiled by both runtimes**:
`Prowl.Runtime` (the .NET engine) and `Prowl.Core2D` (the 2D runtime that translates to C) link the same files, as they do the physics core in
`Prowl.Runtime/Physics2D`. Each runtime has its own thin layer over it (a terrain component and a shattering component) that talks to its scene and physics.
Every type is `internal`, as the physics core's are.

## PixelTerrain: a bitmap you can dig and build

Ported from [DTerrain](https://github.com/crustos/DTerrain) (Dominik Zimny, MIT). A terrain is a bitmap cut into chunks. Each chunk keeps its pixels as
run-length columns (a list of ranges of rows that are ground, per column), which is what makes digging and building cheap and what the collider shapes
are made from:

| | |
|---|---|
| `PixelColumn`, `PixelRange` | one pixel column as ranges of ground rows; `ClearRows`, `SumRange`, `Covers`, `Touches`, `isWithin` |
| `StampShape` | the pixels a circle or a rectangle stamps, as ranges |
| `PixelRectMerge`, `PixelQuadTree` | the ground as rectangles (the box colliders): merged runs, or a quadtree |
| `PixelChainTrace` | the ground's outline as one-sided chains, ground on the left of the way |
| `PixelChunk` | a chunk's columns, its native body and shapes, and the chunks beside it (data; the terrain layer works on it) |
| `PixelRect`, `PixelMath`, `PixelTerrainLimits` | small things |

Differences from DTerrain: the edit operations are rewritten (its `DelRange` removed too much when a range ended on the cut's border, `SingleDelRange`
lost a one-row remainder, and `SumRange` could leave overlapping ranges), and a circle's stamp is no longer one pixel off (`StampShape.OffsetX`).
`tools/ccsharp/conformance/PixelTerrainConformance.cs` checks all of it against a bitmap.

## Shatter: break a convex polygon into fragments

Ported from [Unity-2D-Destruction](https://github.com/crustos/Unity-2D-Destruction) (Matthew Holtzem, MIT). `Fracturer` breaks a convex polygon at its own
vertices and any number of random extra points, into the Voronoi cells of those points or their Delaunay triangles, and again for each sub-shatter step.
Where the original uses a port of Fortune's sweep (about 2,100 lines) and the Clipper library (about 4,800), this cuts the polygon by one half-plane per
other site, and flips a fan triangulation until it is Delaunay: the same pieces, checked against the original's own library on the same sites.

| | |
|---|---|
| `Fracturer` | `Shatter(poly, mode, extraPoints, subshatterSteps)`; the pieces are in `Pieces`, convex, counter-clockwise, at most `MaxVertices` (8) points |
| `PolySet` | polygons as flat lists |
| `ExplodeOptions` | what the original's Explodable component holds |

Pieces never have more than a native polygon shape can (8 points): a bigger one is several. A concave outline is refused.
`tools/ccsharp/conformance/ShatterConformance.cs` checks the pieces tile the polygon, are convex, are Delaunay / Voronoi, and are reproducible from a seed.

## Names

The types were renamed from the originals' (and from Stride2D's copies of them) so that they cannot be mistaken for anything in an engine that imports
`System`, and cannot clash with the 3D terrain's own `TerrainChunk`:

| original | here |
|---|---|
| `Range`, `Column`, `Shape`, `Calc` | `PixelRange`, `PixelColumn`, `StampShape`, `PixelMath` |
| `ColumnQuadTree`, `RectMerge`, `ChainTrace`, `ChainSet` | `PixelQuadTree`, `PixelRectMerge`, `PixelChainTrace`, `PixelChainSet` |
| `TerrainChunk`, `TerrainLimits` | `PixelChunk`, `PixelTerrainLimits` |

## Tests

`python3 build.py ccsharp` translates both conformance programs to C, builds them, and requires the same output as on .NET.
