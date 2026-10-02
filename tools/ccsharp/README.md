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

`conformance/MathConformance.cs` runs 600 hull cases (the port of Box2D's `b2ComputeHull`), the box / capsule / circle / polygon outlines, the
collider geometry and the pose interpolation. The same program is run on .NET and as C built by gcc; the two outputs are identical.
That covers `Pose2D.cs`, `Collider2DGeometry.cs` and `Collider2DOutline.cs`, about 340 lines.

## What the scan says about the rest

Counts are a lower bound: a refusal at a declaration (an `in` parameter, a base list) can stop the compiler before it reaches the bodies, so
each round of fixes reveals the next layer. After clearing the cheap ones (`in` parameters, inline `out` declarations, default
parameters, named arguments, `IDisposable`), what remains is structural:

| refusal | where | what it means |
|---|---|---|
| `==` / `ReferenceEquals` on classes, aliasing, `null` | registry, simulation, `Collider2D` | the code holds object references. Crust classes are single-owner values. |
| interfaces, explicit implementations, `base.M()` | event sink, host interfaces, `Rigidbody2D` | virtual dispatch shaped for .NET |
| generic methods, nested types | the allocation-free query structs | a C#-only optimisation |
| `throw` / `try` | the simulation | no exceptions in Crust |
| `unsafe`, arrays of structs | polygon / edge colliders, `Float2[]` points | the native boundary; `new T[n]` of a struct is refused |

## The way forward

The easy edits are done. The rest has one cause: **objects referring to objects**. The recommended change is a handle design, not more local
fixes: the simulation and registry deal only in integer ids with a generation counter (a stale handle is an integer mismatch, which replaces
both the `ReferenceEquals` liveness checks and the quarantine list), and the object tables live in the engine layer, which stays C#. That
makes `SlotRegistry` and `PhysicsSimulation2D` translatable and leaves the components, which are engine glue, as C#. The native calls
(`PB2`) already are the C shim, so in a C build they are direct calls.

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
