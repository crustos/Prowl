/* gfx2d_web: gfx2d.h for WebAssembly in a browser. The batch is converted exactly as the desktop renderer's (gfx2d_common.h); drawing is WebGPU or WebGL2, done by
 * web/prowl_web.js, which the page gives the module as the "gfx" import namespace:
 *
 *     int  gfx_web_init(int w, int h, const unsigned char *atlas_rgba, int side, const unsigned short *rects, int sprites)   1, or 0 without WebGL2
 *     void gfx_web_draw(const void *sprites, int n, float left, float bottom, float right, float top, float r, float g, float b)
 *     void gfx_web_read(unsigned char *rgba)         the canvas as it was just drawn, RGBA, rows bottom to top (what glReadPixels gives); on WebGPU, which cannot
 *                                                    read synchronously, the newest picture that has come back from the GPU
 *     void gfx_web_shutdown(void)
 *
 * No file system on the page, so gfx_save_frame writes nothing (it returns 0). The pixels are read back only when something asks for them (gfx_pixel,
 * gfx_frame_hash), so a game that only draws pays nothing for them. */
#include <stdint.h>
#include <stdlib.h>

#include "gfx2d.h"
#include "gfx2d_common.h"
#include "gfx2d_ui.h"

#define WEB_IMPORT(name) __attribute__((import_module("gfx"), import_name(#name)))
WEB_IMPORT(gfx_web_init) int gfx_web_init(int w, int h, const unsigned char *atlas, int side, const unsigned short *rects, int sprites);
WEB_IMPORT(gfx_web_draw) void gfx_web_draw(const void *sprites, int n, float l, float b, float r, float t, float cr, float cg, float cb);
WEB_IMPORT(gfx_web_read) void gfx_web_read(unsigned char *rgba);
WEB_IMPORT(gfx_web_shutdown) void gfx_web_shutdown(void);
/* the UI layer (gfx2d_ui.c): textures are given whole (the page copies from `rgba`), meshes and the clip are drawn at once over the frame so far, and the page's
 * input is taken one event at a time: gfx_web_poll writes five ints at `out5` and returns 1, or returns 0 when there is none */
WEB_IMPORT(gfx_web_texture_create) void gfx_web_texture_create(int id, int w, int h, int filter, const unsigned char *rgba);
WEB_IMPORT(gfx_web_texture_update) void gfx_web_texture_update(int id, int x, int y, int w, int h, const unsigned char *image);
WEB_IMPORT(gfx_web_texture_free) void gfx_web_texture_free(int id);
WEB_IMPORT(gfx_web_triangles) void gfx_web_triangles(const float *vertices, int count, int texture);
WEB_IMPORT(gfx_web_clip) void gfx_web_clip(int x0, int y0, int x1, int y1);
WEB_IMPORT(gfx_web_poll) int gfx_web_poll(int *out5);
/* an effect (gfx_effect): one pass over the picture so far, inside the clip; `params` are GFX_FX_PARAMS floats, read at once */
WEB_IMPORT(gfx_web_effect) void gfx_web_effect(int id, const float *params);

static int g_w, g_h, g_ready, g_dirty, g_drawn;
static float g_cx, g_cy, g_half = 5.f, g_bgr, g_bgg, g_bgb;
static unsigned char *g_pixels;
static EngineGpuSprite g_sprites[GFX_MAX_SPRITES];

static void ui_triangles(const float *v, int n, int t) { gfx_web_triangles(v, n, t); g_dirty = 1; }
static void ui_poll(void)
{
    int e[5];
    while (gfx_web_poll(e))
        gfxui_push(e[0], e[1], e[2], e[3], e[4]);
}
static void ui_effect(int id, const float *params) { gfx_web_effect(id, params); g_dirty = 1; }
static const GfxUiBackend g_ui = { gfx_web_texture_create, gfx_web_texture_update, gfx_web_texture_free, ui_triangles, gfx_web_clip, ui_poll, ui_effect };

int gfx_init(int width, int height)
{
    if (g_ready || width < 1 || height < 1 || width > 8192 || height > 8192)
        return 0;
    build_atlas();
    g_pixels = (unsigned char *)calloc((size_t)width * (size_t)height, 4);
    if (!g_pixels || !gfx_web_init(width, height, g_atlas, ATLAS_SIDE, g_rects, 2))
        return 0;
    g_w = width;
    g_h = height;
    g_ready = 1;
    gfxui_attach(&g_ui, width, height);
    return 1;
}

void gfx_shutdown(void)
{
    if (!g_ready)
        return;
    gfxui_detach();
    gfx_web_shutdown();
    free(g_pixels);
    g_pixels = 0;
    g_ready = 0;
}

void gfx_camera(float centerX, float centerY, float halfHeight, float r, float g, float b)
{
    g_cx = centerX; g_cy = centerY; g_half = halfHeight; g_bgr = r; g_bgg = g; g_bgb = b;
}

int gfx_draw(const float *sprites, int count)
{
    int n;
    float half_w;
    if (!g_ready)
        return 0;
    if (count < 0)
        count = 0;
    if (count > GFX_MAX_SPRITES)
        count = GFX_MAX_SPRITES;
    if (sprites == NULL)
        count = 0;
    gfxui_new_frame();
    memcpy(g_batch, sprites, (size_t)count * GFX_SPRITE_FLOATS * sizeof(float));
    g_count = count;
    n = gfx_collect_sprites(g_sprites, GFX_MAX_SPRITES);
    half_w = g_half * ((float)g_w / (float)g_h);          /* the view of gles3_render.h's g3_refresh_camera_bounds */
    if (half_w < 0.01f)
        half_w = 0.01f;
    gfx_web_draw(g_sprites, n, g_cx - half_w, g_cy - g_half, g_cx + half_w, g_cy + g_half, g_bgr, g_bgg, g_bgb);
    g_drawn = n;
    g_dirty = 1;
    return n;
}

static void sync(void)
{
    if (g_ready && g_dirty) {
        gfx_web_read(g_pixels);
        g_dirty = 0;
    }
}

uint32_t gfx_pixel(int x, int y)
{
    const unsigned char *p;
    if (!g_ready || x < 0 || y < 0 || x >= g_w || y >= g_h)
        return 0;
    sync();
    p = &g_pixels[((g_h - 1 - y) * g_w + x) * 4];
    return ((uint32_t)p[3] << 24) | ((uint32_t)p[0] << 16) | ((uint32_t)p[1] << 8) | (uint32_t)p[2];
}

int gfx_frame_hash(void)
{
    uint32_t h = 2166136261u;
    size_t i, n = (size_t)g_w * (size_t)g_h * 4;
    if (!g_ready)
        return 0;
    sync();
    for (i = 0; i < n; i++)
        h = (h ^ g_pixels[i]) * 16777619u;
    return (int)h;
}

int gfx_save_frame(int index) { (void)index; return 0; }   /* no file system on a page */

int gfx_stat(int which)
{
    if (which == GFX_STAT_DRAW_CALLS)
        return g_drawn > 0;
    if (which == GFX_STAT_SPRITES)
        return g_count;
    if (which == GFX_STAT_BYTES_UPLOADED)
        return g_drawn * (int)sizeof(EngineGpuSprite);    /* every frame uploads the whole batch */
    if (which == GFX_STAT_VERTICES)
        return gfxui_vertices();
    if (which == GFX_STAT_EVENTS_DROPPED)
        return gfxui_events_dropped();
    return 0;
}
