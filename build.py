#!/usr/bin/env python3
"""build.py -- build Prowl. The default is a 2D build.

    python3 build.py                  deps check, native 2D physics library, then the managed solution (2D)
    python3 build.py --3d             the same, with the unmaintained 3D physics compiled in as well
    python3 build.py -h

Commands (the default, with none, is `all`):

    all       deps (checked, not cloned), native, managed
    deps      clone what lives beside this repository:  ../box2d  ../CCSharp  (and CCSharp's own ../crust ../coost)
    native    build Box2D-Packed + the pb2_* shim into one shared library (Native/Box2D) and install it for the runtime
    managed   dotnet build Prowl.slnx
    test      the 2D physics tests (Native/Box2D/Tests/Box2DSmoke); add --managed to also `dotnet test` Prowl.Runtime.Test
    scan      how much of the 2D engine can CCSharp translate to C?   (tools/ccsharp/ccsharp_scan.py; args after -- go to it)
    ccsharp   translate-and-run conformance: the 2D math as C, built with gcc, compared with real .NET
    player    translate a game to C and build a native player with no .NET in it:  player GAME [--verify] [--static] [--run] [--sanitize] [--dotnet]
              add --wasm for a WebAssembly (wasm32-wasi) module run under node; --dna to let classes outside the C# subset run on DotNetAnywhere
              (GAME is a folder of .cs files with a static Main; tools/ccsharp/player_build.py has the details)
    gfx       build the 2D renderer (Native/Gfx2D) for this machine: needs the EGL/GLES headers and ../crust
    so        build libprowl2d.so for the editor (prowl.py): the 2D engine as C plus the SDL2 window, one library for ctypes; [-- -o PATH] (default /tmp/libprowl2d.so)
    fxtest    the picture effects (Native/Gfx2D/fx): is the generated code current, and does each effect on the GLES renderer give what its C reference does; add --web for
              WebGL2 and WebGPU too (tools/gfx_fx_test.py; WebGPU needs xvfb-run and mesa-vulkan-drivers)
    webtest   Samples/Draw2D as a native player and as a web page (WebGL2 and WebGPU) run in headless Chromium, and compare the two pictures
    samples   every folder of Samples/ (or the named ones), built as a player and compared with the same game on .NET: samples [NAME..] [--sanitize] [--wasm]
    check3d   does the 3D switch still hold? compiles with and without 3D and compares   (tools/check_physics3d.py)
    status    what was found where
    clean     remove what this script built

Layout. Box2D and CCSharp are NOT part of this repository; they are cloned next to it, as CCSharp itself expects crust and coost to be:

    parent/
      Prowl/      this repository
      box2d/      https://github.com/crustos/box2d
      CCSharp/    https://github.com/crustos/CCSharp         (only for scan / ccsharp)
      crust/      https://github.com/brentharts/crust        (cloned by CCSharp's own build.py)
      coost/      https://github.com/crustos/coost           (ditto)

2D vs 3D. 3D physics (Jitter2) is kept but OFF by default and is not maintained: it is there for someone else to take over. `--3d`
passes -p:ProwlPhysics3D=true, which defines PROWL_PHYSICS_3D, restores Jitter2 and compiles Physics/ and Components/Physics/.
`check3d` guards the seam so a change made without 3D in mind cannot silently break the 3D build.
"""
import argparse
import glob
import os
import platform
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
PARENT = os.path.dirname(ROOT)
SOLUTION = os.path.join(ROOT, "Prowl.slnx")
NATIVE_SRC = os.path.join(ROOT, "Native", "Box2D")
SMOKE = os.path.join(NATIVE_SRC, "Tests", "Box2DSmoke")
BUILD = os.path.join(ROOT, "Build", "Native")          # Build/ is where the csproj files already put their output

REPOS = {
    "box2d": "https://github.com/crustos/box2d.git",
    "CCSharp": "https://github.com/crustos/CCSharp.git",
}


def sibling(name):
    return os.environ.get(name.upper() + "_HOME") or os.path.join(PARENT, name)


def say(msg):
    print("== " + msg, flush=True)


def run(cmd, cwd=None, env=None, check=True):
    print("   $ " + " ".join('"%s"' % c if " " in c else c for c in cmd), flush=True)
    r = subprocess.run(cmd, cwd=cwd, env=env)
    if check and r.returncode != 0:
        sys.exit("build.py: failed (%d): %s" % (r.returncode, " ".join(cmd)))
    return r.returncode


def have(tool):
    return shutil.which(tool) is not None


def rid():
    m = platform.machine().lower()
    arch = "arm64" if m in ("arm64", "aarch64") else ("x86" if m in ("i386", "i686", "x86") else "x64")
    system = {"Windows": "win", "Darwin": "osx"}.get(platform.system(), "linux")
    return "%s-%s" % (system, arch)


def lib_names():
    return {"win": ["prowl_box2d.dll"], "osx": ["libprowl_box2d.dylib"]}.get(rid().split("-")[0], ["libprowl_box2d.so"])


def git_head(path):
    try:
        return subprocess.check_output(["git", "-C", path, "log", "-1", "--format=%h %s"], text=True, stderr=subprocess.DEVNULL).strip()[:70]
    except Exception:
        return None


# ---------------------------------------------------------------------------------------------------------------------------------

def cmd_deps(a):
    say("dependencies beside this repository (%s)" % PARENT)
    for name, url in REPOS.items():
        if name == "CCSharp" and not a.ccsharp:
            continue
        dest = sibling(name)
        if os.path.isdir(os.path.join(dest, ".git")) or os.path.isdir(dest):
            print("   %-8s %s   %s" % (name, dest, git_head(dest) or ""))
            if a.update:
                run(["git", "-C", dest, "pull", "--ff-only"])
        elif a.offline:
            sys.exit("build.py: %s is missing and --offline was given. Clone it:  git clone %s %s" % (name, url, dest))
        else:
            run(["git", "clone", url, dest])
    if a.ccsharp:
        ccs = sibling("CCSharp")
        flags = [] + (["--offline"] if a.offline else []) + (["--update"] if a.update else [])
        run([sys.executable, os.path.join(ccs, "build.py"), "deps"] + flags)


def ensure_dep(name):
    d = sibling(name)
    if not os.path.isdir(d):
        sys.exit("build.py: %s is not at %s\n   run:  python3 build.py deps%s" % (name, d, "  --ccsharp" if name == "CCSharp" else ""))
    return d


def cmd_native(a):
    box2d = ensure_dep("box2d")
    if not have("cmake"):
        sys.exit("build.py: cmake is needed to build the native physics library (https://cmake.org)")
    variant = "avx2" if a.avx2 else "portable"
    bdir = os.path.join(BUILD, rid(), variant)
    say("native 2D physics: Box2D-Packed + the pb2_* shim (%s, %s)" % (rid(), variant))
    gen = ["-G", "Ninja"] if have("ninja") else []
    cfg = ["cmake", "-S", NATIVE_SRC, "-B", bdir, "-DCMAKE_BUILD_TYPE=" + a.config, "-DPROWL_BOX2D_DIR=" + box2d,
           "-DPROWL_B2_AVX2=" + ("ON" if a.avx2 else "OFF")] + gen
    run(cfg)
    run(["cmake", "--build", bdir, "--config", a.config])
    lib = next((p for n in lib_names() for p in glob.glob(os.path.join(bdir, "**", n), recursive=True)), None)
    if not lib:
        sys.exit("build.py: the build finished but %s was not found under %s" % (lib_names()[0], bdir))
    dest = os.path.join(ROOT, "Libraries", rid(), "native")
    os.makedirs(dest, exist_ok=True)
    shutil.copy2(lib, os.path.join(dest, os.path.basename(lib)))
    print("   built     %s" % lib)
    print("   installed %s   (untracked: see .gitignore)" % os.path.join(dest, os.path.basename(lib)))
    return lib


def dotnet_props(a):
    return ["-p:ProwlPhysics3D=%s" % ("true" if a.three_d else "false")]


def cmd_managed(a):
    if not have("dotnet"):
        sys.exit("build.py: the .NET SDK is needed (https://dotnet.microsoft.com)")
    say("managed build: Prowl.slnx, %s physics, %s" % ("2D + 3D" if a.three_d else "2D", a.config))
    run(["dotnet", "build", SOLUTION, "-c", a.config] + dotnet_props(a))


def smoke_env(lib):
    env = dict(os.environ)
    d = os.path.dirname(lib)
    key = {"win": "PATH", "osx": "DYLD_LIBRARY_PATH"}.get(rid().split("-")[0], "LD_LIBRARY_PATH")
    env[key] = d + os.pathsep + env.get(key, "")
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    return env


def cmd_test(a):
    if not have("dotnet"):
        sys.exit("build.py: the .NET SDK is needed to run the tests")
    lib = cmd_native(a)
    say("2D physics tests: the native shim, the engine-independent core, and the real components in a headless engine")
    run(["dotnet", "build", SMOKE, "-c", "Release"], env=smoke_env(lib))
    dll = os.path.join(SMOKE, "bin", "Release", "net10.0", "Box2DSmoke.dll")
    run(["dotnet", dll] + ([] if a.bench else ["--no-bench"]), env=smoke_env(lib))
    if a.managed:
        say("managed tests: Prowl.Runtime.Test (%s)" % ("2D + 3D" if a.three_d else "2D"))
        run(["dotnet", "test", os.path.join(ROOT, "Prowl.Runtime.Test"), "-c", a.config] + dotnet_props(a))


def cmd_all(a):
    for name in ("box2d",):
        ensure_dep(name)
    cmd_native(a)
    cmd_managed(a)


def ccsharp_ready():
    ccs = ensure_dep("CCSharp")
    if not os.path.exists(os.path.join(ccs, "build", "compiler", "ccs.dll")):
        say("building the CCSharp compiler")
        run([sys.executable, os.path.join(ccs, "build.py"), "deps"])
        run([sys.executable, os.path.join(ccs, "build.py"), "compiler"])
    return ccs


def cmd_scan(a):
    ccsharp_ready()
    run([sys.executable, os.path.join(ROOT, "tools", "ccsharp", "ccsharp_scan.py"), "scan"] + a.rest)


def cmd_ccsharp(a):
    ccsharp_ready()
    run([sys.executable, os.path.join(ROOT, "tools", "ccsharp", "ccsharp_scan.py"), "conformance"])


GFX_DESKTOP_SOURCES = ["gfx2d.c", "gfx2d_ui.c", "gfx2d_font.c", "gfx2d_font_data.c"]


def cmd_gfx(a):
    """The 2D renderer (Native/Gfx2D) for this machine: libgfx2d_static.a (linked into a player that draws) and libgfx2d.so (what the .NET reference loads).
    It includes Crust's GLES 3.1 batch renderer from ../crust, and needs the EGL / GLES headers (apt install libegl-dev libgles-dev)."""
    crust = sibling("crust")
    batch = os.path.join(crust, "examples", "unity_pack")
    if not os.path.exists(os.path.join(batch, "gles3_batch.h")):
        sys.exit("build.py: crust is not at %s (python3 build.py deps --ccsharp clones CCSharp, whose own build.py clones crust beside it)" % crust)
    src = os.path.join(ROOT, "Native", "Gfx2D")
    bdir = os.path.join(BUILD, rid(), "gfx2d")
    os.makedirs(bdir, exist_ok=True)
    say("native 2D renderer: Native/Gfx2D (%s)" % rid())
    cc = os.environ.get("CC") or "cc"
    flags = ["-O2", "-ffp-contract=off", "-w", "-fPIC", "-I", src, "-I", batch]
    objs = []
    for name in GFX_DESKTOP_SOURCES:      # gfx2d.c is the renderer; the others are the UI layer's (textures, clip, input, fonts) and are the same C on a page
        obj = os.path.join(bdir, name[:-2] + ".o")
        run([cc] + flags + ["-c", os.path.join(src, name), "-o", obj])
        objs.append(obj)
    static = os.path.join(bdir, "libgfx2d_static.a")
    if os.path.exists(static):
        os.remove(static)
    run(["ar", "rcs", static] + objs)
    so = os.path.join(bdir, "libgfx2d.so")
    run([cc, "-shared", "-o", so] + objs + ["-lEGL", "-lGLESv2", "-lm"])
    dest = os.path.join(ROOT, "Libraries", rid(), "native")
    os.makedirs(dest, exist_ok=True)
    shutil.copy2(so, dest)
    print("   built %s and %s; installed %s" % (static, so, os.path.join(dest, "libgfx2d.so")))


def cmd_so(a):
    """libprowl2d.so: Native/Engine2D/Engine.cs translated to C with the 2D runtime, plus the SDL2-windowed renderer and Box2D, in one shared library that the
    editor (prowl.py) loads with ctypes. Arguments after `--` go to tools/prowl2d_so.py (-o PATH, --no-sdl). Needs the siblings (deps --ccsharp), `native`, a
    .NET SDK for the translator, and libegl-dev libgles-dev libsdl2-dev."""
    run([sys.executable, os.path.join(ROOT, "tools", "prowl2d_so.py")] + list(a.rest))


def cmd_fxtest(a):
    """The effects: the generated files must be what the registry makes, then every effect is drawn by the GLES renderer (and with --web by the page's WebGL2 and
    WebGPU) and compared with the C reference made from the same .fx files."""
    run([sys.executable, os.path.join(ROOT, "tools", "gfx_fx_gen.py"), "--check"])
    cmd_gfx(a)
    run([sys.executable, os.path.join(ROOT, "tools", "gfx_fx_test.py")] + (["--web"] if a.web else []))


def cmd_webtest(a):
    """Samples/Draw2D and Samples/DrawScript2D (the same game, with managed scripts: --dna) each as a native player (the reference frame) and as a page, which
    headless Chromium runs and compares with it. Needs node, Playwright with Chromium, and what `gfx` and `player --web` need."""
    cmd_gfx(a)
    for n in ("Draw2D", "DrawScript2D"):
        say("web test: " + n)
        run([sys.executable, PLAYER_BUILD, os.path.join("Samples", n), "--run"], cwd=ROOT)
        run([sys.executable, PLAYER_BUILD, os.path.join("Samples", n), "--web"], cwd=ROOT)
        for g in ("webgl2", "webgpu"):                      # the same page, drawn by each API, must give the native picture
            cmd = ["node", os.path.join(ROOT, "tools", "web_test.mjs"), os.path.join(ROOT, "Build", "Player", n + "-web"),
                   "--ref", os.path.join(ROOT, "Build", "Player", n, "frame_0000.ppm"), "--gfx", g]
            if g == "webgpu":                               # a headed browser (the canvas must present) on a Vulkan device: xvfb-run and lavapipe where there is no screen
                if not os.environ.get("DISPLAY"):
                    if not shutil.which("xvfb-run"):
                        say("web test: " + n + " with webgpu skipped (needs a display: install xvfb, and mesa-vulkan-drivers for a software Vulkan device)")
                        continue
                    cmd = ["xvfb-run", "-a"] + cmd
            say("web test: " + n + " with " + g)
            run(cmd, cwd=ROOT)


PLAYER_BUILD = os.path.join(ROOT, "tools", "ccsharp", "player_build.py")


def player_flags(a):
    return [f for f, on in (("--verify", a.verify), ("--static", a.static), ("--run", a.run), ("--sanitize", a.sanitize), ("--dotnet", a.dotnet), ("--wasm", a.wasm), ("--web", a.web), ("--dna", a.dna)) if on]


def cmd_player(a):
    if not a.game:
        sys.exit("player needs a GAME folder, e.g.  python3 build.py player Samples/Headless2D --verify --run")
    ccsharp_ready()
    run([sys.executable, PLAYER_BUILD, a.game] + player_flags(a))


def cmd_samples(a):
    """Builds each folder of Samples/ as a player and compares it with .NET (and, with --sanitize, runs it under the sanitizers); prints a table."""
    ccsharp_ready()
    d = os.path.join(ROOT, "Samples")
    names = [n for n in sorted(os.listdir(d)) if os.path.isdir(os.path.join(d, n))] if os.path.isdir(d) else []
    wanted = [a.game] + a.more if a.game else []
    if wanted:
        names = [n for n in names if n in wanted]
    if not names:
        sys.exit("no samples to run")
    results = []
    for n in names:
        say("sample " + n)
        r = subprocess.run([sys.executable, PLAYER_BUILD, os.path.join("Samples", n), "--verify"] + (["--sanitize"] if a.sanitize else []),
                           cwd=ROOT, capture_output=True, text=True)
        out = r.stdout + r.stderr
        ok = r.returncode == 0 and "verify    ok" in out and (not a.sanitize or "sanitize  ok" in out or "sanitize  skipped" in out)
        why = "" if ok else next((l.strip() for l in out.splitlines() if "FAIL" in l or "error" in l or "differ" in l), "see: python3 build.py player Samples/%s --verify" % n)
        results.append((n, ok, why))
    print()
    for n, ok, why in results:
        print("   %-24s %s  %s" % (n, "ok  " if ok else "FAIL", why[:150]))
    bad = [n for n, ok, _ in results if not ok]
    print("\n   %d of %d passed" % (len(results) - len(bad), len(results)))
    if bad:
        sys.exit(1)


def cmd_check3d(a):
    run([sys.executable, os.path.join(ROOT, "tools", "check_physics3d.py")])


def cmd_status(a):
    say("status")
    print("   platform   %s" % rid())
    for tool in ("python3", "git", "cmake", "ninja", "cc", "gcc", "dotnet"):
        print("   %-9s  %s" % (tool, shutil.which(tool) or "-"))
    if have("dotnet"):
        v = subprocess.run(["dotnet", "--version"], capture_output=True, text=True).stdout.strip()
        print("   dotnet     %s" % v)
    for name in ("box2d", "CCSharp", "crust", "coost"):
        d = sibling(name)
        print("   %-9s  %s   %s" % (name, d if os.path.isdir(d) else "(not cloned)", (git_head(d) or "") if os.path.isdir(d) else ""))
    ccs = os.path.join(sibling("CCSharp"), "build", "compiler", "ccs.dll")
    print("   ccs.dll    %s" % (ccs if os.path.exists(ccs) else "(not built)"))
    lib = os.path.join(ROOT, "Libraries", rid(), "native", lib_names()[0])
    print("   native lib %s" % (lib if os.path.exists(lib) else "(not built: python3 build.py native)"))


def cmd_clean(a):
    for p in (BUILD, os.path.join(ROOT, "Libraries", rid(), "native", lib_names()[0])):
        if os.path.isdir(p):
            shutil.rmtree(p)
            print("   removed %s" % p)
        elif os.path.exists(p):
            os.remove(p)
            print("   removed %s" % p)


COMMANDS = {"all": cmd_all, "deps": cmd_deps, "native": cmd_native, "managed": cmd_managed, "test": cmd_test, "scan": cmd_scan,
            "ccsharp": cmd_ccsharp, "gfx": cmd_gfx, "so": cmd_so, "fxtest": cmd_fxtest, "webtest": cmd_webtest, "player": cmd_player, "samples": cmd_samples, "check3d": cmd_check3d, "status": cmd_status, "clean": cmd_clean}


def main():
    argv = sys.argv[1:]
    rest = []
    if "--" in argv:
        i = argv.index("--")
        argv, rest = argv[:i], argv[i + 1:]
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[1], formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__.split("\n", 2)[2])
    ap.add_argument("command", nargs="?", default="all", choices=sorted(COMMANDS))
    ap.add_argument("game", nargs="?", help="player / samples: the game folder (samples: a sample's name)")
    ap.add_argument("more", nargs="*", help="samples: more sample names")
    ap.add_argument("--verify", action="store_true", help="player: also run the game on .NET and require the same output")
    ap.add_argument("--static", action="store_true", help="player: link a fully static executable")
    ap.add_argument("--run", action="store_true", help="player: run the built player and show its output")
    ap.add_argument("--sanitize", action="store_true", help="player / samples: also run the translated C under AddressSanitizer and UBSan")
    ap.add_argument("--dotnet", action="store_true", help="player: only run the game on .NET (the reference)")
    ap.add_argument("--dna", action="store_true", help="player: classes outside the C# subset (lambdas, try/catch) run managed on DotNetAnywhere, in the same executable")
    ap.add_argument("--web", action="store_true", help="player: a page (index.html + wasm + JS) that runs the game in a browser, drawing with WebGL2; fxtest: also the page backends")
    ap.add_argument("--wasm", action="store_true", help="player: build for WebAssembly (wasm32-wasi) and run it under node")
    ap.add_argument("--3d", dest="three_d", action="store_true", help="also compile the unmaintained 3D physics (default: 2D only)")
    ap.add_argument("-c", "--config", default="Release", choices=["Debug", "Release"], help="build configuration (default Release)")
    ap.add_argument("--avx2", action="store_true", help="native: build Box2D with AVX2 (faster, needs an AVX2 CPU; default is portable)")
    ap.add_argument("--managed", action="store_true", help="test: also run `dotnet test` on Prowl.Runtime.Test")
    ap.add_argument("--bench", action="store_true", help="test: also run the benchmark")
    ap.add_argument("--ccsharp", action="store_true", help="deps: also clone CCSharp (and its crust + coost)")
    ap.add_argument("--update", action="store_true", help="deps: git pull --ff-only what is already cloned")
    ap.add_argument("--offline", action="store_true", help="deps: never touch the network; a missing checkout is an error")
    a = ap.parse_args(argv)
    a.rest = rest
    COMMANDS[a.command](a)


if __name__ == "__main__":
    main()
