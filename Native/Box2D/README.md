# Native/Box2D — Prowl's 2D physics backend (Tier A)

Prowl's 2D physics is [Box2D-Packed](https://github.com/crustos/box2d) behind a thin C shim, `prowl_box2d`. C# talks to the
shim only; it never binds `box2d.h` directly. Box2D is **not** part of this repository: it is cloned beside it
(`../box2d`; `python3 build.py deps` does that, or pass `-DPROWL_BOX2D_DIR=...` to cmake).

```
Prowl.Runtime/Physics2D/Native/PB2.cs          raw P/Invoke + fixed-layout structs   (no Prowl deps)
Prowl.Runtime/Physics2D/Native/Box2DWorld.cs   managed wrapper: ABI check, batching  (no Prowl deps)
Native/Box2D/prowl_box2d.{h,c}                 the shim
Native/Box2D/Tests/Box2DSmoke                  standalone tests + benchmark (no NuGet packages needed)
```

## Build

```sh
python3 build.py deps          # clone ../box2d
python3 build.py native        # -> Build/Native/<rid>/<variant>/ and Libraries/<rid>/native/   (add --avx2 for the AVX2 variant)
```

or by hand:

```sh
cmake -S Native/Box2D -B build/box2d -DCMAKE_BUILD_TYPE=Release [-DPROWL_B2_AVX2=ON] [-DPROWL_BOX2D_DIR=/path/to/box2d]
cmake --build build/box2d
```

Output is one shared library (`libprowl_box2d.so` / `prowl_box2d.dll` / `libprowl_box2d.dylib`) with Box2D linked in
statically; only the `pb2_*` symbols are exported. It is copied to `Libraries/<rid>/native/` like the other native libs, and is
git-ignored: it is built, not committed.

## Test

```sh
python3 build.py test          # builds the native library, then runs everything below
```

Or run the harness directly: `cd Native/Box2D/Tests/Box2DSmoke && dotnet build -c Release && dotnet bin/Release/net10.0/Box2DSmoke.dll`
(`--no-bench`, `--micro`). It checks the shim and bindings, the engine-independent core, and the real `Rigidbody2D` /
`Collider2D` / `PhysicsWorld2D` files compiled unmodified against a small headless engine (`EngineStubs/MiniEngine.cs`), including
gizmo geometry against real Box2D shapes and an inspector-metadata lint. `Prowl.Runtime` itself needs NuGet to build, so this
harness is how the 2D layer is exercised without it.

## Design decisions (and the measurements behind them)

* **Why a shim.** The fork's `*Def` structs use `bool x : 1` bitfields; their layout is compiler-defined, so they can't
  be mirrored from C#. Every shim record is made of 4-byte fields; `Box2DWorld.Create` checks all sizes against the
  loaded library and refuses to run on a mismatch.
* **One crossing per frame.** `pb2_step` steps the world and returns body moves, contact events (manifold point /
  normal / impulse already resolved) and sensor events as flat arrays. Kinematic moves and teleports are queued and
  flushed in one call.
* **32 layers.** Box2D-Packed filters are 16-bit; Prowl has 32 layers and a 32x32 matrix. The matrix is evaluated inside
  the shim's custom filter (no managed callback). Layer + collider index are packed into each shape's `userData`, so
  chain / segment shapes inherit them. Sensors ignore the custom filter, so their events are matrix-filtered in the shim.
* **Edges.** `pb2_segments_create` = two-sided edges (Unity `EdgeCollider2D` semantics). `pb2_chain_create` = Box2D's
  one-sided chain with ghost vertices (smoother for characters); winding decides the solid side. Both are tested.
* **Limits inherited from the fork:** single world per process; 16-bit handles => at most 65,535 bodies/shapes.

### Measured (single 2.1 GHz Xeon core, .NET 10, pyramid of boxes, all awake)

| bodies | native step (SSE2) | native step (AVX2) | read transforms, batched | read transforms, per-body |
|-------:|-------:|-------:|-------:|-------:|
|    820 | 0.12 ms | 0.09 ms | 0.004 ms | 0.030 ms |
|  5,050 | 2.86 ms | 2.26 ms | 0.070 ms | 0.208 ms |
|  9,870 | 6.28 ms | 4.94 ms | 0.123 ms | 0.399 ms |

* A managed->native call costs ~6 ns. The boundary is not the bottleneck: the solver is. Batching is still ~3x cheaper
  than per-body reads, and costs ~1-2% of the step.
* `SuppressGCTransition` made no measurable difference (6.0 vs 6.8 ns, then 7.0 vs 5.4 — noise) and was rejected.
* AVX2 is ~21% faster than SSE2 at 10k bodies. Needs a runtime CPU check to pick the library (not wired yet).
* No Jitter2 number: NuGet is unreachable from the build sandbox, so Jitter could not be run. Measure on your machine.
