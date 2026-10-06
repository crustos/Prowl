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

#include "gfx2d_common.h"

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

int engine_collect_gpu_sprites(EngineGpuSprite *out, int max) { return gfx_collect_sprites(out, max); }

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
