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

#define WEB_IMPORT(name) __attribute__((import_module("gfx"), import_name(#name)))
WEB_IMPORT(gfx_web_init) int gfx_web_init(int w, int h, const unsigned char *atlas, int side, const unsigned short *rects, int sprites);
WEB_IMPORT(gfx_web_draw) void gfx_web_draw(const void *sprites, int n, float l, float b, float r, float t, float cr, float cg, float cb);
WEB_IMPORT(gfx_web_read) void gfx_web_read(unsigned char *rgba);
WEB_IMPORT(gfx_web_shutdown) void gfx_web_shutdown(void);

static int g_w, g_h, g_ready, g_dirty, g_drawn;
static float g_cx, g_cy, g_half = 5.f, g_bgr, g_bgg, g_bgb;
static unsigned char *g_pixels;
static EngineGpuSprite g_sprites[GFX_MAX_SPRITES];

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
    return 1;
}

void gfx_shutdown(void)
{
    if (!g_ready)
        return;
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
    return 0;
}
