/* gfx_sand_ref.c: the C reference of GPU sand. The rules of Native/Gfx2D/gfx2d_sand.inc (the GLSL compute shaders) written a second time, in C, so that
 * tools/gfx_sand_test.py can require the GPU to give, cell for cell, what this gives. Change a rule in one and it must change in the other.
 *
 * A cell is a 32-bit value: the element in bits 0..7 (0 air, 1 stone, 2 sand, 3 water) and a shade in bits 8..11. Row 0 is the bottom. A step cuts the grid
 * into blocks of 2 x 2 cells whose corner is at (2 bx - off, 2 by - off), off being the step's parity, and rearranges each block by fixed rules. Blocks never
 * overlap, so running them one after another gives what running them all at once does. The cells beyond the grid are stone and are not written. */
#include <stdint.h>

static uint32_t dens(uint32_t c)
{
    uint32_t e = c & 255u;
    return e == 3u ? 1u : (e == 2u ? 2u : (e == 1u ? 3u : 0u));
}

static int movable(uint32_t c)
{
    uint32_t e = c & 255u;
    return e == 2u || e == 3u;
}

static void swp(uint32_t *p, uint32_t *q)
{
    uint32_t t = *p;
    *p = *q;
    *q = t;
}

static uint32_t mixh(uint32_t seed, uint32_t pass, uint32_t bx, uint32_t by)
{
    uint32_t h = seed ^ (pass * 2654435761u) ^ (bx * 2246822519u) ^ (by * 3266489917u);
    h ^= h >> 15;
    h *= 746135789u;
    h ^= h >> 12;
    h *= 695743881u;
    h ^= h >> 15;
    return h;
}

static int inb(int x, int y, int w, int h) { return x >= 0 && y >= 0 && x < w && y < h; }

/* one step: `pass` is the step's number (its parity is the offset), `seed` the seed */
void sandref_step(uint32_t *cells, int w, int h, uint32_t pass, uint32_t seed)
{
    int off = (int)(pass & 1u);
    int nbx = (w + off + 1) / 2, nby = (h + off + 1) / 2, bx, by;
    for (by = 0; by < nby; by++) {
        for (bx = 0; bx < nbx; bx++) {
            int ox = bx * 2 - off, oy = by * 2 - off;
            uint32_t a, b, c, d, hh;
            if (ox >= w || oy >= h)
                continue;
            a = inb(ox, oy, w, h) ? cells[oy * w + ox] : 1u; /* beyond the grid is stone */
            b = inb(ox + 1, oy, w, h) ? cells[oy * w + ox + 1] : 1u;
            c = inb(ox, oy + 1, w, h) ? cells[(oy + 1) * w + ox] : 1u;
            d = inb(ox + 1, oy + 1, w, h) ? cells[(oy + 1) * w + ox + 1] : 1u;
            /* columns fall */
            if (movable(c) && dens(c) > dens(a)) swp(&c, &a);
            if (movable(d) && dens(d) > dens(b)) swp(&d, &b);
            /* diagonals slide */
            if (movable(c) && dens(c) > dens(b)) swp(&c, &b);
            if (movable(d) && dens(d) > dens(a)) swp(&d, &a);
            /* water hops sideways into air */
            hh = mixh(seed, pass, (uint32_t)bx, (uint32_t)by);
            if (hh & 65536u) {
                if ((a & 255u) == 3u && (b & 255u) == 0u) swp(&a, &b);
                else if ((b & 255u) == 3u && (a & 255u) == 0u) swp(&a, &b);
            }
            if ((hh & 131072u) && (a & 255u) != 0u && (b & 255u) != 0u) {
                if ((c & 255u) == 3u && (d & 255u) == 0u) swp(&c, &d);
                else if ((d & 255u) == 3u && (c & 255u) == 0u) swp(&c, &d);
            }
            if (inb(ox, oy, w, h)) cells[oy * w + ox] = a;
            if (inb(ox + 1, oy, w, h)) cells[oy * w + ox + 1] = b;
            if (inb(ox, oy + 1, w, h)) cells[(oy + 1) * w + ox] = c;
            if (inb(ox + 1, oy + 1, w, h)) cells[(oy + 1) * w + ox + 1] = d;
        }
    }
}

/* the shade of a grain made at a cell: the hash SandSim.WriteColor uses */
uint32_t sandref_shade(int x, int y)
{
    uint32_t h = (uint32_t)x * 73856093u ^ (uint32_t)y * 19349663u;
    h ^= h >> 13;
    h *= 1274126177u;
    h ^= h >> 16;
    return h & 15u;
}

/* the brush: sand, water and stone fill the air cells within the radius, air clears them all */
void sandref_brush(uint32_t *cells, int w, int h, int cx, int cy, int radius, int element)
{
    int dx, dy;
    for (dy = -radius; dy <= radius; dy++) {
        for (dx = -radius; dx <= radius; dx++) {
            int x = cx + dx, y = cy + dy;
            if (!inb(x, y, w, h) || dx * dx + dy * dy > radius * radius)
                continue;
            if (element == 0)
                cells[y * w + x] = 0u;
            else if ((cells[y * w + x] & 255u) == 0u)
                cells[y * w + x] = (uint32_t)element | (sandref_shade(x, y) << 8);
        }
    }
}

/* the colour (r, g, b, a in 0..255) the draw shader gives a cell, or a = 0 for air: the colours of SandSim.WriteColor */
void sandref_color(uint32_t cell, int *out4)
{
    uint32_t e = cell & 255u;
    int v = (int)((cell >> 8) & 15u) - 8;
    out4[0] = out4[1] = out4[2] = out4[3] = 0;
    if (e == 2u) { out4[0] = 194 + v; out4[1] = 176 + v; out4[2] = 120 + v; out4[3] = 255; }
    else if (e == 3u) { out4[0] = 40 + v / 2; out4[1] = 100 + v; out4[2] = 200 + v; out4[3] = 200; }
    else if (e == 1u) { out4[0] = 110 + v / 2; out4[1] = 110 + v / 2; out4[2] = 116 + v / 2; out4[3] = 255; }
}
