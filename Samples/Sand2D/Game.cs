// Sand2D: falling sand and flowing water on the pixels of a destructible terrain, headless. Sand and water are pixels of the terrain's own bitmap; settled
// sand is ground (it has colliders, a ball rests on it) and water is not. Four scenes: sand poured level into a slot with a ball dropped on it (box colliders
// and chain colliders), water that levels out and lets a ball through, then sand that sinks through it, and a pile of sand that falls into a crater dug
// under it. Everything is integer math with the simulation's own random generator, so the same source runs on .NET (the reference) and as a native
// executable and must print the same cells, hash and all.
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;
using Prowl.Runtime.Destruction2D;

[Script, MaxInstances(2)]
class Marker
{
    public Component Self;
    public void Reset() { }
}

static class Game
{
    static int failures = 0;

    static void Check(bool ok, string what)
    {
        if (ok) return;
        failures++;
        Console.WriteLine("FAIL " + what);
    }

    static int Milli(float v) { return (int)(v * 1000f); }

    // 64 x 48 pixels at 8 pixels per unit (8 x 6 units), in 2 x 2 chunks of 32 x 24, with a stone floor 4 pixels thick
    static PixelTerrain2D MakeLayer(Scene2D scene, float originX, int kind)
    {
        PixelTerrain2D layer = new PixelTerrain2D(scene, 64, 48, 2, 2, 8f, originX, 0f, kind);
        for (int x = 0; x < 64; x++)
            for (int y = 0; y < 4; y++) layer.SetPixel(x, y, 110, 110, 116, 255);
        return layer;
    }

    static void Wall(PixelTerrain2D layer, int x0, int x1, int y0, int y1)
    {
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++) layer.SetPixel(x, y, 110, 110, 116, 255);
    }

    static void Start(PixelTerrain2D layer)
    {
        layer.Build();
        layer.EnableSand();
        layer.Sand.Seed(7u);
    }

    static Node MakeBall(Scene2D scene, float x, float y)
    {
        Node ball = scene.NewNode(null);
        ball.SetPosition(x, y);
        scene.AddRigidbody(ball, PB2.BodyDynamic);
        scene.AddCircleCollider(ball, 0.25f);
        return ball;
    }

    // one frame: the sand moves, the colliders that need it are rebuilt, the physics steps
    static void Frame(PixelTerrain2D layer, Scene2D scene)
    {
        layer.SandStep();
        layer.Update();
        Scripts.Tick(scene, 1f / 60f);
    }

    static void Frames(PixelTerrain2D layer, Scene2D scene, int n)
    {
        for (int i = 0; i < n; i++) Frame(layer, scene);
    }

    // a grain that could still fall, slide or flow: sand with air or water below or on a lower diagonal, water with air below or on a lower diagonal
    static int Unsettled(SandSim s)
    {
        int bad = 0;
        for (int y = 1; y < s.Height; y++)
        {
            for (int x = 0; x < s.Width; x++)
            {
                int e = s.ElementAt(x, y);
                if (e == SandSim.Sand)
                {
                    int a = s.ElementAt(x, y - 1), b = s.ElementAt(x - 1, y - 1), c = s.ElementAt(x + 1, y - 1);
                    if (a == SandSim.Air || a == SandSim.Water || b == SandSim.Air || b == SandSim.Water || c == SandSim.Air || c == SandSim.Water) bad++;
                }
                else if (e == SandSim.Water)
                {
                    if (s.ElementAt(x, y - 1) == SandSim.Air || s.ElementAt(x - 1, y - 1) == SandSim.Air || s.ElementAt(x + 1, y - 1) == SandSim.Air) bad++;
                }
            }
        }
        return bad;
    }

    // pixels where the layer's ground columns (what the colliders are made from) disagree with the cells: stone and sand are ground, nothing else is
    static int GroundMismatches(PixelTerrain2D layer)
    {
        int bad = 0;
        for (int x = 0; x < layer.Width; x++)
            for (int y = 0; y < layer.Height; y++)
                if (layer.IsSolid(x, y) != layer.Sand.IsGroundAt(x, y)) bad++;
        return bad;
    }

    // the highest row in a column that holds an element, -1 if none
    static int Top(SandSim s, int x, int element)
    {
        for (int y = s.Height - 1; y >= 0; y--)
            if (s.ElementAt(x, y) == element) return y;
        return -1;
    }

    // the lowest row anywhere that holds an element, -1 if none
    static int Lowest(SandSim s, int element)
    {
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                if (s.ElementAt(x, y) == element) return y;
        return -1;
    }

    static int Hash(SandSim s) { return (int)(s.Hash() & 0x7fffffffu); }

    // ---- 1. sand poured level into a slot; a ball is dropped on it ---------------------------------------------

    static void Slot(Scene2D scene, float originX, int kind, string name)
    {
        PixelTerrain2D layer = MakeLayer(scene, originX, kind);
        Wall(layer, 18, 19, 4, 39);                              // a slot 14 pixels wide (x 20..33) between two walls
        Wall(layer, 34, 35, 4, 39);
        Start(layer);
        SandSim s = layer.Sand;

        int added = 0;
        for (int frame = 0; frame < 10; frame++)                 // ten rows of grains, one across the slot a frame, from above the walls
        {
            for (int x = 20; x <= 33; x++)
                if (layer.AddElement(x, 44, SandSim.Sand)) added++;
            Frame(layer, scene);
        }
        Frames(layer, scene, 150);

        int count = s.Count(SandSim.Sand);
        int unsettled = Unsettled(s);
        int mismatches = GroundMismatches(layer);
        int awake = s.AwakeChunks();
        int stale = s.StaleChunks();
        int lo = 99, hi = -1;
        for (int x = 20; x <= 33; x++)
        {
            int t = Top(s, x, SandSim.Sand);
            if (t < lo) lo = t;
            if (t > hi) hi = t;
        }
        Console.WriteLine(name + ": " + added + " grains, " + count + " counted, unsettled " + unsettled + ", surface rows " + lo + ".." + hi + ", awake chunks " + awake + ", stale " + stale + ", ground mismatches " + mismatches + ", hash " + Hash(s));
        Check(added == 140 && count == added, name + ": every grain poured is still there");
        Check(unsettled == 0, name + ": the sand is at rest");
        Check(awake == 0 && stale == 0, name + ": a settled layer sleeps and has published its ground");
        Check(mismatches == 0, name + ": the ground the colliders are made from is exactly the stone and sand");
        Check(hi - lo <= 2, name + ": poured evenly, the surface is level");

        // a ball dropped into the slot rests on the sand (on the floor it would rest at 0.75): the sand has colliders
        float surface = (hi + 1) / 8f;
        Node ball = MakeBall(scene, originX + 27f / 8f, 4.5f);
        Frames(layer, scene, 150);
        int y = Milli(ball.WorldY());
        int expect = Milli(surface + 0.25f);
        Console.WriteLine(name + ": ball rests at y*1000 " + y + " (sand surface " + Milli(surface) + ")");
        Check(y > expect - 120 && y < expect + 120, name + ": the ball rests on the sand");
        layer.Destroy();
    }

    // ---- 2. water that levels out, then sand that sinks through it ---------------------------------------------

    static void Basin(Scene2D scene, float originX)
    {
        PixelTerrain2D layer = MakeLayer(scene, originX, PixelTerrain2D.Boxes);
        Wall(layer, 0, 1, 4, 29);                                // a basin 24 pixels wide (x 2..25)
        Wall(layer, 26, 27, 4, 29);
        Start(layer);
        SandSim s = layer.Sand;

        int water = 0;
        for (int frame = 0; frame < 30; frame++)                 // a disc a frame; where the last one is still falling the cells are taken, so fewer grains than cells go in
        {
            water += layer.Sprinkle(14, 40, 2, SandSim.Water);
            Frame(layer, scene);
        }
        Frames(layer, scene, 700);

        int count = s.Count(SandSim.Water);
        int unsettled = Unsettled(s);
        int mismatches = GroundMismatches(layer);
        int lo = 99, hi = -1;
        for (int x = 2; x <= 25; x++)
        {
            int t = Top(s, x, SandSim.Water);
            if (t < lo) lo = t;
            if (t > hi) hi = t;
        }
        Console.WriteLine("water: " + water + " grains, " + count + " counted, unsettled " + unsettled + ", surface rows " + lo + ".." + hi + ", ground mismatches " + mismatches + ", hash " + Hash(s));
        Check(count == water && water > 100, "water: every grain is still there");
        Check(unsettled == 0, "water: nothing can still fall");
        Check(hi - lo <= 1, "water: it has found its level");
        Check(mismatches == 0, "water: water is not ground");

        // a ball goes through the water to the floor: it has no colliders
        Node ball = MakeBall(scene, originX + 14f / 8f, 3.5f);
        Frames(layer, scene, 120);
        int y = Milli(ball.WorldY());
        Console.WriteLine("water: ball rests at y*1000 " + y);
        Check(y > 600 && y < 900, "water: a ball sinks to the floor, 0.75");
        scene.Destroy(ball);

        // sand poured in sinks through the water to the floor, and the water rises over it
        int sand = 0;
        for (int frame = 0; frame < 5; frame++)
        {
            sand += layer.Sprinkle(14, 40, 2, SandSim.Sand);
            Frame(layer, scene);
        }
        Frames(layer, scene, 700);
        int sandCount = s.Count(SandSim.Sand);
        int waterCount = s.Count(SandSim.Water);
        unsettled = Unsettled(s);
        mismatches = GroundMismatches(layer);
        int low = Lowest(s, SandSim.Sand);
        Console.WriteLine("sorted: " + sand + " sand, " + sandCount + " counted, water " + waterCount + ", lowest sand row " + low + ", unsettled " + unsettled + ", ground mismatches " + mismatches + ", hash " + Hash(s));
        Check(sandCount == sand && waterCount == water, "sorted: nothing was lost or made");
        Check(low == 4, "sorted: the sand went to the floor through the water");
        Check(unsettled == 0, "sorted: nothing sits where it would sink or fall");
        Check(mismatches == 0, "sorted: the ground is the stone and the sand");
        layer.Destroy();
    }

    // ---- 3. a pile of sand falls into a crater dug under it -----------------------------------------------------

    static void Crater(Scene2D scene, float originX)
    {
        PixelTerrain2D layer = MakeLayer(scene, originX, PixelTerrain2D.Boxes);
        Wall(layer, 0, 63, 4, 11);                               // the ground is 12 pixels thick
        Start(layer);
        SandSim s = layer.Sand;

        int sand = 0;
        for (int x = 20; x < 36; x++)
            for (int y = 12; y < 20; y++)
                if (layer.AddElement(x, y, SandSim.Sand)) sand++;
        Frames(layer, scene, 200);
        int slumped = Unsettled(s);
        int before = s.Count(SandSim.Sand);
        int belowBefore = 0;
        for (int x = 0; x < 64; x++)
            for (int y = 0; y < 12; y++)
                if (s.ElementAt(x, y) == SandSim.Sand) belowBefore++;

        StampShape crater = StampShape.GenerateShapeCircle(5);   // ten pixels across, in the stone under the pile: its top row is the ground's top row
        layer.Paint(crater, 23, 1, true, 0, 0, 0);
        Frames(layer, scene, 400);

        int after = s.Count(SandSim.Sand);
        int inside = 0;
        for (int x = 0; x < 64; x++)
            for (int y = 1; y < 12; y++)
                if (s.ElementAt(x, y) == SandSim.Sand) inside++;
        int unsettled = Unsettled(s);
        int mismatches = GroundMismatches(layer);
        int awake = s.AwakeChunks();
        Console.WriteLine("crater: " + sand + " grains, slumped " + before + " (" + slumped + " unsettled), after the dig " + after + ", " + inside + " in the crater, unsettled " + unsettled + ", awake chunks " + awake + ", ground mismatches " + mismatches + ", hash " + Hash(s));
        Check(sand == 128 && before == sand && belowBefore == 0, "crater: the pile stood on the ground");
        Check(after == sand, "crater: digging stone lost no sand");
        Check(inside > 30, "crater: the sand fell into the hole");
        Check(unsettled == 0, "crater: the sand is at rest again");
        Check(mismatches == 0, "crater: the ground follows the sand into the hole");
        Check(awake == 0, "crater: and the layer sleeps");
        layer.Destroy();
    }

    public static int Main()
    {
        Scripts.Init();
        Scene2D scene = new Scene2D();
        Slot(scene, 0f, PixelTerrain2D.Boxes, "slot (boxes)");
        Slot(scene, 10f, PixelTerrain2D.Chains, "slot (chains)");
        Basin(scene, 20f);
        Crater(scene, 30f);
        if (failures == 0) Console.WriteLine("all checks passed");
        else Console.WriteLine("FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
