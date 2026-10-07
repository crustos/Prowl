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

// ---- the UI layer: textures, meshes, clipping, fonts and input ---------------------------------------------------------------------------
// Added for the in-game UI (Prowl.Core2D's Prowl.UI), and the same as Stride2D's gfx2d.h, so the UI's C# is one source in both. What these draw goes ON TOP of
// what gfx_draw drew, in the order the calls are made, and the next gfx_draw starts a new frame (it also ends any clip). Nothing here costs a draw call
// in GFX_STAT_DRAW_CALLS, which counts the sprite batch as before.
#define GFX_MAX_VERTICES 65536
#define GFX_MAX_TEXTURES 64 // ids 0 (the white texel) to 63
#define GFX_MAX_SIZE 8192
#define GFX_MESH_VERTEX_FLOATS 8 // a mesh vertex: x, y (world units), u, v (v = 0 is the TOP of the image), r, g, b, a (the tint)

#define GFX_FILTER_NEAREST 0
#define GFX_FILTER_LINEAR 1

#define GFX_STAT_VERTICES 3          // mesh vertices drawn since the last gfx_draw
#define GFX_STAT_EVENTS_DROPPED 4    // input events lost because the queue (GFX_MAX_EVENTS) was full, since gfx_init

// ---- input ----------------------------------------------------------------------------------------------------------------------------
//
// What the window, or the page, receives is queued and read with gfx_poll_event: five ints, [type, a, b, c, d]. Positions are in picture pixels, x from the
// left and y from the TOP (the way a UI lays itself out), whatever the backend. The queue holds GFX_MAX_EVENTS; a full queue drops the NEW event.
//
//   type                 a            b           c               d
//   GFX_EVENT_MOUSE_MOVE x            y           0               mods
//   GFX_EVENT_MOUSE_DOWN x            y           button          mods       button: GFX_BUTTON_LEFT / MIDDLE / RIGHT
//   GFX_EVENT_MOUSE_UP   x            y           button          mods
//   GFX_EVENT_WHEEL      x            y           dx              dy         in 1/120 of a notch: dy > 0 scrolls up (away from the user), dx > 0 right
//   GFX_EVENT_KEY_DOWN   key          repeat      0               mods       key: a GFX_KEY_*; repeat is 1 for an auto-repeat
//   GFX_EVENT_KEY_UP     key          0           0               mods
//   GFX_EVENT_TEXT       codepoint    0           0               0          a typed character (Unicode); the window gives Latin-1, the page any character
//   GFX_EVENT_FOCUS      1 or 0       0           0               0          the window or page gained or lost the keyboard
//   GFX_EVENT_CLOSE      0            0           0               0          the window was closed (gfx_end then returns 0 as well)
// A key's `key` is its ASCII code for a printable key (letters as the capital: 'A'), and the GFX_KEY_* below for the others, whatever the layout's text.
#define GFX_MAX_EVENTS 256

#define GFX_EVENT_NONE 0
#define GFX_EVENT_MOUSE_MOVE 1
#define GFX_EVENT_MOUSE_DOWN 2
#define GFX_EVENT_MOUSE_UP 3
#define GFX_EVENT_WHEEL 4
#define GFX_EVENT_KEY_DOWN 5
#define GFX_EVENT_KEY_UP 6
#define GFX_EVENT_TEXT 7
#define GFX_EVENT_FOCUS 8
#define GFX_EVENT_CLOSE 9

#define GFX_BUTTON_LEFT 0
#define GFX_BUTTON_MIDDLE 1
#define GFX_BUTTON_RIGHT 2

#define GFX_MOD_SHIFT 1
#define GFX_MOD_CTRL 2
#define GFX_MOD_ALT 4
#define GFX_MOD_SUPER 8

#define GFX_KEY_ESCAPE 256
#define GFX_KEY_ENTER 257
#define GFX_KEY_TAB 258
#define GFX_KEY_BACKSPACE 259
#define GFX_KEY_INSERT 260
#define GFX_KEY_DELETE 261
#define GFX_KEY_RIGHT 262
#define GFX_KEY_LEFT 263
#define GFX_KEY_DOWN 264
#define GFX_KEY_UP 265
#define GFX_KEY_PAGE_UP 266
#define GFX_KEY_PAGE_DOWN 267
#define GFX_KEY_HOME 268
#define GFX_KEY_END 269
#define GFX_KEY_F1 290 // F1..F12 are 290..301
#define GFX_KEY_LEFT_SHIFT 340
#define GFX_KEY_LEFT_CTRL 341
#define GFX_KEY_LEFT_ALT 342
#define GFX_KEY_LEFT_SUPER 343
#define GFX_KEY_RIGHT_SHIFT 344
#define GFX_KEY_RIGHT_CTRL 345
#define GFX_KEY_RIGHT_ALT 346
#define GFX_KEY_RIGHT_SUPER 347


// Textures. Texture 0 is a plain white texel and always exists. An image is width x height pixels of 4 bytes (r, g, b, a: straight alpha), row by row, the
// first row first; `rgba` must hold width * height * 4 bytes (a null array makes a transparent image). gfx_texture returns an id of 1 or more, or 0 if the
// size is wrong or all the ids are in use.
GFX_API int gfx_texture(int width, int height, int filter, GFX_IN_ARR const uint8_t *rgba);

// Sends the rectangle (x, y, width, height) of `rgba` to the texture: `rgba` is the WHOLE image again (the size the texture was made with), of which only that
// rectangle is read.
GFX_API void gfx_texture_update(int id, int x, int y, int width, int height, GFX_IN_ARR const uint8_t *rgba);
GFX_API void gfx_texture_free(int id);

// Draws `count` mesh vertices (a multiple of 3, GFX_MESH_VERTEX_FLOATS floats each) with a texture (0: white) over the frame; returns how many it drew.
GFX_API int gfx_triangles(GFX_IN_ARR const float *vertices, int count, int texture);

// Limits what the draws after it change to the rectangle (x, y, width, height), in PIXELS from the top left of the picture (the way a screen and the UI's
// layout are measured; the camera does not move it). It holds until the next gfx_clip, gfx_clip_reset or gfx_draw. A rectangle is cut to the picture, and an
// empty one hides everything until the clip changes. It limits meshes (it does not limit the sprite batch, which gfx_draw draws before it). Returns 1.
GFX_API int gfx_clip(int x, int y, int width, int height);
GFX_API int gfx_clip_reset(void);


// ---- input: reading ---------------------------------------------------------------------------------------------------------------------
//
// Writes the oldest queued event to out5 (five ints, above) and returns 1, or returns 0 with out5 untouched when there is none. It first lets the window or
// the page deliver what it has received, so it works between frames; a game normally drains it once a frame.
GFX_API int gfx_poll_event( GFX_OUT_ARR int *out5 );

// ---- fonts -------------------------------------------------------------------------------------------------------------------------------
//
// The renderer carries bitmap fonts (DejaVu Sans, Latin-1; tools/font_bake.py), each baked at one pixel size, so text is sharp when it is drawn at a baked size and
// is scaled (a little softer) at any other. A game lays a string out itself from these numbers: for each character a quad of the glyph's size, put at
// (pen x + bearingX, baseline - bearingY) with the texture coordinates given, then the pen moves right by the advance. Kerning is not applied.
//
// Font `index` is 0 .. gfx_font_count() - 1, in increasing size. gfx_font_texture is the font's atlas (made on first use; 0 if there is no room): draw the
// quads with gfx_triangles and that texture, with the text's colour as the vertices' tint.
GFX_API int gfx_font_count( void );
GFX_API int gfx_font_size( int index );
GFX_API int gfx_font_texture( int index );

// out4: ascent, descent (pixels above and below the baseline), the line height (ascent + descent), the size. Returns 1, or 0 for a bad index.
GFX_API int gfx_font_metrics( int index, GFX_OUT_ARR float *out4 );

// out9: u0, v0, u1, v1 (the glyph's box in the atlas: v = 0 is the TOP), width, height (pixels), bearingX, bearingY (from the pen on the baseline to the box's
// left and TOP edge; y is UP), the advance. Returns 1, or 0 if the font has no such character (it then describes '?'; a bad index describes nothing).
GFX_API int gfx_font_glyph( int index, int codepoint, GFX_OUT_ARR float *out9 );

// Queues an event as if it had come from the window: what a test or a scripted demo uses, the same on every backend. Returns 1, or 0 if the queue was full
// (and counts it in GFX_STAT_EVENTS_DROPPED). An unknown type or one before gfx_init is refused (0).
GFX_API int gfx_inject_event( int type, int a, int b, int c, int d );

// ---- the window (desktop only; gfx2d_sdl.inc) ------------------------------------------------------------------------------------------------
//
// The renderer draws offscreen as always; a window only SHOWS the finished frame. gfx_window_open (after gfx_init) makes an SDL2 window the size of the
// picture, resizable (the picture is letterboxed, never stretched); `visible` 0 makes it hidden. gfx_present reads the frame back and shows it. From then
// on the window's mouse, keyboard, text, focus and close events arrive through gfx_poll_event, in picture pixels, as if gfx_inject_event had made them.
// All three return 0 in a build without SDL (-DGFX_SDL) or with no display, and then nothing else here has an effect: the game runs headless as before.
// Call them from the thread that called gfx_init (SDL and GL both want one thread).
GFX_API int gfx_window_open( int visible );
GFX_API int gfx_window_is_open( void );
GFX_API void gfx_window_close( void );
GFX_API int gfx_present( void );

#endif
