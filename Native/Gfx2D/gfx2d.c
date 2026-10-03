/* gfx2d: see gfx2d.h. The renderer is Crust's (gles3_batch.h); this file is the host it expects, plus the headless context. */
#include <EGL/egl.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "gfx2d.h"
#include "gles3_batch.h" /* from crust/examples/unity_pack: also pulls in gles3_render.h and, through it, our engine_draw.h */

/* ---- the camera: the renderer reads these ----------------------------------------------------------------------------------- */
float Camera_main_pos_x = 0.f;
float Camera_main_pos_y = 0.f;
float Camera_main_pos_z = -10.f;
float Camera_main_orthographicSize = 5.f;
float Camera_main_nearClipPlane = 0.3f;
float Camera_main_farClipPlane = 1000.f;
float Camera_main_background_r = 0.f;
float Camera_main_background_g = 0.f;
float Camera_main_background_b = 0.f;
float Camera_main_aspect = 0.f; /* 0: the target's */
float Camera_main_rect_x = 0.f;
float Camera_main_rect_y = 0.f;
float Camera_main_rect_w = 1.f;
float Camera_main_rect_h = 1.f;
int Camera_main_y_down = 0;

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

int engine_atlas_side(void) { build_atlas(); return ATLAS_SIDE; }
int engine_atlas_page_count(void) { return 1; }
const unsigned char *engine_atlas_rgba(int page) { build_atlas(); return page == 0 ? g_atlas : 0; }
const unsigned char *engine_atlas_normal_rgba(int page) { (void)page; return 0; }
int engine_sprite_count(void) { return 2; }
const unsigned short *engine_sprite_uv_table(void) { build_atlas(); return g_rects; }
const unsigned char *engine_sprite_page_table(void) { return g_pages; }

/* no lights, and nothing of the per-sprite path */
int engine_collect_lights2d(EngineLight2D *out, int max) { (void)out; (void)max; return 0; }
const float *engine_light2d_points(int *count) { if (count) *count = 0; return 0; }
int engine_collect_draws(EngineDraw *out, int max) { (void)out; (void)max; return 0; }
int engine_texture_count(void) { return 0; }
int engine_texture_width(int id) { (void)id; return 0; }
int engine_texture_height(int id) { (void)id; return 0; }
const unsigned char *engine_texture_rgba(int id) { (void)id; return 0; }

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

int engine_collect_gpu_sprites(EngineGpuSprite *out, int max)
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

int engine_collect_gpu_sprites_stable(EngineGpuSprite *out, unsigned *keys, int max)
{
    int i, n = engine_collect_gpu_sprites(out, max); /* only used with GB_GPU_SORT, which gfx2d does not set */
    for (i = 0; i < n; i++)
        keys[i] = (unsigned)out[i].layer << 16;
    return n;
}

/* ---- the context: EGL, headless ---------------------------------------------------------------------------------------------- */
static int g_w, g_h;
static unsigned char *g_pixels;
static EGLDisplay g_dpy;
static EGLContext g_ctx;
static GLuint g_fbo, g_rbo;
static int g_ready;

int gfx_init(int width, int height)
{
    EGLint cfg_attribs[] = { EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
                             EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_NONE };
    EGLint ctx_attribs[] = { EGL_CONTEXT_MAJOR_VERSION, 3, EGL_CONTEXT_MINOR_VERSION, 1, EGL_NONE };
    EGLConfig cfg;
    EGLint major, minor, num_config;

    if (g_ready || width < 1 || height < 1 || width > 8192 || height > 8192)
        return 0;
    /* headless: no window system is needed or wanted (set only if the caller chose nothing) */
    setenv("EGL_PLATFORM", "surfaceless", 0);
    g_dpy = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    if (g_dpy == EGL_NO_DISPLAY || !eglInitialize(g_dpy, &major, &minor))
        return 0;
    if (!eglBindAPI(EGL_OPENGL_ES_API) || !eglChooseConfig(g_dpy, cfg_attribs, &cfg, 1, &num_config) || num_config < 1)
        return 0;
    g_ctx = eglCreateContext(g_dpy, cfg, EGL_NO_CONTEXT, ctx_attribs);
    if (g_ctx == EGL_NO_CONTEXT || !eglMakeCurrent(g_dpy, EGL_NO_SURFACE, EGL_NO_SURFACE, g_ctx))
        return 0;

    glGenFramebuffers(1, &g_fbo);
    glBindFramebuffer(GL_FRAMEBUFFER, g_fbo);
    glGenRenderbuffers(1, &g_rbo);
    glBindRenderbuffer(GL_RENDERBUFFER, g_rbo);
    glRenderbufferStorage(GL_RENDERBUFFER, GL_RGBA8, width, height);
    glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, g_rbo);
    if (glCheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
        return 0;
    g_w = width;
    g_h = height;
    g_pixels = (unsigned char *)calloc((size_t)width * (size_t)height, 4);
    if (!g_pixels || !gb_init())
        return 0;
    g_ready = 1;
    return 1;
}

void gfx_shutdown(void)
{
    if (!g_ready)
        return;
    free(g_pixels);
    g_pixels = 0;
    eglMakeCurrent(g_dpy, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    eglDestroyContext(g_dpy, g_ctx);
    eglTerminate(g_dpy);
    g_ready = 0;
}

void gfx_camera(float centerX, float centerY, float halfHeight, float r, float g, float b)
{
    Camera_main_pos_x = centerX;
    Camera_main_pos_y = centerY;
    Camera_main_orthographicSize = halfHeight;
    Camera_main_background_r = r;
    Camera_main_background_g = g;
    Camera_main_background_b = b;
}

int gfx_draw(const float *sprites, int count)
{
    int n;
    if (!g_ready)
        return 0;
    if (count < 0)
        count = 0;
    if (count > GFX_MAX_SPRITES)
        count = GFX_MAX_SPRITES;
    memcpy(g_batch, sprites, (size_t)count * GFX_SPRITE_FLOATS * sizeof(float));
    g_count = count;
    n = gb_draw(g_w, g_h);
    glFinish();
    glReadPixels(0, 0, g_w, g_h, GL_RGBA, GL_UNSIGNED_BYTE, g_pixels);
    return n;
}

uint32_t gfx_pixel(int x, int y)
{
    const unsigned char *p;
    if (!g_ready || x < 0 || y < 0 || x >= g_w || y >= g_h)
        return 0;
    p = &g_pixels[((g_h - 1 - y) * g_w + x) * 4]; /* GL's rows run bottom to top */
    return ((uint32_t)p[3] << 24) | ((uint32_t)p[0] << 16) | ((uint32_t)p[1] << 8) | (uint32_t)p[2];
}

int gfx_frame_hash(void)
{
    uint32_t h = 2166136261u;
    size_t i, n = (size_t)g_w * (size_t)g_h * 4;
    if (!g_ready)
        return 0;
    for (i = 0; i < n; i++)
        h = (h ^ g_pixels[i]) * 16777619u;
    return (int)h;
}

int gfx_save_frame(int index)
{
    char path[64];
    FILE *f;
    int y, x;
    if (!g_ready)
        return 0;
    snprintf(path, sizeof path, "frame_%04d.ppm", index);
    f = fopen(path, "wb");
    if (!f)
        return 0;
    fprintf(f, "P6\n%d %d\n255\n", g_w, g_h);
    for (y = g_h - 1; y >= 0; y--)
        for (x = 0; x < g_w; x++) {
            const unsigned char *p = &g_pixels[(y * g_w + x) * 4];
            fputc(p[0], f);
            fputc(p[1], f);
            fputc(p[2], f);
        }
    fclose(f);
    return 1;
}

int gfx_stat(int which)
{
    if (which == GFX_STAT_DRAW_CALLS)
        return gb_draw_calls;
    if (which == GFX_STAT_SPRITES)
        return g_count;
    if (which == GFX_STAT_BYTES_UPLOADED)
        return (int)gb_uploaded_bytes;
    return 0;
}
