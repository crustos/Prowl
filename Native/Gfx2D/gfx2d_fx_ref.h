/* gfx2d_fx_ref.h: the CPU reference of the effects (fx/*.fx, the `--- c` sections), for tests and tools. The renderer itself runs GLSL / WGSL; this is the same
 * effects written for the CPU, generated into gfx2d_fx_gen.h by tools/gfx_fx_gen.py. tools/gfx_fx_test.py compares a frame the GPU made with what this makes of the
 * frame before the effect. */
#ifndef GFX2D_FX_REF_H
#define GFX2D_FX_REF_H

#include <math.h>
#include <stdint.h>
#include <string.h>

#include "gfx2d.h"

static float clamp01(float v) { return v < 0.f ? 0.f : (v > 1.f ? 1.f : v); }
static uint8_t to_u8(float v) { return (uint8_t)(clamp01(v) * 255.f + 0.5f); }
/* sinf and friends differ in the last bit between C libraries: in double they agree, so the reference is the same everywhere */
static float sin_det(float x) { return (float)sin((double)x); }
static float cos_det(float x) { return (float)cos((double)x); }
static float pow_det(float x, float y) { return (float)pow((double)x, (double)y); }
static float atan2_det(float y, float x) { return (float)atan2((double)y, (double)x); }

/* the gradient curves (0 ease in-out, 1 linear, 2 ease in, 3 ease out), and a premultiplied colour pm, with alpha a, put over the picture colour c (opacity op) */
static float fxc_curve(int ty, float t)
{
    if (ty == 1) return t;
    if (ty == 2) return t * t;
    if (ty == 3) return 1.f - (1.f - t) * (1.f - t);
    return (-2.f * t + 3.f) * (t * t);
}
static void fxc_over_pm(float *c, const float *pm, float a, float op)
{
    int i;
    for (i = 0; i < 3; i++)
        c[i] = pm[i] * op + c[i] * (1.f - a * op);
}
static void fxc_over(float *c, const float *c1, const float *c2, float f, float op)
{
    float pm[3];
    int i;
    for (i = 0; i < 3; i++)
        pm[i] = c1[i] * c1[3] * (1.f - f) + c2[i] * c2[3] * f;
    fxc_over_pm(c, pm, c1[3] * (1.f - f) + c2[3] * f, op);
}

#define GFX_FX_WANT_SOFT
#include "gfx2d_fx_gen.h"

/* One effect over the rectangle [x0, x1) x [y0, y1) (pixels from the TOP left) of `rgba` (w x h, rows top first, 4 bytes a pixel). The picture is read as it was
 * before the effect (a copy) and written back rounded and clamped. Returns 1, or 0 for an id without a reference. */
static int gfx_fx_ref_apply(int id, const float *params, uint8_t *rgba, int w, int h, int x0, int y0, int x1, int y1)
{
    int x, y, k;
    uint8_t *src;
    if (id < 1 || id > GFX_FX_ID_MAX || gfx_fx_soft[id] == NULL)
        return 0;
    src = (uint8_t *)malloc((size_t)w * (size_t)h * 4);
    if (!src)
        return 0;
    memcpy(src, rgba, (size_t)w * (size_t)h * 4);
    for (y = y0; y < y1; y++)
        for (x = x0; x < x1; x++) {
            size_t at = ((size_t)y * (size_t)w + (size_t)x) * 4;
            float c[4], v = ((float)y + 0.5f) / (float)h;
            for (k = 0; k < 4; k++)
                c[k] = src[at + k] * (1.f / 255.f);
            gfx_fx_soft[id](params, c, ((float)x + 0.5f) / (float)w, v, (float)x + 0.5f, v * (float)h, (float)w, (float)h);
            for (k = 0; k < 4; k++)
                rgba[at + k] = to_u8(c[k]);
        }
    free(src);
    return 1;
}

#endif
