// The 2D runtime's draw batch: what a renderer is handed. Run on .NET and as translated C; the outputs must be identical, and the batch is checked here
// against numbers worked out by hand, so a match cannot mean "identically wrong".
//
//   * layers: sprites are ordered by layer, equal layers in the order they were enabled, and that survives a disable and a re-enable;
//   * a sprite on a child of a rotated, scaled parent: its centre, its half extents (the parent's scale times its own) and its angle are the world's;
//   * a sprite on a falling body is where its node is, which is where the physics put it (the write-back is what the renderer reads);
//   * a destroyed sprite leaves the batch, and its slot is recycled with its defaults.
// (The pixels the renderer then makes of a batch are Native/Gfx2D's, checked by drawing a frame; this program checks the batch itself.)
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

class RenderBatchConformance
{
    static int ok, bad;

    static void Check(bool good, string what)
    {
        if (good) ok++;
        else { bad++; Console.WriteLine($"  FAIL {what}"); }
    }

    static bool Near(float a, float b, float tol) { return MathF.Abs(a - b) <= tol; }
    static int Q(float v) { return (int)(v * 1000f); }

    static float[] data;

    // the sprite at position i of the batch
    static float F(int i, int field) { return data[i * CoreLimits.SpriteFloats + field]; }

    static void Step(Scene2D s)
    {
        s.BeginFixedStep(1f / 60f);
        while (s.NextCall()) { }
        s.BeginFrame(1f / 60f, 1f);
        while (s.NextCall()) { }
    }

    static void Show(string label, int n)
    {
        string line = label + ":";
        for (int i = 0; i < n; i++) line = line + " [" + Q(F(i, 0)) + " " + Q(F(i, 1)) + " " + Q(F(i, 2)) + " " + Q(F(i, 3)) + " " + Q(F(i, 4)) + " layer " + (int)F(i, 10) + "]";
        Console.WriteLine(line);
    }

    public static int Main()
    {
        bool cond;                     // each check is computed on its own line: a string built beside a call would change the order C# evaluates in
        Scene2D scene = new Scene2D();
        data = scene.Render.DrawData;

        // ---- layers -------------------------------------------------------------------------------------------------------------
        Node a = scene.NewNode(null); a.SetPosition(1f, 0f);
        Node b = scene.NewNode(null); b.SetPosition(2f, 0f);
        Node c = scene.NewNode(null); c.SetPosition(3f, 0f);
        Node d = scene.NewNode(null); d.SetPosition(4f, 0f);
        SpriteRenderer2D s1 = scene.AddSprite(a, SpriteRenderer2D.Box, 1f, 1f, 1f, 0f, 0f); s1.Layer = 1;
        SpriteRenderer2D s2 = scene.AddSprite(b, SpriteRenderer2D.Box, 1f, 1f, 0f, 1f, 0f); s2.Layer = 1;
        SpriteRenderer2D s3 = scene.AddSprite(c, SpriteRenderer2D.Disc, 1f, 1f, 0f, 0f, 1f); s3.Layer = 0;
        SpriteRenderer2D s4 = scene.AddSprite(d, SpriteRenderer2D.Disc, 1f, 1f, 1f, 1f, 0f); s4.Layer = 2;
        int n = scene.CollectSprites();
        Show("layers", n);
        cond = n == 4 && Near(F(0, 0), 3f, 1e-4f) && Near(F(1, 0), 1f, 1e-4f) && Near(F(2, 0), 2f, 1e-4f) && Near(F(3, 0), 4f, 1e-4f);
        Check(cond, "ordered by layer (0, 1, 1, 2), the two layer-1 sprites in the order they were enabled");
        cond = F(0, 9) == 1f && F(1, 9) == 0f;
        Check(cond, "the shape travels: sprite 3 is a disc, sprite 1 a box");
        cond = Near(F(1, 5), 1f, 1e-6f) && Near(F(1, 6), 0f, 1e-6f) && Near(F(1, 8), 1f, 1e-6f);
        Check(cond, "and the tint");

        scene.SetEnabled(s1.Self, false);
        n = scene.CollectSprites();
        cond = n == 3 && Near(F(1, 0), 2f, 1e-4f);
        Check(cond, "a disabled sprite leaves the batch");
        scene.SetEnabled(s1.Self, true);
        n = scene.CollectSprites();
        cond = n == 4 && Near(F(1, 0), 2f, 1e-4f) && Near(F(2, 0), 1f, 1e-4f);
        Check(cond, "enabled again it comes back AFTER the sprite already on its layer");
        s2.Visible = false;
        n = scene.CollectSprites();
        cond = n == 3;
        Check(cond, "an invisible sprite is not drawn, though enabled");
        s2.Visible = true;
        s1.Layer = 5;
        n = scene.CollectSprites();
        cond = n == 4 && Near(F(3, 0), 1f, 1e-4f);
        Check(cond, "a layer changed by a script takes effect on the next batch");

        // ---- a child of a rotated, scaled parent ---------------------------------------------------------------------------------
        Node parent = scene.NewNode(null);
        parent.SetLocal(10f, 0f, 1.5707964f, 2f, 1f);
        Node child = scene.NewNode(parent);
        child.SetPosition(1f, 0f);
        SpriteRenderer2D sc = scene.AddSprite(child, SpriteRenderer2D.Box, 1f, 1f, 1f, 1f, 1f);
        sc.Layer = 9;
        n = scene.CollectSprites();
        int last = n - 1;
        Show("child", n);
        // local (1,0) scaled by (2,1) is (2,0); turned a quarter is (0,2); plus (10,0): (10,2). Size 1x1 * lossy (2,1) / 2: half extents (1, 0.5). Angle: a quarter.
        cond = Near(F(last, 0), 10f, 1e-3f) && Near(F(last, 1), 2f, 1e-3f);
        Check(cond, "the child sprite is at the world position (10, 2)");
        cond = Near(F(last, 2), 1f, 1e-3f) && Near(F(last, 3), 0.5f, 1e-3f);
        Check(cond, "its half extents are the parent's scale times its own: (1, 0.5)");
        cond = Near(F(last, 4), 1.5707964f, 1e-3f);
        Check(cond, "and it is turned with the parent");

        // ---- a sprite on a falling body ------------------------------------------------------------------------------------------
        Node ground = scene.NewNode(null);
        ground.SetPosition(0f, -0.5f);
        scene.AddBoxCollider(ground, 40f, 1f);
        Node ball = scene.NewNode(null);
        ball.SetPosition(-6f, 5f);
        scene.AddRigidbody(ball, PB2.BodyDynamic);
        scene.AddCircleCollider(ball, 0.5f);
        SpriteRenderer2D sb = scene.AddSprite(ball, SpriteRenderer2D.Disc, 1f, 1f, 1f, 0.5f, 0f);
        sb.Layer = 20;
        for (int i = 0; i < 240; i++) Step(scene);
        n = scene.CollectSprites();
        last = n - 1;
        float rowX = F(last, 0), rowY = F(last, 1), nodeX = ball.WorldX(), nodeY = ball.WorldY();
        cond = Near(rowX, nodeX, 1e-6f) && Near(rowY, nodeY, 1e-6f);
        Check(cond, "the batch row is exactly where the ball's node is");
        cond = Near(F(last, 1), 0.5f, 0.03f);
        Check(cond, "which is where the physics put it: resting on the ground");

        // ---- destroying and recycling --------------------------------------------------------------------------------------------
        scene.Destroy(ball);
        n = scene.CollectSprites();
        cond = n == last;
        Check(cond, "a destroyed sprite leaves the batch at once");
        Step(scene);
        SpriteRenderer2D again = scene.NewSprite(c, SpriteRenderer2D.Box, 3f, 2f);
        cond = again != null && again.Layer == 0 && again.Visible && Near(again.R, 1f, 1e-6f) && again.Width == 3f;
        Check(cond, "a recycled sprite slot starts from its defaults");
        scene.Finish(again.Self);
        n = scene.CollectSprites();
        cond = n == last + 1;
        Check(cond, "and joins the batch when it is enabled");

        Console.WriteLine($"checks ok {ok} bad {bad}");
        return bad == 0 ? 0 : 1;
    }
}
