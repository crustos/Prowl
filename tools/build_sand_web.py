#!/usr/bin/env python3
"""build_sand_web.py -- build Samples/SlimeJumpDestructSand as a web page (WebAssembly + WebGL2/WebGPU) that plays the bot and draws it.

    python3 tools/build_sand_web.py [-o OUT] [--native]

The sample is headless (Game.cs prints). This assembles a copy of it in a scratch folder with tools/sand_web/WebGame.cs, which draws, in place of Game.cs, and builds
that with `player_build.py --web`. OUT (default Build/Player/SlimeJumpDestructSand-web) holds index.html, prowl_web.js and prowl2d-player.wasm: serve the folder over http
(any static server; the .wasm needs the application/wasm type, which every common server gives). --native builds and runs the same drawing game as a native player
instead (software GL here), which plays two rounds headless and saves frame_0000.ppm and frame_0001.ppm.
Needs what `python3 build.py player Samples/Draw2D --web` needs: clang, lld, wasi-libc (apt install wasi-libc libclang-rt-dev-wasm32) and node."""
import argparse, os, shutil, subprocess, sys, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SAMPLE = os.path.join(ROOT, "Samples", "SlimeJumpDestructSand")
WEB = os.path.join(ROOT, "tools", "sand_web")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-o", "--out", default=os.path.join(ROOT, "Build", "Player", "SlimeJumpDestructSand-web"))
    ap.add_argument("--native", action="store_true", help="build and run a native player instead of a page")
    a = ap.parse_args()
    work = tempfile.mkdtemp(prefix="sandweb_")
    game = os.path.join(work, "SlimeJumpDestructSand")
    shutil.copytree(SAMPLE, game, ignore=shutil.ignore_patterns("*.md", "*.gif", "Game.cs"))
    shutil.copy2(os.path.join(WEB, "WebGame.cs"), os.path.join(game, "WebGame.cs"))
    cmd = [sys.executable, os.path.join(ROOT, "tools", "ccsharp", "player_build.py"), game, "-o", a.out]
    cmd += ["--run"] if a.native else ["--web"]
    r = subprocess.run(cmd)
    if r.returncode == 0 and not a.native:                      # the sample's own page (speed buttons, a new run after a win) in place of the generic one
        with open(os.path.join(WEB, "index.html"), encoding="utf-8") as f:
            html = f.read()
        with open(os.path.join(a.out, "index.html"), "w", encoding="utf-8") as f:
            f.write(html.replace("__PROWL_FILES__", "[]"))
        print("page      %s  (serve the folder over http: python3 -m http.server -d %s)" % (os.path.join(a.out, "index.html"), a.out))
    shutil.rmtree(work, ignore_errors=True)
    sys.exit(r.returncode)

if __name__ == "__main__":
    main()
