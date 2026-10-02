# Prowl

![Github top languages](https://img.shields.io/github/languages/top/crustos/Prowl)
[![GitHub license](https://img.shields.io/github/license/crustos/Prowl?style=flat-square)](LICENSE)

**A Unity-like game engine, now a 2D engine, on [Box2D-Packed](https://github.com/crustos/box2d), whose core is being rewritten in a C# subset that translates to C.**

Prowl is an MIT-licensed engine written in C# on .NET 10, with a GameObject/Component model, a full editor, and a Unity-shaped scripting API. This
fork changes two things about it:

1. **The physics is 2D, and it is Box2D.** `Rigidbody2D`, `Collider2D`, triggers, layers and scene queries run on Box2D-Packed (Box2D v3 with a
   cache-friendly handle layout) through a thin C shim. The old 3D physics (Jitter) is kept but switched off, and nobody maintains it: see [3D.md](3D.md).
2. **The core is moving to C.** Not by hand. The core is being rewritten in a *subset* of C# that a compiler ([CCSharp](https://github.com/crustos/CCSharp))
   translates to C, so the same source runs on .NET in the editor and as plain C in a shipped game, right next to Box2D.

The second point is a direction, and most of it is not built yet. This page separates what exists, what has been proven, and what is still a plan.

> **Status in one paragraph.** 2D physics works and is tested (261 checks against the real native library, plus the real components running in a headless
> engine). About 340 lines of the engine, the geometry, outline and pose math, have been translated to C and produce output identical to the same C# on .NET.
> The rest of the core does not translate yet; the compiler says exactly why (see [The core rewrite](#the-core-rewrite-c-to-c)). **A full `dotnet build` of the
> whole solution has not been run since these changes**, because the machine they were developed on could not reach NuGet; do that first (see [Building](#building)).

1. [What works today](#what-works-today)
2. [How the 2D physics is built](#how-the-2d-physics-is-built)
3. [Box2D-Packed](#box2d-packed)
4. [The core rewrite: C# to C](#the-core-rewrite-c-to-c)
5. [Building](#building)
6. [Repository map](#repository-map)
7. [Roadmap](#roadmap)
8. [Contributing, acknowledgments, license](#contributing)

---

## What works today

**2D physics** (components live in `Prowl.Runtime/Components/Physics2D`):

- **Bodies.** `Rigidbody2D` with Static / Kinematic / Dynamic types, mass, gravity scale, damping, freeze constraints (position X/Y, rotation), continuous
  detection for fast bodies, sleeping, and interpolation between fixed steps. Angles are in degrees. Force, acceleration, impulse and velocity-change modes.
  A kinematic body moved with `MovePosition` carries what rides on it.
- **Colliders.** Circle, Box (with rounded corners), Capsule, convex Polygon (up to 8 points, Box2D's limit) and Edge (two-sided, open or looped). A
  collider on a child GameObject belongs to the nearest rigidbody above it; one with no rigidbody is static level geometry. Lossy scale, offsets and
  rotations are folded into the shape.
- **Events.** `OnCollisionBegin2D` / `OnCollisionEnd2D` with contact point, normal and impulse, and `OnTriggerEnter2D` / `Stay2D` / `Exit2D`. The normal
  points from the other collider toward you, so `collision.Normal.Y > 0.5f` is a ground check.
- **Layers.** All 32 layers and the existing 32x32 collision matrix. The matrix is evaluated inside the native filter, so a layer pair costs no managed
  callback.
- **Queries.** `Raycast`, `RaycastAll`, `OverlapPoint`, `OverlapCircle`, `OverlapBox`, with a `QueryFilter2D` (layer mask, ignore a collider or a whole
  rigidbody, exclude triggers).
- **Editor.** Collider gizmos that draw the shape the physics actually builds (green, yellow for triggers, red when a shape is invalid), vertex handles
  and a link to the owning rigidbody when selected; inspector tooltips, ranges and `ShowIf`; a 2D section in Project Settings (gravity, sub-steps, worker
  threads); *GameObject > 2D Physics* menu items.

```csharp
// using Prowl.Runtime; using Prowl.Vector;
public class Player : MonoBehaviour
{
    private Rigidbody2D body = null!;
    private Collider2D feet = null!;

    public override void OnEnable()
    {
        body = GetComponent<Rigidbody2D>()!;
        feet = GetComponent<Collider2D>()!;
    }

    public void Jump()
    {
        // ask the scene's 2D world whether there is ground just below, ignoring ourselves
        var world = GameObject.Scene.Physics2D;
        var filter = QueryFilter2D.Default.Ignoring(body).Ignoring(feet);
        if (world.Raycast(body.Position, new Float2(0, -1), out RaycastHit2D hit, 0.6f, filter))
            body.AddForce(new Float2(0, 6), ForceMode.Impulse);
    }

    public override void OnCollisionBegin2D(Collision2D collision)
    {
        if (collision.Normal.Y > 0.5f) { /* landed */ }
    }
}
```

**Not there yet:** joints (Box2D has them; they are not wrapped), one-sided chains in the components (the shim has them), extrapolation (it falls back to
no interpolation), a tilemap, and a 2D sample scene (the existing samples are 3D). Particles collide with planes but not with the 2D world.

**Limits you should know about.** Box2D-Packed has one world per process, so only one scene at a time can have live 2D bodies; a preview scene beside
the running game gets inert components and one logged error rather than corrupting the live world. Handles are 16-bit, which caps a world at 65,535 bodies
and 65,535 shapes. A collider that is destroyed or disabled while overlapping a trigger sends no Exit.

The rest of the engine is unchanged Prowl: the editor (scene view, hierarchy, inspector, undo/redo, docking, hot-reloading scripts, playtest in the
editor), `SpriteRenderer` with sprite-sheet slicing and a sprite editor, GameObject-based UI, particles, audio via MiniAudio, an input action system,
prefabs with nested-prefab support, a build system for standalone apps, and `Prowl.Runtime` usable on its own without the editor. The 3D renderer is
described in [3D.md](3D.md); it is still there and it is not behind the 3D-physics switch.

---

## How the 2D physics is built

```
  Box2D-Packed                C      ../box2d, built into the shim below
        ^   linked statically
  prowl_box2d                 C      Native/Box2D/prowl_box2d.c     the only thing C# talks to
        ^   P/Invoke, one crossing per frame
  PB2 + Box2DWorld            C#     Prowl.Runtime/Physics2D/Native     checks the ABI when it starts
        ^
  PhysicsSimulation2D         C#     Prowl.Runtime/Physics2D            ownership, registries, events, queries
        ^
  PhysicsWorld2D, Rigidbody2D, Collider2D ...   C#   engine glue: Transforms, interpolation, gizmos, inspector
```

**The shim exists because Box2D-Packed's definition structs use `bool x : 1` bitfields.** Their layout is up to the compiler, so C# cannot mirror
them safely. Every record the shim exposes is made of 4-byte fields, and `Box2DWorld.Create` compares all their sizes against the loaded library and
refuses to run on a mismatch, rather than corrupting memory.

**One call per frame.** `pb2_step` steps the world and returns every body that moved, every contact begin and end (with the manifold's point, normal and
impulse already worked out), and every sensor overlap, as flat arrays. A kinematic move or a teleport is queued and flushed in one call. Box2D identifies
bodies and colliders to the shim by small integers carried in `userData`, so an event turns back into a managed object with one array read.

**The layer matrix lives in the native filter.** Box2D-Packed filters are 16 bits; Prowl has 32 layers. The shim packs the layer into each shape's
`userData` and evaluates the 32x32 matrix in a custom filter callback, which also makes chain and segment shapes inherit it. Sensors ignore that
filter, so the shim applies the matrix to sensor events itself.

**Sleeping bodies are woken when something changes under them.** Box2D does not wake a sleeping body when a static collider is moved into or out from
under it, or when its motion locks change, so a platform moved through its Transform would leave bodies hanging in the air. The shim wakes bodies
overlapping the old and new bounds of a moved static body, and on a flag change.

**Gizmos and the physics share one geometry path.** Gizmos run in the editor with nothing playing, so they cannot ask the physics world. Each collider
resolves its dimensions in one place used by both the shape builder and the gizmo, and the polygon gizmo uses a line-for-line port of Box2D's own hull
code, so it agrees with Box2D about which point sets are valid at all. That agreement is tested over 4,200 point sets, including near-degenerate ones,
with zero disagreements.

### How it was tested without building the whole engine

`Prowl.Runtime` needs NuGet to build, so the 2D layer is exercised by a harness that does not (`Native/Box2D/Tests/Box2DSmoke`, 261 checks):

- the native shim and its bindings, against the real library;
- the engine-independent core (pose interpolation, the slot registry, ownership handoff between scenes, event ordering, handlers that destroy objects in
  the middle of dispatch, queries);
- the **real** `Rigidbody2D`, `Collider2D` and `PhysicsWorld2D` source files, compiled unmodified against a small headless engine (`EngineStubs/MiniEngine.cs`
  mirrors the engine's real namespaces, because an earlier version that did not hid a genuine compile error);
- gizmo geometry against real Box2D shapes: every drawn segment must touch the shape and the space just outside it must be empty;
- an inspector-metadata lint that uses the same member lookup the editor's attribute handlers use.

The harness is a stand-in, and it can only be as faithful as its stubs. It does not replace building the real solution.

---

## Box2D-Packed

[Box2D-Packed](https://github.com/crustos/box2d) is a fork of Box2D v3 (Erin Catto's, MIT) built for this kind of use:

- **4-byte handles.** Dropping the world index and shrinking `index1` to 16 bits makes `b2ShapeId` 4 bytes instead of 8, so a handle fits a register and an
  array of them packs twice as densely. The price is the 65,535 limit above, and a single world.
- **Bit-packed definition structs**, which is what forces the shim.
- **Intrusive execution.** `box2d_pack.py` can compile *game code written in C* into the physics engine at marked points (for example, when a contact begins),
  so game logic runs where the engine produced the event instead of reading an event array afterwards. The fork's [paper](https://doi.org/10.5281/zenodo.23002562)
  makes the case for this; **this repository has not measured it.** It is the reason the core is being moved to C, and the reason that is more than
  an exercise.

What this repository has measured, on one 2.1 GHz core, a pyramid of boxes, .NET 10 (`python3 build.py test --bench`):

| bodies | native step, portable | native step, AVX2 | reading every transform back, batched | the same, one native call per body |
|---:|---:|---:|---:|---:|
| 5,050 | 2.86 ms | 2.26 ms | 0.07 ms | 0.21 ms |
| 9,870 | 6.28 ms | 4.94 ms | 0.12 ms | 0.40 ms |

A managed-to-native call costs about 6 ns. So the P/Invoke boundary is not where the time goes: the solver is. Batching is still about three times
cheaper than per-body reads, but it saves tenths of a millisecond, not milliseconds, and `SuppressGCTransition` made no measurable difference. **There is no
comparison with Jitter here**; the machine these were taken on could not restore the package to run it. The AVX2 build is about 21% faster than the portable
one at 10k bodies, and is opt-in (`--avx2`) because it needs a CPU that has it.

---

## The core rewrite: C# to C

### Why

The physics now lives in C, but the game around it is C#: every event crosses a boundary, and a script is a managed object that C reaches through an id. The
interesting version is the one where the engine core and the physics are the *same kind of code*, in one translation unit, so a contact handler is a function
the physics engine calls. That needs the core in C. Writing it twice (C# for the editor, C for the player) means two cores that drift apart. So the plan is to
write it once, in C#, in a subset that is mechanically translatable.

### The pipeline

```
C#  --Roslyn-->  Crust C++ subset  --cpprust-->  C  --gcc-->  native
    (CCSharp)                       (crust)
```

[CCSharp](https://github.com/crustos/CCSharp) is a C# cross-compiler on Roslyn. [Crust](https://github.com/brentharts/crust)'s `cpprust` lowers its C++ subset to plain
C. There is no garbage collector and no .NET class library; the standard library is replaced by a small corelib (`CCSharp/corelib`) plus
[coost](https://github.com/crustos/coost). A construct outside the subset is not mistranslated: **it is refused, by name and line**, so the scan below can
count them.

**The C# stays the source of truth.** The same files compile on .NET (that is how they run in the editor and in the tests) and through CCSharp. The generated C is
never edited by hand, and a conformance test keeps the two honest.

### What the subset is

The subset is C# that does not need a runtime. The ones that matter for this code:

- A **class is a single-owner value**, not a shared reference. Assigning one object to another, returning an existing one, or comparing two with `==` is refused,
  because C# would alias and Crust would copy. There is no `null` for an owned class.
- No `throw` / `try`, lambdas or delegates, `is` / `as` patterns, LINQ, generic *methods*, nested types, `params`, named or optional arguments, operator
  overloads, `in` parameters, or inline `out var`. Interfaces are limited (an explicit interface implementation, or `base.M()`, is refused).
- `new T[n]` of a struct is refused (the elements would be null); use a `List<T>` or an arena. A float cannot be *printed*, though it can be computed.

Most of that is what makes the C small and predictable: no runtime to carry, no hidden allocation, no exceptions to unwind.

### What is proven

`python3 build.py ccsharp` translates `Pose2D`, `Collider2DGeometry` and `Collider2DOutline` (about 340 lines, including the port of Box2D's hull code), builds them
with gcc, and runs a program over 600 hull cases, every outline type and the geometry and pose maths. The same program runs on .NET. **The two outputs are
identical.** That is the whole claim: those three files run as C.

### What is not

`python3 build.py scan` asks the compiler about the rest, in tiers. Today **3 of 14 files translate unchanged (343 of 2,008 lines, 17%)**. The counts are a lower
bound, because a refusal at a declaration can stop the compiler before it reaches the bodies; each round of fixes reveals the next layer. After clearing the cheap
ones (small structs passed by value, `out` declarations hoisted, default parameters turned into overloads), what is left has one cause:

> **Objects refer to objects.** The slot registry, the simulation and the components hold references to each other, compare them with `==`, and use `null`. Crust
> classes are single-owner values, so none of that translates.

The planned fix is a handle design rather than more local edits. The simulation and the registry would deal only in integer handles:

```csharp
// a sketch of the plan; not written yet
struct ColliderHandle { public int Index; public int Generation; }

bool Alive(ColliderHandle h) { return generations[h.Index] == h.Generation; }   // replaces ReferenceEquals and the quarantine list
```

A stale handle is then an integer mismatch. That replaces both the `ReferenceEquals` liveness checks and the quarantine that stops a freed slot being reused
mid-dispatch, and it makes `SlotRegistry` and `PhysicsSimulation2D` translatable. The object tables move to the engine layer, which stays ordinary C#. The
native calls (`PB2`) already *are* the C shim, so in a C build they become direct calls.

### Two builds, one API

The intended end state is two builds of the same engine:

| | managed build | packed C build |
|---|---|---|
| what runs | .NET: the editor, play mode, hot-reloading scripts | the core as C, in one unit with Box2D-Packed |
| exists today | yes (this is what the tests exercise) | **no**: only the math tier translates |
| scripts | any C# | the subset |

Hot reload and arbitrary C# stay in the managed build. The packed build is for shipping. How much of a typical game script fits the subset is an open question
this project has not answered yet.

### What came out of it for CCSharp

Running real code through the compiler found real bugs, fixed in [CCSharp](https://github.com/crustos/CCSharp) with tests that compare against real .NET:
on .NET SDK 9 and later CCSharp's own test suite could not run (every indexer failed to bind); a compiler crash when a base class is in a different file from
an upcast; `float %` was emitted verbatim, which C rejects; and the corelib was missing `MathF`, `Math.Clamp`, trig functions and several overloads. One
limit is in Crust itself: `Add(list, xy[i])` leaves the element access unlowered when a list is also an argument, so the 2D code reads elements into locals
first. Details and the full scan are in [tools/ccsharp/README.md](tools/ccsharp/README.md).

---

## Building

Box2D and CCSharp are **not** part of this repository. They are cloned next to it, the way CCSharp itself expects `crust` and `coost`:

```
parent/
  Prowl/      this repository
  box2d/      https://github.com/crustos/box2d
  CCSharp/    https://github.com/crustos/CCSharp      (only for scan / ccsharp)
  crust/      https://github.com/brentharts/crust     (cloned by CCSharp's own build.py)
  coost/      https://github.com/crustos/coost        (ditto)
```

You need `python3`, `git`, `cmake`, a C compiler, and the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

```sh
python3 build.py deps          # clone ../box2d
python3 build.py               # native 2D physics library, then `dotnet build Prowl.slnx`
python3 build.py test          # the 261 checks (builds the native library first)
```

| command | what it does |
|---|---|
| `all` (default) | native library, then the managed solution, 2D only |
| `deps [--ccsharp]` | clone `../box2d` (and `../CCSharp` with its `crust` and `coost`) |
| `native [--avx2]` | build Box2D-Packed and the shim into one shared library and install it under `Libraries/<rid>/native` (git-ignored) |
| `managed` | `dotnet build Prowl.slnx` |
| `test [--managed] [--bench]` | the 2D physics tests; `--managed` also runs `Prowl.Runtime.Test` |
| `scan [-- -v --json f]` | how much of the 2D engine CCSharp can translate, and what refuses |
| `ccsharp` | translate the 2D math to C, build it, run it, and diff with real .NET |
| `check3d` | does the 3D on/off switch still hold? (needs no NuGet) |
| `status`, `clean` | where everything was found; remove what the script built |

`--3d` adds the unmaintained 3D physics to any of these (see [3D.md](3D.md)). `python3 build.py -h` has the rest.

**Please run a real `dotnet build` first.** Everything outside the engine itself was verified: the native library, the 2D layer through the harness, the 3D switch with
Roslyn against reference assemblies, and CCSharp's 73 tests from a fresh clone. The editor changes, the edits to existing engine files (`MonoBehaviour`,
`SceneDispatcher`, `Scene`), and `Prowl.Analyzers` have not been compiled for real, because the machine they were written on could not reach NuGet.

---

## Repository map

| path | what is there |
|---|---|
| `Prowl.Runtime/Physics2D/` | the engine-independent 2D core: simulation, slot registry, pose interpolation, collider geometry and outlines |
| `Prowl.Runtime/Physics2D/Native/` | the C# bindings to the shim (no other Prowl dependency) |
| `Prowl.Runtime/Physics2D/Engine/` | `PhysicsWorld2D` and the public types: `Collision2D`, `RaycastHit2D`, `QueryFilter2D` |
| `Prowl.Runtime/Components/Physics2D/` | `Rigidbody2D` and the colliders |
| `Prowl.Runtime/PhysicsCommon/` | what 2D and 3D share (collision matrix, force modes) |
| `Native/Box2D/` | the C shim, its CMake project, and the test harness |
| `tools/ccsharp/` | the translatability scan and the C-vs-.NET conformance test |
| `tools/check_physics3d.py` | the 3D switch checker |
| `Prowl.Runtime/Physics/`, `Components/Physics/` | 3D physics; compiled only with `--3d` |
| `build.py` | the build script |

---

## Roadmap

Roughly in order, and honest about what is a guess.

1. **A real build.** `dotnet build Prowl.slnx` and `dotnet test` on a machine with NuGet, fixing whatever the harness could not see.
2. **A 2D sample scene**, and the pieces a 2D game reaches for first: joints, a tilemap, one-sided platforms.
3. **The handle redesign** of the registry and simulation, until `scan` reports them translatable and `build.py ccsharp` runs them as C beside the math.
4. **The core into the subset**: transforms, the component lifecycle and event dispatch, tier by tier, each one gated by the conformance test.
5. **A packed C build**, with game code compiled into Box2D-Packed at its injection points. This is where the paper's claim gets measured here.
6. **Open:** how much ordinary game-script C# fits the subset, and what the authoring story is when it does not.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Two things are useful beyond the usual: run `python3 build.py check3d` after a change, so the 3D switch does not rot, and run
`python3 build.py scan` to see whether a change moved the translatable fraction.

## Acknowledgments

Prowl is [ProwlEngine/Prowl](https://github.com/ProwlEngine/Prowl) (copyright Michael Sakharov, see [LICENSE](LICENSE)) and its contributors, and this fork is built on that work. The editor, renderer, audio, UI and asset
pipeline are theirs. Box2D is by Erin Catto. [Crust](https://github.com/brentharts/crust) and the Box2D-Packed additions (`box2d_pack.py`, intrusive execution, the 4-byte handles) are by Brent Hartshorn. Hat tip to
[Raylib](https://github.com/raysan5/raylib), which shaved hours off Prowl's early development.

### Upstream contributors

[Michael (Wulferis)](https://twitter.com/Wulferis), [Abdiel Lopez (PaperPrototype)](https://github.com/PaperPrototype), [Josh Davis](https://github.com/10xJosh),
[ReCore67](https://github.com/recore67), [Isaac Marovitz](https://github.com/IsaacMarovitz), [Kuvrot](https://github.com/Kuvrot), [JaggerJo](https://github.com/JaggerJo),
[Jihad Khawaja](https://github.com/jihadkhawaja), [Jasper Honkasalo](https://github.com/japsuu), [Kai Angulo (k0t)](https://github.com/sinnwrig),
[Bruno Massa](https://github.com/brmassa), [Mark Saba (ZeppelinGames)](https://github.com/ZeppelinGames), [Chandler Cox (Tryibion)](https://github.com/Tryibion),
[EJTP (Unified)](https://github.com/EJTP), [Paolo (xZekro51)](https://github.com/xZekro51), [Kouame Benoit Junior Augustin (ZedDevStuff)](https://github.com/ZedDevStuff)

### Dependencies

- [Box2D-Packed](https://github.com/crustos/box2d) - 2D physics (native, built from source beside this repo)
- [Silk.NET](https://github.com/dotnet/Silk.NET) - windowing, input, OpenGL and audio bindings
- [Jitter Physics 2](https://github.com/notgiven688/jitterphysics2) - 3D physics, only with `--3d`
- [Magick.NET](https://github.com/dlemstra/Magick.NET) - image processing
- [Prowl.Echo](https://github.com/ProwlEngine/Anthology) (serialization), Paper / Origami / Quill / Scribe (UI and text), Rosetta (editor localisation), Vector (math), Unwrapper, Photonic, Clay
- Build-time tooling only: [CCSharp](https://github.com/crustos/CCSharp), [Crust](https://github.com/brentharts/crust), [coost](https://github.com/crustos/coost)

## License

MIT, see [LICENSE](LICENSE).
