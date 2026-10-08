// GpuSand2D: falling sand and water simulated entirely on the GPU (compute shaders in Native/Gfx2D/gfx2d_sand.inc). Nothing of the grid is on the CPU
// except the counts and a hash that this game reads to check itself; the same source runs on .NET and as a translated player and must print the same.
using System;
using Prowl.Native.Gfx2D;

static class Game
{
    const int Air = 0, Stone = 1, Sand = 2, Water = 3;
    const int W = 128, H = 96;
    static int frame;
    static float[] none;

    public static int Init()
    {
        none = new float[8];
        if (GFX.Init(640, 480) == 0) { Console.WriteLine("no GLES 3 here"); return 1; }
        if (GFX.SandInit(W, H) == 0) { Console.WriteLine("no compute shaders here"); return 2; }
        GFX.Camera(0f, 0f, H / 2f, 0.05f, 0.06f, 0.09f);
        GFX.SandSeed(7);
        GFX.SandBrush(64, 0, 40, Stone);                    // a stone bowl: a disc, then its inside is cut out
        GFX.SandBrush(64, 8, 34, Air);
        GFX.SandBrush(64, 70, 12, Sand);
        GFX.SandBrush(40, 60, 8, Water);
        return 0;
    }

    public static void Frame()
    {
        GFX.SandStep(2);
        GFX.Draw(none, 0);
        GFX.SandDraw(-W / 2f, -H / 2f, W / 2f, H / 2f);
        if (frame % 60 == 0)
            Console.WriteLine("frame " + frame + " sand=" + GFX.SandCount(Sand) + " water=" + GFX.SandCount(Water) + " hash=" + GFX.SandHash());
        frame++;
    }

    public static int Main()
    {
        int rc = Init();
        if (rc != 0) return rc;
        int sand0 = GFX.SandCount(Sand), water0 = GFX.SandCount(Water);
        for (int i = 0; i < 300; i++) Frame();
        Console.WriteLine("end hash=" + GFX.SandHash() + " frame=" + GFX.FrameHash());
        int sand1 = GFX.SandCount(Sand), water1 = GFX.SandCount(Water);
        GFX.Shutdown();
        // nothing is lost or made, and it is not where it began
        return sand0 > 0 && water0 > 0 && sand0 == sand1 && water0 == water1 ? 0 : 1;
    }
}
