/* gfx2d_ui.c: the UI layer of gfx2d.h: the texture table, mesh drawing, the clip, and the input queue. Everything that is not the GPU; see gfx2d_ui.h. */
#include <stdlib.h>
#include <string.h>

#include "gfx2d.h"
#include "gfx2d_font.h"
#include "gfx2d_ui.h"

#define GFX_FX_WANT_INFO /* the effects' names and defaults (generated from fx/*.fx) */
#include "gfx2d_fx_gen.h"

typedef struct Tex { int used, w, h, filter; } Tex;

static const GfxUiBackend *g_be;
static int g_w, g_h;
static Tex g_tex[GFX_MAX_TEXTURES];
static int g_clip[4];
static int g_nverts;
static int g_nfx; /* effects queued since the frame began */
static int g_events[GFX_MAX_EVENTS][5];
static int g_ev_head, g_ev_count, g_ev_dropped;

void gfxui_attach(const GfxUiBackend *be, int width, int height)
{
    static const uint8_t white[4] = { 255, 255, 255, 255 };
    g_be = be;
    g_w = width;
    g_h = height;
    memset(g_tex, 0, sizeof g_tex);
    g_tex[0].used = 1;
    g_tex[0].w = g_tex[0].h = 1;
    g_tex[0].filter = GFX_FILTER_NEAREST;
    g_be->texture_create(0, 1, 1, GFX_FILTER_NEAREST, white);
    g_clip[0] = g_clip[1] = 0;
    g_clip[2] = width;
    g_clip[3] = height;
    g_nverts = 0;
    g_ev_head = g_ev_count = g_ev_dropped = 0;
}

void gfxui_detach(void)
{
    int i;
    if (!g_be)
        return;
    for (i = 0; i < GFX_MAX_TEXTURES; i++)
        if (g_tex[i].used)
            g_be->texture_free(i);
    memset(g_tex, 0, sizeof g_tex);
    gfx_font_reset();
    g_be = 0;
}

void gfxui_new_frame(void)
{
    if (!g_be)
        return;
    g_clip[0] = g_clip[1] = 0;
    g_clip[2] = g_w;
    g_clip[3] = g_h;
    g_nverts = 0;
    g_nfx = 0;
    g_be->clip(0, 0, g_w, g_h);
}

int gfxui_vertices(void) { return g_nverts; }
int gfxui_events_dropped(void) { return g_ev_dropped; }

int gfx_texture(int width, int height, int filter, const uint8_t *rgba)
{
    int id;
    uint8_t *blank = 0;
    if (!g_be || width < 1 || height < 1 || width > GFX_MAX_SIZE || height > GFX_MAX_SIZE)
        return 0;
    for (id = 1; id < GFX_MAX_TEXTURES && g_tex[id].used; id++) {}
    if (id >= GFX_MAX_TEXTURES)
        return 0;
    if (rgba == NULL) {
        blank = (uint8_t *)calloc((size_t)width * (size_t)height, 4);
        if (!blank)
            return 0;
        rgba = blank;
    }
    g_tex[id].used = 1;
    g_tex[id].w = width;
    g_tex[id].h = height;
    g_tex[id].filter = filter == GFX_FILTER_LINEAR ? GFX_FILTER_LINEAR : GFX_FILTER_NEAREST;
    g_be->texture_create(id, width, height, g_tex[id].filter, rgba);
    free(blank);
    return id;
}

void gfx_texture_update(int id, int x, int y, int width, int height, const uint8_t *rgba)
{
    if (!g_be || rgba == NULL || id < 1 || id >= GFX_MAX_TEXTURES || !g_tex[id].used)
        return;
    if (x < 0) { width += x; x = 0; }
    if (y < 0) { height += y; y = 0; }
    if (x + width > g_tex[id].w) width = g_tex[id].w - x;
    if (y + height > g_tex[id].h) height = g_tex[id].h - y;
    if (width <= 0 || height <= 0)
        return;
    g_be->texture_update(id, x, y, width, height, rgba);
}

void gfx_texture_free(int id)
{
    if (!g_be || id < 1 || id >= GFX_MAX_TEXTURES || !g_tex[id].used)
        return;
    g_be->texture_free(id);
    g_tex[id].used = 0;
}

int gfx_triangles(const float *vertices, int count, int texture)
{
    if (!g_be || vertices == NULL || count < 3)
        return 0;
    if (count > GFX_MAX_VERTICES)
        count = GFX_MAX_VERTICES;
    count -= count % 3;
    if (count <= 0)
        return 0;
    if (texture < 0 || texture >= GFX_MAX_TEXTURES || !g_tex[texture].used)
        texture = 0;
    g_be->triangles(vertices, count, texture);
    g_nverts += count;
    return count;
}

int gfx_clip(int x, int y, int width, int height)
{
    int x0, y0, x1, y1;
    if (!g_be)
        return 0;
    x0 = x < 0 ? 0 : (x > g_w ? g_w : x);
    y0 = y < 0 ? 0 : (y > g_h ? g_h : y);
    x1 = width <= 0 ? x0 : ((long long)x + width > g_w ? g_w : x + width);
    y1 = height <= 0 ? y0 : ((long long)y + height > g_h ? g_h : y + height);
    if (x1 < x0) x1 = x0;
    if (y1 < y0) y1 = y0;
    if (x0 == g_clip[0] && y0 == g_clip[1] && x1 == g_clip[2] && y1 == g_clip[3])
        return 1;
    g_clip[0] = x0; g_clip[1] = y0; g_clip[2] = x1; g_clip[3] = y1;
    g_be->clip(x0, y0, x1, y1);
    return 1;
}

int gfx_effect(int effect, const float *params, int count)
{
    float slot[GFX_FX_PARAMS];
    int i, given;
    if (!g_be || g_be->effect == NULL || effect < 1 || effect > GFX_FX_ID_MAX || gfx_fx_info[effect].name == NULL || g_nfx >= GFX_MAX_EFFECTS)
        return 0;
    given = params == NULL || count < 0 ? 0 : (count > GFX_FX_PARAMS ? GFX_FX_PARAMS : count);
    for (i = 0; i < GFX_FX_PARAMS; i++) {
        float v = i < given ? params[i] : gfx_fx_info[effect].defaults[i];
        if (!(v >= -1e30f && v <= 1e30f)) /* not a number, or beyond any use: the default (every backend then sees only finite values) */
            v = gfx_fx_info[effect].defaults[i];
        slot[i] = v;
    }
    g_nfx++;
    g_be->effect(effect, slot);
    return 1;
}

int gfx_clip_reset(void) { return gfx_clip(0, 0, g_w, g_h); }

/* ---- input ------------------------------------------------------------------------------------------------------------------ */

int gfxui_push(int type, int a, int b, int c, int d)
{
    int *e;
    if (g_ev_count >= GFX_MAX_EVENTS) {
        g_ev_dropped++;
        return 0;
    }
    e = g_events[(g_ev_head + g_ev_count) % GFX_MAX_EVENTS];
    e[0] = type; e[1] = a; e[2] = b; e[3] = c; e[4] = d;
    g_ev_count++;
    return 1;
}

void gfxui_close(void) { gfxui_push(GFX_EVENT_CLOSE, 0, 0, 0, 0); }

int gfx_poll_event(int *out5)
{
    if (!g_be || out5 == NULL)
        return 0;
    if (g_ev_count == 0 && g_be->poll != NULL)
        g_be->poll();
    if (g_ev_count == 0)
        return 0;
    memcpy(out5, g_events[g_ev_head], 5 * sizeof(int));
    g_ev_head = (g_ev_head + 1) % GFX_MAX_EVENTS;
    g_ev_count--;
    return 1;
}

int gfx_inject_event(int type, int a, int b, int c, int d)
{
    if (!g_be || type < GFX_EVENT_MOUSE_MOVE || type > GFX_EVENT_CLOSE)
        return 0;
    return gfxui_push(type, a, b, c, d);
}
