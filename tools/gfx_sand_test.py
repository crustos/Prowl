#!/usr/bin/env python3
"""gfx_sand_test.py -- GPU sand (Native/Gfx2D/gfx2d_sand.inc) on the GLES 3.1 compute renderer, checked against its C reference.

    python3 tools/gfx_sand_test.py [--lib libgfx2d.so] [--quick]

The compute shaders and tools/gfx_sand_ref.c are the same rules written twice, in integer math, so they must agree exactly, cell for cell: random scenes of
several sizes (odd ones too, where the 2 x 2 blocks are cut by the grid's edge) are stepped on the GPU and by the reference and compared after 1, 2, 3, 10, 50
and 200 steps, with several seeds. Also checked: nothing is lost or made and stone never moves; the brush puts what the reference puts (fills air, erases
everything, is cut by the edge of the grid); sand alone comes to rest; the same seed gives the same cells and another seed gives other water; and the
picture drawn from a grid has, at every pixel, the colour the reference gives its cell. Needs the headless GLES renderer (python3 build.py gfx).
"""
import argparse
import ctypes
import os
import random
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

AIR, STONE, SAND, WATER = 0, 1, 2, 3
failures = 0


def fail(msg):
    global failures
    failures += 1
    if failures <= 20:
        print("FAIL " + msg)


def build_ref(tmp):
    so = os.path.join(tmp, "libgfxsandref.so")
    subprocess.check_call(["cc", "-O2", "-w", "-shared", "-fPIC", "-o", so, os.path.join(HERE, "gfx_sand_ref.c")])
    lib = ctypes.CDLL(so)
    lib.sandref_step.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_uint32, ctypes.c_uint32]
    lib.sandref_brush.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int]
    lib.sandref_color.argtypes = [ctypes.c_uint32, ctypes.POINTER(ctypes.c_int)]
    return lib


def load_gfx(path):
    g = ctypes.CDLL(path)
    g.gfx_init.argtypes = [ctypes.c_int, ctypes.c_int]
    g.gfx_camera.argtypes = [ctypes.c_float] * 6
    g.gfx_draw.argtypes = [ctypes.c_void_p, ctypes.c_int]
    g.gfx_pixel.argtypes = [ctypes.c_int, ctypes.c_int]
    g.gfx_pixel.restype = ctypes.c_uint32
    g.gfx_sand_init.argtypes = [ctypes.c_int, ctypes.c_int]
    g.gfx_sand_seed.argtypes = [ctypes.c_int]
    g.gfx_sand_brush.argtypes = [ctypes.c_int] * 4
    g.gfx_sand_step.argtypes = [ctypes.c_int]
    g.gfx_sand_draw.argtypes = [ctypes.c_float] * 4
    g.gfx_sand_upload.argtypes = [ctypes.c_void_p]
    g.gfx_sand_download.argtypes = [ctypes.c_void_p]
    g.gfx_sand_count.argtypes = [ctypes.c_int]
    return g


def cells_array(values):
    return (ctypes.c_uint32 * len(values))(*values)


def random_scene(rnd, w, h, density):
    """cells: stone, sand and water by percent (a tuple); a random shade in each grain"""
    stone, sand, water = density
    out = []
    for _ in range(w * h):
        r = rnd.random() * 100
        e = STONE if r < stone else (SAND if r < stone + sand else (WATER if r < stone + sand + water else AIR))
        out.append(e | (rnd.randrange(16) << 8) if e else 0)
    return out


def download(gfx, n):
    buf = (ctypes.c_uint32 * n)()
    if not gfx.gfx_sand_download(buf):
        fail("download failed")
    return buf


def counts(cells):
    c = [0, 0, 0, 0]
    for v in cells:
        c[v & 255] += 1
    return c


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lib", default=os.path.join(ROOT, "Libraries", "linux-x64", "native", "libgfx2d.so"))
    ap.add_argument("--quick", action="store_true", help="fewer scenes")
    a = ap.parse_args()
    tmp = tempfile.mkdtemp()
    ref = build_ref(tmp)
    gfx = load_gfx(a.lib)
    if not gfx.gfx_init(64, 48):
        print("no GLES 3.1 here: the GPU sand cannot be tested")
        return 2
    if not gfx.gfx_sand_init(8, 8):
        print("gfx_sand_init failed: no compute shaders (run with PROWL2D_GFX_DEBUG=1 to see why)")
        return 2

    rnd = random.Random(20260110)

    # ---- 1. the GPU's steps are the reference's, cell for cell -----------------------------------------------------------------------------------
    sizes = [(16, 16), (33, 17), (64, 48), (100, 75), (7, 5), (1, 9), (257, 129)]
    densities = [(10, 25, 15), (15, 40, 0), (5, 0, 45), (30, 20, 20)]
    seeds = [0, 1, 12345, -1]
    checkpoints = [1, 2, 3, 10, 50, 200]
    scenes = 0
    compared = 0
    t_gpu = 0.0
    for (w, h) in (sizes[:4] if a.quick else sizes):
        for d in densities:
            seed = seeds[scenes % len(seeds)]
            scenes += 1
            start = random_scene(rnd, w, h, d)
            n = w * h
            if not gfx.gfx_sand_init(w, h):
                fail("init %dx%d" % (w, h))
                continue
            gfx.gfx_sand_seed(seed)
            gfx.gfx_sand_upload(cells_array(start))
            r = cells_array(start)
            done = 0
            for cp in checkpoints:
                t = time.time()
                gfx.gfx_sand_step(cp - done)
                got = download(gfx, n)
                t_gpu += time.time() - t
                for step in range(done, cp):
                    ref.sandref_step(r, w, h, step, seed & 0xFFFFFFFF)
                done = cp
                compared += 1
                if bytes(got) != bytes(r):
                    bad = sum(1 for i in range(n) if got[i] != r[i])
                    fail("%dx%d density %s seed %d: after %d steps the GPU and the reference differ in %d cells" % (w, h, d, seed, cp, bad))
                    break
            else:
                before, after = counts(start), counts(list(got))
                if before != after:
                    fail("%dx%d: grains were lost or made: %s -> %s" % (w, h, before, after))
                if any((start[i] & 255) == STONE and (got[i] & 255) != STONE for i in range(n)):
                    fail("%dx%d: stone moved" % (w, h))
    print("steps      %d scenes, %d comparisons with the reference, all cells, GPU time %.2fs" % (scenes, compared, t_gpu))

    # ---- 2. the brush -----------------------------------------------------------------------------------------------------------------------------
    brushes = 0
    for (w, h) in [(40, 30), (33, 17)]:
        for k in range(6 if a.quick else 14):
            start = random_scene(rnd, w, h, (10, 20, 10))
            gfx.gfx_sand_init(w, h)
            gfx.gfx_sand_upload(cells_array(start))
            r = cells_array(start)
            for _ in range(3):
                cx, cy, rad, el = rnd.randrange(-5, w + 5), rnd.randrange(-5, h + 5), rnd.randrange(0, 9), rnd.choice([AIR, STONE, SAND, WATER])
                gfx.gfx_sand_brush(cx, cy, rad, el)
                ref.sandref_brush(r, w, h, cx, cy, rad, el)
                brushes += 1
            if bytes(download(gfx, w * h)) != bytes(r):
                fail("brush on %dx%d (case %d) differs from the reference" % (w, h, k))
    print("brush      %d brush strokes match the reference (fill air, erase all, cut by the edge)" % brushes)

    # ---- 3. sand alone comes to rest -------------------------------------------------------------------------------------------------------------
    w, h = 48, 40
    gfx.gfx_sand_init(w, h)
    gfx.gfx_sand_seed(5)
    gfx.gfx_sand_brush(24, 34, 8, SAND)
    gfx.gfx_sand_step(600)
    one = bytes(download(gfx, w * h))
    gfx.gfx_sand_step(1)
    two = bytes(download(gfx, w * h))
    gfx.gfx_sand_step(1)
    three = bytes(download(gfx, w * h))
    if not (one == two == three):
        fail("a pile of sand was still moving after 600 steps")
    print("rest       a pile of sand is %s after 600 steps" % ("still" if one == two == three else "STILL MOVING"))

    # ---- 4. the seed -----------------------------------------------------------------------------------------------------------------------------
    w, h = 40, 30
    start = random_scene(rnd, w, h, (8, 5, 35))
    hashes = []
    for seed in (3, 3, 4):
        gfx.gfx_sand_init(w, h)
        gfx.gfx_sand_seed(seed)
        gfx.gfx_sand_upload(cells_array(start))
        gfx.gfx_sand_step(30)
        hashes.append(gfx.gfx_sand_hash())
    if hashes[0] != hashes[1]:
        fail("the same seed gave different cells")
    if hashes[0] == hashes[2]:
        fail("another seed gave the same water")
    print("seed       same seed same cells: %s; another seed other water: %s" % (hashes[0] == hashes[1], hashes[0] != hashes[2]))

    # ---- 5. the picture: every pixel is the reference's colour for its cell -------------------------------------------------------------------------------
    gfx.gfx_shutdown if False else None
    W, H = 64, 48
    gfx.gfx_camera(0.0, 0.0, H / 2.0, 0.10, 0.20, 0.30)               # the visible world is x -32..32, y -24..24: a 64 x 48 grid fits it pixel for pixel
    start = random_scene(rnd, W, H, (15, 25, 20))
    gfx.gfx_sand_init(W, H)
    gfx.gfx_sand_upload(cells_array(start))
    gfx.gfx_draw(None, 0)
    gfx.gfx_sand_draw(-32.0, -24.0, 32.0, 24.0)
    bg = (round(0.10 * 255), round(0.20 * 255), round(0.30 * 255))
    worst = 0
    for y in range(H):
        for x in range(W):
            cell = start[y * W + x]
            rgba = (ctypes.c_int * 4)()
            ref.sandref_color(cell, rgba)
            al = rgba[3] / 255.0
            want = [round(rgba[i] * al + bg[i] * (1 - al)) if rgba[3] else bg[i] for i in range(3)]
            p = gfx.gfx_pixel(x, H - 1 - y)                            # gfx_pixel counts rows from the top
            got = [(p >> 16) & 255, (p >> 8) & 255, p & 255]
            worst = max(worst, max(abs(got[i] - want[i]) for i in range(3)))
    if worst > 2:
        fail("the drawn grid differs from the reference colours by up to %d" % worst)
    print("draw       %d x %d pixels, worst difference from the reference colours %d (of 255)" % (W, H, worst))

    # ---- speed (for the record) -------------------------------------------------------------------------------------------------------------------------------
    gfx.gfx_sand_init(256, 256)
    gfx.gfx_sand_brush(128, 200, 60, SAND)
    gfx.gfx_sand_brush(128, 100, 40, WATER)
    t = time.time()
    gfx.gfx_sand_step(500)
    gfx.gfx_sand_count(SAND)                                           # reading waits for the GPU
    dt = time.time() - t
    print("speed      256 x 256, 500 steps: %.2fs (%.1f ms a step; this is Mesa's software GL, a GPU is far faster)" % (dt, dt * 2))

    print("all tests passed" if failures == 0 else "FAILURES: %d" % failures)
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
