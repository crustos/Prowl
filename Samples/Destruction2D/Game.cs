// Shatter2D: destructible terrain and shattering sprites together, headless. A crate lands on the terrain; one detonation shatters the
// crate into fragments, digs a crater under it and pushes everything near away; the fragments fall into the crater. The same source runs on
// .NET (the reference) and as a native executable, and must print the same thing.
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

    // the batch of meshes is a triangle list: the sum of its triangles' areas
    static float BatchArea(Scene2D scene, int vertices)
    {
        float sum = 0f;
        for (int t = 0; t < vertices / 3; t++)
        {
            int a = t * 3 * CoreLimits.MeshVertexFloats;
            int b = a + CoreLimits.MeshVertexFloats;
            int c = b + CoreLimits.MeshVertexFloats;
            float ax = scene.Render.MeshData[a], ay = scene.Render.MeshData[a + 1];
            float bx = scene.Render.MeshData[b], by = scene.Render.MeshData[b + 1];
            float cx = scene.Render.MeshData[c], cy = scene.Render.MeshData[c + 1];
            float area = ((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)) * 0.5f;
            if (area < 0f) area = -area;
            sum += area;
        }
        return sum;
    }

    public static int Main()
    {
        Scripts.Init();
        Scene2D scene = new Scene2D();

        // ground: 64 x 32 pixels at 8 pixels per unit (8 x 4 units), ground up to row 11 (1.5 units)
        PixelTerrain2D ground = new PixelTerrain2D(scene, 64, 32, 2, 1, 8f, 0f, 0f, PixelTerrain2D.Boxes);
        for (int x = 0; x < 64; x++)
            for (int y = 0; y < 12; y++) ground.SetPixel(x, y, 120, 90, 50, 255);
        ground.Build();

        // a one-unit crate dropped from a height
        Node crate = scene.NewNode(null);
        crate.SetPosition(4f, 3f);
        scene.AddRigidbody(crate, PB2.BodyDynamic);
        scene.AddBoxCollider(crate, 1f, 1f);
        scene.AddSprite(crate, SpriteRenderer2D.Box, 1f, 1f, 0.8f, 0.5f, 0.2f);
        Scripts.AddMarker(crate);

        Shatter2D boom = new Shatter2D(scene, 42u);
        ExplodeOptions opt = new ExplodeOptions();
        opt.Mode = Fracturer.Voronoi;
        opt.ExtraPoints = 3;
        opt.RenderLayer = 5;
        opt.Texture = 1;
        opt.UvMinX = 0.25f; opt.UvMinY = 0.5f; opt.UvMaxX = 0.75f; opt.UvMaxY = 1f;       // the crate is the upper right quarter of texture 1

        for (int frame = 0; frame < 90; frame++)
        {
            ground.Update();
            Scripts.Tick(scene, 1f / 60f);
        }
        int cy0 = Milli(crate.WorldY());
        Console.WriteLine("crate rests at y*1000 " + cy0);
        Check(cy0 > 1950 && cy0 < 2050, "the crate rests on the ground (about 2.0)");

        // ---- the detonation: shatter, dig, push ----
        float bx = crate.WorldX(), by = crate.WorldY(), ba = crate.WorldAngle();
        int n = boom.Explode(crate, opt);
        Console.WriteLine("fragments " + n + " of " + boom.Requested + " pieces");
        Check(n > 4 && n == boom.Requested && n == boom.Fragments.Count, "the crate shattered into every piece asked for");
        bool gone = !scene.IsLive(crate);
        Check(gone, "the crate is destroyed");

        float areaSum = 0f;
        for (int i = 0; i < boom.Areas.Count; i++) areaSum += boom.Areas[i];
        Console.WriteLine("fragment area*1000 " + Milli(areaSum));
        Check(areaSum > 0.998f && areaSum < 1.002f, "the fragments' areas add up to the crate's (1.0)");

        // the meshes: one triangle fan per fragment, covering the crate, with texture coordinates that follow the crate's sprite
        int verts = scene.CollectMeshes();
        float batchArea = BatchArea(scene, verts);
        Console.WriteLine("mesh batch: " + verts + " vertices, area*1000 " + Milli(batchArea));
        Check(batchArea > 0.998f && batchArea < 1.002f, "the mesh batch covers the crate exactly");
        float cs = MathF.Cos(ba), sn = MathF.Sin(ba);
        float worstUv = 0f;
        for (int v = 0; v < verts; v++)
        {
            int b = v * CoreLimits.MeshVertexFloats;
            float dx = scene.Render.MeshData[b] - bx, dy = scene.Render.MeshData[b + 1] - by;
            float lx = dx * cs + dy * sn, ly = -dx * sn + dy * cs;                                   // back into the crate's space
            float u = opt.UvMinX + (lx + 0.5f) * (opt.UvMaxX - opt.UvMinX);
            float w = opt.UvMinY + (ly + 0.5f) * (opt.UvMaxY - opt.UvMinY);
            float eu = scene.Render.MeshData[b + 2] - u, ew = scene.Render.MeshData[b + 3] - w;
            if (eu < 0f) eu = -eu;
            if (ew < 0f) ew = -ew;
            if (eu > worstUv) worstUv = eu;
            if (ew > worstUv) worstUv = ew;
        }
        Console.WriteLine("worst texture coordinate error*1000000 " + (int)(worstUv * 1000000f));
        Check(worstUv < 2e-3f, "every vertex's texture coordinate is its place in the crate's part of the texture");

        float minX = 1e9f, maxX = -1e9f;
        for (int i = 0; i < boom.Fragments.Count; i++)
        {
            float fx = boom.Fragments[i].WorldX();
            if (fx < minX) minX = fx;
            if (fx > maxX) maxX = fx;
        }
        float spreadBefore = maxX - minX;

        ground.ClearDirty();
        StampShape crater = StampShape.GenerateShapeCircle(8);                                                  // a crater of 2 units, centred under the crate
        ground.Paint(crater, 24, 4, true, 0, 0, 0);
        int pushed = boom.AddExplosionForce(bx, by - 0.4f, 3f, 5f, 0f);
        Console.WriteLine("pushed " + pushed + " colliders");
        Check(pushed == n, "the explosion reached every fragment");

        for (int frame = 0; frame < 300; frame++)
        {
            ground.Update();
            Scripts.Tick(scene, 1f / 60f);
        }

        float lo = 1e9f, hi = -1e9f, fast = 0f, left = 1e9f, right = -1e9f;
        bool allLive = true;
        for (int i = 0; i < boom.Fragments.Count; i++)
        {
            Node f = boom.Fragments[i];
            if (!scene.IsLive(f)) { allLive = false; continue; }
            float fy = f.WorldY(), fx = f.WorldX();
            if (fy < lo) lo = fy;
            if (fy > hi) hi = fy;
            if (fx < left) left = fx;
            if (fx > right) right = fx;
            Component rc = scene.Find(f, ComponentKind.Rigidbody2D);
            float vx = rc.Body.VelocityX(), vy = rc.Body.VelocityY();
            float speed = vx * vx + vy * vy;
            if (speed > fast) fast = speed;
        }
        Console.WriteLine("after 300 frames: y*1000 " + Milli(lo) + ".." + Milli(hi) + ", x*1000 " + Milli(left) + ".." + Milli(right) + ", spread before*1000 " + Milli(spreadBefore) + ", fastest^2*1000 " + Milli(fast));
        Check(allLive, "every fragment is still there");
        Check(lo > 0.35f && hi < 3.5f, "the fragments came to rest on the ground and in the crater, none through it or away in the air");
        Check(fast < 0.01f, "the fragments have settled");
        Check((right - left) > spreadBefore + 0.3f, "the explosion spread the fragments apart");
        bool craterOk = !ground.IsSolid(32, 8) && ground.IsSolid(32, 3) && ground.IsSolid(5, 5);
        Check(craterOk, "the crater is open and the ground beside it intact");
        int verts2 = scene.CollectMeshes();
        float area2 = BatchArea(scene, verts2);
        Check(verts2 == verts && area2 > 0.998f && area2 < 1.002f, "the fragments are still the same pieces of crate: rigid bodies keep their area");

        // ---- the pools are bounded: asking for more fragments than the scene has room for stops cleanly ----
        Node big = scene.NewNode(null);
        big.SetPosition(6.5f, 3f);
        scene.AddRigidbody(big, PB2.BodyDynamic);
        scene.AddBoxCollider(big, 1f, 1f);
        ExplodeOptions many = new ExplodeOptions();
        many.Mode = Fracturer.Triangle;
        many.ExtraPoints = 6;
        many.SubshatterSteps = 2;
        int made = boom.Explode(big, many);
        Console.WriteLine("heavy shatter: " + made + " of " + boom.Requested + " pieces fit");
        Check(made > 0 && made < boom.Requested && made == boom.Fragments.Count, "a shatter bigger than the pools stops with the fragments it could make");
        for (int frame = 0; frame < 30; frame++)
        {
            ground.Update();
            Scripts.Tick(scene, 1f / 60f);
        }
        int problems = scene.Validate();
        Check(problems == 0, "the scene is consistent afterwards");

        ground.Destroy();
        if (failures == 0) Console.WriteLine("all checks passed");
        else Console.WriteLine("FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
