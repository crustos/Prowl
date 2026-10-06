#!/usr/bin/env python3
"""wasm_build.py -- the WebAssembly (wasm32-wasi) half of `python3 build.py player GAME --wasm`.

Box2D-Packed and the pb2_* shim are compiled with clang for wasm32-wasi into two archives (Build/Native/wasm32/), and the translated player.c is
linked with them into one module, run under node (WASI) by the host script that CC# --wasm already uses (DotNetAnywhere's tools/run_wasm.mjs).

Box2D is built without SIMD (BOX2D_DISABLE_SIMD, the scalar path): Box2D v3 is deterministic across its SIMD paths, so the native player (SSE2) and
this one print the same thing. -ffp-contract=off is as in the native build.
"""
import glob
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROWL = os.path.dirname(os.path.dirname(HERE))
OUT = os.path.join(PROWL, "Build", "Native", "wasm32")


def _ccs2c():
    sys.path.insert(0, os.path.join(os.environ.get("CCSHARP_HOME") or os.path.join(os.path.dirname(PROWL), "CCSharp"), "crust"))
    import ccs2c
    return ccs2c


def available():
    """(True, "") when this machine can build for wasm32-wasi (and CC# is new enough to have --wasm), else (False, why)."""
    try:
        c = _ccs2c()
    except ImportError:
        return False, "no CCSharp beside this repository"
    if not hasattr(c, "wasm_available"):
        return False, "the CCSharp at %s has no --wasm (use a newer one)" % os.path.dirname(os.path.dirname(c.__file__))
    ok, why = c.wasm_available()
    if not ok:
        return False, why
    box2d = box2d_dir()
    if not os.path.exists(os.path.join(box2d, "CMakeLists.txt")):
        return False, "Box2D-Packed is not at %s (python3 build.py deps)" % box2d
    return True, ""


def box2d_dir():
    return os.environ.get("BOX2D_HOME") or os.path.join(os.path.dirname(PROWL), "box2d")


def _cflags(c):
    return c._wasm_cflags() + ["-O2", "-ffp-contract=off", "-w", "-std=gnu17", "-DBOX2D_DISABLE_SIMD"]


def _compile(cc, flags, src, obj, incs):
    r = subprocess.run([cc] + flags + incs + ["-c", src, "-o", obj], capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit("wasm_build: clang rejected %s:\n%s" % (src, "\n".join(l for l in r.stderr.splitlines() if "error" in l)[:1500]))


def build_natives(force=False):
    """libbox2d.a and libprowl_box2d_static.a for wasm32-wasi under Build/Native/wasm32. Returns the folder. Rebuilt when a source is newer."""
    c = _ccs2c()
    cc = c.wasm_compiler()
    box2d = box2d_dir()
    srcs = sorted(glob.glob(os.path.join(box2d, "src", "*.c")))
    shim = os.path.join(PROWL, "Native", "Box2D", "prowl_box2d.c")
    heads = glob.glob(os.path.join(box2d, "src", "*.h")) + glob.glob(os.path.join(box2d, "include", "box2d", "*.h")) + [os.path.join(PROWL, "Native", "Box2D", "prowl_box2d.h")]
    libs = [os.path.join(OUT, "libbox2d.a"), os.path.join(OUT, "libprowl_box2d_static.a")]
    newest = max(os.path.getmtime(p) for p in srcs + heads + [shim])
    if not force and all(os.path.exists(l) and os.path.getmtime(l) >= newest for l in libs):
        return OUT
    print("   wasm natives: Box2D-Packed (%d files) and the shim, clang --target=wasm32-wasi" % len(srcs))
    shutil.rmtree(os.path.join(OUT, "o"), ignore_errors=True)
    os.makedirs(os.path.join(OUT, "o", "b2"), exist_ok=True)
    os.makedirs(os.path.join(OUT, "o", "shim"), exist_ok=True)
    flags = _cflags(c)
    incs = ["-I" + os.path.join(box2d, "include"), "-I" + os.path.join(box2d, "src")]
    from concurrent.futures import ThreadPoolExecutor
    jobs = [(s, os.path.join(OUT, "o", "b2", os.path.basename(s)[:-2] + ".o")) for s in srcs]
    with ThreadPoolExecutor(max_workers=os.cpu_count() or 4) as ex:
        list(ex.map(lambda j: _compile(cc, flags, j[0], j[1], incs), jobs))
    shim_o = os.path.join(OUT, "o", "shim", "prowl_box2d.o")
    _compile(cc, flags, shim, shim_o, incs)
    for lib, objs in ((libs[0], [j[1] for j in jobs]), (libs[1], [shim_o])):
        if os.path.exists(lib):
            os.remove(lib)
        subprocess.run(["llvm-ar", "rcs", lib] + objs, check=True)
    return OUT


def link_player(out_dir, name="prowl2d-player"):
    """Links out_dir/player.c with the wasm archives into out_dir/NAME.wasm, and writes the launcher NAME and run_wasm.mjs beside it. Returns the launcher."""
    c = _ccs2c()
    natives = build_natives()
    module = os.path.join(out_dir, name + ".wasm")
    if os.path.exists(module):
        os.remove(module)
    cmd = ([c.wasm_compiler()] + _cflags(c) + ["-I.", "-o", module, "player.c", "-L" + natives, "-lprowl_box2d_static", "-lbox2d", "-lm"]
           + ["-fuse-ld=lld", "-Wl,-z,stack-size=8388608"])
    r = subprocess.run(cmd, cwd=out_dir, capture_output=True, text=True)
    if r.returncode != 0:
        errs = [l for l in r.stderr.splitlines() if "error" in l or "undefined" in l]
        print("player_build: clang (wasm32) rejected the translated C:\n   " + "\n   ".join(e[:200] for e in errs[:8]))
        sys.exit(1)
    launcher = os.path.join(out_dir, name)
    c._wasm_package(launcher, name, c.dna_home(), False)
    return launcher, module
