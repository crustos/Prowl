// gfx2d: the 2D renderer of the Prowl player, as a small C library.
//
// It draws a batch of sprites (coloured boxes and discs) in ONE instanced draw call, by Crust's GLES 3.1 batch renderer
// (crust/examples/unity_pack/gles3_batch.h: the same vertex and fragment shaders, instance buffer and tint blend that unity_pack's 2D GPU path
// uses), into an offscreen framebuffer it can save or hash. It needs a GLES 3.1 context and makes one itself, headless (EGL surfaceless: Mesa's
// llvmpipe on a machine with no GPU, a real GPU otherwise). A window is not here yet.
//
// Like prowl_box2d.h this header is the only place the API is written down: tools/ccsharp/gen_pb2.py --lib gfx2d reads it, from the markers below, and
// writes the C# bindings (a .NET flavor with [LibraryImport], and the C flavor the C# -> C translator compiles against). Every pointer parameter says
// what it is: GFX_IN_ARR / GFX_OUT_ARR (an array), GFX_IN / GFX_OUT (one record).
#ifndef GFX2D_H
#define GFX2D_H

#include <stdint.h>

#define GFX_API
#define GFX_IN
#define GFX_OUT
#define GFX_IN_ARR
#define GFX_OUT_ARR
#define GFX_VIEW(type, count)

// ---- sprites ----------------------------------------------------------------------------------------------------------------
// A batch is `count` records of GFX_SPRITE_FLOATS floats each, in this order:
//    x, y            the centre, in world units
//    halfW, halfH    half the size, in world units
//    angle           radians, counter-clockwise
//    r, g, b, a      the tint, 0..1 (the shape's white is multiplied by it; a disc's edge is anti-aliased into its alpha)
//    shape           GFX_SHAPE_BOX or GFX_SHAPE_DISC
//    layer           0..255: a higher layer draws over a lower one; equal layers draw in the order given
//    (one float spare)
#define GFX_SPRITE_FLOATS 12
#define GFX_MAX_SPRITES 8192

#define GFX_SHAPE_BOX 0
#define GFX_SHAPE_DISC 1

// what gfx_stat() can say
#define GFX_STAT_DRAW_CALLS 0   // draw calls the last gfx_draw made (1, or 0 for an empty batch)
#define GFX_STAT_SPRITES 1      // sprites it drew
#define GFX_STAT_BYTES_UPLOADED 2   // instance bytes the last gfx_draw sent to the GPU (only what changed since the frame before)

// ---- the renderer -----------------------------------------------------------------------------------------------------------

// Makes the GL context and an offscreen width x height target. Returns 1, or 0 if there is no GLES 3.1 here (nothing else may then be called).
GFX_API int gfx_init(int width, int height);
GFX_API void gfx_shutdown(void);

// The view: the world point at the centre, half the visible height in world units (the width follows the target's aspect), the background colour.
GFX_API void gfx_camera(float centerX, float centerY, float halfHeight, float r, float g, float b);

// Draws a batch (see above) over the background and returns how many sprites it drew. The frame can then be read back.
GFX_API int gfx_draw(GFX_IN_ARR const float *sprites, int count);

// The last frame: one pixel as 0xAARRGGBB (x from the left, y from the TOP), a hash of the whole frame (the same pixels always give the same hash),
// and a file, frame_NNNN.ppm in the current directory. gfx_save_frame returns 1 if it was written.
GFX_API uint32_t gfx_pixel(int x, int y);
GFX_API int gfx_frame_hash(void);
GFX_API int gfx_save_frame(int index);

GFX_API int gfx_stat(int which);

#endif
