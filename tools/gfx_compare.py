#!/usr/bin/env python3
"""gfx_compare.py -- how close are two pictures, in the way that matters for a renderer?

    python3 tools/gfx_compare.py A.ppm B.ppm [--png OUT.png]

Two correct rasterisers do not give the same bytes: a pixel whose centre is a hair inside an edge, or a bilinear weight rounded in 8 bits, differs by a few
levels in one and not the other. What must hold is that they agree everywhere that is not an edge. So this reports
    mean   the mean absolute difference per channel, 0..255
    big    the share of pixels where any channel differs by more than TOL levels (default 24): edges and nothing else should be there
    worst  the largest single-channel difference
and exits 0 if mean <= MEAN_MAX and big <= BIG_MAX (percent), else 1. --png writes A, B and the difference side by side.
"""
import argparse
import sys

try:
    import numpy as np
    from PIL import Image
except ImportError:
    sys.exit("gfx_compare: needs numpy and Pillow (pip install numpy pillow)")


def load(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.int16)


def compare(a, b, tol=24):
    if a.shape != b.shape:
        return None
    d = np.abs(a - b)
    pix = d.max(axis=2)
    return {"mean": float(d.mean()), "big": float((pix > tol).mean() * 100.0), "worst": int(d.max()), "pixels": int(pix.size), "bigcount": int((pix > tol).sum())}


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("a")
    ap.add_argument("b")
    ap.add_argument("--tol", type=int, default=24)
    ap.add_argument("--mean-max", type=float, default=0.6)
    ap.add_argument("--big-max", type=float, default=0.8, help="percent of pixels allowed to differ by more than --tol")
    ap.add_argument("--png", default=None)
    a = ap.parse_args(argv)
    A, B = load(a.a), load(a.b)
    r = compare(A, B, a.tol)
    if r is None:
        print("sizes differ: %s vs %s" % (A.shape, B.shape))
        return 1
    ok = r["mean"] <= a.mean_max and r["big"] <= a.big_max
    print("mean %.3f  big %.3f%% (%d px > %d)  worst %d   %s" % (r["mean"], r["big"], r["bigcount"], a.tol, r["worst"], "ok" if ok else "DIFFERENT"))
    if a.png:
        diff = np.clip(np.abs(A - B) * 4, 0, 255).astype(np.uint8)
        strip = np.concatenate([A.astype(np.uint8), B.astype(np.uint8), diff], axis=1)
        Image.fromarray(strip).save(a.png)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
