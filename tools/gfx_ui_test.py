#!/usr/bin/env python3
"""
gfx_ui_test.py - tests the UI layer of Native/Gfx2D (textures, meshes, clip, fonts, input) on the desktop GLES renderer, and with --web in headless Chromium.

  python3 tools/gfx_ui_test.py [--web] [--ui]

The pictures are compared with Native/Gfx2D/test/expected/*.png, drawn by an independent CPU rasteriser (Stride2D's soft backend), so a mistake in the GL code
cannot hide in both sides. Needs `python3 build.py gfx` (EGL/GLES headers and ../crust), numpy and Pillow.
"""
import os, shutil, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(ROOT, "Native", "Gfx2D")
sys.path.insert(0, HERE)
results = []


def report(name, status, why=""):
    results.append((name, status))
    print("  %-40s %-4s  %s" % (name, status, why))


def rid():
    sys.path.insert(0, ROOT)
    import importlib
    b = importlib.import_module("build")
    return b.rid()


def compare(a, b, mean_max=0.1, big_max=0.1):
    import numpy as np
    from PIL import Image
    x = np.asarray(Image.open(a).convert("RGB"), dtype=np.int16)
    y = np.asarray(Image.open(b).convert("RGB"), dtype=np.int16)
    if x.shape != y.shape:
        return False, "sizes differ"
    d = np.abs(x - y)
    mean, big, worst = d.mean(), 100.0 * (d.max(axis=2) > 24).mean(), int(d.max())
    return mean <= mean_max and big <= big_max, "mean %.3f  big %.3f%%  worst %d" % (mean, big, worst)


def build_test(work, scene):
    lib = os.path.join(ROOT, "Build", "Native", rid(), "gfx2d", "libgfx2d_static.a")
    if not os.path.exists(lib): sys.exit("gfx_ui_test: run `python3 build.py gfx` first (%s is missing)" % lib)
    exe = os.path.join(work, scene + "_test")
    t = os.path.join(SRC, "test")
    r = subprocess.run(["cc", "-std=gnu99", "-O2", "-ffp-contract=off", "-o", exe, os.path.join(t, "font_test.c"), os.path.join(t, scene + "_scene.c"), lib, "-lEGL", "-lGLESv2", "-lm"],
                       capture_output=True, text=True)
    if r.returncode: sys.exit("gfx_ui_test: " + r.stderr[-300:])
    return exe


def native_input(work):
    lib = os.path.join(ROOT, "Build", "Native", rid(), "gfx2d", "libgfx2d_static.a")
    exe = os.path.join(work, "input_test")
    r = subprocess.run(["cc", "-std=gnu99", "-O2", "-o", exe, os.path.join(SRC, "test", "input_test.c"), lib, "-lEGL", "-lGLESv2", "-lm"], capture_output=True, text=True)
    if r.returncode: return report("input queue and textures", "FAIL", r.stderr[-200:])
    r = subprocess.run([exe], cwd=work, capture_output=True, text=True)
    report("input queue and textures", "ok" if r.returncode == 0 and "input ok" in r.stdout else "FAIL", (r.stdout.strip().splitlines() or [r.stderr[-100:]])[-1][:100])


def ui_text(work):
    """UIText (Prowl.Core2D/UI) in a game, translated to C by CCSharp and run against .NET (build.py player --verify); the font scene it draws must be the reference one."""
    r = subprocess.run([sys.executable, os.path.join(ROOT, "build.py"), "player", "Samples/UIText2D", "--verify", "--run"], cwd=ROOT, capture_output=True, text=True)
    last = [l for l in r.stdout.splitlines() if l.startswith("verify")]
    report("UIText: translated C == .NET", "ok" if r.returncode == 0 and last and "ok" in last[-1] else "FAIL", (last or r.stdout.splitlines() or [r.stderr[-100:]])[-1][:120])
    ok, msg = compare(os.path.join(SRC, "test", "expected", "font.png"), os.path.join(ROOT, "Build", "Player", "UIText2D", "frame_0000.ppm"))
    report("UIText: font scene vs the CPU rasteriser", "ok" if ok else "FAIL", msg)


def native(work):
    from PIL import Image
    pics = {}
    for scene in ("font", "clip"):
        exe = build_test(work, scene)
        r = subprocess.run([exe], cwd=work, capture_output=True, text=True)
        if r.returncode: report(scene + " scene (GLES)", "FAIL", (r.stdout + r.stderr)[-150:]); continue
        r2 = subprocess.run([exe], cwd=work, capture_output=True, text=True)
        same = [l for l in r.stdout.splitlines() if l.startswith("hash")] == [l for l in r2.stdout.splitlines() if l.startswith("hash")]
        pic = os.path.join(work, scene + ".ppm")
        shutil.move(os.path.join(work, "frame_0000.ppm"), pic)
        pics[scene] = pic
        report(scene + " scene is repeatable", "ok" if same else "FAIL")
        ok, msg = compare(os.path.join(SRC, "test", "expected", scene + ".png"), pic)
        report(scene + " scene vs the CPU rasteriser", "ok" if ok else "FAIL", msg)
        if scene == "font":
            nums = [l for l in r.stdout.splitlines() if l.startswith("font ")]
            good = len(nums) == 4 and all(("size %d" % s) in n and "?-fallback 0" in n for s, n in zip((14, 20, 28, 40), nums))
            report("font metrics", "ok" if good else "FAIL", nums[0][:80] if nums else "")
        else:
            im = Image.open(pic).convert("RGB")
            bg = im.getpixel((2, 2))
            hidden = [im.getpixel((x, y)) for x in range(266, 334, 4) for y in range(190, 226, 4)]
            outside = [im.getpixel((x, y)) for x in range(30, 90, 3) for y in range(126, 152, 2)]
            report("clip hides what it should", "ok" if all(p == bg for p in hidden) and all(p == bg for p in outside) else "FAIL")
    return pics


def main():
    ap = __import__("argparse").ArgumentParser(description=__doc__, formatter_class=__import__("argparse").RawDescriptionHelpFormatter)
    ap.add_argument("--web", action="store_true"); ap.add_argument("--ui", action="store_true", help="also build and run Samples/UIText2D (needs CCSharp and the .NET SDK)")
    a = ap.parse_args()
    try:
        import numpy, PIL  # noqa
    except ImportError:
        sys.exit("gfx_ui_test: needs numpy and Pillow")
    work = tempfile.mkdtemp(prefix="gfx_ui_test_")
    try:
        native_input(work)
        pics = native(work)
        if a.ui: ui_text(work)
        if a.web: web(work, pics)
    finally:
        shutil.rmtree(work, ignore_errors=True)
    bad = [n for n, s in results if s == "FAIL"]
    print("\n%d ok, %d skipped, %d failed" % (sum(s == "ok" for _, s in results), sum(s == "skip" for _, s in results), len(bad)))
    sys.exit(1 if bad else 0)


def web(work, pics):
    """Each scene as a page, drawn by WebGL2 and by WebGPU in headless Chromium (tools/web_test.mjs), compared with the same reference picture."""
    from PIL import Image
    if not (shutil.which("clang") and shutil.which("node")):
        return report("web (wasm build)", "skip", "needs clang (wasm32-wasi) and node")
    for scene in ("font", "clip"):
        page = os.path.join(work, scene + "_page")
        os.makedirs(page)
        srcs = [os.path.join(SRC, f) for f in ("gfx2d_web.c", "gfx2d_ui.c", "gfx2d_font.c", "gfx2d_font_data.c")] + [os.path.join(SRC, "test", f) for f in (scene + "_scene.c", "web_entry.c")]
        r = subprocess.run(["clang", "--target=wasm32-wasi", "-mexec-model=reactor", "-O2", "-ffp-contract=off", "-fuse-ld=lld", "-w", "-I", SRC, "-o", os.path.join(page, "prowl2d-player.wasm")]
                           + srcs + ["-Wl,--allow-undefined"], capture_output=True, text=True)
        if r.returncode:
            report(scene + " scene (wasm build)", "FAIL", r.stderr.strip()[-200:]); continue
        html = open(os.path.join(SRC, "web", "index.html")).read().replace("__PROWL_FILES__", "[]")
        open(os.path.join(page, "index.html"), "w").write(html)
        shutil.copy(os.path.join(SRC, "web", "prowl_web.js"), page)
        ref = os.path.join(work, scene + "_ref.ppm")
        Image.open(os.path.join(SRC, "test", "expected", scene + ".png")).convert("RGB").save(ref)
        for api in ("webgl2", "webgpu"):
            cmd = ["node", os.path.join(HERE, "web_test.mjs"), page, "--frames", "1", "--ref", ref, "--gfx", api, "--tol", "24"]
            if api == "webgpu" and not os.environ.get("DISPLAY"):
                if not shutil.which("xvfb-run"): report("%s scene %s" % (scene, api), "skip", "needs xvfb-run"); continue
                cmd = ["xvfb-run", "-a"] + cmd
            r = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
            out = r.stdout.strip().splitlines()
            line = next((l for l in out if l.startswith("compare")), out[-1] if out else r.stderr[-120:])
            if r.returncode == 2: report("%s scene %s" % (scene, api), "skip", "playwright is not installed"); continue
            report("%s scene %s vs the CPU rasteriser" % (scene, api), "ok" if r.returncode == 0 else "FAIL", line[:150])


if __name__ == "__main__":
    main()
