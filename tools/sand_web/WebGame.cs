// The page front end of SlimeJumpDestructSand (built by tools/build_sand_web.py together with the sample's own files, in place of its Game.cs). It starts as a demo: the bot
// plays the shaft. The first key or click or touch hands the slime to the person (B hands it back), and the page's keyboard and touch buttons arrive here as the
// events gfx2d.h describes. Every frame draws the world: the wall is an old building (brick in 4-unit tiles of different kinds: plain, painted white and peeling,
// cracked, bricks missing, bare concrete, an exposed pipe), the air behind is the same brick dimmed. The terrain is one texture (144 x 768 pixels, one per terrain
// pixel) that is refreshed where the camera looks; the crates, boulders, fragments, the slime, the shots and the teleporter are triangles over it. What the page
// needs to know it is told in lines on the log: MODE, ROOM, WARP.
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;
using Prowl.Native.Gfx2D;
using Prowl.Runtime.Destruction2D;

static class Game
{
    const int W = 540;                 // the canvas: 18 units wide, 24 high (30 pixels a unit)
    const int H = 720;
    const int PxW = 144;
    const int PxH = 768;
    const int MaxVerts = 9000;

    static float[] none;
    static float[] polyX;
    static float[] polyY;
    static float[] verts;
    static int nv;
    static byte[] pixels;              // the terrain as the screen sees it, rows from the top
    static int[] wallTex;              // 0xRRGGBB of a wall pixel, by terrain pixel (x + y * PxW), y from the bottom
    static int[] backTex;
    static int texture;
    static float camY;
    static float acc;
    static int ticks;
    static int anim;
    static int[] ev;
    static bool kL, kR, kJ, kD, mDown;
    static bool pL, pR, pJ, pD, pMouse;   // pressed since the last frame, even if let go again before it (a quick tap must still count)
    static float mouseX, mouseY;       // the pointer, in canvas pixels
    static float aimX, aimY;
    static int roomSeen;
    static int warpsSeen;
    static int flash;                  // frames left of the teleport's rings
    static float flashAX, flashAY, flashBX, flashBY;
    static float lastPX, lastPY;

    // brick(): results
    static int bR, bG, bB, bCol, bRow;
    static bool bMortar;

    static float Rnd(int a, int b, int salt)
    {
        uint h = (uint)a * 374761393u + (uint)b * 668265263u + (uint)salt * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h = h ^ (h >> 16);
        return (float)(h & 0xFFFFFFu) / 16777216f;
    }

    static int Clamp(float v)
    {
        int i = (int)v;
        if (i < 0) return 0;
        if (i > 255) return 255;
        return i;
    }

    static int Pack(float r, float g, float b) { return (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b); }
    static int ShadePacked(int c, float k) { return Pack((float)((c >> 16) & 255) + k, (float)((c >> 8) & 255) + k, (float)(c & 255) + k); }
    static int MixPacked(int c, int d, float t)
    {
        float cr = (float)((c >> 16) & 255), cg = (float)((c >> 8) & 255), cb = (float)(c & 255);
        float dr = (float)((d >> 16) & 255), dg = (float)((d >> 8) & 255), db = (float)(d & 255);
        return Pack(cr + (dr - cr) * t, cg + (dg - cg) * t, cb + (db - cb) * t);
    }

    // a brick wall pixel: 12 x 6 bricks, every other row shifted by half a brick; sets bR/bG/bB, the brick (bCol, bRow) and whether it is mortar
    static void Brick(int x, int y)
    {
        int row = y / 6;
        int xo = x;
        if ((row & 1) == 1) xo = x + 6;
        int col = xo / 12;
        int ix = xo % 12;
        int iy = y % 6;
        bCol = col;
        bRow = row;
        float n = Rnd(col, row, 1);
        if (ix == 0 || iy == 0)
        {
            bMortar = true;
            bR = 104; bG = 96; bB = 88;
            return;
        }
        bMortar = false;
        float r = (float)(150 + (int)(n * 36f) - 18);
        float g = (float)(72 + (int)(n * 20f) - 10);
        float b = (float)(54 + (int)(n * 14f) - 7);
        float k = (Rnd(x, y, 2) - 0.5f) * 6f;
        if (iy == 5) k -= 14f;
        else if (iy == 1) k += 10f;
        bR = Clamp(r + k); bG = Clamp(g + k); bB = Clamp(b + k);
    }

    static int BrickColor() { return (bR << 16) | (bG << 8) | bB; }

    static int WallPixel(int x, int y)
    {
        int tx = x / 32;
        int ty = y / 32;
        float h = Rnd(tx, ty, 7);
        int lx = x - tx * 32;
        int ly = y - ty * 32;
        Brick(x, y);
        int c = BrickColor();
        bool mortar = bMortar;
        int key0 = bCol;
        int key1 = bRow;
        if (h < 0.30f) return c;
        if (h < 0.44f)                                          // painted white, peeling
        {
            float patch = Rnd(x / 4, y / 4, tx * 31 + ty) * 0.6f + Rnd(x / 9, y / 7, 5) * 0.4f;
            if (patch > 0.30f)
            {
                int paint = 0xE2DED2;
                if (mortar) paint = 0xBEBAAE;
                float k = (Rnd(x, y, 3) - 0.5f) * 5f;
                if (y % 6 == 5 && !mortar) k -= 14f;
                return ShadePacked(paint, k);
            }
            return c;
        }
        if (h < 0.60f)                                          // cracked
        {
            int cx = 8 + (int)(Rnd(tx, ty, 11) * 16f);
            for (int yy = 0; yy <= ly; yy++)
                cx += (int)(Rnd(tx, ty * 40 + yy, 12) * 3f) - 1;
            int d = lx - cx;
            if (d < 0) d = -d;
            int wid = 0;
            if (ly % 5 != 0) wid = 1;
            int d2 = lx - (cx + (ly - 14));
            if (d2 < 0) d2 = -d2;
            if (d <= wid || (ly > 14 && d2 == 0)) return 0x241A18;
            if (d == 2 && ly % 3 != 0) return ShadePacked(c, -26f);
            return c;
        }
        if (h < 0.72f)                                          // bricks missing
        {
            if (!mortar && Rnd(key0, key1, 21 + tx * 3 + ty) < 0.42f)
            {
                if (y % 6 == 5) return 0x2C2220;
                return 0x1E1616;
            }
            return c;
        }
        if (h < 0.82f)                                          // bare concrete
        {
            int g = 122 + (int)((Rnd(x / 3, y / 3, 6) - 0.5f) * 18f);
            int col = (g << 16) | (g << 8) | (g - 3);
            if (lx == 0 || ly == 0) col = ShadePacked(col, -34f);
            if (Rnd(tx, ty, 9) > 0.5f && ((lx * 3 + ly * 5) % 41 < 3 || Rnd(x / 3, y / 3, 4) > 0.93f)) col = ShadePacked(col, -22f);
            return col;
        }
        // an exposed pipe, across the tile or down it
        bool vertical = Rnd(tx, ty, 13) > 0.5f;
        int u = lx;
        int v = ly;
        if (vertical) { u = ly; v = lx; }
        if (v >= 12 && v < 20)
        {
            int k = v - 12;
            float sh = -46f;
            if (k == 1) sh = -10f;
            else if (k == 2) sh = 24f;
            else if (k == 3) sh = 46f;
            else if (k == 4) sh = 36f;
            else if (k == 5) sh = 14f;
            else if (k == 6) sh = -8f;
            else if (k == 7) sh = -40f;
            int col = ShadePacked(0x60686C, sh);
            int um = u % 16;
            if (um == 0 || um == 1 || um == 2 || um == 15) col = ShadePacked(col, -30f);
            if (Rnd(x / 3, y / 3, 14) > 0.82f) col = MixPacked(col, 0x96542C, 0.7f);
            return col;
        }
        if ((v >= 10 && v < 12) || (v >= 20 && v < 22)) return ShadePacked(c, -34f);
        return c;
    }

    static int BackPixel(int x, int y)
    {
        Brick(x, y);
        float t = 0.10f;
        if (bMortar) t = 0.16f;
        return MixPacked(0x100E14, BrickColor(), t + Rnd(x / 16, y / 16, 17) * 0.05f);
    }

    static void Put(int px, int row, int c)
    {
        int i = (row * PxW + px) * 4;
        pixels[i] = (byte)((c >> 16) & 255);
        pixels[i + 1] = (byte)((c >> 8) & 255);
        pixels[i + 2] = (byte)(c & 255);
        pixels[i + 3] = 255;
    }

    // refreshes the rows of the picture that the camera can see
    static void Paint(int y0, int y1)
    {
        for (int y = y0; y < y1; y++)
        {
            int row = PxH - 1 - y;
            for (int x = 0; x < PxW; x++)
            {
                int code = Destruct.Code(x, y);
                int c;
                if (code == 0) c = backTex[x + y * PxW];
                else if (code == 1) c = wallTex[x + y * PxW];
                else if (code == 2) c = ShadePacked(0xDEBE60, (float)(((x * 7 + y * 13) % 5 - 2) * 4));
                else c = ShadePacked(0x4082E6, (float)(((x * 5 + y * 3) % 5 - 2) * 4));
                Put(x, row, c);
            }
        }
        GFX.TextureUpdate(texture, 0, PxH - y1, PxW, y1 - y0, pixels);
    }

    // ---- triangles ---------------------------------------------------------------------------------------------------------------------

    static void V(float x, float y, float u, float v, float r, float g, float b, float a)
    {
        int i = nv * 8;
        verts[i] = x; verts[i + 1] = y; verts[i + 2] = u; verts[i + 3] = v;
        verts[i + 4] = r; verts[i + 5] = g; verts[i + 6] = b; verts[i + 7] = a;
        nv++;
    }

    static void Quad(float x0, float y0, float x1, float y1, float r, float g, float b)
    {
        if (nv + 6 > MaxVerts) return;
        V(x0, y1, 0f, 0f, r, g, b, 1f); V(x1, y1, 0f, 0f, r, g, b, 1f); V(x1, y0, 0f, 0f, r, g, b, 1f);
        V(x0, y1, 0f, 0f, r, g, b, 1f); V(x1, y0, 0f, 0f, r, g, b, 1f); V(x0, y0, 0f, 0f, r, g, b, 1f);
    }

    static void Flush(int tex)
    {
        if (nv > 0) GFX.Triangles(verts, nv, tex);
        nv = 0;
    }

    // a convex polygon of a collider (in the node's space), filled, with a darker rim: the same polygon, a little smaller, on top of it
    static void Poly(Node bn, Collider2D col, float fr, float fg, float fb, float rr, float rg, float rb)
    {
        int n = col.PolyCount;
        if (n < 3 || nv + 6 * (n - 2) > MaxVerts) return;
        float cx = 0f, cy = 0f;
        for (int i = 0; i < n; i++)
        {
            float x, y;
            bn.ToWorld(col.Poly[2 * i], col.Poly[2 * i + 1], out x, out y);
            polyX[i] = x; polyY[i] = y;
            cx += x; cy += y;
        }
        cx /= (float)n; cy /= (float)n;
        for (int i = 1; i + 1 < n; i++)
        {
            V(polyX[0], polyY[0], 0f, 0f, rr, rg, rb, 1f); V(polyX[i], polyY[i], 0f, 0f, rr, rg, rb, 1f); V(polyX[i + 1], polyY[i + 1], 0f, 0f, rr, rg, rb, 1f);
        }
        float s = 0.78f;
        for (int i = 0; i < n; i++)
        {
            polyX[i] = cx + (polyX[i] - cx) * s;
            polyY[i] = cy + (polyY[i] - cy) * s;
        }
        for (int i = 1; i + 1 < n; i++)
        {
            V(polyX[0], polyY[0], 0f, 0f, fr, fg, fb, 1f); V(polyX[i], polyY[i], 0f, 0f, fr, fg, fb, 1f); V(polyX[i + 1], polyY[i + 1], 0f, 0f, fr, fg, fb, 1f);
        }
    }

    static void QuadA(float x0, float y0, float x1, float y1, float r, float g, float b, float a)
    {
        if (nv + 6 > MaxVerts) return;
        V(x0, y1, 0f, 0f, r, g, b, a); V(x1, y1, 0f, 0f, r, g, b, a); V(x1, y0, 0f, 0f, r, g, b, a);
        V(x0, y1, 0f, 0f, r, g, b, a); V(x1, y0, 0f, 0f, r, g, b, a); V(x0, y0, 0f, 0f, r, g, b, a);
    }

    // the teleporter: a tall ring of sparks, turning, over a soft glow
    static void Portal(float cx, float cy)
    {
        float t = (float)anim * 0.06f;
        QuadA(cx - 0.55f, cy - 0.95f, cx + 0.55f, cy + 0.95f, 0.42f, 0.2f, 0.85f, 0.35f);
        QuadA(cx - 0.35f, cy - 0.7f, cx + 0.35f, cy + 0.7f, 0.7f, 0.5f, 1f, 0.25f);
        for (int i = 0; i < 22; i++)
        {
            float a = t + (float)i * 0.285599f;
            float x = cx + MathF.Cos(a) * 0.62f;
            float y = cy + MathF.Sin(a) * 1.02f;
            float s = 0.07f + 0.05f * MathF.Sin(a * 3f + t * 2f);
            if ((i & 1) == 0) Quad(x - s, y - s, x + s, y + s, 0.72f, 0.4f, 1f);
            else Quad(x - s, y - s, x + s, y + s, 0.35f, 0.92f, 1f);
        }
    }

    // a ring of sparks widening and fading from a point (age 0..70)
    static void Ring(float cx, float cy, int age)
    {
        float r = 0.4f + (float)age * 0.045f;
        float a = 1f - (float)age / 70f;
        for (int i = 0; i < 18; i++)
        {
            float an = (float)i * 0.349066f + (float)age * 0.02f;
            float x = cx + MathF.Cos(an) * r;
            float y = cy + MathF.Sin(an) * r * 1.4f;
            float s = 0.09f * a + 0.02f;
            if ((i & 1) == 0) QuadA(x - s, y - s, x + s, y + s, 0.72f, 0.4f, 1f, a);
            else QuadA(x - s, y - s, x + s, y + s, 0.35f, 0.92f, 1f, a);
        }
    }

    static void DrawNodes(Scene2D scene)
    {
        for (int q = 0; q < scene.NodeHighWater; q++)
        {
            Node bn = scene.NodeAt(q);
            if (bn == null || !bn.Alive || bn.Destroyed) continue;
            int tag = bn.Tag;
            if (tag == Shared.TagBulletPlayer)
            {
                Quad(bn.WorldX() - 0.12f, bn.WorldY() - 0.12f, bn.WorldX() + 0.12f, bn.WorldY() + 0.12f, 1f, 0.94f, 0.47f);
                continue;
            }
            Component cc = scene.Find(bn, ComponentKind.PolygonCollider2D);
            if (cc == null) cc = scene.Find(bn, ComponentKind.BoxCollider2D);
            if (cc == null) continue;
            Collider2D col = cc.Collider;
            if (col.ShapeKind != Collider2D.Polygon)
            {
                // a box: its corners as a polygon
                if (tag == Shared.TagGoal)
                {
                    Portal(bn.WorldX(), bn.WorldY());
                    continue;
                }
                if (tag == Shared.TagPlayer)
                {
                    float px = bn.WorldX(), py = bn.WorldY();
                    float hw = col.SizeX * 0.5f, hh = col.SizeY * 0.5f;
                    Quad(px - hw, py - hh, px + hw, py + hh, 0.08f, 0.35f, 0.16f);
                    Quad(px - hw + 0.05f, py - hh + 0.05f, px + hw - 0.05f, py + hh - 0.05f, 0.27f, 0.86f, 0.43f);
                    Quad(px - 0.30f, py + 0.02f, px - 0.06f, py + 0.30f, 1f, 1f, 1f);
                    Quad(px + 0.06f, py + 0.02f, px + 0.30f, py + 0.30f, 1f, 1f, 1f);
                    Quad(px - 0.22f, py + 0.05f, px - 0.12f, py + 0.17f, 0.05f, 0.1f, 0.05f);
                    Quad(px + 0.12f, py + 0.05f, px + 0.22f, py + 0.17f, 0.05f, 0.1f, 0.05f);
                    continue;
                }
                float ox = col.OffsetX, oy = col.OffsetY;
                float bw = col.SizeX * 0.5f, bh = col.SizeY * 0.5f;
                float x0, y0, x1, y1, x2, y2, x3, y3;
                bn.ToWorld(ox - bw, oy - bh, out x0, out y0);
                bn.ToWorld(ox - bw, oy + bh, out x1, out y1);
                bn.ToWorld(ox + bw, oy + bh, out x2, out y2);
                bn.ToWorld(ox + bw, oy - bh, out x3, out y3);
                if (nv + 12 > MaxVerts) continue;
                float cr = 0.77f, cg = 0.47f, cb = 0.2f, rr = 0.43f, rg = 0.24f, rb = 0.08f;
                int it = tag - Destruct.TagCrateBase;
                if (tag >= Destruct.TagCrateBase && !Destruct.ItemIsCrate(it)) { cr = 0.54f; cg = 0.54f; cb = 0.52f; rr = 0.24f; rg = 0.24f; rb = 0.23f; }
                float mx = (x0 + x1 + x2 + x3) * 0.25f, my = (y0 + y1 + y2 + y3) * 0.25f;
                V(x0, y0, 0f, 0f, rr, rg, rb, 1f); V(x1, y1, 0f, 0f, rr, rg, rb, 1f); V(x2, y2, 0f, 0f, rr, rg, rb, 1f);
                V(x0, y0, 0f, 0f, rr, rg, rb, 1f); V(x2, y2, 0f, 0f, rr, rg, rb, 1f); V(x3, y3, 0f, 0f, rr, rg, rb, 1f);
                float s = 0.8f;
                x0 = mx + (x0 - mx) * s; y0 = my + (y0 - my) * s; x1 = mx + (x1 - mx) * s; y1 = my + (y1 - my) * s;
                x2 = mx + (x2 - mx) * s; y2 = my + (y2 - my) * s; x3 = mx + (x3 - mx) * s; y3 = my + (y3 - my) * s;
                V(x0, y0, 0f, 0f, cr, cg, cb, 1f); V(x1, y1, 0f, 0f, cr, cg, cb, 1f); V(x2, y2, 0f, 0f, cr, cg, cb, 1f);
                V(x0, y0, 0f, 0f, cr, cg, cb, 1f); V(x2, y2, 0f, 0f, cr, cg, cb, 1f); V(x3, y3, 0f, 0f, cr, cg, cb, 1f);
                continue;
            }
            if (bn.Layer == Layers.Wall) continue;
            Poly(bn, col, 0.62f, 0.38f, 0.30f, 0.27f, 0.16f, 0.13f);          // a fragment: brick rubble
        }
    }

    // ---- the game ------------------------------------------------------------------------------------------------------------------------

    static void Start()
    {
        World.Build();
        ticks = 0;
        acc = 0f;
        camY = World.player.WorldY();
        lastPX = World.player.WorldX();
        lastPY = World.player.WorldY();
        roomSeen = -1;
        warpsSeen = 0;
    }

    public static int Init()
    {
        none = new float[GFX.SpriteFloats];
        ev = new int[5];
        polyX = new float[8];
        polyY = new float[8];
        verts = new float[MaxVerts * 8];
        pixels = new byte[PxW * PxH * 4];
        wallTex = new int[PxW * PxH];
        backTex = new int[PxW * PxH];
        if (GFX.Init(W, H) == 0) { Console.WriteLine("no GLES 3 here"); return 1; }
        for (int y = 0; y < PxH; y++)
            for (int x = 0; x < PxW; x++)
            {
                wallTex[x + y * PxW] = WallPixel(x, y);
                backTex[x + y * PxW] = BackPixel(x, y);
            }
        texture = GFX.Texture(PxW, PxH, 0, pixels);
        Start();
        SetMode(true);
        Paint(0, PxH);
        GFX.TextureUpdate(texture, 0, 0, PxW, PxH, pixels);
        return 0;
    }

    static void SetMode(bool bot)
    {
        InputState.BotOn = bot;
        if (bot) Console.WriteLine("MODE watch");
        else Console.WriteLine("MODE play");
    }

    // the keys the page sends are the letters and arrows of gfx2d.h's events; the pointer is in canvas pixels
    static void Key(int key, bool down, bool repeat)
    {
        bool control = false;
        if (key == 65 || key == 263) { kL = down; if (down) pL = true; control = true; }
        else if (key == 68 || key == 262) { kR = down; if (down) pR = true; control = true; }
        else if (key == 87 || key == 265 || key == 32) { kJ = down; if (down) pJ = true; control = true; }
        else if (key == 83 || key == 264) { kD = down; if (down) pD = true; control = true; }
        else if (down && !repeat && key == 66) SetMode(!InputState.BotOn);
        else if (down && !repeat && key == 82) Shared.WarpRequested = true;
        if (control && down && InputState.BotOn) SetMode(false);
    }

    static void PollInput()
    {
        while (GFX.PollEvent(ev) != 0)
        {
            int t = ev[0];
            if (t == 1 || t == 2 || t == 3)
            {
                mouseX = (float)ev[1];
                mouseY = (float)ev[2];
                if (t == 2 && ev[3] == 0) { mDown = true; pMouse = true; if (InputState.BotOn) SetMode(false); }
                if (t == 3 && ev[3] == 0) mDown = false;
            }
            else if (t == 5) Key(ev[1], true, ev[2] != 0);
            else if (t == 6) Key(ev[1], false, false);
            else if (t == 8 && ev[1] == 0) { kL = false; kR = false; kJ = false; kD = false; mDown = false; pL = false; pR = false; pJ = false; pD = false; pMouse = false; }
        }
    }

    // what the person is doing, as the bot would have written it: A/D or the arrows walk, W, up or space jump, the pointer aims and a click fires, S or down fires straight down
    static void ApplyInput()
    {
        float px = World.player.WorldX();
        float py = World.player.WorldY();
        float move = 0f;
        if (kR || pR) move += 1f;
        if (kL || pL) move -= 1f;
        InputState.Move = move;
        InputState.Jump = kJ || pJ;
        InputState.Lasso = false;
        InputState.ChangeLassoLength = 0;
        aimX = 9f + (mouseX / (float)W - 0.5f) * 18f;
        aimY = camY + (0.5f - mouseY / (float)H) * 24f;
        if (kD || pD)
        {
            InputState.Attack = true;
            InputState.AimX = px;
            InputState.AimY = py - 4f;
        }
        else if (mDown || pMouse)
        {
            InputState.Attack = true;
            InputState.AimX = aimX;
            InputState.AimY = aimY;
        }
        else
        {
            InputState.Attack = false;
            InputState.AimX = aimX;
            InputState.AimY = aimY;
        }
    }

    // one frame (1/60 s): the world steps at 100 Hz, the picture is drawn
    public static void Frame()
    {
        PollInput();
        acc += 1f / 60f;
        while (acc >= Cfg.Dt)
        {
            acc -= Cfg.Dt;
            if (!InputState.BotOn) ApplyInput();
            World.Tick();
            ticks++;
        }
        pL = false; pR = false; pJ = false; pD = false; pMouse = false;
        anim++;
        Scene2D scene = World.scene;
        float px = World.player.WorldX();
        float py = World.player.WorldY();
        if (Shared.Warps != warpsSeen)
        {
            warpsSeen = Shared.Warps;
            Console.WriteLine("WARP " + Shared.Warps);
        }
        if (MathF.Abs(py - lastPY) > 15f)                                  // sent back to the start: the camera jumps, and rings open at both ends
        {
            camY = py;
            flash = 70;
            flashAX = lastPX; flashAY = lastPY; flashBX = px; flashBY = py;
        }
        lastPX = px;
        lastPY = py;
        int room = Level.RoomOfY(py);
        if (room != roomSeen)
        {
            roomSeen = room;
            Console.WriteLine("ROOM " + (room + 1));
        }
        camY += (py - 1.5f - camY) * 0.1f;
        float lo = 12f, hi = 84f;
        if (camY < lo) camY = lo;
        if (camY > hi) camY = hi;
        int y0 = (int)((camY - 14f) * 8f);
        int y1 = (int)((camY + 14f) * 8f);
        if (y0 < 0) y0 = 0;
        if (y1 > PxH) y1 = PxH;
        Paint(y0, y1);
        GFX.Camera(9f, camY, 12f, 0.06f, 0.055f, 0.08f);
        GFX.Draw(none, 0);
        nv = 0;
        V(0f, 96f, 0f, 0f, 1f, 1f, 1f, 1f); V(18f, 96f, 1f, 0f, 1f, 1f, 1f, 1f); V(18f, 0f, 1f, 1f, 1f, 1f, 1f, 1f);
        V(0f, 96f, 0f, 0f, 1f, 1f, 1f, 1f); V(18f, 0f, 1f, 1f, 1f, 1f, 1f, 1f); V(0f, 0f, 0f, 1f, 1f, 1f, 1f, 1f);
        Flush(texture);
        DrawNodes(scene);
        if (flash > 0)
        {
            Ring(flashAX, flashAY, 70 - flash);
            Ring(flashBX, flashBY, 70 - flash);
            flash--;
        }
        Flush(0);
    }

    // headless: the bot plays until the teleporter has sent it back twice; then a key is "pressed" (injected) and the slime must be the person's, walking
    public static int Main()
    {
        if (Init() != 0) return 1;
        int frames = 0;
        while (Shared.Warps < 2 && frames < 9000)
        {
            Frame();
            frames++;
            if (frames == 300) GFX.SaveFrame(0);
            if (frames == 700) GFX.SaveFrame(1);
        }
        string a = "frames " + frames + " warps " + Shared.Warps + " at tick " + ticks + " sand hash " + Destruct.SandHash();
        Console.WriteLine(a);
        for (int i = 0; i < 30; i++) Frame();                                  // a moment after the second warp, back at the start
        float x0 = World.player.WorldX();
        GFX.InjectEvent(5, 68, 0, 0, 0);                                       // D down
        for (int i = 0; i < 40; i++) Frame();
        float x1 = World.player.WorldX();
        GFX.InjectEvent(6, 68, 0, 0, 0);
        bool took = !InputState.BotOn && x1 > x0 + 0.5f;
        string b = "took over " + took + " x*1000 " + (int)(x0 * 1000f) + " -> " + (int)(x1 * 1000f);
        Console.WriteLine(b);
        GFX.InjectEvent(5, 66, 0, 0, 0);                                       // B: back to the bot
        GFX.InjectEvent(6, 66, 0, 0, 0);
        Frame();
        bool back = InputState.BotOn;
        GFX.SaveFrame(2);
        string c = "bot back " + back;
        Console.WriteLine(c);
        return Shared.Warps >= 2 && took && back ? 0 : 1;
    }
}
