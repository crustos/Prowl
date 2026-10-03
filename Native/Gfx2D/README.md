# Native/Gfx2D: the 2D renderer of the Prowl player

A small C library that draws a batch of sprites (coloured boxes and discs) in **one instanced draw call**, using Crust's GLES 3.1 batch renderer
(`crust/examples/unity_pack/gles3_batch.h`, the 2D GPU path of `unity_pack`) into an offscreen framebuffer, which it can save as a PPM, hash, or sample.

```
gfx2d.h        the API: 8 functions and a flat float batch. The only place it is written down: tools/ccsharp/gen_pb2.py --lib gfx2d reads it
engine_draw.h  the host's view of what Crust's renderer asks of its engine (three structs, ~15 functions), fixed here instead of generated per project
gfx2d.c        answers that interface (a procedural two-sprite atlas, no lights), makes a headless EGL context, and #includes Crust's renderer
```

## What is Crust's and what is ours

Crust's, **included and not copied** (so it stays in step with its source, and a change to what it expects stops this compiling, which is intended): the vertex and fragment
shaders, the instance layout (24 bytes a sprite: position, half extents, rotation, tint, layer), the one `glDrawArraysInstanced`, the persistent instance buffer that uploads only the
runs that changed, the tint blend, the camera and letterbox. Ours: the atlas (sprite 0 is a white box, sprite 1 a white disc with its edge anti-aliased into its alpha, each with the two
extruded border pixels the renderer's atlas convention wants), the camera globals, the EGL context, the readback, and turning the game's float batch into the renderer's sprites (half
floats, 16-bit rotation, bytes of tint, stable layer order).

Not used: Crust's URP 2D lights, normal maps, per-sprite effects (flash, dissolve, outline), GPU sort, texture atlases from images. `gfx2d.c` says there are none. The shader supports them; wiring a
`Light2D` and `SpriteEffect` component through is the obvious next step and needs nothing new from the renderer.

## Measured (Mesa llvmpipe, software GL, OpenGL ES 3.2 on this machine)

| | |
|---|---|
| a batch of 7 sprites (rotated, tinted, translucent, layered) | 1 draw call; **168 bytes uploaded: exactly 7 x 24** |
| the same batch again | **0 bytes uploaded**, the same frame hash |
| `Samples/Bounce2D`: 30 frames of 832x384 over a 30 s game | 1 draw call each, 12 to 19 sprites, 168 to 360 bytes uploaded |
| the whole player, simulation + 30 frames rendered and written as PPM | about 0.68 s wall, 100 MiB resident (Mesa's JIT is most of it) |

No GPU was available to measure; those numbers are the CPU rasteriser's.

## Limits

* **Headless only.** The context is EGL surfaceless into a framebuffer object. There is no window and no presentation (Crust has a GLFW viewer, `gles3_window.c`; it is not hooked up).
* Needs OpenGL ES 3.1 (Mesa gives 3.2 without a GPU). `gfx_init` returns 0 when there is none, and the sample falls back to drawing text.
* Build needs the EGL/GLES development headers (`apt install libegl-dev libgles-dev`) and a `crust` checkout beside this repository: `python3 build.py gfx`.
* A program that links it loads `libEGL` and `libGLESv2` at run time, so it cannot be fully static.
* Linux x86-64 is all that has been built.
