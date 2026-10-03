# Native/Box2D — Prowl's 2D physics backend (Tier A)

Prowl's 2D physics is [Box2D-Packed](https://github.com/crustos/box2d) behind a thin C shim, `prowl_box2d`. C# talks to the
shim only; it never binds `box2d.h` directly. Box2D is **not** part of this repository: it is cloned beside it
(`../box2d`; `python3 build.py deps` does that, or pass `-DPROWL_BOX2D_DIR=...` to cmake).

```
Prowl.Runtime/Physics2D/Native/PB2.Generated.cs  P/Invoke + fixed-layout structs, GENERATED from the header (tools/ccsharp/gen_pb2.py)
Prowl.Runtime/Physics2D/SimCore2D.cs             the simulation over those bindings: step, records, event stream (also builds as C)
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
  be mirrored from C#. Every shim record is made of 4-byte fields; the generated `PB2.VerifyAbi()` checks all sizes against the
  loaded library when the world is created and refuses to run on a mismatch.
* **One crossing per frame.** `pb2_step` steps the world and returns body moves, contact events (manifold point /
  normal / impulse already resolved) and sensor events as flat arrays. Kinematic moves and teleports are queued and
  flushed in one call.
* **32 layers.** Box2D-Packed filters are 16-bit; Prowl has 32 layers and a 32x32 matrix. The matrix is evaluated inside
  the shim's custom filter (no managed callback). Layer + collider index are packed into each shape's `userData`, so
  chain / segment shapes inherit them. Sensors ignore the custom filter, so their events are matrix-filtered in the shim.
* **Edges.** `pb2_segments_create` = two-sided edges (Unity `EdgeCollider2D` semantics). `pb2_chain_create` = Box2D's
  one-sided chain with ghost vertices (smoother for characters); winding decides the solid side. Both are tested.
* **Joints.** One record, `PB2JointDef`, describes every joint type (distance, revolute, prismatic, wheel, weld, motor, filter);
  `pb2_joint_create` / `pb2_joint_apply` take it, and the `p[]` slot table in `prowl_box2d.h` says what each float means per type.
  The body that owns a joint is body B and what it connects to (a body, or the world through a hidden static ground body that
  the shim creates on first use) is body A, so a revolute angle or a prismatic translation reads as "how far has *my* body
  moved". A joint dies with either of its bodies; the managed simulation records which bodies each joint connects so it can tell the
  owning component, which then recreates the joint when both bodies exist again. Force / torque thresholds are reported through
  `pb2_step` (`PB2StepInfo::joints`) as the managed joint indices to break. The mover and pogo joints are not wrapped: they are
  character-controller helpers rather than general constraints.
* **The header is the source of the managed bindings.** Every pointer parameter is marked `PB2_IN` / `PB2_OUT` / `PB2_IN_ARR` / `PB2_OUT_ARR` (they expand to nothing),
  and `tools/ccsharp/gen_pb2.py` generates the .NET bindings and the C-build bindings from it; `gen_pb2.py runtime` writes the committed `PB2.Generated.cs` and `gen_pb2.py check` (also run by `build.py ccsharp`) fails if it is stale. Add a
  function to the header, mark its pointers, regenerate, and both flavors have it.
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
