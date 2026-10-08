# prowl.py — the dotnet-free 2D editor

PyQt5 editor (floating windows) over the engine, which is the C# runtime translated to C and loaded with ctypes.

    python3 build.py so            # builds /tmp/libprowl2d.so (+ libprowl2d.h); PROWL2D_LIB overrides the path
    python3 prowl.py [project.json] [--viewport]
    python3 prowl.py --demo slime  # Samples/SlimeJumpDestruct, played in the viewport
    python3 prowl.py --export-ascii DIR project.json
    python3 prowl.py --import-ascii OUT.json --sprites a.txt … --levels b.txt …
    python3 prowl.py --selftest    # model, GUI (offscreen), engine tests; run under xvfb-run for the viewport tests

Windows: Project (File menu), Palette, Sprite editor, Level editor, Effects, Lights, and the engine's own SDL2 viewport (P play, R reset, Home fit, wheel zoom, right-drag pan).

## Effects
The Effects window (Window menu) holds a level's picture effects as a stack, first to last: Add effect (grouped menu), Up / Down, Duplicate, Remove, Reset, and a check box to switch one off.
The controls of the selected effect are built from the effect registry (Native/Gfx2D/fx/*.fx, generated into prowl_editor/fxdefs.py), so a new .fx file shows up here with no GUI code:
a slider and number box per float, a color button with alpha, a combo box per enum. The viewport applies the stack live, in play mode too. Each change is an undo step
(a slider drag is one). In the project JSON a level has `"effects": [{"effect": "tint", "values": {"amount": 0.5}, "enabled": true}]`; a project naming an unknown effect or
parameter is refused with the place. The same effects run in a game: `gfx_effect(id, params, count)` (`GFX.Effect` in C#) after `gfx_draw`, on the GLES, WebGL2 and WebGPU renderers.

## Lights
The Lights window (and the Lights tool of the level editor) put up to 8 point or spot lights in a level, over an ambient color: `lights` and `lighting` in a level's JSON.
A light has a place in cells (x right, y down from the top edge), a radius in cells, an intensity, a color, and for a spot a direction, cone and edge softness. In the level editor's Lights tool,
click an empty place to add a light, drag one to move it, right-click one to remove it. The viewport lights its picture with them live (before the level's effects), and they stay
where they were put when the camera pans or zooms. Moving a light, or a slider, is one undo step.

## Project JSON
`{"format":"prowl2d-project","version":1, palette, sprites, tiles, levels}`. Sprite frames are rows of palette key letters; `.` is index 0 (transparent).
Recolouring an index recolours every pixel that uses it, so scripts animate by changing one palette entry.

## Sprite ASCII
    # sprite: coin
    # fps: 6
    # palette: Y=#ffd23c O=#e08a1e
    .YY.
    YOOY
    (blank line between frames)
Unknown letters on import are mapped to colours in a dialog (or guessed).

## Level emoji
    # level: first steps
    # empty: ⬛
    # 🧱 = brick (solid) -> brick
    ⬛⬛🪙⬛
    🧱🧱🧱🧱
Emoji names come from Unicode (🧱 → "brick") and link to the sprite with the same name; view as emoji or sprites.

Tiles can be `solid` (static collider), `dynamic` (a movable body, like a crate) and `diggable` (a blast removes it, like dirt); the emoji legend writes them as `# 📦 = crate (dynamic) -> crate`. In the viewport's play mode a click drops a ball and B blasts at the pointer.

Not yet: Unity scene import, generating the player's C#/C from the GUI, WASM window.
