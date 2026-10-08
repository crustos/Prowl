/* gfx2d_ui.h: what the UI layer of gfx2d.h (textures, meshes, clip, fonts, input) needs from a renderer. The layer itself, gfx2d_ui.c, is the same C on the
 * desktop (gfx2d.c) and on a page (gfx2d_web.c); each of them gives it the few functions that touch the GPU. */
#ifndef GFX2D_UI_H
#define GFX2D_UI_H

#include <stdint.h>

typedef struct GfxUiBackend
{
    void (*texture_create)(int id, int width, int height, int filter, const uint8_t *rgba);
    void (*texture_update)(int id, int x, int y, int w, int h, const uint8_t *image); /* `image` is the whole picture */
    void (*texture_free)(int id);
    void (*triangles)(const float *vertices, int count, int texture); /* over the frame so far */
    void (*clip)(int x0, int y0, int x1, int y1);                      /* pixels from the TOP left, end exclusive: 0,0,w,h is no clip */
    void (*poll)(void);                                                 /* may be NULL: takes what the page has received and gfxui_push()es it */
    void (*effect)(int id, const float *params);                        /* may be NULL: one pass over the frame so far, inside the clip; params: GFX_FX_PARAMS floats */
} GfxUiBackend;

/* gfx_init calls attach (with the picture's size), gfx_shutdown calls detach, gfx_draw calls new_frame before it draws. */
void gfxui_attach(const GfxUiBackend *be, int width, int height);
void gfxui_detach(void);
void gfxui_new_frame(void);
/* the draw calls of the frame so far that the UI made: GFX_STAT_VERTICES */
int gfxui_vertices(void);
int gfxui_events_dropped(void);
/* a backend reports input */
int gfxui_push(int type, int a, int b, int c, int d);
void gfxui_close(void);

#endif
