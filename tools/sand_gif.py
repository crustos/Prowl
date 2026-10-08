#!/usr/bin/env python3
"""sand_gif.py -- draw an animated GIF of Samples/SlimeJumpDestructSand from its own run.

    python3 tools/sand_gif.py [-o OUT.gif] [--every N] [--scale S]

The sample is headless, so this copies it to a scratch folder, adds a recorder (it prints the terrain window and the nodes every N frames), runs it with the
native player, and draws the printout with Pillow: rock, sand and water from the terrain bitmap, boxes and fragment triangles from the colliders.
The sample itself is not changed."""
import argparse, os, shutil, subprocess, sys, tempfile
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SAMPLE = os.path.join(ROOT, "Samples", "SlimeJumpDestructSand")

REC = '''using System;
using Prowl.Core2D;
using Prowl.Runtime.Destruction2D;
static class Rec
{
    static int Mi(float v) { return (int)(v * 1000f); }
    public static void Frame(Scene2D scene, int frame, Node player)
    {
        if (frame % EVERY != 0) return;
        int y0 = (int)((player.WorldY() - 13f) * 8f);
        int y1 = y0 + 208;
        if (y0 < 0) { y0 = 0; y1 = 208; }
        if (y1 > 768) { y1 = 768; y0 = 560; }
        string head = "F " + frame + " " + y0 + " " + y1;
        Console.WriteLine(head);
        for (int y = y0; y < y1; y++)
        {
            string row = "R";
            int last = -1;
            int run = 0;
            for (int x = 0; x < 144; x++)
            {
                int c = Destruct.Code(x, y);
                if (c == last) { run++; continue; }
                if (run > 0) row = row + " " + last + ":" + run;
                last = c;
                run = 1;
            }
            row = row + " " + last + ":" + run;
            Console.WriteLine(row);
        }
        for (int q = 0; q < scene.NodeHighWater; q++)
        {
            Node bn = scene.NodeAt(q);
            if (bn == null || !bn.Alive || bn.Destroyed) continue;
            Component cc = scene.Find(bn, ComponentKind.PolygonCollider2D);
            string line = "N " + bn.Tag + " " + bn.Layer + " " + Mi(bn.WorldX()) + " " + Mi(bn.WorldY());
            if (cc != null)
            {
                Collider2D col = cc.Collider;
                line = line + " P " + col.PolyCount;
                for (int i = 0; i < col.PolyCount; i++)
                {
                    float wx;
                    float wy;
                    bn.ToWorld(col.Poly[2 * i], col.Poly[2 * i + 1], out wx, out wy);
                    line = line + " " + Mi(wx) + " " + Mi(wy);
                }
                Console.WriteLine(line);
                continue;
            }
            cc = scene.Find(bn, ComponentKind.BoxCollider2D);
            if (cc != null)
            {
                Collider2D col = cc.Collider;
                float wx;
                float wy;
                float hw = col.SizeX * 0.5f;
                float hh = col.SizeY * 0.5f;
                line = line + " P 4";
                bn.ToWorld(col.OffsetX - hw, col.OffsetY - hh, out wx, out wy); line = line + " " + Mi(wx) + " " + Mi(wy);
                bn.ToWorld(col.OffsetX - hw, col.OffsetY + hh, out wx, out wy); line = line + " " + Mi(wx) + " " + Mi(wy);
                bn.ToWorld(col.OffsetX + hw, col.OffsetY + hh, out wx, out wy); line = line + " " + Mi(wx) + " " + Mi(wy);
                bn.ToWorld(col.OffsetX + hw, col.OffsetY - hh, out wx, out wy); line = line + " " + Mi(wx) + " " + Mi(wy);
                Console.WriteLine(line);
                continue;
            }
            Console.WriteLine(line + " O");
        }
        Console.WriteLine("E");
    }
}
'''

def record(every):
    work = tempfile.mkdtemp(prefix="sandgif_")
    game = os.path.join(work, "SlimeJumpDestructSand")
    shutil.copytree(SAMPLE, game, ignore=shutil.ignore_patterns("*.md", "*.gif"))
    p = os.path.join(game, "Destruct.cs")
    s = open(p, encoding="utf-8").read()
    anchor = "    public static int SandHash()"
    code = ("    public static int Code(int x, int y)\n    {\n        int e = rock.Sand.ElementAt(x, y);\n        if (e == SandSim.Sand) return 2;\n"
            "        if (e == SandSim.Water) return 3;\n        if (rock.IsSolid(x, y)) return 1;\n        return 0;\n    }\n\n")
    assert anchor in s
    if "int Code(" not in s:
        s = s.replace(anchor, code + anchor, 1)
    open(p, "w", encoding="utf-8").write(s)
    open(os.path.join(game, "Rec.cs"), "w", encoding="utf-8").write(REC.replace("EVERY", str(every)))
    p = os.path.join(game, "Game.cs")
    s = open(p, encoding="utf-8").read()
    anchor = "            World.Tick();\n"
    assert anchor in s
    open(p, "w", encoding="utf-8").write(s.replace(anchor, anchor + "            Rec.Frame(scene, frame, player);\n", 1))
    out = os.path.join(work, "pkg")
    r = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "ccsharp", "player_build.py"), game, "-o", out, "--run"], capture_output=True, text=True)
    if r.returncode != 0 and "won=1" not in r.stdout:
        sys.exit(r.stdout[-3000:] + r.stderr[-3000:])
    return r.stdout, work

def parse(text):
    frames, cur = [], None
    for ln in text.splitlines():
        if ln.startswith("F "):
            _, f, y0, y1 = ln.split()
            cur = {"frame": int(f), "y0": int(y0), "y1": int(y1), "rows": [], "nodes": []}
        elif cur is None:
            continue
        elif ln.startswith("R"):
            row = []
            for t in ln.split()[1:]:
                c, n = t.split(":")
                row.append((int(c), int(n)))
            cur["rows"].append(row)
        elif ln.startswith("N "):
            w = ln.split()
            node = {"tag": int(w[1]), "layer": int(w[2]), "x": int(w[3]) / 1000, "y": int(w[4]) / 1000, "poly": None}
            if w[5] == "P":
                n = int(w[6])
                v = [int(t) / 1000 for t in w[7:7 + 2 * n]]
                node["poly"] = list(zip(v[0::2], v[1::2]))
            cur["nodes"].append(node)
        elif ln == "E":
            frames.append(cur)
            cur = None
    return frames

SAND, WATER = (222, 190, 96), (64, 130, 230)

# ---- the walls: an old building. The terrain's solid pixels are drawn as wall tiles of 32 x 32 pixels (4 units), each a different kind, picked by a hash of its place:
# red brick, brick painted white (peeling), cracked brick, brick with bricks missing, bare concrete, and brick with an exposed pipe. The air behind is the same
# brick, dim, as the inside of the building. Nothing here touches the game: the terrain is still just solid pixels.
def hsh(a, b, salt=0):
    h = (a * 374761393 + b * 668265263 + salt * 2246822519) & 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
    return (h ^ (h >> 16)) & 0xFFFFFF

def rnd(a, b, salt=0):
    return hsh(a, b, salt) / float(0x1000000)

def mix(c, d, t):
    return (int(c[0] + (d[0] - c[0]) * t), int(c[1] + (d[1] - c[1]) * t), int(c[2] + (d[2] - c[2]) * t))

def shade(c, k):
    return (max(0, min(255, int(c[0] + k))), max(0, min(255, int(c[1] + k))), max(0, min(255, int(c[2] + k))))

BW, BH = 12, 6                                       # a brick: 12 x 6 pixels
def brick(x, y, tint=0):
    row = y // BH
    xo = x + (BW // 2 if row & 1 else 0)
    col = xo // BW
    ix, iy = xo % BW, y % BH
    n = rnd(col, row, 1)
    if ix == 0 or iy == 0:                            # mortar
        return (104, 96, 88), (col, row), True
    base = (150 + int(n * 36) - 18, 72 + int(n * 20) - 10, 54 + int(n * 14) - 7)
    if iy == BH - 1:
        base = shade(base, -14)
    elif iy == 1:
        base = shade(base, 10)
    return shade(base, tint + (rnd(x, y, 2) - 0.5) * 6), (col, row), False

def wall_pixel(x, y):
    tx, ty = x // 32, y // 32
    h = rnd(tx, ty, 7)
    lx, ly = x - tx * 32, y - ty * 32
    c, key, mortar = brick(x, y)
    if h < 0.30:                                      # plain brick
        return c
    if h < 0.44:                                      # painted white, peeling: the paint covers most of it, in patches
        patch = rnd(x // 4, y // 4, tx * 31 + ty) * 0.6 + rnd(x // 9, y // 7, 5) * 0.4
        if patch > 0.30:
            paint = (226, 222, 210) if not mortar else (190, 186, 174)
            return shade(paint, (rnd(x, y, 3) - 0.5) * 5 - (14 if y % BH == BH - 1 and not mortar else 0))
        return c
    if h < 0.60:                                      # cracked: a dark crack wanders down the tile, and a second one branches from it
        cx = 8 + int(rnd(tx, ty, 11) * 16)
        for yy in range(ly + 1):
            cx += int(rnd(tx, ty * 40 + yy, 12) * 3) - 1
        if abs(lx - cx) <= (1 if ly % 5 else 0) or (ly > 14 and abs(lx - (cx + (ly - 14))) == 0):
            return (36, 26, 24)
        if abs(lx - cx) == 2 and ly % 3:
            return shade(c, -26)
        return c
    if h < 0.72:                                      # bricks missing: a dark recess where they were
        if not mortar and rnd(key[0], key[1], 21 + tx * 3 + ty) < 0.42:
            return (44, 34, 32) if iy_top(y) else (30, 22, 22)
        return c
    if h < 0.82:                                      # bare concrete
        g = 122 + int((rnd(x // 3, y // 3, 6) - 0.5) * 18)
        col = (g, g, g - 3)
        if lx == 0 or ly == 0:
            col = shade(col, -34)                     # a seam
        if rnd(tx, ty, 9) > 0.5 and ((lx * 3 + ly * 5) % 41 < 3 or rnd(x // 3, y // 3, 4) > 0.93):
            col = shade(col, -22)                     # a stain or a pit
        return col
    # an exposed pipe, across the tile or down it, over the brick
    vertical = rnd(tx, ty, 13) > 0.5
    u, v = (ly, lx) if vertical else (lx, ly)         # v: across the pipe; u: along it
    if 12 <= v < 20:
        k = v - 12
        metal = (96, 104, 108)
        col = shade(metal, (-46, -10, 24, 46, 36, 14, -8, -40)[k])
        if u % 16 in (0, 1, 2) or (u % 16) == 15:     # a joint with a flange
            col = shade(col, -30)
        if rnd(x // 3, y // 3, 14) > 0.82:
            col = mix(col, (150, 84, 44), 0.7)        # rust
        return col
    if 10 <= v < 12 or 20 <= v < 22:
        return shade(c, -34)                          # the pipe's shadow on the wall
    return c

def iy_top(y):
    return y % BH == BH - 1

def back_pixel(x, y):
    c, key, mortar = brick(x, y)
    t = 0.10 if not mortar else 0.16
    return mix((16, 14, 20), c, t + rnd(x // 16, y // 16, 17) * 0.05)

_tex = []
def textures():
    if not _tex:
        w = Image.new("RGB", (144, 768)); b = Image.new("RGB", (144, 768))
        wp, bp = w.load(), b.load()
        for y in range(768):
            for x in range(144):
                wp[x, 767 - y] = wall_pixel(x, y)
                bp[x, 767 - y] = back_pixel(x, y)
        _tex.extend([wp, bp])
    return _tex

CRATE_IDS = (200, 201, 202, 207, 208, 209, 210)          # item i has Tag 200 + i: the crates (Game.cs adds them in this order)
def draw(fr, scale, noise):
    ppu = 8
    h = fr["y1"] - fr["y0"]
    wp, bp = textures()
    small = Image.new("RGB", (144, h))
    sp = small.load()
    for ri, row in enumerate(fr["rows"]):
        y = fr["y0"] + ri                      # terrain pixel row (0 is the bottom of the world)
        x = 0
        for c, n in row:
            for xx in range(x, x + n):
                if c == 0:
                    col = bp[xx, 767 - y]
                elif c == 1:
                    col = wp[xx, 767 - y]
                elif c == 2:
                    col = shade(SAND, ((xx * 7 + y * 13) % 5 - 2) * 4)
                else:
                    col = shade(WATER, ((xx * 5 + y * 3) % 5 - 2) * 4)
                sp[xx, fr["y1"] - 1 - y] = col
            x += n
    img = small.resize((144 * scale, h * scale), Image.NEAREST)
    d = ImageDraw.Draw(img)
    def S(wx, wy):
        return (wx * ppu * scale, (fr["y1"] - wy * ppu) * scale)
    bullets = []
    for n in fr["nodes"]:
        t = n["tag"]
        if n["layer"] == 7 and n["poly"] is None:
            continue                           # the terrain's chunk nodes
        if t == 7:
            bullets.append(S(n["x"], n["y"]))
            continue
        if n["poly"] is None:
            continue
        pts = [S(x, y) for x, y in n["poly"]]
        if t == 1:                             # the slime
            d.polygon(pts, fill=(70, 220, 110), outline=(20, 90, 40))
            cx, cy = S(n["x"], n["y"])
            e = scale * 2
            d.ellipse([cx - 2 * e, cy - e * 1.5, cx - 0.4 * e, cy - e * 0.1], fill=(255, 255, 255))
            d.ellipse([cx + 0.4 * e, cy - e * 1.5, cx + 2 * e, cy - e * 0.1], fill=(255, 255, 255))
        elif t == 6:                           # the goal
            d.polygon(pts, outline=(255, 215, 0))
        elif t >= 200:
            if t in CRATE_IDS:
                d.polygon(pts, fill=(196, 120, 50), outline=(110, 60, 20))
            else:
                d.polygon(pts, fill=(138, 138, 132), outline=(62, 62, 60))
        else:                                  # a fragment
            d.polygon(pts, fill=(158, 96, 76), outline=(70, 40, 34))
    for bx, by in bullets:
        d.ellipse([bx - scale * 2, by - scale * 2, bx + scale * 2, by + scale * 2], fill=(255, 240, 120))
    return img

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-o", "--out", default=os.path.join(SAMPLE, "slimejumpdestructsand.gif"))
    ap.add_argument("--every", type=int, default=8)
    ap.add_argument("--scale", type=int, default=2)
    ap.add_argument("--dump", help="keep the raw printout here")
    a = ap.parse_args()
    text, work = record(a.every)
    if a.dump:
        open(a.dump, "w").write(text)
    frames = parse(text)
    print("recorded %d frames" % len(frames))
    shutil.rmtree(work, ignore_errors=True)
    imgs = [draw(f, a.scale, 4) for f in frames]
    # one palette for all frames (a frame of its own would lose the slime's green), made from a montage of some frames and the picture's key colours
    keys = [(70, 220, 110), (20, 90, 40), (255, 255, 255), (255, 240, 120), (255, 215, 0), (196, 120, 50), (110, 60, 20), (138, 138, 132), (62, 62, 60), (158, 96, 76), (70, 40, 34), SAND, WATER]
    sw = Image.new("RGB", (len(keys) * 8, 8))
    for i, k in enumerate(keys):
        sw.paste(k, (i * 8, 0, i * 8 + 8, 8))
    pick = imgs[::max(1, len(imgs) // 12)]
    mont = Image.new("RGB", (imgs[0].width, imgs[0].height * len(pick) + 8))
    for i, im in enumerate(pick):
        mont.paste(im, (0, i * im.height))
    mont.paste(sw, (0, len(pick) * imgs[0].height))
    pal = mont.quantize(colors=128, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    plist = pal.getpalette()[:384]
    for i, k in enumerate(keys):                    # the key colours are exact, in the last entries
        plist[(128 - len(keys) + i) * 3:(128 - len(keys) + i) * 3 + 3] = list(k)
    pal.putpalette(plist)
    q = [im.quantize(palette=pal, dither=Image.Dither.NONE) for im in imgs]
    q[0].save(a.out, save_all=True, append_images=q[1:], duration=int(a.every * 10), loop=0, optimize=True)
    print("wrote %s (%d frames, %dx%d, %d KiB)" % (a.out, len(q), imgs[0].width, imgs[0].height, os.path.getsize(a.out) // 1024))

if __name__ == "__main__":
    main()
