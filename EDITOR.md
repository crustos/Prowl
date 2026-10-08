# prowl.py — the dotnet-free 2D editor

PyQt5 editor (floating windows) over the engine, which is the C# runtime translated to C and loaded with ctypes.

    python3 build.py so            # builds /tmp/libprowl2d.so (+ libprowl2d.h); PROWL2D_LIB overrides the path
    python3 prowl.py [project.json] [--viewport]
    python3 prowl.py --demo slime  # Samples/SlimeJumpDestruct, played in the viewport
    python3 prowl.py --export-ascii DIR project.json
    python3 prowl.py --import-ascii OUT.json --sprites a.txt … --levels b.txt …
    python3 prowl.py --selftest    # model, GUI (offscreen), engine tests; run under xvfb-run for the viewport tests

Windows: Project (File menu), Palette, Sprite editor, Level editor, and the engine's own SDL2 viewport (P play, R reset, Home fit, wheel zoom, right-drag pan).

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
