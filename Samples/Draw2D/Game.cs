// Draw2D: balls falling onto a floor, drawn. The same source runs on .NET and as a translated player; both print a hash of frames the renderer drew
// (Native/Gfx2D). On the web (--wasm) the renderer is WebGL2 and the page steps Frame() once per animation frame.
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;
using Prowl.Native.Gfx2D;

static class Game
{
    static Scene2D scene;
    static int frame;
    static int sprites;

    public static int Init()
    {
        Scripts.Init();
        if (GFX.Init(640, 360) == 0) { Console.WriteLine("no GLES 3 here"); return 1; }
        GFX.Camera(0f, 3f, 4f, 0.08f, 0.10f, 0.16f);
        scene = new Scene2D();
        Node ground = scene.NewNode(null);
        ground.SetPosition(0f, -0.5f);
        scene.AddBoxCollider(ground, 14f, 1f);
        scene.AddSprite(ground, SpriteRenderer2D.Box, 14f, 1f, 0.25f, 0.55f, 0.30f);
        for (int i = 0; i < 6; i++)
        {
            Node b = scene.NewNode(null);
            b.SetPosition(-2.5f + i, 2f + i * 0.9f);
            scene.AddRigidbody(b, PB2.BodyDynamic);
            scene.AddCircleCollider(b, 0.45f);
            scene.AddSprite(b, SpriteRenderer2D.Disc, 0.9f, 0.9f, 0.9f - i * 0.12f, 0.3f + i * 0.1f, 0.2f + i * 0.12f);
        }
        return 0;
    }

    // one frame of the game: step, collect, draw
    public static void Frame()
    {
        Scripts.Tick(scene, 1f / 60f);
        sprites = GFX.Draw(scene.Render.DrawData, scene.CollectSprites());
        if (frame % 60 == 0)
            Console.WriteLine("frame " + frame + " sprites=" + sprites + " hash=" + GFX.FrameHash());
        frame++;
    }

    public static int Main()
    {
        if (Init() != 0) return 1;
        for (int i = 0; i < 240; i++) Frame();
        Console.WriteLine("end hash=" + GFX.FrameHash() + " calls=" + GFX.Stat(GFX.StatDrawCalls));
        GFX.SaveFrame(0);                                   // frame_0000.ppm: the web test compares the page's pixels with it
        GFX.Shutdown();
        return sprites == 7 ? 0 : 1;
    }
}
