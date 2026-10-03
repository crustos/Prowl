# Prowl.Core2D: the 2D runtime that translates to C

A small 2D game runtime written in the C# subset that CC# translates to C (see `tools/ccsharp`), so that **a game written against it runs on .NET
and as a standalone native executable with no .NET in it**, from one source. About 1,700 lines, no dependency but the simulation core under it
(`Prowl.Runtime/Physics2D`, shared with the engine's own 2D physics). `Prowl.Core2D.csproj` is the single list of its files: the .NET build compiles
it, `python3 build.py player` reads the same list.

```
Node          a place in the scene: position, angle, scale, a parent and children; the world transform is cached
Component     the record the lifecycle works on, whatever the component is
Scene2D       nodes, components, which are active, and the order the game's callbacks run in
Rigidbody2D, Collider2D, World2D    the physics components and what connects them to the simulation core (SimCore2D)
SpriteRenderer2D, Renderer2D        a coloured box or disc on a node, and the draw batch of all of them that a renderer is handed
[Script]      marks a class of the game's as a script; tools/ccsharp/gen_scripts.py writes the call sink for it
```

## Writing a game

```csharp
[Script, MaxInstances(16)]
class Ball
{
    public Component Self;                    // set when the script is added to a node
    public int Hits;
    public void Reset() { Hits = 0; }         // optional: puts a recycled instance back to its starting state

    public void OnCollisionBegin2D(Collision2D hit) { if (++Hits >= 4) Scene2D.Current.Destroy(Self.Node); }
    public void OnTriggerEnter2D(Collider2D other) { ... }
    public void Update() { ... }
}

Scripts.Init();                               // generated
Scene2D scene = new Scene2D();
Node n = scene.NewNode(null);
scene.AddRigidbody(n, PB2.BodyDynamic);
scene.AddCircleCollider(n, 0.4f);
Scripts.AddBall(n);                           // generated: a Ball component on n
for (...) Scripts.Tick(scene, dt);            // generated: the engine's fixed-step loop, once a frame
```

The callbacks a script may have: `Start Update FixedUpdate LateUpdate OnEnable OnDisable OnCollisionBegin2D(Collision2D) OnCollisionEnd2D(Collision2D)
OnTriggerEnter2D(Collider2D) OnTriggerStay2D(Collider2D) OnTriggerExit2D(Collider2D)`. The generator reads which a class has from its source, where the
.NET engine reads it by reflection, and refuses a callback whose signature is nearly right (it would silently never run). `Samples/Bounce2D` is a
complete game.

## How it runs: a stream of calls

The subset has no virtual dispatch, interface or delegate, so the scene cannot call a script. It produces a **stream of calls** and a generated
sink performs them:

```csharp
scene.BeginFixedStep(dt);                     // or BeginFrame(dt, alpha)
while (scene.NextCall()) Scripts.Invoke(scene);   // a switch on the component's kind and the callback
```

Each `NextCall` decides what runs next from the scene **as it is then**, so a callback can enable, disable, destroy and create things and the very next
call reflects it. The frame is the engine's: a fixed step is *Start, FixedUpdate, the physics step (the game's edits of nodes pushed into bodies first),
collision and trigger messages, the end of the frame*; a frame is *Start, Update, physics poses written into nodes, LateUpdate, the end of the frame*.

## Rules the runtime keeps (each is checked by a conformance program that compares C with .NET, with model or hand-derived expectations)

- **Enabled in hierarchy** = the component is enabled, its node is active all the way up, and it is not destroyed. OnEnable / OnDisable come when that
  changes, and only while the scene is active (`SetSceneActive`).
- **Start** runs once, before a component's first per-frame callback, and the component leaves that channel for good.
- Per-frame callbacks run in **(execution order, registration sequence)** order, from lists rebuilt only at the start of a phase, so a change in the
  middle of one cannot disturb the walk. Disabled and destroyed components are skipped.
- **Destroy** stops a node's and its children's callbacks at once, queues OnDisable for each enabled component, and frees the slots at the end of the frame.
- Pose sync: after the scene writes a body's pose into its node it remembers the node's **world version** (the greatest id along the path to the root).
  Any other version later is an edit by the game, pushed into the body as a teleport before the next step (a kinematic body is moved to it), and a
  teleport wakes a sleeping body.

## Differences from the .NET engine (deliberate, and the reasons)

- **OnEnable / OnDisable are queued**, delivered as the very next calls after the callback that caused them, where the .NET engine runs them inside
  `SetActive`. The scene cannot re-enter game code in the middle of the game's own callback. Code after `SetActive(true)` in the same callback runs first.
- A component added during `Update` can get `LateUpdate` before its `Start` that frame. The .NET engine does the same; a scenario pins it.
- A component's settings are read when it is **enabled**; to change one on a live component, disable and enable it. The .NET components rebuild live.
- Scripts and physics components are **pooled** (`[MaxInstances(N)]` is the most that exist at once; `Scripts.AddX` returns null at the cap, as
  unity_pack's `Instantiate` does). Memory is fixed: `python3 build.py arenas` prints it.

## Drawing

A `SpriteRenderer2D` is a box or a disc of a size and a tint on a node. `scene.AddSprite(node, SpriteRenderer2D.Disc, w, h, r, g, b)` makes one; its tint and layer are read every frame, so a script can
change them whenever it likes (a ball in `Bounce2D` goes from blue to red as it wears out). Each frame `scene.CollectSprites()` turns the enabled sprites into `scene.Render.DrawData` (12 floats each: x, y,
half width, half height, angle, r, g, b, a, shape, layer), ordered by layer and, within a layer, by the order they were enabled. Nothing in `Prowl.Core2D` draws: a game hands that batch to the renderer,
`GFX.Draw(scene.Render.DrawData, scene.CollectSprites())`, which is `Native/Gfx2D` (see its README). The batch is checked on its own (`RenderBatchConformance`) against hand-derived numbers.

## Hazards and limits

- **A Node or Component reference is not checked after its target is freed.** Slots are recycled, so a reference kept past the end of the frame in which its
  node was destroyed names whatever reuses the slot (a conformance test of this repository was bitten by exactly that). Look nodes up again, or compare
  `scene.IsLive(n)` in the same frame; a handle type (index + generation) is the planned fix.
- One scene and one simulation per process (`Scene2D` and `SimCore2D` are arena classes of capacity 1).
- Not here yet: joints; a body-less collider following its node when the node moves; the layer matrix (all layers collide); a body's mass override;
  scene files (a game builds its scene in code); input; audio; a window (the renderer draws offscreen); lights and sprite effects (the renderer has them, they are not wired); textures.
- The C# a game may use is the subset: for example no exceptions, no LINQ, no lambdas, no `string` built with `?:`, no string-keyed dictionaries (the list is what the translator refuses, not a promise that
  everything else works). `build.py player --check`
  says which line and why, as `File.cs(line,col): error CCS0001: ...`.
