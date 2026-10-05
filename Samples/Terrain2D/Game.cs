// Terrain2D: destructible terrain under physics, headless. Two terrains side by side, one with box colliders and one with chains.
// On each: a ball is dropped on flat ground, a crater is dug under it and it falls in, and a platform is built in the air for a second
// ball to land on. The same source runs on .NET (the reference) and as a native executable, and must print the same thing.
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;
using Prowl.Runtime.Destruction2D;

// counts what the terrain's colliders tell a ball: one Begin and one End per chunk of terrain, however many shapes of it the ball touches
[Script, MaxInstances(8)]
class Marker
{
    public Component Self;
    public int Begins, Ends;
    public void Reset() { Begins = 0; Ends = 0; }
    public void OnCollisionBegin2D(Collision2D hit) { if (hit.Other.ShapeKind == Collider2D.Terrain) Begins++; }
    public void OnCollisionEnd2D(Collision2D hit) { if (hit.Other.ShapeKind == Collider2D.Terrain) Ends++; }
}

static class Game
{
    static int failures = 0;
    static Marker lastMarker;                                   // the script of the ball MakeBall made last

    static void Check(bool ok, string what)
    {
        if (ok) return;
        failures++;
        Console.WriteLine("FAIL " + what);
    }

    static int Milli(float v) { return (int)(v * 1000f); }

    // 64 x 32 pixels at 8 pixels per unit (8 x 4 units), in 2 x 2 chunks of 32 x 16: ground up to row 11, a bump across a chunk border
    static PixelTerrain2D MakeLayer(Scene2D scene, float originX, int kind)
    {
        PixelTerrain2D layer = new PixelTerrain2D(scene, 64, 32, 2, 2, 8f, originX, 0f, kind);
        for (int x = 0; x < 64; x++)
        {
            int top = 12;
            if (x >= 52 && x < 60) top = 20;                   // a bump whose top is in the upper chunk row
            for (int y = 0; y < top; y++) layer.SetPixel(x, y, 120, 90, 50, 255);
        }
        layer.Build();
        return layer;
    }

    static Node MakeBall(Scene2D scene, float x, float y)
    {
        Node ball = scene.NewNode(null);
        ball.SetPosition(x, y);
        scene.AddRigidbody(ball, PB2.BodyDynamic);
        scene.AddCircleCollider(ball, 0.25f);
        lastMarker = Scripts.AddMarker(ball);
        return ball;
    }

    public static int Main()
    {
        Scripts.Init();
        Scene2D scene = new Scene2D();

        PixelTerrain2D boxes = MakeLayer(scene, 0f, PixelTerrain2D.Boxes);
        PixelTerrain2D chains = MakeLayer(scene, 10f, PixelTerrain2D.Chains);
        Console.WriteLine("built: boxes " + boxes.ShapeCount + " shapes, chains " + chains.ShapeCount + " shapes");
        Check(boxes.Ok && chains.Ok, "layers built");
        bool groundOk = boxes.IsSolid(10, 11) && !boxes.IsSolid(10, 12) && boxes.IsSolid(55, 19) && !boxes.IsSolid(55, 20);
        Check(groundOk, "the bitmap became ground");

        // a platform in the air, built (ground added) before anything falls on it: rows 24..26, columns 40..55
        StampShape platform = StampShape.GenerateShapeRect(16, 3);
        boxes.Paint(platform, 40, 24, false, 90, 90, 90);
        chains.Paint(platform, 40, 24, false, 90, 90, 90);
        bool platformOk = boxes.IsSolid(48, 25) && chains.IsSolid(48, 25);
        Check(platformOk, "the platform was built");

        // a 16 x 16 pixel block (2 x 2 units) whose four-point outline is forced into pieces of three points: nothing may fall through
        PixelTerrain2D split = new PixelTerrain2D(scene, 16, 16, 1, 1, 8f, 20f, 0f, PixelTerrain2D.Chains);
        for (int x = 0; x < 16; x++)
            for (int y = 0; y < 16; y++) split.SetPixel(x, y, 200, 200, 200, 255);
        split.MaxChainPoints = 3;
        split.Build();
        Console.WriteLine("split block: " + split.ShapeCount + " chains");
        bool splitOk = split.ShapeCount == 2;
        Check(splitOk, "the block's outline was split into two chains");
        Node ballE = MakeBall(scene, 21f, 3.0f);
        Marker markE = lastMarker;
        Node ballF = MakeBall(scene, 20.4f, 3.0f);
        Marker markF = lastMarker;
        Node ballG = MakeBall(scene, 21.6f, 3.0f);
        Marker markG = lastMarker;

        Node ballA = MakeBall(scene, 2f, 3.5f);
        Marker markA = lastMarker;            // lands on the flat ground, then a crater opens under it
        Node ballB = MakeBall(scene, 12f, 3.5f);
        Marker markB = lastMarker;
        Node ballC = MakeBall(scene, 6f, 3.9f);
        Marker markC = lastMarker;           // lands on the platform
        Node ballD = MakeBall(scene, 16f, 3.9f);
        Marker markD = lastMarker;

        StampShape crater = StampShape.GenerateShapeCircle(8);       // 16 pixels across; its pixels are columns 9..23 of the box at 8
        int rebuiltA = 0, rebuiltB = 0;

        for (int frame = 0; frame < 300; frame++)
        {
            if (frame == 120)
            {
                boxes.ClearDirty();                                // a renderer has uploaded everything so far
                chains.ClearDirty();
                boxes.Paint(crater, 8, 4, true, 0, 0, 0);          // centred under the ball at pixel (16, 12)
                chains.Paint(crater, 8, 4, true, 0, 0, 0);
            }
            int a = boxes.Update();
            int b = chains.Update();
            if (frame == 120) { rebuiltA = a; rebuiltB = b; }
            Scripts.Tick(scene, 1f / 60f);
            if (frame == 119 || frame == 299)
                Console.WriteLine("frame " + frame + " y*1000: A " + Milli(ballA.WorldY()) + " B " + Milli(ballB.WorldY()) + " C " + Milli(ballC.WorldY()) + " D " + Milli(ballD.WorldY()));
            if (frame == 119)
            {
                int ya = Milli(ballA.WorldY()), yb = Milli(ballB.WorldY());
                Check(ya > 1650 && ya < 1850 && yb > 1650 && yb < 1850, "balls A and B rest on the ground (about 1.75)");
            }
        }

        Console.WriteLine("crater rebuilt " + rebuiltA + " chunks (boxes), " + rebuiltB + " (chains); platform and crater left " + boxes.ShapeCount + " / " + chains.ShapeCount + " shapes");
        int fe = Milli(ballE.WorldY()), ff = Milli(ballF.WorldY()), fg = Milli(ballG.WorldY());
        Console.WriteLine("balls on the split block y*1000: " + fe + " " + ff + " " + fg);
        bool blockOk = fe > 2150 && fe < 2350 && ff > 2150 && ff < 2350 && fg > 2150 && fg < 2350;
        Check(blockOk, "three balls rest on top of the split block (about 2.25): no gap at the seam");
        int fa = Milli(ballA.WorldY()), fb = Milli(ballB.WorldY()), fc = Milli(ballC.WorldY()), fd = Milli(ballD.WorldY());
        Check(fa > 550 && fa < 950, "ball A fell into the crater and rests near its bottom (boxes)");
        Check(fb > 550 && fb < 950, "ball B fell into the crater and rests near its bottom (chains)");
        Check(fc > 3500 && fc < 3750 && fd > 3500 && fd < 3750, "balls C and D rest on the built platform (about 3.625)");
        Check(rebuiltA == 1 && rebuiltB == 1, "the crater rebuilt only the chunk whose ground it changed");
        bool craterOk = !boxes.IsSolid(16, 8) && !chains.IsSolid(16, 8) && boxes.IsSolid(16, 3) && chains.IsSolid(16, 3);
        Check(craterOk, "the crater is air above its bottom and ground below");

        // the pixels know what happened, and say which rectangle a renderer would upload
        bool pixelsOk = boxes.AlphaAt(16, 8) == 0 && boxes.AlphaAt(16, 3) == 255 && boxes.AlphaAt(48, 25) == 255;
        Check(pixelsOk, "the pixels follow the digging and building");
        Check(boxes.PixelsDirty && boxes.DirtyX0 == 9 && boxes.DirtyX1 == 23 && chains.DirtyX0 == 9 && chains.DirtyX1 == 23, "the dirty rectangle is the crater's, not the whole bitmap");
        Console.WriteLine("dirty rectangle x " + boxes.DirtyX0 + ".." + boxes.DirtyX1 + " y " + boxes.DirtyY0 + ".." + boxes.DirtyY1);

        Console.WriteLine("terrain contacts begins/ends: A " + markA.Begins + "/" + markA.Ends + " B " + markB.Begins + "/" + markB.Ends + " C " + markC.Begins + "/" + markC.Ends
                          + " D " + markD.Begins + "/" + markD.Ends + " E " + markE.Begins + "/" + markE.Ends + " F " + markF.Begins + "/" + markF.Ends + " G " + markG.Begins + "/" + markG.Ends);
        bool dugOk = markA.Begins == 2 && markA.Ends == 1 && markB.Begins == 2 && markB.Ends == 1;
        Check(dugOk, "a ball told: it landed, the crater took the ground from under it, it landed in the crater (2 Begins, 1 End; not one pair per shape or per rebuild)");
        bool restOk = markC.Begins == 1 && markC.Ends == 0 && markD.Begins == 1 && markD.Ends == 0;
        Check(restOk, "balls on the built platform were told once that they landed, and never that they left");
        bool blockEventsOk = markE.Begins == 1 && markE.Ends == 0 && markF.Begins == 1 && markF.Ends == 0 && markG.Begins == 1 && markG.Ends == 0;
        Check(blockEventsOk, "balls on the block of two chains (one collider, two shapes) were told once");

        boxes.Destroy();
        chains.Destroy();
        split.Destroy();
        if (failures == 0) Console.WriteLine("all checks passed");
        else Console.WriteLine("FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
