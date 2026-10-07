/* gfx2d: see gfx2d.h. The renderer is Crust's (gles3_batch.h); this file is the host it expects, plus the headless context. */
#include <EGL/egl.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "gfx2d.h"
#include "gfx2d_ui.h"
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
static int g_dirty; /* a frame has been drawn that g_pixels does not hold yet: the pixels are read back only when something asks */

/* ---- the UI layer's drawing: meshes with textures over the frame, and the clip (gfx2d_ui.c has the rest) ---------------------------------------------- */
static GLuint g_mesh_prog, g_mesh_vao, g_mesh_vbo;
static GLint g_mesh_view, g_mesh_tex;
static GLuint g_ui_tex[GFX_MAX_TEXTURES];
static int g_ui_texw[GFX_MAX_TEXTURES];

static const char *MESH_VS =
    "#version 300 es\n"
    "layout(location = 0) in vec2 a_pos;\n"
    "layout(location = 1) in vec2 a_uv;\n"
    "layout(location = 2) in vec4 a_col;\n"
    "uniform vec4 u_view;\n" /* left, bottom, right, top in world units */
    "out vec2 v_uv;\n"
    "out vec4 v_col;\n"
    "void main() {\n"
    "  vec2 n = (a_pos - u_view.xy) / (u_view.zw - u_view.xy);\n"
    "  gl_Position = vec4(n * 2.0 - 1.0, 0.0, 1.0);\n"
    "  v_uv = a_uv;\n"
    "  v_col = a_col;\n"
    "}\n";
static const char *MESH_FS =
    "#version 300 es\n"
    "precision highp float;\n"
    "uniform sampler2D u_tex;\n"
    "in vec2 v_uv;\n"
    "in vec4 v_col;\n"
    "out vec4 o_col;\n"
    "void main() { o_col = texture(u_tex, v_uv) * v_col; }\n";

static GLuint ui_shader(GLenum type, const char *src)
{
    GLuint s = glCreateShader(type);
    GLint ok = 0;
    glShaderSource(s, 1, &src, 0);
    glCompileShader(s);
    glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok) {
        glDeleteShader(s);
        return 0;
    }
    return s;
}

static int ui_gl_init(void)
{
    GLuint v = ui_shader(GL_VERTEX_SHADER, MESH_VS), f = ui_shader(GL_FRAGMENT_SHADER, MESH_FS);
    GLint ok = 0;
    if (!v || !f)
        return 0;
    g_mesh_prog = glCreateProgram();
    glAttachShader(g_mesh_prog, v);
    glAttachShader(g_mesh_prog, f);
    glLinkProgram(g_mesh_prog);
    glDeleteShader(v);
    glDeleteShader(f);
    glGetProgramiv(g_mesh_prog, GL_LINK_STATUS, &ok);
    if (!ok)
        return 0;
    g_mesh_view = glGetUniformLocation(g_mesh_prog, "u_view");
    g_mesh_tex = glGetUniformLocation(g_mesh_prog, "u_tex");
    glGenVertexArrays(1, &g_mesh_vao);
    glGenBuffers(1, &g_mesh_vbo);
    glBindVertexArray(g_mesh_vao);
    glBindBuffer(GL_ARRAY_BUFFER, g_mesh_vbo);
    glBufferData(GL_ARRAY_BUFFER, (GLsizeiptr)GFX_MAX_VERTICES * GFX_MESH_VERTEX_FLOATS * sizeof(float), 0, GL_DYNAMIC_DRAW);
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, GFX_MESH_VERTEX_FLOATS * sizeof(float), (const void *)0);
    glVertexAttribPointer(1, 2, GL_FLOAT, GL_FALSE, GFX_MESH_VERTEX_FLOATS * sizeof(float), (const void *)(2 * sizeof(float)));
    glVertexAttribPointer(2, 4, GL_FLOAT, GL_FALSE, GFX_MESH_VERTEX_FLOATS * sizeof(float), (const void *)(4 * sizeof(float)));
    glEnableVertexAttribArray(0);
    glEnableVertexAttribArray(1);
    glEnableVertexAttribArray(2);
    glBindVertexArray(0);
    return 1;
}

static void ui_texture_create(int id, int w, int h, int filter, const uint8_t *rgba)
{
    GLint f = filter == GFX_FILTER_LINEAR ? GL_LINEAR : GL_NEAREST;
    glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
    glActiveTexture(GL_TEXTURE0);
    glGenTextures(1, &g_ui_tex[id]);
    glBindTexture(GL_TEXTURE_2D, g_ui_tex[id]);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, rgba);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, f);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, f);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    g_ui_texw[id] = w;
}

static void ui_texture_update(int id, int x, int y, int w, int h, const uint8_t *image)
{
    if (!g_ui_tex[id])
        return;
    glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, g_ui_tex[id]);
    glPixelStorei(GL_UNPACK_ROW_LENGTH, g_ui_texw[id]); /* `image` is the whole picture: read the rectangle out of it in place */
    glPixelStorei(GL_UNPACK_SKIP_PIXELS, x);
    glPixelStorei(GL_UNPACK_SKIP_ROWS, y);
    glTexSubImage2D(GL_TEXTURE_2D, 0, x, y, w, h, GL_RGBA, GL_UNSIGNED_BYTE, image);
    glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
    glPixelStorei(GL_UNPACK_SKIP_PIXELS, 0);
    glPixelStorei(GL_UNPACK_SKIP_ROWS, 0);
}

static void ui_texture_free(int id)
{
    if (g_ui_tex[id])
        glDeleteTextures(1, &g_ui_tex[id]);
    g_ui_tex[id] = 0;
}

static void ui_triangles(const float *vertices, int count, int texture)
{
    glBindFramebuffer(GL_FRAMEBUFFER, g_fbo);
    glViewport(0, 0, g_w, g_h);
    glEnable(GL_BLEND); /* straight alpha into the colour; the picture's own alpha stays as the background made it */
    glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ZERO, GL_ONE);
    glUseProgram(g_mesh_prog);
    glUniform4f(g_mesh_view, g3_left, g3_bottom, g3_right, g3_top);
    glUniform1i(g_mesh_tex, 0);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, g_ui_tex[texture] ? g_ui_tex[texture] : g_ui_tex[0]);
    glBindVertexArray(g_mesh_vao);
    glBindBuffer(GL_ARRAY_BUFFER, g_mesh_vbo);
    glBufferSubData(GL_ARRAY_BUFFER, 0, (GLsizeiptr)count * GFX_MESH_VERTEX_FLOATS * sizeof(float), vertices);
    glDrawArrays(GL_TRIANGLES, 0, count);
    glBindVertexArray(0);
    g_dirty = 1;
}

static void ui_clip(int x0, int y0, int x1, int y1)
{
    if (x0 == 0 && y0 == 0 && x1 == g_w && y1 == g_h) {
        glDisable(GL_SCISSOR_TEST);
        return;
    }
    glEnable(GL_SCISSOR_TEST);
    glScissor(x0, g_h - y1, x1 - x0, y1 - y0); /* GL counts rows from the bottom */
}

static const GfxUiBackend g_ui = { ui_texture_create, ui_texture_update, ui_texture_free, ui_triangles, ui_clip, 0 };

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
    if (!g_pixels || !gb_init() || !ui_gl_init())
        return 0;
    g_ready = 1;
    gfxui_attach(&g_ui, width, height);
    return 1;
}

void gfx_shutdown(void)
{
    if (!g_ready)
        return;
    gfxui_detach();
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
    if (sprites == NULL)
        count = 0;
    gfxui_new_frame(); /* a new frame has no clip (and the clear below must not be clipped either) */
    memcpy(g_batch, sprites, (size_t)count * GFX_SPRITE_FLOATS * sizeof(float));
    g_count = count;
    n = gb_draw(g_w, g_h);
    g_dirty = 1;
    return n;
}

/* the pixels are read when something asks for them, so a frame the UI is still drawing over is read once, when it is done */
static void sync(void)
{
    if (g_ready && g_dirty) {
        glBindFramebuffer(GL_FRAMEBUFFER, g_fbo);
        glFinish();
        glPixelStorei(GL_PACK_ALIGNMENT, 1);
        glReadPixels(0, 0, g_w, g_h, GL_RGBA, GL_UNSIGNED_BYTE, g_pixels);
        g_dirty = 0;
    }
}

uint32_t gfx_pixel(int x, int y)
{
    const unsigned char *p;
    if (!g_ready || x < 0 || y < 0 || x >= g_w || y >= g_h)
        return 0;
    sync();
    p = &g_pixels[((g_h - 1 - y) * g_w + x) * 4]; /* GL's rows run bottom to top */
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

int gfx_save_frame(int index)
{
    char path[64];
    FILE *f;
    int y, x;
    if (!g_ready)
        return 0;
    sync();
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
    if (which == GFX_STAT_VERTICES)
        return gfxui_vertices();
    if (which == GFX_STAT_EVENTS_DROPPED)
        return gfxui_events_dropped();
    return 0;
}
