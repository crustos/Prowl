# gfx2d effects

One file per effect. Each is written **once**, in the three languages the renderer needs, and `tools/gfx_fx_gen.py` generates everything else from it:

| `NAME.fx` holds | becomes |
| --- | --- |
| `id`, `name`, `title`, `group` | the `GFX_FX_*` constant in `gfx2d.h` (and `GFX.Fx*` in C#), the entry in `prowl_editor/fxdefs.py` |
| `param:` lines | the packing of the 128-float block `gfx_effect` takes, and the editor's controls (type, range, default, label) |
| `--- glsl` | the fragment shader of the GL backend (`gfx2d_fx_gen.h`) and of WebGL2 (`web/prowl_web.js`) |
| `--- wgsl` | the WebGPU shader (`web/prowl_web.js`) |
| `--- c` | the CPU reference the SOFT backend runs (`gfx2d_fx_gen.h`), which the GPU backends are tested against |

The file format is documented at the top of `tools/gfx_fx_gen.py`. Never edit a generated file: change the `.fx` and run

    python3 tools/gfx_fx_gen.py          # write the generated files
    python3 tools/gfx_fx_gen.py --check  # is what is committed what the registry makes?

(`gfx_build.py` and `gfx_test.py` do this themselves.) Ids are written down and never renumbered: a project file may keep them.

`python3 tools/gfx_test.py --web` draws `test/fx_scene.c` with every backend, compares each with the CPU picture, and checks the pixels against the formulas.

## Helpers, pixel position and the OpenToonz ports

Beside `--- glsl`, `--- wgsl` and `--- c` a file may have `--- glsl_lib`, `--- wgsl_lib` and `--- c_lib`: helper functions placed before the effect. The C ones all land in
one file, so give them a prefix of their own (`fxb_` in blend.fx, `fxh_` in hsv_adjust.fx, ...). Every body also has, besides `c` and `uv`, the pixel's position `px`, `py`
(the pixel's middle, counted from the TOP left) and the picture's size `pw`, `ph`, in all three languages. Shared helpers for all effects: `fxc_curve(type, t)` (the
OpenToonz gradient curves: 0 ease in-out, 1 linear, 2 ease in, 3 ease out) and `fxc_over(c, color1, color2, f, opacity)` (two colors mixed by f, premultiplied, put over the picture);
the CPU side uses `pow_det`, `atan2_det`, `sin_det` and `cos_det`, which compute in double so that every C library rounds alike.

The effects ported from OpenToonz (see OPENTOONZ-LICENSE.txt) keep OpenToonz's names and parameters where the model allows. An effect here has one input, the picture, so
a blend mode blends a *color* over the picture, and OpenToonz's multi-key color spectrums are two colors. A gradient's center is a fraction of the picture, its lengths are in pixels.

Group `Light` (written for Prowl2D, not ported): `lights` (three point lights over an ambient color) and `spot_light`. A pixel becomes `picture * (ambient + light) + light * glow`;
a light's position is a fraction of the picture (Y from the top) and its radius a fraction of the picture's height. They light the flat picture only: no normal maps and no shadows yet.
