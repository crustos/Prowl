using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;
using Prowl.Runtime.Destruction2D;

// The world of SlimeJumpDestructSand: one terrain bitmap, the shaft, with falling sand and flowing water in it (PixelTerrain2D.EnableSand), and the things that break.
//
//   * The rock is the terrain's stone: 18 x 96 units at 8 pixels a unit (144 x 768 pixels, 3 x 12 chunks), with seven rooms cut into it (Level.cs). A bullet digs a
//     1.5-unit hole. Sand and water are pixels of the same bitmap: a pile of sand is ground (the slime walks on it, it has colliders), water is not.
//   * Whatever is dug out lets what is above it move: the terrain wakes the sand and water around a dug or built pixel, so a hole in a floor lets the dune on it run
//     through, and the pool above it drain, into the room below. That is the point of the demo.
//   * Crates and boulders (Shatter2D) are bodies a bullet detonates: they shatter into fragments, the blast pushes things away and digs a crater (a crate a big one),
//     and what is beside it goes off a moment later. A boulder also crumbles: a little sand is sprinkled where it was.
//   * Two silos in the rock (a sand one in room 4, a water tank in room 5) are sealed by a wall of rock: the "plugs". Shoot the plug and the contents run out.
//
// Everything runs from Game's loop (Step, once per fixed step, before Scripts.Tick), never from inside a script's callback.
static class Destruct
{
    public const int TagCrateBase = 200;           // item i has Tag TagCrateBase + i
    const float BlastRadius = 3.5f;
    const float BlastForce = 60f;
    const int ChainFuse = 12;                      // steps from one blast to the next
    const int Ppu = 8;                             // terrain pixels per unit
    const int PxW = 144;
    const int PxH = 768;
    const int CrateKind = 0;
    const int BoulderKind = 1;

    static PixelTerrain2D rock;
    static Shatter2D boom;
    static ExplodeOptions crateOpt;
    static ExplodeOptions rockOpt;
    static StampShape bulletHole;
    static StampShape crateHole;
    static StampShape boulderHole;
    static Node[] items;
    static int[] fuse;                             // -1 idle, -2 spent, otherwise steps until it detonates
    static int[] kind;
    static int itemCount;
    static float[] plugs;                          // x, y, room of each plug: a point inside the rock that closes a silo
    static int plugCount;
    static int frame;

    public static int Digs;
    public static int Detonations;
    public static int Fragments;
    public static int FragmentsGone;
    public static int BoulderCount;
    public static int RubbleGrains;

    static Node NodeAt(Node[] a, int i) { return a[i]; }
    static void SetNode(Node[] a, int i, Node n) { a[i] = n; }
    static int IntAt(int[] a, int i) { return a[i]; }
    static void SetInt(int[] a, int i, int v) { a[i] = v; }
    static float FloatAt(float[] a, int i) { return a[i]; }
    static void SetFloat(float[] a, int i, float v) { a[i] = v; }

    static int Px(float units) { return (int)(units * (float)Ppu + 0.5f); }

    public static PixelTerrain2D Terrain() { return rock; }

    // ---- building the shaft ----------------------------------------------------------------------------------------------------

    static void RockPixel(int x, int y)
    {
        int shade = ((x * 7 + y * 13) % 5) * 3 + ((y / 16) % 2) * 7;           // a little grain, and strata
        rock.SetPixel(x, y, 92 + shade, 88 + shade, 96 + shade, 255);
    }

    static void Rock(float x0, float y0, float x1, float y1)
    {
        int ix0 = Px(x0), ix1 = Px(x1), iy0 = Px(y0), iy1 = Px(y1);
        for (int x = ix0; x < ix1; x++)
            for (int y = iy0; y < iy1; y++)
                RockPixel(x, y);
    }

    static void Air(float x0, float y0, float x1, float y1)
    {
        int ix0 = Px(x0), ix1 = Px(x1), iy0 = Px(y0), iy1 = Px(y1);
        for (int x = ix0; x < ix1; x++)
            for (int y = iy0; y < iy1; y++)
                rock.SetPixel(x, y, 0, 0, 0, 0);
    }

    static void Fill(float x0, float y0, float x1, float y1, int element)
    {
        int ix0 = Px(x0), ix1 = Px(x1), iy0 = Px(y0), iy1 = Px(y1);
        for (int x = ix0; x < ix1; x++)
            for (int y = iy0; y < iy1; y++)
                rock.AddElement(x, y, element);
    }

    static void AddPlug(float x, float y, int room)
    {
        SetFloat(plugs, plugCount * 3, x);
        SetFloat(plugs, plugCount * 3 + 1, y);
        SetFloat(plugs, plugCount * 3 + 2, (float)room);
        plugCount++;
    }

    public static void Init(Scene2D scene, int maxItems)
    {
        rock = new PixelTerrain2D(scene, PxW, PxH, 3, 12, (float)Ppu, 0f, 0f, PixelTerrain2D.Boxes);
        rock.PhysicsLayer = Layers.Wall;
        rock.Friction = 0f;                        // the slime does not catch on rock
        Rock(0f, 0f, Level.Width, Level.Height);
        for (int i = 0; i < Level.Rooms; i++)
            Air(Level.SideWall, Level.AirBottom(i), Level.Width - Level.SideWall, Level.AirTop(i));

        plugs = new float[16];
        plugCount = 0;

        // room 2: a basin in the floor for a pool: ridges of rock at either end (the water goes between them; the sand of room 1 falls in at the right)
        float f2 = Level.AirBottom(2);
        Rock(3f, f2, 4f, f2 + 2f);
        Rock(14.5f, f2, 15.5f, f2 + 2f);

        // room 4: a sand silo in the rock at the right, sealed: a block of rock 5 x 5.5 units against the wall with a cavity inside, its door 1.5 units of rock
        float a4 = Level.AirTop(4);
        Rock(11.5f, a4 - 5.5f, Level.Width - Level.SideWall, a4);
        Air(13f, a4 - 4.5f, 16f, a4 - 1f);
        AddPlug(12.2f, a4 - 2.75f, 4);

        // room 5: a water tank in the rock at the left, sealed the same way
        float a5 = Level.AirTop(5);
        Rock(Level.SideWall, a5 - 5.5f, 6f, a5);
        Air(2.5f, a5 - 4.5f, 5.2f, a5 - 1f);
        AddPlug(5.6f, a5 - 2.75f, 5);

        rock.Build();
        rock.EnableSand();
        rock.Sand.Seed(2024u);

        // room 1: a dune on the floor, 9 units wide and up to 3 high, with the place where the slime digs just past its foot: when the floor opens, the dune runs into the hole
        float f1 = Level.AirBottom(1);
        int cx = Px(8.8f);
        int hw = Px(4.5f);
        int hmax = Px(3f);
        for (int x = cx - hw; x <= cx + hw; x++)
        {
            float t = (float)(x - cx) / (float)hw;
            int h = (int)((1f - t * t) * (float)hmax);
            for (int y = 0; y < h; y++) rock.AddElement(x, Px(f1) + y, SandSim.Sand);
        }
        // room 2: the pool, 10.5 units wide and 1.5 deep, between the ridges
        Fill(4f, f2, 14.5f, f2 + 1.5f, SandSim.Water);
        // room 4: the silo's sand; room 5: the tank's water
        Fill(13f, a4 - 4.5f, 16f, a4 - 1f, SandSim.Sand);
        Fill(2.5f, a5 - 4.5f, 5.2f, a5 - 1f, SandSim.Water);
        // let it settle before the slime comes: the water levels out, the dune finds its slope, and the colliders are made
        for (int i = 0; i < 300; i++)
        {
            rock.SandStep();
            rock.Update();
        }

        boom = new Shatter2D(scene, 1234u);
        crateOpt = new ExplodeOptions();
        crateOpt.Mode = Fracturer.Voronoi;
        crateOpt.ExtraPoints = 3;
        crateOpt.FragmentLayer = Layers.Debris;
        crateOpt.RenderLayer = 3;
        crateOpt.Texture = 1;
        crateOpt.R = 0.85f; crateOpt.G = 0.6f; crateOpt.B = 0.35f;
        rockOpt = new ExplodeOptions();
        rockOpt.Mode = Fracturer.Voronoi;
        rockOpt.ExtraPoints = 5;
        rockOpt.FragmentLayer = Layers.Debris;
        rockOpt.RenderLayer = 3;
        rockOpt.Texture = 1;
        rockOpt.R = 0.55f; rockOpt.G = 0.55f; rockOpt.B = 0.6f;
        bulletHole = StampShape.GenerateShapeRect(14, 10);         // 1.75 x 1.25 units, with straight sides: a hole through a floor stays wider than the slime (a round one narrows at its bottom)
        crateHole = StampShape.GenerateShapeCircle(16);            // 4 units: a crate's crater
        boulderHole = StampShape.GenerateShapeCircle(9);           // 2.25

        items = new Node[maxItems + 1];
        fuse = new int[maxItems + 1];
        kind = new int[maxItems + 1];
        itemCount = 0;
        frame = 0;
    }

    // ---- the things that break ------------------------------------------------------------------------------------------------

    static void AddItem(Scene2D scene, float x, float y, float w, float h, int k, float r, float g, float b)
    {
        Node n = scene.NewNode(null);
        n.SetPosition(x, y);
        n.Layer = Layers.Crate;
        n.Tag = TagCrateBase + itemCount;
        scene.AddRigidbody(n, PB2.BodyDynamic);
        scene.AddBoxCollider(n, w, h);
        scene.AddSprite(n, SpriteRenderer2D.Box, w, h, r, g, b);
        SetNode(items, itemCount, n);
        SetInt(fuse, itemCount, -1);
        SetInt(kind, itemCount, k);
        itemCount++;
    }

    public static void AddCrate(Scene2D scene, float x, float y) { AddItem(scene, x, y, 1f, 1f, CrateKind, 0.8f, 0.55f, 0.3f); }

    public static void AddBoulder(Scene2D scene, float x, float y)
    {
        AddItem(scene, x, y, 1.7f, 1.4f, BoulderKind, 0.5f, 0.5f, 0.56f);
        BoulderCount++;
    }

    public static int ItemCount() { return itemCount; }
    public static bool ItemIsCrate(int i) { return IntAt(kind, i) == CrateKind; }
    // (a node is an arena slot: once an item is destroyed, its slot is handed to a fragment, which would pass for it: so an item that has gone off is marked spent)
    public static bool ItemLive(Scene2D scene, int i) { return IntAt(fuse, i) != -2 && scene.IsLive(NodeAt(items, i)); }
    public static float ItemX(int i) { return NodeAt(items, i).WorldX(); }
    public static float ItemY(int i) { return NodeAt(items, i).WorldY(); }

    public static void Queue(int i, int steps)
    {
        if (i < 0 || i >= itemCount) return;
        if (IntAt(fuse, i) < 0 && IntAt(fuse, i) != -2) SetInt(fuse, i, steps);
    }

    // ---- looking at the terrain ----------------------------------------------------------------------------------------------

    public static bool SolidAt(float wx, float wy)
    {
        int px = (int)(wx * (float)Ppu);
        int py = (int)(wy * (float)Ppu);
        if (wx < 0f || wy < 0f || px >= PxW || py >= PxH) return false;
        return rock.IsSolid(px, py);
    }

    public static int PlugCount() { return plugCount; }
    public static float PlugX(int i) { return FloatAt(plugs, i * 3); }
    public static float PlugY(int i) { return FloatAt(plugs, i * 3 + 1); }
    public static int PlugRoom(int i) { return (int)FloatAt(plugs, i * 3 + 2); }
    public static bool PlugClosed(int i) { return SolidAt(PlugX(i), PlugY(i)); }

    // is there a way down through the floor of `room` at x: a column 1 unit wide (the slime is 0.9) with no ground from the floor's top to its bottom?
    static bool OpenAt(int room, float x)
    {
        for (float dx = -0.5f; dx <= 0.5f; dx += 0.125f)
            for (float y = Level.FloorBottom(room) + 0.05f; y < Level.AirBottom(room); y += 0.125f)
                if (SolidAt(x + dx, y)) return false;
        return true;
    }

    // the opening in the floor of `room` nearest to x, or -1 if the floor is whole
    public static float HoleX(int room, float x)
    {
        float best = -1f;
        float bestD = 1000f;
        for (float hx = 2.2f; hx < Level.Width - 2.2f; hx += 0.25f)
        {
            float d = MathF.Abs(hx - x);
            if (d >= bestD) continue;
            if (OpenAt(room, hx)) { best = hx; bestD = d; }
        }
        return best;
    }

    // what is in the rooms: grains of an element between the bottom of a room's floor and the top of its air
    public static int GrainsIn(int room, int element)
    {
        int n = 0;
        int y0 = Px(Level.FloorBottom(room));
        int y1 = Px(Level.AirTop(room));
        for (int y = y0; y < y1; y++)
            for (int x = 0; x < PxW; x++)
                if (rock.Sand.ElementAt(x, y) == element) n++;
        return n;
    }

    public static int GroundPixels()
    {
        int n = 0;
        for (int y = 0; y < PxH; y++)
            for (int x = 0; x < PxW; x++)
                if (rock.IsSolid(x, y)) n++;
        return n;
    }

    // what a terrain pixel is, for drawing it: 0 air, 1 rock, 2 sand, 3 water
    public static int Code(int x, int y)
    {
        int e = rock.Sand.ElementAt(x, y);
        if (e == SandSim.Sand) return 2;
        if (e == SandSim.Water) return 3;
        if (rock.IsSolid(x, y)) return 1;
        return 0;
    }

    public static int SandHash() { return (int)(rock.Sand.Hash() & 0x7fffffffu); }
    public static int AwakeCount() { return rock.Sand.AwakeChunks(); }

    // ---- breaking things ---------------------------------------------------------------------------------------------------------

    // a bullet struck something solid at (x, y), moving along (dx, dy) (a unit vector): if that is rock or sand, it digs
    public static bool BulletHitsDirt(float x, float y, float dx, float dy)
    {
        for (int k = 0; k < 10; k++)
        {
            float px = x + dx * (0.05f + 0.1f * k);
            float py = y + dy * (0.05f + 0.1f * k);
            if (SolidAt(px, py))
            {
                Dig(px, py, bulletHole, 7, 5);
                return true;
            }
        }
        return false;
    }

    static void Dig(float wx, float wy, StampShape shape, int offX, int offY)
    {
        int px = (int)(wx * (float)Ppu) - offX;
        int py = (int)(wy * (float)Ppu) - offY;
        if (rock.Paint(shape, px, py, true, 0, 0, 0)) Digs++;
    }

    // once per fixed step, before the physics step: the sand and water move, the colliders that changed are rebuilt, the fuses burn
    public static void Step(Scene2D scene)
    {
        frame++;
        rock.SandStep();
        rock.Update();
        for (int i = 0; i < itemCount; i++)
        {
            int f = IntAt(fuse, i);
            if (f < 0) continue;
            if (f == 0) { Detonate(scene, i); continue; }
            SetInt(fuse, i, f - 1);
        }
    }

    static void Detonate(Scene2D scene, int i)
    {
        Node n = NodeAt(items, i);
        bool live = ItemLive(scene, i);
        int k = IntAt(kind, i);
        SetInt(fuse, i, -2);                                        // spent
        if (!live) return;
        float cx = n.WorldX();
        float cy = n.WorldY();
        int made = 0;
        if (k == BoulderKind) made = boom.Explode(n, rockOpt);
        else made = boom.Explode(n, crateOpt);
        Detonations++;
        Fragments += made;
        for (int f = 0; f < boom.Fragments.Count; f++)
        {
            Node frag = boom.Fragments[f];                              // (a list element cannot be passed straight into a call, in the C build)
            Scripts.AddDebrisScript(frag);
        }
        float radius = BlastRadius;
        float force = BlastForce;
        if (k == BoulderKind) { radius = 2.5f; force = 35f; }
        int pushed = boom.AddExplosionForce(cx, cy, radius, force, 0f);

        // the crater: a crate's is big enough to open a floor most of the way; a boulder crumbles into a little sand where it stood
        int rubble = 0;
        if (k == BoulderKind)
        {
            Dig(cx, cy - 0.2f, boulderHole, 9, 9);
            rubble = rock.Sprinkle((int)(cx * (float)Ppu), (int)((cy + 0.2f) * (float)Ppu), 7, SandSim.Sand);
            RubbleGrains += rubble;
        }
        else
            Dig(cx, cy - 0.3f, crateHole, 16, 16);

        // whatever is within reach goes off a little later
        int chained = 0;
        for (int j = 0; j < itemCount; j++)
        {
            if (j == i || IntAt(fuse, j) != -1) continue;
            if (!ItemLive(scene, j)) continue;
            Node o = NodeAt(items, j);
            float dx = o.WorldX() - cx;
            float dy = o.WorldY() - cy;
            if (dx * dx + dy * dy < radius * radius) { SetInt(fuse, j, ChainFuse); chained++; }
        }
        string what = "crate";
        if (k == BoulderKind) what = "boulder";
        string line = "boom step=" + frame + " " + what + "=" + i + " x*1000=" + (int)(cx * 1000f) + " y*1000=" + (int)(cy * 1000f) + " fragments=" + made + " pushed=" + pushed + " rubble=" + rubble + " chained=" + chained;
        Console.WriteLine(line);
    }

    public static void Shutdown()
    {
        rock.Destroy();
    }
}

// A fragment's lifetime: it lies there for a while, then goes (its body, collider and mesh with it), which also gives the scene's pools back.
[Script(Order = 30), MaxInstances(64)]
class DebrisScript
{
    public Component Self;
    float life;
    Scene2D scene;
    Node node;

    public void Start()
    {
        scene = Scene2D.Current;
        node = Self.Node;
        life = 2.5f;
    }

    public void FixedUpdate()
    {
        life -= Cfg.Dt;
        if (life > 0f) return;
        Destruct.FragmentsGone++;
        scene.Destroy(node);
    }
}
