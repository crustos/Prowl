/* gfx_fx_ref.c: the effects' CPU reference as a tiny shared library for tools/gfx_fx_test.py (ctypes).
 *   cc -O2 -ffp-contract=off -shared -fPIC -I Native/Gfx2D -o libgfxfxref.so tools/gfx_fx_ref.c -lm */
#include <stdlib.h>
#include "gfx2d_fx_ref.h"

int fxref_apply(int id, const float *params, uint8_t *rgba, int w, int h, int x0, int y0, int x1, int y1)
{
    return gfx_fx_ref_apply(id, params, rgba, w, h, x0, y0, x1, y1);
}
