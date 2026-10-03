/* The host's view of what Crust's GLES 3.1 renderer (gles3_render.h, gles3_batch.h) asks of the engine it draws.
 *
 * In unity_pack this header is generated for each project, and its functions are the packed engine's. Here it is fixed, and gfx2d.c answers it from the
 * batch of sprites the game hands over: a procedural two-sprite atlas, no lights, no per-sprite textures. The structs are copied from the packer's
 * (tools/unity_pack.py and tools/unity_pack_gpu2d.py in crust) and must stay the same size and layout: the renderer reads EngineGpuSprite from the GPU
 * with these offsets. If the renderer's expectations move, gfx2d.c stops compiling here, which is the point of including it instead of copying it.
 */
#ifndef PROWL_GFX2D_ENGINE_DRAW_H
#define PROWL_GFX2D_ENGINE_DRAW_H

typedef struct EngineDraw {
    float x, y, half_w, half_h;
    float m00, m01, m10, m11; /* local XY -> world XY (full quat) */
    float r, g, b;
    float a; /* tint alpha */
    int tex; /* engine_texture_* index; -1 if none */
    int sorting_layer;
    int sorting_order;
    int flags; /* 1: lit by the 2D lights */
    int go;
} EngineDraw;

typedef struct EngineGpuSprite {
    float x, y;               /* world position */
    unsigned short hw, hh;    /* half extents, IEEE half (negative: a flip) */
    unsigned short sprite;    /* the sprite table's index; 0xffff: no texture */
    short rot;                /* rotation, 65536 a turn */
    unsigned char r, g, b, a; /* tint */
    unsigned char layer;      /* sorting layer */
    unsigned char flags;      /* 1: lit by the 2D lights */
    unsigned char effect, arg;
} EngineGpuSprite;            /* 24 bytes */

typedef struct EngineLight2D {
    float x, y;
    float r, g, b;
    int type;
    float inner, outer;
    float cos_inner, cos_outer;
    float dir_x, dir_y;
    float falloff;
    unsigned layer_mask;
    float normal_distance;
    float cos_r, sin_r;
    int shape_start, shape_count;
    float falloff_size;
    int cookie;
    float half_w, half_h;
} EngineLight2D;

int engine_atlas_side(void);
int engine_atlas_page_count(void);
const unsigned char *engine_atlas_rgba(int page);        /* side * side RGBA8 */
const unsigned char *engine_atlas_normal_rgba(int page); /* or 0 */
int engine_sprite_count(void);
const unsigned short *engine_sprite_uv_table(void);      /* u0 v0 u1 v1, 0..65535 */
const unsigned char *engine_sprite_page_table(void);
int engine_collect_gpu_sprites(EngineGpuSprite *out, int max);
int engine_collect_gpu_sprites_stable(EngineGpuSprite *out, unsigned *keys, int max);
int engine_collect_lights2d(EngineLight2D *out, int max);
const float *engine_light2d_points(int *count);

/* the per-sprite path (g3_*), which gfx2d does not use: stubs that say there is nothing */
int engine_collect_draws(EngineDraw *out, int max);
int engine_texture_count(void);
int engine_texture_width(int id);
int engine_texture_height(int id);
const unsigned char *engine_texture_rgba(int id);

#endif
