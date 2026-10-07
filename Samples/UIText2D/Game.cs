// UIText2D: text from the renderer's baked fonts, drawn by Prowl.Core2D.UI.UIText. The same source runs on .NET and as a translated player; both print the
// hash of the picture (Native/Gfx2D), and the picture is the same as the C renderer's own font test draws (Native/Gfx2D/test/font_scene.c): 55eb0199 on the CPU
// rasteriser, which tools/gfx_ui_test.py compares pixel by pixel (that test draws it in C; this one draws it through UIText). It also wraps a paragraph and measures it, and clips it, so the translated code is exercised.
using System;
using Prowl.Core2D.UI;
using Prowl.Native.Gfx2D;

static class Game
{
    const int W = 400, H = 240;
    static float[] none;                                    // an empty sprite batch: gfx_draw starts the frame, the text goes over it
    static int[] title;
    static int[] tint;
    static int[] green;
    static int[] para;
    static int[] lineStart;
    static int[] lineCount;
    static int titleLen, tintLen, greenLen, paraLen;

    static int At(int[] a, int i) { return a[i]; }       // (a static array is read through a parameter: the translator will not subscript it in a call's arguments)

    public static int Init()
    {
        none = new float[GFX.SpriteFloats];
        title = new int[64]; tint = new int[16]; green = new int[16]; para = new int[256]; lineStart = new int[16]; lineCount = new int[16];
        if (GFX.Init(W, H) == 0) { Console.WriteLine("no GLES 3 here"); return 1; }
        if (UIText.Init(W, H) != 4) { Console.WriteLine("fonts != 4"); return 1; }
        UIText.PixelCamera(W, H, 0.12f, 0.14f, 0.2f);
        titleLen = UIText.Load(title, "Hello, World! Stride2D 0123 ");
        title[titleLen++] = 233; title[titleLen++] = 252; title[titleLen++] = 223;      // e acute, u umlaut, sharp s: the rest of Latin-1 is put in by code, not in a string
        tintLen = UIText.Load(tint, "tint: red");
        greenLen = UIText.Load(green, "green");
        paraLen = UIText.Load(para, "Text is laid out in C#: measured, wrapped at spaces, and drawn as one quad per glyph.");
        return 0;
    }

    public static int Main()
    {
        if (Init() != 0) return 1;
        GFX.Draw(none, 0);
        float top = 8f;
        for (int f = 0; f < UIText.FontCount(); f++)
        {
            UIText.Draw(f, title, 0, titleLen, 8f, top, 1f, 1f, 1f, 1f);
            top += UIText.LineHeight(f) + 4f;
        }
        UIText.Draw(0, tint, 0, tintLen, 8f, 228f - UIText.Ascent(0), 1f, 0.4f, 0.4f, 1f);
        UIText.Draw(0, green, 0, greenLen, 120f, 228f - UIText.Ascent(0), 0.4f, 1f, 0.4f, 1f);
        GFX.SaveFrame(0);                                    // frame_0000.ppm: the font scene
        Console.WriteLine("hash " + GFX.FrameHash());
        int lines = UIText.Wrap(1, para, paraLen, 180f, lineStart, lineCount, 16);
        Console.WriteLine("wrapped into " + lines + " lines; the title is " + (int)UIText.Measure(2, title, 0, titleLen) + " px wide at 28 px");
        for (int i = 0; i < lines; i++)
        {
            int ls = At(lineStart, i), lc = At(lineCount, i);
            int px = (int)UIText.Measure(1, para, ls, lc);
            Console.WriteLine("line " + i + ": " + lc + " chars, " + px + " px");
        }
        GFX.Draw(none, 0);
        float y = 8f;
        GFX.Clip(20, 20, 150, 90);
        for (int i = 0; i < lines; i++)
        {
            int ls = At(lineStart, i), lc = At(lineCount, i);
            UIText.Draw(1, para, ls, lc, 24f, y + 20f, 0.9f, 1f, 0.6f, 1f);
            y += UIText.LineHeight(1);
        }
        GFX.ClipReset();
        Console.WriteLine("clipped hash " + GFX.FrameHash() + " vertices " + GFX.Stat(GFX.StatVertices));
        GFX.SaveFrame(1);                                    // frame_0001.ppm: the clipped paragraph
        GFX.Shutdown();
        return 0;
    }
}
