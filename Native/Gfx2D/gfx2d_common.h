/* gfx2d_common.h: what both renderers of gfx2d.h share, so the batch means the same on a desktop (Crust's GLES 3.1 batch renderer, gfx2d.c) and on
 * the web (WebGL2, gfx2d_web.c + web/prowl_gfx.js): the procedural atlas, the batch the game hands over, and its conversion to the renderer's 24-byte
 * sprites (sorted by layer, half-float extents, 16-bit rotation, tint bytes). Included once by each; it defines statics. */
#ifndef GFX2D_COMMON_H
#define GFX2D_COMMON_H
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include "gfx2d.h"
#include "engine_draw.h"

/* ---- the atlas: two sprites, drawn here, once ------------------------------------------------------------------------------- */
/* Sprite 0 is a white box, sprite 1 a white disc whose edge is anti-aliased into its alpha. The batch's tint colours them. Each is surrounded by two
 * pixels of its own edge pixels, so linear sampling at the border never reads a neighbour (the packer's atlas does the same). */
#define ATLAS_SIDE 128
#define BOX_X 4
#define BOX_Y 4
#define BOX_SIZE 32
#define DISC_X 44
#define DISC_Y 4
#define DISC_SIZE 64

static unsigned char g_atlas[ATLAS_SIDE * ATLAS_SIDE * 4];
static unsigned short g_rects[2 * 4];
static unsigned char g_pages[2];
static int g_atlas_ready;

static float disc_alpha(int x, int y)
{
    float c = (DISC_SIZE - 1) * 0.5f;
    float d = sqrtf((x - c) * (x - c) + (y - c) * (y - c));
    float a = c + 0.5f - d; /* 1 inside, 0 outside, a pixel wide ramp at the edge */
    return a < 0.f ? 0.f : (a > 1.f ? 1.f : a);
}

static int clampi(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

static void build_atlas(void)
{
    int x, y;
    if (g_atlas_ready)
        return;
    for (y = 0; y < ATLAS_SIDE; y++) {
        for (x = 0; x < ATLAS_SIDE; x++) {
            unsigned char *p = &g_atlas[(y * ATLAS_SIDE + x) * 4];
            int in_box = x >= BOX_X - 2 && x < BOX_X + BOX_SIZE + 2 && y >= BOX_Y - 2 && y < BOX_Y + BOX_SIZE + 2;
            int in_disc = x >= DISC_X - 2 && x < DISC_X + DISC_SIZE + 2 && y >= DISC_Y - 2 && y < DISC_Y + DISC_SIZE + 2;
            p[0] = p[1] = p[2] = 255;
            p[3] = 0;
            if (in_box) {
                p[3] = 255; /* a solid box: its edge pixels are solid, so the border is too */
            } else if (in_disc) {
                int sx = clampi(x - DISC_X, 0, DISC_SIZE - 1), sy = clampi(y - DISC_Y, 0, DISC_SIZE - 1);
                p[3] = (unsigned char)lrintf(disc_alpha(sx, sy) * 255.f);
            }
        }
    }
    /* the sprite table: each rectangle normalised to 0..65535 */
#define U16(v) ((unsigned short)lrintf((float)(v) / (float)ATLAS_SIDE * 65535.f))
    g_rects[0] = U16(BOX_X); g_rects[1] = U16(BOX_Y); g_rects[2] = U16(BOX_X + BOX_SIZE); g_rects[3] = U16(BOX_Y + BOX_SIZE);
    g_rects[4] = U16(DISC_X); g_rects[5] = U16(DISC_Y); g_rects[6] = U16(DISC_X + DISC_SIZE); g_rects[7] = U16(DISC_Y + DISC_SIZE);
#undef U16
    g_pages[0] = g_pages[1] = 0;
    g_atlas_ready = 1;
}

/* ---- the batch the game handed over, as the renderer's sprites -------------------------------------------------------------- */
static float g_batch[GFX_MAX_SPRITES * GFX_SPRITE_FLOATS];
static int g_count;

/* IEEE half, rounded to nearest even */
static unsigned short to_half(float f)
{
    union { float f; unsigned int u; } v;
    unsigned int sign, mant;
    int exp;
    v.f = f;
    sign = (v.u >> 16) & 0x8000u;
    exp = (int)((v.u >> 23) & 0xff) - 127 + 15;
    mant = v.u & 0x7fffffu;
    if (exp <= 0) {
        if (exp < -10)
            return (unsigned short)sign;
        mant = (mant | 0x800000u) >> (1 - exp);
        if (mant & 0x1000u)
            mant += 0x2000u;
        return (unsigned short)(sign | (mant >> 13));
    }
    if (exp >= 31)
        return (unsigned short)(sign | 0x7bffu);
    if ((mant & 0x1fffu) > 0x1000u || ((mant & 0x1fffu) == 0x1000u && (mant & 0x2000u)))
        mant += 0x2000u;
    if (mant & 0x800000u) {
        mant = 0;
        exp++;
    }
    return (unsigned short)(sign | ((unsigned)exp << 10) | (mant >> 13));
}

static unsigned char to_byte(float v)
{
    v = v < 0.f ? 0.f : (v > 1.f ? 1.f : v);
    return (unsigned char)lrintf(v * 255.f);
}

static int by_layer(const void *a, const void *b)
{
    int ia = *(const int *)a, ib = *(const int *)b;
    int la = (int)g_batch[ia * GFX_SPRITE_FLOATS + 10], lb = (int)g_batch[ib * GFX_SPRITE_FLOATS + 10];
    if (la != lb)
        return la - lb;
    return ia - ib; /* equal layers keep the order they were given in */
}

static int gfx_collect_sprites(EngineGpuSprite *out, int max)
{
    static int order[GFX_MAX_SPRITES];
    int i, n = g_count < max ? g_count : max;
    for (i = 0; i < n; i++)
        order[i] = i;
    qsort(order, (size_t)n, sizeof order[0], by_layer);
    for (i = 0; i < n; i++) {
        const float *s = &g_batch[order[i] * GFX_SPRITE_FLOATS];
        EngineGpuSprite *o = &out[i];
        int rot = (int)lrintf(s[4] * (65536.f / 6.28318530718f));
        memset(o, 0, sizeof *o);
        o->x = s[0];
        o->y = s[1];
        o->hw = to_half(s[2]);
        o->hh = to_half(s[3]);
        o->sprite = (unsigned short)((int)s[9] == GFX_SHAPE_DISC ? 1 : 0);
        o->rot = (short)(rot & 0xffff);
        o->r = to_byte(s[5]);
        o->g = to_byte(s[6]);
        o->b = to_byte(s[7]);
        o->a = to_byte(s[8]);
        o->layer = (unsigned char)clampi((int)s[10], 0, 255);
    }
    return n;
}

#endif
