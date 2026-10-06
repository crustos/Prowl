using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;
using Prowl.Runtime.Destruction2D;

// The destruction layer SlimeJumpDestruct adds to SlimeJump. Three engine features work together here, on top of Box2D-Packed:
//
//   * PixelTerrain2D (Prowl.Runtime.Destruction2D, the DTerrain port): a wall of dirt across the way, a bitmap with box colliders that the slime's
//     bullets dig out. A dig changes the pixels, and Update() rebuilds the chunks' native shapes, so the slime can walk through the hole.
//   * Shatter2D (the Unity-2D-Destruction port): wooden crates. A bullet detonates a crate: it is shattered into Voronoi fragments (dynamic bodies
//     with polygon colliders and a mesh each, moving as the crate was), and AddExplosionForce pushes everything near, with Box2D's overlap query.
//   * Box2D itself: the fragments and the crates are ordinary rigidbodies; the blast also kills enemies in its radius, digs a crater where it
//     touches the dirt, and sets off the crates next to it a few steps later (a chain).
//
// Everything runs from Game's loop (Step, once per fixed step, before Scripts.Tick), never from inside a script's callback: a bullet only
// queues a detonation, so no node is destroyed under the feet of the script that is running.
static class Destruct
{
    public const int TagCrateBase = 200;           // crate i has Tag TagCrateBase + i
    const float BlastRadius = 3.5f;
    const float BlastForce = 60f;
    const int ChainFuse = 12;                      // steps from one crate's blast to the next crate's
    const int DirtPpu = 8;                         // terrain pixels per unit
    public const float DirtX = 34f;                // lower left corner of the dirt wall, and its size, in units
    public const float DirtY = 4f;
    const int DirtW = 16;
    const int DirtH = 72;

    static PixelTerrain2D dirt;
    static Shatter2D boom;
    static ExplodeOptions opt;
    static StampShape bulletHole;
    static StampShape blastHole;
    static Node[] crates;
    static int[] fuse;                             // -1 idle, otherwise steps until it detonates
    static int crateCount;
    static int frame;

    public static int Digs;                        // dig calls that changed the terrain
    public static int Detonations;
    public static int Fragments;
    public static int BlastKills;
    public static int FragmentsGone;

    static Node NodeAt(Node[] a, int i) { return a[i]; }
    static void SetNode(Node[] a, int i, Node n) { a[i] = n; }
    static int IntAt(int[] a, int i) { return a[i]; }
    static void SetInt(int[] a, int i, int v) { a[i] = v; }

    public static void Init(Scene2D scene, int maxCrates)
    {
        // the dirt wall: 2 x 9 units at 8 pixels per unit, in two chunks; on the walls' physics layer, so bullets, the bot's probes and the
        // enemies' sight treat it as a wall
        dirt = new PixelTerrain2D(scene, DirtW, DirtH, 1, 2, (float)DirtPpu, DirtX, DirtY, PixelTerrain2D.Boxes);
        dirt.PhysicsLayer = Layers.Wall;
        dirt.Friction = 0f;                        // as the other walls: the slime does not catch on them
        for (int x = 0; x < DirtW; x++)
            for (int y = 0; y < DirtH; y++)
            {
                int shade = ((x / 2 + y / 3) % 3) * 8;              // a little texture
                dirt.SetPixel(x, y, 118 + shade, 84 + shade, 48, 255);
            }
        dirt.Build();
        bulletHole = StampShape.GenerateShapeCircle(6);            // 1.5 units across
        blastHole = StampShape.GenerateShapeCircle(10);            // 2.5 units

        boom = new Shatter2D(scene, 1234u);
        opt = new ExplodeOptions();
        opt.Mode = Fracturer.Voronoi;
        opt.ExtraPoints = 3;
        opt.FragmentLayer = Layers.Debris;
        opt.RenderLayer = 3;
        opt.Texture = 1;
        opt.R = 0.85f; opt.G = 0.6f; opt.B = 0.35f;

        crates = new Node[maxCrates + 1];
        fuse = new int[maxCrates + 1];
        crateCount = 0;
        frame = 0;
    }

    public static void AddCrate(Scene2D scene, float x, float y)
    {
        Node n = scene.NewNode(null);
        n.SetPosition(x, y);
        n.Layer = Layers.Crate;
        n.Tag = TagCrateBase + crateCount;
        scene.AddRigidbody(n, PB2.BodyDynamic);
        scene.AddBoxCollider(n, 1f, 1f);
        scene.AddSprite(n, SpriteRenderer2D.Box, 1f, 1f, 0.8f, 0.55f, 0.3f);
        SetNode(crates, crateCount, n);
        SetInt(fuse, crateCount, -1);
        crateCount++;
    }

    public static int CrateCount() { return crateCount; }
    // (a node is an arena slot: once the crate is destroyed, its slot is handed to a fragment, which would pass for the crate. So a crate that has gone off is
    // marked spent, and only an unspent one is looked at.)
    public static bool CrateLive(Scene2D scene, int i) { return IntAt(fuse, i) != -2 && scene.IsLive(NodeAt(crates, i)); }
    public static float CrateX(int i) { return NodeAt(crates, i).WorldX(); }
    public static float CrateY(int i) { return NodeAt(crates, i).WorldY(); }

    // a bullet hit crate i: it goes off in a step (a second hit changes nothing)
    public static void Queue(int i, int steps)
    {
        if (i < 0 || i >= crateCount) return;
        if (IntAt(fuse, i) < 0) SetInt(fuse, i, steps);
    }

    // is dirt in the way along the slime's row (a little above and below it too), 0.6 to 2.6 units ahead?
    public static bool DirtAhead(float x, float y)
    {
        for (int k = 0; k < 11; k++)
        {
            float wx = x + 0.6f + k * 0.2f;
            for (int r = -1; r <= 1; r++)
                if (SolidAt(wx, y + r * 0.3f)) return true;
        }
        return false;
    }

    public static bool SolidAt(float wx, float wy)
    {
        float fx = (wx - DirtX) * DirtPpu;
        float fy = (wy - DirtY) * DirtPpu;
        if (fx < 0f || fy < 0f || fx >= (float)DirtW || fy >= (float)DirtH) return false;
        return dirt.IsSolid((int)fx, (int)fy);
    }

    public static int SolidPixels()
    {
        int n = 0;
        for (int x = 0; x < DirtW; x++)
            for (int y = 0; y < DirtH; y++)
                if (dirt.IsSolid(x, y)) n++;
        return n;
    }

    // a bullet struck something solid at (x, y), moving along (dx, dy) (a unit vector): if that is dirt, it digs
    public static bool BulletHitsDirt(float x, float y, float dx, float dy)
    {
        for (int k = 0; k < 4; k++)
        {
            float px = x + dx * (0.1f + 0.1f * k);
            float py = y + dy * (0.1f + 0.1f * k);
            if (SolidAt(px, py))
            {
                Dig(px, py, bulletHole, 6);
                return true;
            }
        }
        return false;
    }

    static void Dig(float wx, float wy, StampShape shape, int radius)
    {
        int px = (int)((wx - DirtX) * DirtPpu);
        int py = (int)((wy - DirtY) * DirtPpu);
        if (dirt.Paint(shape, px - radius, py - radius, true, 0, 0, 0)) Digs++;
    }

    // once per fixed step, before the physics step
    public static void Step(Scene2D scene)
    {
        frame++;
        dirt.Update();
        for (int i = 0; i < crateCount; i++)
        {
            int f = IntAt(fuse, i);
            if (f < 0) continue;
            if (f == 0) { Detonate(scene, i); continue; }
            SetInt(fuse, i, f - 1);
        }
    }

    static void Detonate(Scene2D scene, int i)
    {
        Node n = NodeAt(crates, i);
        bool live = CrateLive(scene, i);
        SetInt(fuse, i, -2);                                        // spent
        if (!live) return;
        float cx = n.WorldX();
        float cy = n.WorldY();
        int made = boom.Explode(n, opt);
        Detonations++;
        Fragments += made;
        for (int k = 0; k < boom.Fragments.Count; k++)
        {
            Node frag = boom.Fragments[k];                              // (a list element cannot be passed straight into a call, in the C build)
            Scripts.AddDebrisScript(frag);
        }
        int pushed = boom.AddExplosionForce(cx, cy, BlastRadius, BlastForce, 0f);

        int slain = 0;
        for (int e = 0; e < Shared.EnemyCount(); e++)
        {
            if (!Shared.EnemyAlive(e)) continue;
            float dx = Shared.EnemyX(e) - cx;
            float dy = Shared.EnemyY(e) - cy;
            if (dx * dx + dy * dy < BlastRadius * BlastRadius)
            {
                Shared.DamageEnemy(e, 3f, dx, dy);                  // lethal for either kind
                slain++;
            }
        }
        BlastKills += slain;

        // the crater, where the blast reaches the dirt
        bool nearDirt = cx > DirtX - 1.5f && cx < DirtX + 2f + 1.5f && cy < DirtY + 9f + 1.5f;
        if (nearDirt) Dig(cx, cy + 0.5f, blastHole, 10);

        // a crate within reach goes off a little later
        int chained = 0;
        for (int j = 0; j < crateCount; j++)
        {
            if (j == i || IntAt(fuse, j) != -1) continue;
            if (!CrateLive(scene, j)) continue;
            Node o = NodeAt(crates, j);
            float dx = o.WorldX() - cx;
            float dy = o.WorldY() - cy;
            if (dx * dx + dy * dy < BlastRadius * BlastRadius) { SetInt(fuse, j, ChainFuse); chained++; }
        }
        Console.WriteLine("boom step=" + frame + " crate=" + i + " x*1000=" + (int)(cx * 1000f) + " fragments=" + made + " pushed=" + pushed + " enemies hit=" + slain + " chained=" + chained);
    }

    public static void Shutdown()
    {
        dirt.Destroy();
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
