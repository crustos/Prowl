#!/usr/bin/env python3
"""player_build.py -- turn a game written against the 2D runtime into a standalone native executable, with no .NET in it.

    python3 tools/ccsharp/player_build.py GAME_DIR [-o OUT] [--check] [--verify] [--static] [--cc CC]
    (or:  python3 build.py player GAME_DIR ...)

GAME_DIR holds the game's C# files: scripts (classes marked [Script]) and a class with a static Main. The engine they run on is
Prowl.Core2D (its file list is Prowl.Core2D/Prowl.Core2D.csproj). What this does:

  1. generates the call sink for the game's scripts (tools/ccsharp/gen_scripts.py) and the C flavor of the native bindings;
  2. translates engine + game + sink to ONE C file with CC# (this is the only step that needs .NET, and only because the translator is
     built on Roslyn: the output does not);
  3. writes a package, OUT/, that needs nothing but a C compiler: player.c, the Box2D header, the static libraries, a Makefile, build.sh;
  4. builds it: OUT/prowl2d-player.

  --check    stop after step 2 and print what is outside the C# subset, as `File.cs(line,col): error CODE: message` (the form MSBuild and
             editors already read), exiting 1 if there is anything. This is what an editor's "C build" mode runs on a script as it is saved.
  --verify   also run the same game on .NET (the reference) and require both to print the same thing.
  --static   link a fully static executable (no loader, no libc.so): `ldd` then says "not a dynamic executable".
  --dna      what the subset cannot hold (lambdas, try/catch, generics ...) is built as managed C#, run by DotNetAnywhere linked into the player; the
             rest stays native C. The classes that use it follow it (the call sink, Main). A `// dna` line in a game file makes --dna the default.
             Output: the player (or player.wasm with --wasm) with player.managed.dll and corlib.dll beside it.
  --web      a page that runs the game in a browser: OUT/index.html, prowl_web.js and prowl2d-player.wasm (a WASI reactor; WebGL2 draws the sprite batch). The
             game's class with Main also has `public static int Init()` and `public static void Frame()`: the page calls Init once, then Frame once per 1/60 s.
             Implies --wasm. Serve OUT over http. See Samples/Draw2D.
  --wasm     build for WebAssembly (wasm32-wasi) instead: OUT/prowl2d-player.wasm, a launcher OUT/prowl2d-player that runs it under node, and
             run_wasm.mjs (the host, from DotNetAnywhere). Box2D and the shim are compiled for wasm32 first (Build/Native/wasm32). With
             --verify the output is also compared with the .NET run. A game that draws is not supported yet (the renderer is EGL/GLES).
"""
import argparse
import glob
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ccsharp_scan as scan   # noqa: E402
import gen_pb2                # noqa: E402
import gen_scripts            # noqa: E402

PROWL = scan.PROWL


def uses_gfx(files):
    """Does the game draw? (It names the renderer's bindings.) Such a game links the renderer and the system's GL libraries."""
    for path in files:
        with open(path, encoding="utf-8-sig") as f:
            text = re.sub(r"//[^\n]*", "", f.read())
        if re.search(r"Prowl\.Native\.Gfx2D|\bGFX\.", text):
            return True
    return False


def gfx_hint(files):
    return uses_gfx(files)


def check_constants():
    """The batch's width is written in two places, the header the renderer reads and the C# that fills it. They must agree."""
    with open(os.path.join(PROWL, "Native", "Gfx2D", "gfx2d.h"), encoding="utf-8") as f:
        c = re.search(r"#define\s+GFX_SPRITE_FLOATS\s+(\d+)", f.read())
    with open(os.path.join(PROWL, "Prowl.Core2D", "CoreLimits.cs"), encoding="utf-8") as f:
        cs = re.search(r"SpriteFloats\s*=\s*(\d+)", f.read())
    if not c or not cs or c.group(1) != cs.group(1):
        sys.exit("player_build: GFX_SPRITE_FLOATS (%s, Native/Gfx2D/gfx2d.h) and CoreLimits.SpriteFloats (%s) differ: the renderer would read a different batch than the runtime writes"
                 % (c.group(1) if c else "?", cs.group(1) if cs else "?"))


def game_files(game_dir):
    out = []
    for dp, dn, fn in os.walk(game_dir):
        dn[:] = [d for d in dn if d not in ("obj", "bin", "generated")]
        out += [os.path.join(dp, f) for f in sorted(fn) if f.endswith(".cs") and not f.endswith(".g.cs")]
    return sorted(out)


def find_main(files):
    """The `Namespace.Class` that has the static Main."""
    for path in files:
        with open(path, encoding="utf-8-sig") as f:
            text = scan_text = f.read()
        text = re.sub(r"//[^\n]*", "", text)
        m = re.search(r"\bstatic\s+(?:int|void)\s+Main\s*\(", text)
        if not m:
            continue
        classes = [c for c in re.finditer(r"\bclass\s+(\w+)", text) if c.start() < m.start()]
        if not classes:
            continue
        ns = re.search(r"^\s*namespace\s+([\w.]+)", text, re.M)
        return (ns.group(1) + "." if ns else "") + classes[-1].group(1)
    return None


# What the translator says when it refuses, in the forms it says it, as the compiler diagnostics editors already understand.
_REFUSAL = re.compile(r"^(?P<file>/\S+?\.cs):(?P<line>\d+): (?P<msg>.+)$")
_ROSLYN = re.compile(r"^(?P<file>.+?\.cs): \((?P<line>\d+),(?P<col>\d+)\): error (?P<code>CS\d+): (?P<msg>.*)$")


def diagnostics(text):
    """csc-style lines for what CC# printed: `File.cs(12,1): error CCS0001: ...` for a construct outside the subset, and the C# compiler's own
    errors as they are."""
    out = []
    for line in text.splitlines():
        m = _ROSLYN.match(line.strip())
        if m:
            out.append("%s(%s,%s): error %s: %s" % (m.group("file"), m.group("line"), m.group("col"), m.group("code"), m.group("msg")))
            continue
        m = _REFUSAL.match(line.strip())
        if m:
            out.append("%s(%s,1): error CCS0001: not in the C build's C# subset: %s" % (m.group("file"), m.group("line"), m.group("msg")))
    return out


def generate(game, out_dir, gfx):
    """The generated inputs of a build: the bindings of each native library the game uses (both flavors), their headers in one folder, and the call sink
    for the game's scripts. Returns the sink's path and the include folder."""
    gen_dir = os.path.join(out_dir, "generated")
    os.makedirs(gen_dir, exist_ok=True)
    # the bindings of each native library the game uses, both flavors, and their headers in ONE directory (the translator takes one)
    include = os.path.join(gen_dir, "include")
    shutil.rmtree(include, ignore_errors=True)
    os.makedirs(include)
    libs = [("box2d", os.path.join(PROWL, "Native", "Box2D", "prowl_box2d.h"))]
    if gfx:
        libs.append(("gfx2d", os.path.join(PROWL, "Native", "Gfx2D", "gfx2d.h")))
    for lib, header in libs:
        gen_pb2.use(lib)
        gen_pb2.generate(os.path.join(gen_dir, "bindings"))
        shutil.copy2(header, include)
    gen_pb2.use("box2d")
    sink = os.path.join(gen_dir, "Scripts.g.cs")
    gen_scripts.generate(sink, game)
    return sink, include


def translate(game, out_dir, main_class, gfx, dna=False):
    """Runs the translator over engine + game + generated sink; returns (path of the C file, list of diagnostics, raw output)."""
    gen_dir = os.path.join(out_dir, "generated")
    sink, nat_inc = generate(game, out_dir, gfx)
    ccs2c = os.path.join(scan.ccsharp_home(), "crust", "ccs2c.py")
    c_dir = os.path.join(out_dir, "c")
    shutil.rmtree(c_dir, ignore_errors=True)
    cmd = ([sys.executable, ccs2c] + scan.CORE2D_FILES(game) + game + [sink]
           + ["--bindings=" + os.path.join(gen_dir, "bindings", "c"), "--include=" + nat_inc, "--main=" + main_class,
              "--name=player", "--convert=" + c_dir, "--c"] + (["--dna"] if dna else []))
    r = subprocess.run(cmd, capture_output=True, text=True)
    raw = r.stdout + r.stderr
    c_file = os.path.join(c_dir, "player.c")
    if r.returncode != 0 or not os.path.exists(c_file):
        return None, diagnostics(raw), raw
    return c_file, [], raw


MAKEFILE = """# Builds the player from the translated C. Needs a C compiler and nothing else: no .NET, no CMake.
CC ?= cc
CFLAGS ?= -O2 -ffp-contract=off -w

prowl2d-player: player.c prowl_box2d.h lib/libprowl_box2d_static.a lib/libbox2d.a
\t$(CC) $(CFLAGS) -I. -o $@ player.c -Llib -lprowl_box2d_static -lbox2d -lm

# a fully static executable: no loader, no shared libc
static: player.c prowl_box2d.h lib/libprowl_box2d_static.a lib/libbox2d.a
\t$(CC) $(CFLAGS) -static -I. -o prowl2d-player player.c -Llib -lprowl_box2d_static -lbox2d -lm

clean:
\trm -f prowl2d-player

.PHONY: static clean
"""


# A game that draws: the renderer, and the system's EGL and GLES libraries it loads. Not fully static: GL drivers are loaded at run time.
GFX_MAKEFILE = """# Builds the player from the translated C. Needs a C compiler and the EGL / GLES development files (libegl-dev libgles-dev): no .NET, no CMake.
CC ?= cc
CFLAGS ?= -O2 -ffp-contract=off -w

prowl2d-player: player.c prowl_box2d.h gfx2d.h lib/libprowl_box2d_static.a lib/libbox2d.a lib/libgfx2d_static.a
\t$(CC) $(CFLAGS) -I. -o $@ player.c -Llib -lgfx2d_static -lprowl_box2d_static -lbox2d -lEGL -lGLESv2 -lm

clean:
\trm -f prowl2d-player frame_*.ppm

.PHONY: clean
"""


def package(out_dir, c_file, native_dir, gfx):
    shutil.copy2(c_file, os.path.join(out_dir, "player.c"))
    shutil.copy2(os.path.join(PROWL, "Native", "Box2D", "prowl_box2d.h"), os.path.join(out_dir, "prowl_box2d.h"))
    if gfx:
        shutil.copy2(os.path.join(PROWL, "Native", "Gfx2D", "gfx2d.h"), os.path.join(out_dir, "gfx2d.h"))
    os.makedirs(os.path.join(out_dir, "lib"), exist_ok=True)
    for name in ("libprowl_box2d_static.a", "libbox2d.a") + (("libgfx2d_static.a",) if gfx else ()):
        src = scan.find_native_lib(name)
        if src is None:
            sys.exit("player_build: %s is missing (python3 build.py native builds it)" % name)
        shutil.copy2(src, os.path.join(out_dir, "lib", name))
    with open(os.path.join(out_dir, "Makefile"), "w", newline="\n") as f:
        f.write(GFX_MAKEFILE if gfx else MAKEFILE)
    with open(os.path.join(out_dir, "build.sh"), "w", newline="\n") as f:
        f.write("#!/bin/sh\n# Builds the player with just a C compiler.   ./build.sh [static]\ncd \"$(dirname \"$0\")\" && make \"$@\"\n")
    os.chmod(os.path.join(out_dir, "build.sh"), 0o755)


def compile_player(out_dir, cc, static, gfx):
    exe = os.path.join(out_dir, "prowl2d-player")
    if os.path.exists(exe):
        os.remove(exe)
    libs = ["-Llib"] + (["-lgfx2d_static"] if gfx else []) + ["-lprowl_box2d_static", "-lbox2d"] + (["-lEGL", "-lGLESv2"] if gfx else []) + ["-lm"]
    cmd = [cc, "-O2", "-ffp-contract=off", "-w", "-I.", "-o", "prowl2d-player", "player.c"] + libs
    if static:
        cmd.insert(1, "-static")
    g = subprocess.run(cmd, cwd=out_dir, capture_output=True, text=True)
    if g.returncode != 0:
        errs = [l for l in g.stderr.splitlines() if "error" in l or "undefined" in l]
        print("player_build: the C compiler rejected the translated C:\n   " + "\n   ".join(e[:200] for e in errs[:8]))
        sys.exit(1)
    return exe


def sanitize_run(out_dir, cc, expected_stdout):
    """Builds the translated C again with AddressSanitizer and UBSan and runs it. Returns None if clean, else what went wrong. (Leak detection is
    off: an arena class lives until the process ends, by design, and LeakSanitizer discards buffered output.)"""
    cmd = [cc, "-O1", "-g", "-w", "-fsanitize=address,undefined", "-fno-omit-frame-pointer", "-I.", "-o", "player_asan", "player.c",
           "-Llib", "-lprowl_box2d_static", "-lbox2d", "-lm"]
    b = subprocess.run(cmd, cwd=out_dir, capture_output=True, text=True)
    if b.returncode != 0:
        return "the sanitizer build failed: " + b.stderr[-300:]
    env = dict(os.environ, ASAN_OPTIONS="detect_leaks=0", UBSAN_OPTIONS="print_stacktrace=1")
    r = subprocess.run([os.path.join(out_dir, "player_asan")], cwd=out_dir, capture_output=True, text=True, env=env)
    bad = [l for l in r.stderr.splitlines() if "ERROR: AddressSanitizer" in l or "runtime error" in l]
    if bad:
        return "%d report(s): %s" % (len(bad), bad[0][:200])
    if r.stdout != expected_stdout:
        return "output differs under the sanitizers"
    return None


def run_dotnet(game, out_dir, native_dir):
    """The same game on .NET (the reference), against the same native library."""
    gen_dir = os.path.join(out_dir, "generated")
    nets = [os.path.join(gen_dir, "bindings", "net", n) for n in sorted(os.listdir(os.path.join(gen_dir, "bindings", "net")))]
    files = nets + scan.CORE2D_FILES(game) + game + [os.path.join(gen_dir, "Scripts.g.cs")]
    work = os.path.join(out_dir, "dotnet-reference")
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    env = {"LD_LIBRARY_PATH": native_dir + os.pathsep + os.environ.get("LD_LIBRARY_PATH", "")}
    return scan.run_dotnet_reference(files, work, env)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("game", help="a folder of the game's C# files")
    ap.add_argument("-o", "--out", help="the package folder (default Build/Player/<game folder name>)")
    ap.add_argument("--check", action="store_true", help="only translate: print what is outside the C# subset, exit 1 if anything is")
    ap.add_argument("--verify", action="store_true", help="also run the game on .NET and require the same output")
    ap.add_argument("--static", action="store_true", help="link a fully static executable")
    ap.add_argument("--dna", action="store_true", help="classes outside the C# subset (lambdas, try/catch ...) run managed, on DotNetAnywhere linked into the player")
    ap.add_argument("--web", action="store_true", help="a page (index.html + wasm + JS) that runs the game in a browser; the game has `static int Init()` and `static void Frame()`. Implies --wasm")
    ap.add_argument("--wasm", action="store_true", help="build for WebAssembly (wasm32-wasi), run under node")
    ap.add_argument("--cc", default=os.environ.get("CC") or "cc")
    ap.add_argument("--run", action="store_true", help="run the built player and show its output")
    ap.add_argument("--sanitize", action="store_true", help="also run the translated C under AddressSanitizer and UBSan")
    ap.add_argument("--dotnet", action="store_true", help="only run the game on .NET (the reference); no translation")
    a = ap.parse_args()

    if a.web:
        a.wasm = True
    game_dir = os.path.abspath(a.game)
    files = game_files(game_dir)
    if not files:
        sys.exit("player_build: no .cs files in %s" % game_dir)
    main_class = find_main(files)
    if not main_class:
        sys.exit("player_build: no class with a static Main in %s" % game_dir)
    name = os.path.basename(game_dir.rstrip(os.sep))
    out_dir = os.path.abspath(a.out or os.path.join(PROWL, "Build", "Player", name + ("-web" if a.web else "-wasm" if a.wasm else "")))
    os.makedirs(out_dir, exist_ok=True)
    native_dir = scan.native_library_dir()
    if a.wasm:
        import wasm_build
        ok, why = wasm_build.available()
        if not ok:
            sys.exit("player_build: --wasm: " + why)
        if a.static or a.sanitize:
            sys.exit("player_build: --static and --sanitize are for the native player; --wasm builds a wasm module")
    if a.dna and (a.static or a.sanitize):
        sys.exit("player_build: --dna is not combined with --static or --sanitize (yet)")
    if native_dir is None and not (a.wasm and not a.verify):
        sys.exit("player_build: the native library is not built (python3 build.py native)")

    if not a.dna and any(re.search(r"^//\s*dna\s*$", open(f, encoding="utf-8-sig").read(2000), re.M) for f in files):
        a.dna = True                          # a game that says so (a `// dna` line in a file) is always built with --dna
    gfx = uses_gfx(files)
    if gfx and a.wasm and not a.web:
        sys.exit("player_build: --wasm: a game that draws needs a renderer: --web builds it for a page (WebGL2); under node there is no GL")

    if gfx:
        check_constants()
        if a.static:
            print("note: a game that draws needs GL, which is loaded at run time: building dynamically (libc, libm, libEGL, libGLESv2), not statically")
            a.static = False
    if a.dotnet:
        generate(files, out_dir, gfx)
        ref, err = run_dotnet(files, out_dir, native_dir)
        if ref is None:
            print(err)
            return 1
        print(ref)
        return 0
    print("game      %s  (%d file%s, entry %s%s)" % (os.path.relpath(game_dir, PROWL), len(files), "" if len(files) == 1 else "s", main_class, ", draws" if gfx else ""))
    try:
        c_file, diags, raw = translate(files, out_dir, main_class, gfx, a.dna)
    except gen_scripts.GenError as e:
        print("%s: error CCS0002: %s" % (game_dir, e))
        return 1
    if c_file is None:
        if diags:
            print("\n".join(diags))
            print("\n%d construct(s) outside the C# subset: the C build cannot translate this game." % len(diags))
            if not a.dna:
                print("hint: --dna builds those classes as managed code, run by DotNetAnywhere inside the player (a `// dna` line in a game file makes it the default).")
            if any(re.search(r"error CS(0246|0234|0103|0117|1061|1501|0305)", d) for d in diags):
                print("note: a 'could not be found' / 'does not contain a definition' error may be a typo, or a .NET API that the C build's library does not\n"
                      "      have (there is no LINQ, for one): see tools/ccsharp/README.md, 'Subset limits'.")
        else:
            print("player_build: the translator failed:\n" + raw[-1500:])
        return 1
    print("translated  %s  (%d lines of C)" % (os.path.relpath(c_file, PROWL), sum(1 for _ in open(c_file))))
    if a.check:
        print("ok: the game is inside the C# subset")
        return 0

    hybrid = a.dna and os.path.exists(os.path.join(os.path.dirname(c_file), "player.bridge.c"))
    if hybrid:
        import wasm_build
        shutil.copy2(os.path.join(PROWL, "Native", "Box2D", "prowl_box2d.h"), os.path.join(out_dir, "prowl_box2d.h"))
        exe = wasm_build.link_hybrid(out_dir, os.path.dirname(c_file), a.wasm, a.cc if a.cc != "cc" else None, gfx=gfx, web=a.web, main_class=main_class)
        if a.web:
            print("             a page: serve %s over http (python3 -m http.server) and open index.html" % os.path.relpath(out_dir, PROWL))
            return 0
        print("built       %s  (hybrid: native C + managed on DotNetAnywhere%s)" % (os.path.relpath(exe, PROWL), ", wasm32" if a.wasm else ""))
    elif a.web:
        module = wasm_build.link_web(out_dir, c_file, main_class)
        print("built       %s  (%d KiB); a page: serve %s over http (python3 -m http.server) and open index.html" % (os.path.relpath(module, PROWL),
              os.path.getsize(module) // 1024, os.path.relpath(out_dir, PROWL)))
        return 0
    elif a.wasm:
        shutil.copy2(c_file, os.path.join(out_dir, "player.c"))
        shutil.copy2(os.path.join(PROWL, "Native", "Box2D", "prowl_box2d.h"), os.path.join(out_dir, "prowl_box2d.h"))
        exe, module = wasm_build.link_player(out_dir)
        print("built       %s  (%d KiB; run it with %s, which needs node 20+)" % (os.path.relpath(module, PROWL), os.path.getsize(module) // 1024,
                                                                                os.path.relpath(exe, PROWL)))
    else:
        package(out_dir, c_file, native_dir, gfx)
        exe = compile_player(out_dir, a.cc, a.static, gfx)
        print("built       %s  (%d KiB)" % (os.path.relpath(exe, PROWL), os.path.getsize(exe) // 1024))
        print("package     %s  (rebuild with only a C compiler: `make` or ./build.sh in it)" % os.path.relpath(out_dir, PROWL))

    rc = 0
    for old in glob.glob(os.path.join(out_dir, "frame_*.ppm")):
        os.remove(old)
    native_out = subprocess.run([exe], capture_output=True, text=True, cwd=out_dir)   # (for --wasm: the launcher, so the wasm module's output)
    if native_out.returncode != 0:
        print("the player exited with %d" % native_out.returncode)
        rc = 1
    if a.sanitize:
        if gfx:
            print("sanitize  skipped: a game that draws loads the system's GL, which is not run under the sanitizers")
        else:
            why = sanitize_run(out_dir, a.cc, native_out.stdout)
            if why is None:
                print("sanitize  ok: no AddressSanitizer or UBSan reports")
            else:
                print("sanitize  FAILED: " + why)
                rc = 1
    if a.run:
        print("\n" + native_out.stdout)
    if a.verify:
        ref, err = run_dotnet(files, out_dir, native_dir)
        if ref is None:
            print("verify    FAILED to run the .NET reference: %s" % err)
            return 1
        if ref == native_out.stdout:
            print("verify    ok: the %s and the .NET run print identical output (%d lines)" % ("wasm module" if a.wasm else "native player", len(ref.splitlines())))
        else:
            x, y = ref.splitlines(), native_out.stdout.splitlines()
            first = next((i for i in range(max(len(x), len(y))) if i >= len(x) or i >= len(y) or x[i] != y[i]), 0)
            print("verify    FAILED: outputs differ at line %d\n   .NET  : %s\n   native: %s"
                  % (first + 1, x[first] if first < len(x) else "(none)", y[first] if first < len(y) else "(none)"))
            rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main())
