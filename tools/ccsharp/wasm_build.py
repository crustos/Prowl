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


def gfx_web_sources(gfx_dir):
    """The renderer for a page: gfx2d_web.c, and the UI layer (textures, clip, input, fonts) that is the same C as on the desktop."""
    return [os.path.join(gfx_dir, f) for f in ("gfx2d_web.c", "gfx2d_ui.c", "gfx2d_font.c", "gfx2d_font_data.c")]


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


def link_hybrid(out_dir, c_dir, wasm, cc=None, name="prowl2d-player", gfx=False, web=False, main_class=None):
    """A game with managed classes (--dna): the translated C, the glue that calls DotNetAnywhere, the DotNetAnywhere runtime (with the native functions
    the managed code may call in its FFI table), and Box2D, into out_dir/NAME (a native executable) or out_dir/NAME.wasm with its launcher. The managed
    assembly (player.managed.dll, the name the glue looks for beside the executable) and corlib.dll are copied beside it. Returns the executable
    (or the launcher)."""
    c = _ccs2c()
    home, bdir = c.dna_prepare(wasm)
    manifest = os.path.join(c_dir, "player.ffi.json")
    flag = ["--wasm"] if wasm else []
    if os.path.exists(manifest):
        c.dna_run(flag + ["--ffi", manifest, "--lib-only", "--no-corlib"], home, bdir)
        lib = os.path.join(bdir, "libdna_ffi_wasm.a" if wasm else "libdna_ffi.a")
    else:
        lib = os.path.join(bdir, "libdna_wasm.a" if wasm else "libdna.a")
    for f in ("player.c", "player.bridge.c", "player.managed.dll", "player.ffi.json"):
        if os.path.exists(os.path.join(c_dir, f)):
            shutil.copy2(os.path.join(c_dir, f), os.path.join(out_dir, f))
    shutil.copy2(os.path.join(bdir, "corlib.dll"), out_dir)
    srcs = ["player.c", "player.bridge.c"]
    inc = ["-I.", "-I" + os.path.join(home, "native", "src")]
    if wasm and gfx and not web:
        sys.exit("player_build: a game that draws needs --web under wasm (under node there is no GL)")
    if web:
        return _link_hybrid_web(c, home, bdir, lib, out_dir, c_dir, main_class, name)
    if wasm:
        natives = build_natives()
        module = os.path.join(out_dir, name + ".wasm")
        cmd = [c.wasm_compiler(cc)] + _cflags(c) + inc + ["-o", module] + srcs + [lib, "-L" + natives, "-lprowl_box2d_static", "-lbox2d", "-lm"] + c._WASM_LDFLAGS
        exe = os.path.join(out_dir, name)
    else:
        sys.path.insert(0, HERE)
        import ccsharp_scan as scan
        libs = []
        for n in ("libprowl_box2d_static.a", "libbox2d.a"):
            p = scan.find_native_lib(n)
            if p is None:
                sys.exit("player_build: %s is missing (python3 build.py native builds it)" % n)
            libs.append(p)
        exe = os.path.join(out_dir, name)
        if gfx:
            g = scan.find_native_lib("libgfx2d_static.a")
            if g is None:
                sys.exit("player_build: libgfx2d_static.a is missing (python3 build.py gfx builds it)")
            inc.append("-I" + os.path.join(PROWL, "Native", "Gfx2D"))
            libs = [g] + libs + ["-lEGL", "-lGLESv2"]
        cmd = [cc or "cc", "-O2", "-ffp-contract=off", "-w"] + inc + ["-o", exe] + srcs + [lib] + libs + ["-lm", "-lpthread"]
    r = subprocess.run(cmd, cwd=out_dir, capture_output=True, text=True)
    if r.returncode != 0:
        errs = [l for l in (r.stdout + r.stderr).splitlines() if "error" in l or "undefined" in l]
        print("player_build: the C compiler rejected the hybrid build:\n   " + "\n   ".join(e[:220] for e in errs[:10]))
        sys.exit(1)
    if wasm:
        c._wasm_package(exe, "player", home, True)
    return exe


def write_page(out_dir, files):
    """The page and its host script. `files` are the ones the page fetches and hands to the module as its file system (a hybrid player's assemblies)."""
    web = os.path.join(PROWL, "Native", "Gfx2D", "web")
    shutil.copy2(os.path.join(web, "prowl_web.js"), os.path.join(out_dir, "prowl_web.js"))
    with open(os.path.join(web, "index.html"), encoding="utf-8") as f:
        html = f.read()
    import json
    with open(os.path.join(out_dir, "index.html"), "w", encoding="utf-8") as f:
        f.write(html.replace("__PROWL_FILES__", json.dumps(files)))


WEB_ENTRY = """
/* the page's entry points (player_build --web): the game's Init() once, then Frame() once per 1/60 s */
__attribute__((export_name("prowl_init"))) int prowl_init(void) { return %(c)s_Init(); }
__attribute__((export_name("prowl_frame"))) void prowl_frame(void) { %(c)s_Frame(); }
"""


def link_web(out_dir, c_file, main_class, name="prowl2d-player"):
    """The player as a WASI reactor for a page: out_dir/NAME.wasm, with the page (index.html), its host script (prowl_web.js) beside it. The module exports
    prowl_init / prowl_frame (the game's `static int Init()` and `static void Frame()`) and imports the "gfx" functions of Native/Gfx2D/gfx2d_web.c, which
    web/prowl_gfx... prowl_web.js implements with WebGL2. Returns the module's path."""
    import re
    c = _ccs2c()
    natives = build_natives()
    cname = main_class.replace(".", "_")
    with open(c_file, encoding="utf-8") as f:
        text = f.read()
    for sig in ("static int %s_Init(void)" % cname, "static void %s_Frame(void)" % cname):
        if sig not in text:
            sys.exit("player_build: --web needs the game's class %s to have `public static int Init()` and `public static void Frame()` (the page calls them: Init once, "
                     "Frame once per 1/60 s); %r is not in the translated C. See Samples/Draw2D." % (main_class, sig))
    web_c = os.path.join(out_dir, "player.web.c")
    with open(web_c, "w", encoding="utf-8") as f:
        f.write(text + WEB_ENTRY % {"c": cname})
    gfx_dir = os.path.join(PROWL, "Native", "Gfx2D")
    module = os.path.join(out_dir, name + ".wasm")
    if os.path.exists(module):
        os.remove(module)
    cmd = ([c.wasm_compiler()] + _cflags(c) + ["-I.", "-I" + gfx_dir, "-I" + os.path.join(PROWL, "Native", "Box2D"), "-mexec-model=reactor", "-o", module, web_c, *gfx_web_sources(gfx_dir),
           "-L" + natives, "-lprowl_box2d_static", "-lbox2d", "-lm"] + ["-fuse-ld=lld", "-Wl,-z,stack-size=8388608", "-Wl,--export=prowl_init", "-Wl,--export=prowl_frame"])
    r = subprocess.run(cmd, cwd=out_dir, capture_output=True, text=True)
    if r.returncode != 0:
        errs = [l for l in r.stderr.splitlines() if "error" in l or "undefined" in l]
        print("player_build: clang (wasm32) rejected the web build:\n   " + "\n   ".join(e[:220] for e in errs[:8]))
        sys.exit(1)
    os.remove(web_c)
    write_page(out_dir, [])
    return module


HYBRID_ENTRY = """
/* the page's entry points (player_build --web --dna): the game's managed Init() once, then Frame() once per 1/60 s, called through DotNetAnywhere */
__attribute__((export_name("prowl_init"))) int prowl_init(void) {
\tstatic DNA_Method *m;
\tDNA_Value r;
\tif (m == NULL) m = ccs_find("%(ns)s", "%(cls)s", "Init", ">i");
\tccs_call(m, NULL, 0, &r);
\treturn r.u.i;
}
__attribute__((export_name("prowl_frame"))) void prowl_frame(void) {
\tstatic DNA_Method *m;
\tif (m == NULL) m = ccs_find("%(ns)s", "%(cls)s", "Frame", ">v");
\tccs_call(m, NULL, 0, NULL);
}
"""


def _link_hybrid_web(c, home, bdir, lib, out_dir, c_dir, main_class, name):
    """The hybrid page: the translated C, the DotNetAnywhere glue, the runtime, Box2D and the web renderer in one wasm reactor; player.managed.dll and
    corlib.dll beside it, which the page's WASI gives the runtime as files (prowl_web.js: `files`). The game's Init() and Frame() are called where they
    are: through DotNetAnywhere when the class is managed (it is, once it uses a managed script), directly when it stayed native."""
    cname = main_class.replace(".", "_")
    ns, _, cls = main_class.rpartition(".")
    with open(os.path.join(c_dir, "player.c"), encoding="utf-8") as f:
        player = f.read()
    with open(os.path.join(c_dir, "player.bridge.c"), encoding="utf-8") as f:
        glue = f.read()
    native = ("static int %s_Init(void)" % cname) in player
    if native:
        player += WEB_ENTRY % {"c": cname}
    else:
        glue += HYBRID_ENTRY % {"ns": ns, "cls": cls}
    for fn, text in (("player.web.c", player), ("player.bridge.web.c", glue)):
        with open(os.path.join(out_dir, fn), "w", encoding="utf-8") as f:
            f.write(text)
    shutil.copy2(os.path.join(c_dir, "player.managed.dll"), os.path.join(out_dir, "player.managed.dll"))
    shutil.copy2(os.path.join(bdir, "corlib.dll"), out_dir)
    natives = build_natives()
    gfx_dir = os.path.join(PROWL, "Native", "Gfx2D")
    module = os.path.join(out_dir, name + ".wasm")
    if os.path.exists(module):
        os.remove(module)
    cmd = ([c.wasm_compiler()] + _cflags(c) + ["-I.", "-I" + gfx_dir, "-I" + os.path.join(PROWL, "Native", "Box2D"), "-I" + os.path.join(home, "native", "src"),
           "-mexec-model=reactor", "-o", module, "player.web.c", "player.bridge.web.c", *gfx_web_sources(gfx_dir), lib,
           "-L" + natives, "-lprowl_box2d_static", "-lbox2d", "-lm"]
           + ["-fuse-ld=lld", "-Wl,-z,stack-size=8388608", "-Wl,--export=prowl_init", "-Wl,--export=prowl_frame", "-Wl,--export-table", "-Wl,--growable-table"])
    r = subprocess.run(cmd, cwd=out_dir, capture_output=True, text=True)
    for fn in ("player.web.c", "player.bridge.web.c"):
        os.remove(os.path.join(out_dir, fn))
    if r.returncode != 0:
        errs = [l for l in r.stderr.splitlines() if "error" in l or "undefined" in l]
        print("player_build: clang (wasm32) rejected the hybrid web build:\n   " + "\n   ".join(e[:220] for e in errs[:10]))
        sys.exit(1)
    write_page(out_dir, ["player.managed.dll", "corlib.dll"])
    return module
