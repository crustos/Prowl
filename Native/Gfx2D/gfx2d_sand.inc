/* gfx2d_sand.inc: GPU sand (gfx2d.h, "GPU sand"). Included by gfx2d.c, after the context and the camera, so it shares them.
 *
 * The grid is one GL_R32UI texture. A step is a compute dispatch over 2 x 2 blocks of it (a Margolus neighbourhood: the blocks move by one cell in x and y
 * every step), each invocation owning its four cells, so nothing is shared between invocations and nothing is atomic. The rules, and the hash that picks the
 * water's hops, are written twice, here in GLSL and in C in tools/gfx_sand_ref.c, and tools/gfx_sand_test.py requires the two to give the same cells.
 * Change one and the other must change with it.
 *
 * The structure of the elements (air, stone, sand, water; the shade of a grain; the colours) is the one of Prowl.Runtime/Destruction2D's SandSim, the CPU
 * simulation, so a scene looks the same in both. The two do not step alike (SandSim scans cells with a stamp per cell; this one cuts blocks), and they are
 * not meant to give the same cells. */

#define SAND_MAX_SIDE 4096

static GLuint g_sand_tex, g_sand_step_prog, g_sand_brush_prog, g_sand_draw_prog;
static GLint g_sand_u_size, g_sand_u_off, g_sand_u_pass, g_sand_u_seed;
static GLint g_sand_b_size, g_sand_b_center, g_sand_b_radius, g_sand_b_element;
static GLint g_sand_d_view, g_sand_d_rect, g_sand_d_size, g_sand_d_cells;
static int g_sand_w, g_sand_h;
static unsigned int g_sand_seed, g_sand_pass;
static int g_sand_programs; /* 1 once the three programs are built, -1 if they would not build */

/* ---- the step: one invocation per 2 x 2 block ---------------------------------------------------------------------------------------------------- */
static const char *SAND_STEP_CS =
    "#version 310 es\n"
    "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n"
    "layout(r32ui, binding = 0) uniform highp uimage2D u_cells;\n"
    "uniform ivec2 u_size;\n"
    "uniform int u_off;\n"
    "uniform uint u_pass;\n"
    "uniform uint u_seed;\n"
    /* the elements: 0 air, 1 stone, 2 sand, 3 water. Density: what may fall through what (stone is never displaced and never moves) */
    "uint dens(uint c) { uint e = c & 255u; return e == 3u ? 1u : (e == 2u ? 2u : (e == 1u ? 3u : 0u)); }\n"
    "bool movable(uint c) { uint e = c & 255u; return e == 2u || e == 3u; }\n"
    "void swp(inout uint p, inout uint q) { uint t = p; p = q; q = t; }\n"
    "bool inb(ivec2 p) { return p.x >= 0 && p.y >= 0 && p.x < u_size.x && p.y < u_size.y; }\n"
    "uint mixh(uint bx, uint by) {\n"
    "  uint h = u_seed ^ (u_pass * 2654435761u) ^ (bx * 2246822519u) ^ (by * 3266489917u);\n"
    "  h ^= h >> 15; h *= 746135789u; h ^= h >> 12; h *= 695743881u; h ^= h >> 15;\n"
    "  return h;\n"
    "}\n"
    "void main() {\n"
    "  ivec2 b = ivec2(gl_GlobalInvocationID.xy);\n"
    "  ivec2 o = b * 2 - ivec2(u_off);\n" /* the block's bottom-left cell; with an offset of 1 it is outside the grid on the left and the bottom */
    "  if (o.x >= u_size.x || o.y >= u_size.y) return;\n"
    "  ivec2 pa = o, pb = o + ivec2(1, 0), pc = o + ivec2(0, 1), pd = o + ivec2(1, 1);\n"
    "  uint a = inb(pa) ? imageLoad(u_cells, pa).r : 1u;\n" /* beyond the grid is stone */
    "  uint bb = inb(pb) ? imageLoad(u_cells, pb).r : 1u;\n"
    "  uint c = inb(pc) ? imageLoad(u_cells, pc).r : 1u;\n"
    "  uint d = inb(pd) ? imageLoad(u_cells, pd).r : 1u;\n"
    /* columns fall: the upper cell goes down when it is denser than the lower one */
    "  if (movable(c) && dens(c) > dens(a)) swp(c, a);\n"
    "  if (movable(d) && dens(d) > dens(bb)) swp(d, bb);\n"
    /* diagonals slide: what is left on top goes down to the other column */
    "  if (movable(c) && dens(c) > dens(bb)) swp(c, bb);\n"
    "  if (movable(d) && dens(d) > dens(a)) swp(d, a);\n"
    /* water hops sideways into air in a row of the block, when the hash says so; the upper row only if it rests on something */
    "  uint h = mixh(uint(b.x), uint(b.y));\n"
    "  if ((h & 65536u) != 0u) {\n"
    "    if ((a & 255u) == 3u && (bb & 255u) == 0u) swp(a, bb);\n"
    "    else if ((bb & 255u) == 3u && (a & 255u) == 0u) swp(a, bb);\n"
    "  }\n"
    "  if ((h & 131072u) != 0u && (a & 255u) != 0u && (bb & 255u) != 0u) {\n"
    "    if ((c & 255u) == 3u && (d & 255u) == 0u) swp(c, d);\n"
    "    else if ((d & 255u) == 3u && (c & 255u) == 0u) swp(c, d);\n"
    "  }\n"
    "  if (inb(pa)) imageStore(u_cells, pa, uvec4(a, 0u, 0u, 0u));\n"
    "  if (inb(pb)) imageStore(u_cells, pb, uvec4(bb, 0u, 0u, 0u));\n"
    "  if (inb(pc)) imageStore(u_cells, pc, uvec4(c, 0u, 0u, 0u));\n"
    "  if (inb(pd)) imageStore(u_cells, pd, uvec4(d, 0u, 0u, 0u));\n"
    "}\n";

/* ---- the brush: one invocation per cell of the square around the centre ------------------------------------------------------------------------- */
static const char *SAND_BRUSH_CS =
    "#version 310 es\n"
    "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n"
    "layout(r32ui, binding = 0) uniform highp uimage2D u_cells;\n"
    "uniform ivec2 u_size;\n"
    "uniform ivec2 u_center;\n"
    "uniform int u_radius;\n"
    "uniform uint u_element;\n"
    "void main() {\n"
    "  ivec2 d = ivec2(gl_GlobalInvocationID.xy) - ivec2(u_radius);\n"
    "  ivec2 p = u_center + d;\n"
    "  if (p.x < 0 || p.y < 0 || p.x >= u_size.x || p.y >= u_size.y) return;\n"
    "  if (d.x * d.x + d.y * d.y > u_radius * u_radius) return;\n"
    "  if (u_element == 0u) { imageStore(u_cells, p, uvec4(0u)); return; }\n"
    "  if ((imageLoad(u_cells, p).r & 255u) != 0u) return;\n"
    /* the shade: a hash of the cell (the same one SandSim uses for a grain's colour) */
    "  uint h = uint(p.x) * 73856093u ^ uint(p.y) * 19349663u;\n"
    "  h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;\n"
    "  imageStore(u_cells, p, uvec4(u_element | ((h & 15u) << 8), 0u, 0u, 0u));\n"
    "}\n";

/* ---- the picture: a quad over the grid's rectangle, each fragment reads its cell ----------------------------------------------------------------- */
static const char *SAND_DRAW_VS =
    "#version 300 es\n"
    "uniform vec4 u_view;\n" /* left, bottom, right, top of the picture in world units, as the mesh shader's */
    "uniform vec4 u_rect;\n" /* left, bottom, right, top of the grid */
    "out vec2 v_uv;\n"
    "void main() {\n"
    "  vec2 q = vec2(float(gl_VertexID & 1), float((gl_VertexID >> 1) & 1));\n"
    "  vec2 w = mix(u_rect.xy, u_rect.zw, q);\n"
    "  vec2 n = (w - u_view.xy) / (u_view.zw - u_view.xy);\n"
    "  gl_Position = vec4(n * 2.0 - 1.0, 0.0, 1.0);\n"
    "  v_uv = q;\n"
    "}\n";
static const char *SAND_DRAW_FS =
    "#version 300 es\n"
    "precision highp float;\n"
    "precision highp int;\n"
    "precision highp usampler2D;\n"
    "uniform usampler2D u_cells;\n"
    "uniform ivec2 u_size;\n"
    "in vec2 v_uv;\n"
    "out vec4 o_col;\n"
    "void main() {\n"
    "  ivec2 c = min(ivec2(floor(v_uv * vec2(u_size))), u_size - ivec2(1));\n"
    "  uint cell = texelFetch(u_cells, c, 0).r;\n"
    "  uint e = cell & 255u;\n"
    "  int v = int((cell >> 8) & 15u) - 8;\n" /* the colours of SandSim.WriteColor */
    "  if (e == 2u) o_col = vec4(float(194 + v), float(176 + v), float(120 + v), 255.0) / 255.0;\n"
    "  else if (e == 3u) o_col = vec4(float(40 + v / 2), float(100 + v), float(200 + v), 200.0) / 255.0;\n"
    "  else if (e == 1u) o_col = vec4(float(110 + v / 2), float(110 + v / 2), float(116 + v / 2), 255.0) / 255.0;\n"
    "  else discard;\n"
    "}\n";

static GLuint sand_program(const char *cs, const char *vs, const char *fs)
{
    GLuint p = glCreateProgram(), a, b = 0;
    GLint ok = 0;
    a = ui_shader(cs ? GL_COMPUTE_SHADER : GL_VERTEX_SHADER, cs ? cs : vs);
    if (fs)
        b = ui_shader(GL_FRAGMENT_SHADER, fs);
    if (!a || (fs && !b)) {
        if (a) glDeleteShader(a);
        if (b) glDeleteShader(b);
        glDeleteProgram(p);
        return 0;
    }
    glAttachShader(p, a);
    if (b)
        glAttachShader(p, b);
    glLinkProgram(p);
    glDeleteShader(a);
    if (b)
        glDeleteShader(b);
    glGetProgramiv(p, GL_LINK_STATUS, &ok);
    if (!ok) {
        glDeleteProgram(p);
        return 0;
    }
    return p;
}

static int sand_programs(void)
{
    if (g_sand_programs)
        return g_sand_programs > 0;
    g_sand_step_prog = sand_program(SAND_STEP_CS, 0, 0);
    g_sand_brush_prog = sand_program(SAND_BRUSH_CS, 0, 0);
    g_sand_draw_prog = sand_program(0, SAND_DRAW_VS, SAND_DRAW_FS);
    if (!g_sand_step_prog || !g_sand_brush_prog || !g_sand_draw_prog) {
        if (getenv("PROWL2D_GFX_DEBUG"))
            fprintf(stderr, "gfx2d: the sand shaders did not build (compute %d, brush %d, draw %d)\n", g_sand_step_prog != 0, g_sand_brush_prog != 0, g_sand_draw_prog != 0);
        g_sand_programs = -1;
        return 0;
    }
    g_sand_u_size = glGetUniformLocation(g_sand_step_prog, "u_size");
    g_sand_u_off = glGetUniformLocation(g_sand_step_prog, "u_off");
    g_sand_u_pass = glGetUniformLocation(g_sand_step_prog, "u_pass");
    g_sand_u_seed = glGetUniformLocation(g_sand_step_prog, "u_seed");
    g_sand_b_size = glGetUniformLocation(g_sand_brush_prog, "u_size");
    g_sand_b_center = glGetUniformLocation(g_sand_brush_prog, "u_center");
    g_sand_b_radius = glGetUniformLocation(g_sand_brush_prog, "u_radius");
    g_sand_b_element = glGetUniformLocation(g_sand_brush_prog, "u_element");
    g_sand_d_view = glGetUniformLocation(g_sand_draw_prog, "u_view");
    g_sand_d_rect = glGetUniformLocation(g_sand_draw_prog, "u_rect");
    g_sand_d_size = glGetUniformLocation(g_sand_draw_prog, "u_size");
    g_sand_d_cells = glGetUniformLocation(g_sand_draw_prog, "u_cells");
    g_sand_programs = 1;
    return 1;
}

void gfx_sand_free(void)
{
    if (g_sand_tex)
        glDeleteTextures(1, &g_sand_tex);
    g_sand_tex = 0;
    g_sand_w = g_sand_h = 0;
}

int gfx_sand_init(int width, int height)
{
    int *zeros;
    if (!g_ready || width < 1 || height < 1 || width > SAND_MAX_SIDE || height > SAND_MAX_SIDE)
        return 0;
    if (!sand_programs())
        return 0;
    gfx_sand_free();
    zeros = (int *)calloc((size_t)width * (size_t)height, sizeof(int));
    if (!zeros)
        return 0;
    glGenTextures(1, &g_sand_tex);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, g_sand_tex);
    glTexStorage2D(GL_TEXTURE_2D, 1, GL_R32UI, width, height);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST); /* an integer texture can only be read unfiltered */
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
    glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, width, height, GL_RED_INTEGER, GL_UNSIGNED_INT, zeros);
    free(zeros);
    g_sand_w = width;
    g_sand_h = height;
    g_sand_seed = 0;
    g_sand_pass = 0;
    return glGetError() == GL_NO_ERROR;
}

void gfx_sand_seed(int seed)
{
    g_sand_seed = (unsigned int)seed;
    g_sand_pass = 0;
}

int gfx_sand_brush(int cx, int cy, int radius, int element)
{
    int side, groups;
    if (!g_sand_tex)
        return 0;
    if (radius < 0)
        radius = 0;
    if (radius > SAND_MAX_SIDE)
        radius = SAND_MAX_SIDE;
    if (element < 0 || element > 3)
        return 0;
    side = 2 * radius + 1;
    groups = (side + 7) / 8;
    glUseProgram(g_sand_brush_prog);
    glUniform2i(g_sand_b_size, g_sand_w, g_sand_h);
    glUniform2i(g_sand_b_center, cx, cy);
    glUniform1i(g_sand_b_radius, radius);
    glUniform1ui(g_sand_b_element, (GLuint)element);
    glBindImageTexture(0, g_sand_tex, 0, GL_FALSE, 0, GL_READ_WRITE, GL_R32UI);
    glDispatchCompute((GLuint)groups, (GLuint)groups, 1);
    glMemoryBarrier(GL_SHADER_IMAGE_ACCESS_BARRIER_BIT | GL_TEXTURE_FETCH_BARRIER_BIT);
    return 1;
}

int gfx_sand_step(int steps)
{
    int i;
    if (!g_sand_tex)
        return 0;
    glUseProgram(g_sand_step_prog);
    glUniform2i(g_sand_u_size, g_sand_w, g_sand_h);
    glUniform1ui(g_sand_u_seed, g_sand_seed);
    glBindImageTexture(0, g_sand_tex, 0, GL_FALSE, 0, GL_READ_WRITE, GL_R32UI);
    for (i = 0; i < steps; i++) {
        int off = (int)(g_sand_pass & 1u);
        int bx = (g_sand_w + off + 1) / 2, by = (g_sand_h + off + 1) / 2; /* blocks, counting the partial ones the offset leaves at the edges */
        glUniform1i(g_sand_u_off, off);
        glUniform1ui(g_sand_u_pass, g_sand_pass);
        glDispatchCompute((GLuint)((bx + 7) / 8), (GLuint)((by + 7) / 8), 1);
        glMemoryBarrier(GL_SHADER_IMAGE_ACCESS_BARRIER_BIT | GL_TEXTURE_FETCH_BARRIER_BIT); /* the next step reads what this one wrote */
        g_sand_pass++;
    }
    return (int)g_sand_pass;
}

int gfx_sand_draw(float left, float bottom, float right, float top)
{
    if (!g_ready || !g_sand_tex)
        return 0;
    glBindFramebuffer(GL_FRAMEBUFFER, g_fbo);
    glViewport(0, 0, g_w, g_h);
    glEnable(GL_BLEND); /* straight alpha into the colour, the picture's own alpha as the background made it: the same blend as the meshes */
    glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ZERO, GL_ONE);
    glUseProgram(g_sand_draw_prog);
    glUniform4f(g_sand_d_view, g3_left, g3_bottom, g3_right, g3_top);
    glUniform4f(g_sand_d_rect, left, bottom, right, top);
    glUniform2i(g_sand_d_size, g_sand_w, g_sand_h);
    glUniform1i(g_sand_d_cells, 0);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, g_sand_tex);
    glBindVertexArray(0);
    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
    g_dirty = 1;
    return 1;
}

int gfx_sand_upload(const int *cells)
{
    if (!g_sand_tex || !cells)
        return 0;
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, g_sand_tex);
    glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
    glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, g_sand_w, g_sand_h, GL_RED_INTEGER, GL_UNSIGNED_INT, cells);
    return glGetError() == GL_NO_ERROR;
}

int gfx_sand_download(int *cells)
{
    GLuint fbo;
    unsigned int *rgba;
    size_t i, n;
    if (!g_sand_tex || !cells)
        return 0;
    n = (size_t)g_sand_w * (size_t)g_sand_h;
    rgba = (unsigned int *)malloc(n * 4 * sizeof(unsigned int));
    if (!rgba)
        return 0;
    glMemoryBarrier(GL_ALL_BARRIER_BITS); /* what the compute shaders wrote must be visible to this read */
    glGenFramebuffers(1, &fbo);
    glBindFramebuffer(GL_FRAMEBUFFER, fbo);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, g_sand_tex, 0);
    glPixelStorei(GL_PACK_ALIGNMENT, 4);
    glReadPixels(0, 0, g_sand_w, g_sand_h, GL_RGBA_INTEGER, GL_UNSIGNED_INT, rgba); /* an integer image reads back as RGBA_INTEGER; the cell is the red */
    glBindFramebuffer(GL_FRAMEBUFFER, g_fbo);
    glDeleteFramebuffers(1, &fbo);
    for (i = 0; i < n; i++)
        cells[i] = (int)rgba[i * 4];
    free(rgba);
    return glGetError() == GL_NO_ERROR;
}

int gfx_sand_count(int element)
{
    int *cells, n = 0;
    size_t i, total = (size_t)g_sand_w * (size_t)g_sand_h;
    if (!g_sand_tex)
        return 0;
    cells = (int *)malloc(total * sizeof(int));
    if (!cells || !gfx_sand_download(cells)) {
        free(cells);
        return 0;
    }
    for (i = 0; i < total; i++)
        if ((cells[i] & 255) == element)
            n++;
    free(cells);
    return n;
}

int gfx_sand_hash(void)
{
    int *cells;
    unsigned int h = 2166136261u;
    size_t i, total = (size_t)g_sand_w * (size_t)g_sand_h;
    if (!g_sand_tex)
        return 0;
    cells = (int *)malloc(total * sizeof(int));
    if (!cells || !gfx_sand_download(cells)) {
        free(cells);
        return 0;
    }
    for (i = 0; i < total; i++)
        h = (h ^ (unsigned int)cells[i]) * 16777619u;
    free(cells);
    return (int)h;
}
