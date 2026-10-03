# Translating the 2D engine to C with CCSharp

Goal: run the core of Prowl's 2D engine as C. [CCSharp](https://github.com/crustos/CCSharp) compiles a subset of C# to the Crust C++
subset, which [crust](https://github.com/brentharts/crust) lowers to C. This directory measures how far the 2D code is from that subset, and
proves what already works.

```sh
python3 build.py deps --ccsharp         # clone ../CCSharp (and its ../crust ../coost)
python3 build.py ccsharp                # conformance: translate -> C -> gcc -> run, diff with real .NET
python3 build.py scan                   # how much translates, and what refuses
python3 build.py scan -- -v --json r.json
```

`tools/ccsharp/ccsharp_scan.py` does the work (`overlay`, `scan`, `conformance`). Its docstring explains the overlay it writes to
`/tmp/prowl-ccs-overlay`: a copy of CCSharp's corelib plus declarations that let the 2D code *bind*, so the compiler gets as far as
saying what is outside the subset.

## What is proven to run in C

Each conformance program is run on .NET and as C built by gcc; the two outputs must be identical (`python3 build.py ccsharp`).

* `conformance/MathConformance.cs` runs 600 hull cases (the port of Box2D's `b2ComputeHull`), the box / capsule / circle / polygon outlines, the
  collider geometry and the pose interpolation. That covers `Pose2D.cs`, `Collider2DGeometry.cs` and `Collider2DOutline.cs`, about 340 lines.
* `conformance/HandleConformance.cs` drives `HandleTable` and `TriggerSet`, about 180 lines, through thousands of random operations, each compared against a naive
  reference model written in the program (reuse order, quarantine and flush, generations across `Clear`, the capacity-checked `AddWithin`, set membership,
  remove-by-collider), so "identical output" cannot mean "identically wrong"; the line to look at is `mismatches 0`.
* `conformance/RegistryConformance.cs` drives the **arena registry**: `Registry2D` with its `BodyRecord` and `JointRecord` arena classes, about 170 lines. As above, against a model,
  and it also checks what only arenas have: a recycled record comes back reset, the pose a record holds is the one it was given, the capacity is a hard limit that refuses cleanly (including
  when slots are tied up in a quarantine), a joint's two references name the right bodies, and "which joints die with this body" finds exactly the live joints that touch it, with the world
  (null) never matching a body. It uses ONE registry for all its sections, because the arena belongs to the class: a second `Registry2D` would find the 256 body slots used up.

* `conformance/SimConformance.cs` drives the **simulation core**, `SimCore2D` (400 lines, with everything under it), against the real Box2D library through the generated bindings: the native world, the
  step, the pose writes into the body arena and the event stream. It plays the engine layer: it builds bodies and colliders, reads `while (core.NextEvent())`, and acts on the events, including
  **removing colliders in the middle of a pair's events and destroying a joint on its break event**. `leaks` (events naming something already removed) must be 0, a ball that passes through the trigger
  untouched must get Enter, Stay and Exit with both directions, and a batched teleport must work. Its first run printed identical output in C and .NET; reading the output found a bug in the shim (a
  teleported sleeping body stayed asleep), now fixed and covered by a harness test.

* `conformance/NodeConformance.cs`, `LifecycleConformance.cs` and `PhysicsSceneConformance.cs` gate the three tiers of the **2D runtime** (`Prowl.Core2D`, see its README). The node program checks 1.37 million
  facts against a model that recomputes every world transform from the root with no cache (and found that its own random walk overflowed to NaN before it found anything in the code). The lifecycle
  program pins 14 scenarios with their exact call order and then runs a stress test in which scripts create, destroy, enable, disable and reparent things in the middle of dispatch, with invariants checked on
  every call and the scene's own `Validate()` after every step. The physics program builds a scene through the public API and asserts hand-derived numbers on the real library. **Each was checked by planting bugs
  in the runtime** (four in `Scene2D`, five in `World2D`): all are caught, one only by the stress run, and the first physics scenario missed two of the five, which is why it now has a scenario that edits a *moving*
  body and one in which a collider dies inside a trigger and another takes its slot.
* `conformance/RenderBatchConformance.cs` checks the **draw batch** a renderer is handed (layer order, stable for equal layers and after a disable and re-enable; a sprite on a child of a rotated, scaled parent against
  hand-computed centre, half extents and angle; a sprite on a falling body; destroy and recycle). Five planted bugs in `Renderer2D`, all caught. What the renderer makes of a batch is `Native/Gfx2D`'s, and is
  checked by drawing frames: `--verify` compares every rendered frame's pixel hash between the translated player and the .NET run.
* `build.py player` (`player_build.py`) builds a whole game: see `Samples/Bounce2D`. It is also a conformance check of its own: `--verify` runs the game on .NET and as native C and requires the same output.

A match proves those files do the same thing in C. It says nothing about files that are not in a program, which is what `scan` is for.

## The native bindings are generated from the header

`conformance/NativeConformance.cs` calls the real Box2D shim: it builds a world, a stack of boxes, a polygon from a `float[]`, a revolute joint filled in field by
field, a batched teleport through a `List<struct>`, ray casts into a `List<struct>`, event arrays read through accessors, and an overlap query. It runs on .NET and as
translated C linked against the same `libprowl_box2d`, and the two outputs are identical. Both sides call the same C# (`Prowl.Native.Box2D.PB2`), generated from
`Native/Box2D/prowl_box2d.h` by `gen_pb2.py`, in two flavors with identical signatures:

| flavor | what it is |
|---|---|
| `net` | `[LibraryImport]` over the real pointers; each pointer-taking function wrapped so its public signature has none |
| `c` | `extern` members with `[Cpp]` templates that spell the call in C, and structs that name the header's C structs. Compiled as CCSharp `--bindings`; it holds no code |

```sh
python3 tools/ccsharp/gen_pb2.py generate Build/Bindings     # write both flavors (net/PB2.net.cs, c/PB2.c.cs) to look at them
python3 tools/ccsharp/gen_pb2.py --lib gfx2d generate Build/Bindings   # the same for the renderer's header (GFX.net.cs, GFX.c.cs)
python3 tools/ccsharp/gen_pb2.py runtime                     # write Prowl.Runtime/Physics2D/Native/PB2.Generated.cs, the one copy the engine compiles
python3 tools/ccsharp/gen_pb2.py check                       # is that committed file what the header generates today?
python3 build.py ccsharp                                     # builds the native library if needed, then runs every conformance
```

The header says what a pointer means with markers that expand to nothing, so the C ABI is untouched: `PB2_IN`, `PB2_OUT` (a record, by `ref`), `PB2_IN_ARR`, `PB2_OUT_ARR`
(`T[]`, or `List<Record>`), and `PB2_VIEW(T, countField)` on an `intptr_t` field (an array behind a pointer, read through an accessor). The generator refuses a pointer
parameter without one, so a new native function cannot be added without saying what its pointers are. A record's array member (`float p[12]`) becomes
`GetP` / `SetP` accessors, because the subset has no arrays inside structs. Records use the C field names, since in the C flavor a field *is* the C field.

Crust's `--emit-decls` digests C++ classes, not C headers, so the generator has its own small parser. It is the one place a different front end could be dropped in.

## Arenas: where they pay, and what they cost (measured)

`python3 build.py arenas` (`arena_report.py`) translates the arena classes and has the C compiler report `sizeof`, so the capacities in `PhysicsLimits` are numbers and not estimates. At the
shipped limits (256 bodies, 64 joints): a body record is **44 bytes**, a joint record 24, plus 12 per slot for the tables that find them, **16.2 KiB in all, 51% of a 32 KiB L1**. (An earlier
guess of 48 bytes, and of 512 bodies as "L1-sized", were both wrong: 512 bodies measure 32.5 KiB, the whole cache. Hence the report.) 32768 bodies would be 1.75 MiB: not even L2.

Two findings from moving the first piece onto arenas, the second one against expectation:

* **An id by itself is cheaper as an integer.** A `HandleTable` slot is 4 bytes; an arena record is its own fields plus the 8-byte pointer that finds it by index. So colliders, which the
  simulation knows only as ids, stay on the integer table. A record pays when it holds data that is hot and reached through references: a body's pose (written for every moving body on every step) and
  a joint's two bodies.
* **The gain at this scale is real but modest.** Delivering and reading back 64 to 256 poses, the old way (an interface call into a component-sized object, scattered among other allocations)
  against a direct write into the record: **1.13 to 1.34x faster**, in three runs. At these counts both layouts fit in L2, so what is saved is the interface call and the scatter; the `atan2` inside
  `BodyPose2D.Push` is about 5 of the ~6 ns either way. (A first measurement said the records were 20% *slower*: it did a registry lookup where `Rigidbody2D` keeps its record, which the real code
  does not.) The large cache effect is for working sets bigger than the cache, which the capacity limit is there to prevent.

The limits are engine limits, enforced: the registry returns -1 for the body after `PhysicsLimits.Bodies` (the simulation then throws), and the C build would abort. Only a `Rigidbody2D` takes a
body slot; a collider with no rigidbody is static geometry and takes none.

## How to read `scan`

`CLEAN` means the compiler had nothing to refuse in the file, no more. `BLOCKED` means the file could not bind: it names a type that does not exist in what the
scan was given, usually because the file that defines it is itself blocked. The scan used to have a hole here: when its own stand-ins failed to compile, every file
of the tier fell through to `CLEAN` having never been compiled (it reported 83% translatable at a moment when the true figure was 16%). Now a context that does not
compile is a hard failure, `SCAN FAILED (not a result)`, unless every error is a missing type that a blocked file of the program defines, in which case the tier is
blocked by that type. If you add a construct to the generated PB2 context (an attribute, a new kind of struct) and the scan fails like this, the overlay needs to learn
it: see `gen_pb2_context`, which, for instance, rewrites an `[InlineArray]` struct into one with an indexer because the corelib has neither.

## What the scan says about the rest

Counts are a lower bound: a refusal at a declaration (an `in` parameter, a base list) can stop the compiler before it reaches the bodies, so
each round of fixes reveals the next layer. After clearing the cheap ones (`in` parameters, inline `out` declarations, default
parameters, named arguments, `IDisposable`), what remains is structural:

| refusal | where | what it means |
|---|---|---|
| host objects, interfaces, `MonoBehaviour`, explicit implementations, `base.M()` | `PhysicsSimulation2D`, `Rigidbody2D`, the colliders and joints | the engine layer: virtual dispatch and objects shaped for .NET, on purpose |
| `throw` / `try` | the engine layer's guards and `finally` | no exceptions in Crust; the core has none |
| `VerifyAbi()` (a .NET-only call) | `PhysicsSimulation2D` | lives in the engine layer deliberately: the C build has one header and no mismatch to catch |

The simulation core, the registry, the tables and the math no longer appear here: they are `CLEAN` and proven by conformance.

## Subset limits worth knowing before you write translatable code

Found writing a whole runtime and a game in it (each is refused with its line and a remedy, or is a rule of the arena model):

* **Arrays are values, not references**: `if (arr == null)` is refused (use a flag); an array field cannot be initialised where it is declared (allocate it in the constructor or in `Main`).
* **A string built with `?:`** is refused (assign it in an `if`); so is a **string literal passed beside a call** in one argument list (`Check(Near(a, b), "text")`: compute the condition on its own line first),
  because C# evaluates the call first and a string temporary would be built before it.
* **An arena reference is not checked after its target is freed**: slots are recycled, so a kept `Node` can silently name its replacement. Look it up again, or use it only in the frame it was obtained.
* **`[MaxInstances]` lives in the global namespace** (as in unity_pack), so a script needs no `using` for it.
* Output is `Console.WriteLine` only (no `Write`): build a line as a string and print it once.

Found by writing the integer tables; each has a one-line workaround, none is specific to Prowl.

* **Overloads are resolved by argument count.** Two methods with the same name and the same number of parameters are refused, so `IsLive(int)` and
  `IsLive(Handle2D)` cannot coexist (the second is `IsHandleLive`).
* ~~`new Struct(..)` written directly as a call argument is not lowered~~: fixed in CCSharp (the emitter now hoists it to a local, as it always did for classes). Two struct
  constructions side by side are fine; one beside a call with side effects is refused, because hoisting it would reorder them.
* **A list element as an argument beside another list** is left unlowered, as the section below already says; the same happens for a *field* list
  (`Push(_free, ref _count, _held[i])`). Read the element into a local.
* **Arenas are per class.** `[MaxInstances(N)]` bounds the objects of a class in the whole program, whichever collection holds them, and nothing is freed one at a time: a registry that
  creates and destroys must recycle. See the CCSharp README.
* The corelib implements `List<T>.Count` / indexer / `Add` / `Clear` / `RemoveAt` / `Insert` (the last two are new, mapped onto Crust's `erase` / `insert`) and `Dictionary`
  `Count` / indexer / `ContainsKey` / `Remove` / `Clear`; `Contains`, `IndexOf`, `List.Remove`, `Dictionary.Add`, `TryGetValue`, `Stack<T>` and `HashSet<T>` are declared but empty.
  The tables predate `RemoveAt` and still use a list kept as a stack with a separate count, and indexer assignment instead of `Add`. A `long` dictionary key works.

## The way forward

The 2D simulation's native surface and its event hand-off are done. The core (`SimCore2D`) holds no object, interface or delegate; the engine layer (`PhysicsSimulation2D`) holds the managed hosts, the
ownership of the one native world among scenes, and a dispatch loop that is a bare `switch` over the core's event stream (`while (core.NextEvent())`: each event is decided from the world as it is when
asked, so a handler can destroy things; see the main README for why a precomputed list and registered callbacks both fail). What is left of the 2D physics is the engine layer, which stays C# by design.
What to do next is about what that layer should become in a C build: the components' lifecycle and the engine's own transform and message dispatch, tier by tier, each gated by a conformance program.

## What was fixed in CCSharp along the way

All in `prowl-2d-fixes`, each with a test that compares against real .NET. None of this is specific to Prowl.

* `DefaultMemberAttribute`: on .NET SDK 9+ **CCSharp's own suite could not run** (CS0656 on every indexer).
* `string.Format` overloads (interpolated strings with 2+ holes failed to bind), and the test runner no longer needs a .NET 8 runtime.
* `MathF`, `Math.Clamp`, the trig / exp / log functions, float overloads of `Math.Min/Max/Abs`.
* float `%` and `%=` lowered to `fmod` (C has no float `%`).
* `InAttribute` (so `in` parameters reach the emitter, which then refuses them), and the two-argument `ArgumentException` constructors.
* a crash when a base class is declared in a different file from the upcast ("Syntax node is not within syntax tree").

## Known limit in Crust (not CCSharp)

cpprust leaves an element access unlowered when it is an argument to a call that also passes a `List<T>`:
`Add(segs, xy[2 * i], xy[2 * i + 1])` is rejected by gcc, while `Sum(xy[i], xy[i + 1])` works. The 2D code reads the elements into locals first.
