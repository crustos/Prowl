/* gfx_fx_web_scene.c: the effects' test scene as a WebAssembly reactor for a page (tools/gfx_fx_test.py --web builds and drives it). The page's script writes the
 * scene (sprites, a mesh), an effect and its floats into the module's memory through the pointers below, and steps one frame: the frame draws the scene, then
 * (optionally) a clip, an effect, a second effect and a mesh after them. */
#include <stdio.h>

#include "gfx2d.h"

static float g_sprites[GFX_MAX_SPRITES * GFX_SPRITE_FLOATS], g_mesh[256 * GFX_MESH_VERTEX_FLOATS], g_late[16 * GFX_MESH_VERTEX_FLOATS];
static float g_p1[GFX_FX_PARAMS], g_p2[GFX_FX_PARAMS];
static int g_cfg[16]; /* nsprites, nmesh, effect1, effect2, clip x, y, w, h (w < 0: no clip), nlate */

#define EXPORT(n) __attribute__((export_name(n)))
EXPORT("fxt_sprites") float *fxt_sprites(void) { return g_sprites; }
EXPORT("fxt_mesh") float *fxt_mesh(void) { return g_mesh; }
EXPORT("fxt_late") float *fxt_late(void) { return g_late; }
EXPORT("fxt_p1") float *fxt_p1(void) { return g_p1; }
EXPORT("fxt_p2") float *fxt_p2(void) { return g_p2; }
EXPORT("fxt_cfg") int *fxt_cfg(void) { return g_cfg; }

EXPORT("prowl_init") int prowl_init(void)
{
    if (!gfx_init(160, 120)) {
        printf("gfx_init failed\n");
        return 1;
    }
    gfx_camera(0.f, 0.f, 4.f, 0.08f, 0.12f, 0.2f);
    return 0;
}

EXPORT("prowl_frame") void prowl_frame(void)
{
    gfx_draw(g_sprites, g_cfg[0]);
    if (g_cfg[1] > 0)
        gfx_triangles(g_mesh, g_cfg[1], 0);
    if (g_cfg[6] >= 0)
        gfx_clip(g_cfg[4], g_cfg[5], g_cfg[6], g_cfg[7]);
    if (g_cfg[2] > 0)
        printf("effect %d -> %d\n", g_cfg[2], gfx_effect(g_cfg[2], g_p1, GFX_FX_PARAMS));
    if (g_cfg[3] > 0)
        gfx_effect(g_cfg[3], g_p2, GFX_FX_PARAMS);
    if (g_cfg[8] > 0)
        gfx_triangles(g_late, g_cfg[8], 0);
    fflush(stdout);
}
