#!/usr/bin/env python3
"""gfx_fx_test.py -- the effects of gfx2d (Native/Gfx2D/fx/*.fx) on the GLES renderer, checked against their C reference.

    python3 tools/gfx_fx_test.py [--lib libgfx2d.so] [--seed N] [--web]

Each effect is run on a scene of sprites and meshes (default values, then random values inside the parameters' ranges), and the picture the GPU makes is compared
with what the C reference (tools/gfx_fx_ref.c: the `--- c` sections of the same .fx files) makes of the picture the frame had before the effect. It also checks that
an effect honours the clip, only changes what was drawn before it, and that effects run in order. Needs the headless GLES renderer (python3 build.py gfx).

--web does the same on the page's two backends: the scene is built as a WebAssembly module (clang --target=wasm32-wasi; tools/gfx_fx_web_scene.c), and headless
Chromium draws it with WebGL2 (SwiftShader) and, where there is a display (xvfb-run) and a Vulkan device (mesa-vulkan-drivers), WebGPU (tools/gfx_fx_web_test.mjs).
"""
import argparse
import ctypes
import json
import shutil
import os
import random
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)
from prowl_editor import fx as fxmod          # noqa: E402
from prowl_editor.fxdefs import EFFECTS       # noqa: E402

W, H = 160, 120
TOL = 3          # levels of 255: the GPU computes in float and rounds, the reference in float and rounds, but the two differ in the last bit now and then
MAX_EDGE = 8     # pixels (of 19200) allowed beyond TOL: an effect with a jump in it (a gradient's wrap, a hue's) puts a pixel on the other side when a last bit differs


def build_ref(tmp):
    so = os.path.join(tmp, "libgfxfxref.so")
    subprocess.check_call(["cc", "-O2", "-ffp-contract=off", "-w", "-shared", "-fPIC", "-I", os.path.join(ROOT, "Native", "Gfx2D"),
                           "-o", so, os.path.join(HERE, "gfx_fx_ref.c"), "-lm"])
    lib = ctypes.CDLL(so)
    lib.fxref_apply.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_float), ctypes.c_char_p, ctypes.c_int, ctypes.c_int,
                                ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int]
    return lib


def load_gfx(path):
    g = ctypes.CDLL(path)
    f = ctypes.POINTER(ctypes.c_float)
    g.gfx_init.argtypes = [ctypes.c_int, ctypes.c_int]
    g.gfx_camera.argtypes = [ctypes.c_float] * 6
    g.gfx_draw.argtypes = [f, ctypes.c_int]
    g.gfx_triangles.argtypes = [f, ctypes.c_int, ctypes.c_int]
    g.gfx_effect.argtypes = [ctypes.c_int, f, ctypes.c_int]
    g.gfx_clip.argtypes = [ctypes.c_int] * 4
    g.gfx_save_frame.argtypes = [ctypes.c_int]
    return g


def floats(values):
    return (ctypes.c_float * len(values))(*values)


def scene_sprites():
    r = random.Random(7)
    out = []
    for i in range(14):
        out += [r.uniform(-5, 5), r.uniform(-3, 3), r.uniform(0.4, 1.4), r.uniform(0.3, 1.0), r.uniform(0, 6.28),
                r.random(), r.random(), r.random(), 1.0, i % 2, i, 0.0]
    return out


def scene_mesh():
    """A quad with a different colour at each corner (a smooth range of colours for the effects to work on), and a translucent triangle."""
    def v(x, y, c):
        return [x, y, 0.5, 0.5] + c
    a, b, c, d = v(-3, -2, [1, 0.2, 0.1, 1]), v(3, -2, [0.1, 1, 0.3, 1]), v(3, 2, [0.2, 0.3, 1, 1]), v(-3, 2, [1, 1, 0.2, 1])
    t = [v(-1, -1, [1, 1, 1, 0.5]), v(2, 0, [0, 0, 0, 0.5]), v(0, 2, [1, 0, 1, 0.5])]
    return a + b + c + a + c + d + sum(t, [])


class Rig:
    def __init__(self, gfx, work):
        self.g, self.work, self.n = gfx, work, 0
        if not gfx.gfx_init(W, H):
            raise SystemExit("gfx_init failed: no GLES 3.1 here")
        gfx.gfx_camera(0.0, 0.0, 4.0, 0.08, 0.12, 0.2)
        self.sprites, self.mesh = floats(scene_sprites()), floats(scene_mesh())

    def base(self):
        self.g.gfx_draw(self.sprites, len(self.sprites) // 12)
        self.g.gfx_triangles(self.mesh, len(self.mesh) // 8, 0)

    def grab(self):
        self.n += 1
        self.g.gfx_save_frame(self.n)
        with open(os.path.join(self.work, "frame_%04d.ppm" % self.n), "rb") as h:
            data = h.read()
        head, rest = data.split(b"255\n", 1)
        rgb = rest[:W * H * 3]
        out = bytearray(W * H * 4)
        out[0::4], out[1::4], out[2::4], out[3::4] = rgb[0::3], rgb[1::3], rgb[2::3], b"\xff" * (W * H)
        return bytes(out)


def compare(a, b):
    worst, bad = 0, 0
    for i in range(0, len(a), 4):
        d = max(abs(a[i] - b[i]), abs(a[i + 1] - b[i + 1]), abs(a[i + 2] - b[i + 2]))
        worst = max(worst, d)
        bad += d > TOL
    return worst, bad


def param_sets(name, rnd):
    e = EFFECTS[name]
    yield "defaults", fxmod.defaults(name)
    for k in range(3):
        vals = {}
        for p in e["params"]:
            if p["type"] == "color":
                vals[p["name"]] = [rnd.random() for _ in range(3)] + [rnd.uniform(0.3, 1)]
            elif p["type"] == "bool":
                vals[p["name"]] = rnd.random() < 0.5
            elif p["type"] in ("int", "enum"):
                vals[p["name"]] = rnd.randint(int(p["min"]), int(p["max"]))
            else:
                lo, hi = max(p["min"], -1e4), min(p["max"], 1e4)
                d = p["default"][0]
                spread = min(1000.0, max(abs(d), 0.02 * (hi - lo)))     # around the default, not across the whole range: a period of 1 pixel is a sawtooth that wraps at every
                vals[p["name"]] = min(hi, max(lo, d + rnd.uniform(-0.5, 0.5) * spread))     # pixel, where a GPU and a CPU round the other way now and then
        yield "random %d" % (k + 1), vals


def web_cases(rnd):
    """The cases the page runs: (label, case, how to get the expected picture). Case 0 is the scene alone."""
    out = [("scene", {}, lambda ref, base, run: base)]
    for name in sorted(EFFECTS, key=lambda n: EFFECTS[n]["id"]):
        for tag, vals in param_sets(name, rnd):
            fid, packed = fxmod.pack(name, vals)
            out.append(("%s (%s)" % (name, tag), {"e1": fid, "p1": packed}, lambda ref, base, run, fid=fid, packed=packed: run(fid, packed, base)))
    fid, packed = fxmod.pack("hsv_adjust", {"hueShift": 90, "satScale": 1.5})
    out.append(("clip: effect only inside the rectangle", {"e1": fid, "p1": packed, "clip": [30, 20, 70, 50]},
                lambda ref, base, run: run(fid, packed, base, (30, 20, 100, 70))))
    f1, p1 = fxmod.pack("hsv_adjust", {"hueShift": 120})
    f2, p2 = fxmod.pack("levels", {})
    out.append(("two effects in order", {"e1": f1, "p1": p1, "e2": f2, "p2": p2}, lambda ref, base, run: run(f2, p2, run(f1, p1, base))))
    ft, pt = fxmod.pack("tint", {})
    out.append(("order: a draw after the effect is untouched", {"e1": ft, "p1": pt, "late": 3}, None))
    return out


def run_web(a, rnd, ref):
    """The page's backends. Returns (ok, failed)."""
    ok = bad = 0
    work = tempfile.mkdtemp(prefix="gfxfxweb")
    page = os.path.join(work, "page")
    os.makedirs(page)
    gfx_dir = os.path.join(ROOT, "Native", "Gfx2D")
    srcs = [os.path.join(HERE, "gfx_fx_web_scene.c")] + [os.path.join(gfx_dir, f) for f in ("gfx2d_web.c", "gfx2d_ui.c", "gfx2d_font.c", "gfx2d_font_data.c")]
    if not shutil.which("clang") or not shutil.which("node"):
        print("skip  web: needs clang (wasm32-wasi) and node")
        return 0, 0
    r = subprocess.run(["clang", "--target=wasm32-wasi", "-mexec-model=reactor", "-O2", "-ffp-contract=off", "-fuse-ld=lld", "-w", "-I", gfx_dir, "-o",
                        os.path.join(page, "prowl2d-player.wasm")] + srcs + ["-Wl,--allow-undefined"], capture_output=True, text=True)
    if r.returncode:
        print("FAIL  web: the wasm build:", r.stderr.strip()[-300:])
        return 0, 1
    shutil.copy(os.path.join(gfx_dir, "web", "prowl_web.js"), page)
    with open(os.path.join(gfx_dir, "web", "index.html"), encoding="utf-8") as h:
        html = h.read().replace("__PROWL_FILES__", "[]")
    with open(os.path.join(page, "index.html"), "w", encoding="utf-8") as h:
        h.write(html)
    cases = web_cases(rnd)
    job = {"sprites": scene_sprites(), "mesh": scene_mesh(), "late": [-4, -4, 0.5, 0.5, 1, 1, 1, 1, 4, -4, 0.5, 0.5, 1, 1, 1, 1, 0, 4, 0.5, 0.5, 1, 1, 1, 1],
           "cases": [c for _, c, _ in cases]}
    cf = os.path.join(work, "cases.json")
    with open(cf, "w") as h:
        json.dump(job, h)
    for api in ("webgl2", "webgpu"):
        outf = os.path.join(work, api + ".bin")
        cmd = ["node", os.path.join(HERE, "gfx_fx_web_test.mjs"), "--dir", page, "--gfx", api, "--cases", cf, "--out", outf]
        if api == "webgpu" and not os.environ.get("DISPLAY"):
            if not shutil.which("xvfb-run"):
                print("skip  %s: needs a display (xvfb-run) and a Vulkan device (mesa-vulkan-drivers)" % api)
                continue
            cmd = ["xvfb-run", "-a"] + cmd
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=900)
        line = (r.stdout.strip().splitlines() or [""])[-1]
        if r.returncode in (3, 4):
            print("skip  %s: %s" % (api, line[:150]))
            continue
        if r.returncode:
            print("FAIL  %s: %s" % (api, line[:300]))
            bad += 1
            continue
        info = json.loads(line)
        if info["backend"] != api:
            print("skip  %s: the page used %s instead" % (api, info["backend"]))
            continue
        with open(outf, "rb") as h:
            data = h.read()
        size = W * H * 4
        pics = [data[i * size:(i + 1) * size] for i in range(len(cases))]
        base = pics[0]

        def run_ref(fid, packed, pixels, clip=(0, 0, W, H)):
            buf = ctypes.create_string_buffer(pixels, len(pixels))
            ref.fxref_apply(fid, floats(packed), buf, W, H, *clip)
            return buf.raw

        for (label, case, want), got in zip(cases[1:], pics[1:]):
            if want is None:                                   # a mesh drawn after the effect keeps its own colour
                px = ((H - 8) * W + W // 2) * 4
                good = max(abs(c - 255) for c in got[px:px + 3]) <= 1
                worst, count = (0, 0) if good else (255, 1)
            else:
                worst, count = compare(got, want(ref, base, run_ref))
            good = count <= MAX_EDGE
            ok += good
            bad += not good
            print("%s  %-8s %-44s worst %d%s" % ("ok  " if good else "FAIL", api, label, worst, "" if count == 0 else ", %d pixels beyond %d" % (count, TOL)))
        if info["logs"]:
            print("      page log:", "; ".join(info["logs"])[:300])
    shutil.rmtree(work, ignore_errors=True)
    return ok, bad


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lib", default=os.environ.get("PROWL2D_GFX_LIB") or os.path.join(ROOT, "Build", "Native", "linux-x64", "gfx2d", "libgfx2d.so"))
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--web", action="store_true", help="also the page's backends, WebGL2 and WebGPU (needs clang for wasm32, node + playwright)")
    a = ap.parse_args()
    rnd = random.Random(a.seed)
    tmp = tempfile.mkdtemp(prefix="gfxfx")
    os.chdir(tmp)
    ref, rig = build_ref(tmp), None
    rig = Rig(load_gfx(a.lib), tmp)
    g = rig.g
    ok = bad = 0

    def run_ref(name_id, packed, pixels, clip=(0, 0, W, H)):
        buf = ctypes.create_string_buffer(pixels, len(pixels))
        ref.fxref_apply(name_id, floats(packed), buf, W, H, *clip)
        return buf.raw

    def report(label, worst, count):
        nonlocal ok, bad
        if count <= MAX_EDGE:
            ok += 1
            print("ok    %-44s worst %d%s" % (label, worst, "" if count == 0 else " (%d edge pixels)" % count))
        else:
            bad += 1
            print("FAIL  %-44s worst %d, %d pixels beyond %d" % (label, worst, count, TOL))

    rig.base()
    before = rig.grab()
    changed_any = {}
    for name in sorted(EFFECTS, key=lambda n: EFFECTS[n]["id"]):
        for tag, vals in param_sets(name, rnd):
            fid, packed = fxmod.pack(name, vals)
            rig.base()
            assert g.gfx_effect(fid, floats(packed), len(packed)) == 1, name
            got = rig.grab()
            want = run_ref(fid, packed, before)
            worst, count = compare(got, want)
            report("%s (%s)" % (name, tag), worst, count)
            changed_any[name] = changed_any.get(name, False) or got != before

    # an effect inside a clip changes only that rectangle
    fid, packed = fxmod.pack("hsv_adjust", {"hueShift": 90, "satScale": 1.5})
    rig.base()
    g.gfx_clip(30, 20, 70, 50)
    g.gfx_effect(fid, floats(packed), len(packed))
    got = rig.grab()
    want = run_ref(fid, packed, before, (30, 20, 100, 70))
    report("clip: effect only inside the rectangle", *compare(got, want))

    # only what was drawn BEFORE the effect: a mesh after it is not touched
    fid, packed = fxmod.pack("tint", {})
    rig.base()
    g.gfx_effect(fid, floats(packed), len(packed))
    late = floats([-4, -4, 0.5, 0.5, 1, 1, 1, 1, 4, -4, 0.5, 0.5, 1, 1, 1, 1, 0, 4, 0.5, 0.5, 1, 1, 1, 1])
    g.gfx_triangles(late, 3, 0)
    got = rig.grab()
    px = (W // 2 + 0) * 4 + (H - 8) * W * 4          # near the bottom centre: inside the late white triangle
    assert got[px:px + 3] == b"\xff\xff\xff" or max(abs(c - 255) for c in got[px:px + 3]) <= 1, "a mesh drawn after an effect was changed by it: %r" % got[px:px + 3]
    ok += 1
    print("ok    %-44s" % "order: a draw after the effect is untouched")

    # two effects in a row: the second works on what the first left
    f1, p1 = fxmod.pack("hsv_adjust", {"hueShift": 120})
    f2, p2 = fxmod.pack("levels", {})
    rig.base()
    g.gfx_effect(f1, floats(p1), len(p1))
    g.gfx_effect(f2, floats(p2), len(p2))
    got = rig.grab()
    want = run_ref(f2, p2, run_ref(f1, p1, before))
    report("two effects in order", *compare(got, want))

    # bad calls are refused, and a frame's effects are limited
    rig.base()
    assert g.gfx_effect(0, None, 0) == 0 and g.gfx_effect(9999, None, 0) == 0 and g.gfx_effect(-1, None, 0) == 0
    n = sum(g.gfx_effect(fid, None, 0) for _ in range(70))
    assert n == 64, n
    ok += 1
    print("ok    %-44s" % "refusals and the GFX_MAX_EFFECTS limit")

    silent = [n for n, c in changed_any.items() if not c]
    if silent:
        bad += 1
        print("FAIL  effects that changed nothing in any test:", ", ".join(silent))
    g.gfx_shutdown()
    if a.web:
        wok, wbad = run_web(a, random.Random(a.seed), ref)
        ok, bad = ok + wok, bad + wbad
    print("\n%d ok, %d failed" % (ok, bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
