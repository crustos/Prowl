// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Native.Gfx2D;

namespace Prowl.Core2D.UI
{
    /// <summary>
    /// Text for the in-game UI: measuring, word wrapping and drawing with the renderer's baked fonts (DejaVu Sans at 14, 20, 28 and 40 pixels).
    /// Text is an int[] of character codes (Latin-1; anything else draws as '?'), because that is what the C subset handles best; Load fills one from a string.
    /// Positions are in PIXELS with y from the TOP, the way a screen is measured; the renderer's camera must show one world unit per pixel, which
    /// PixelCamera sets. Glyphs sit on whole pixels, so text is sharp at a baked size.
    /// </summary>
    public static class UIText
    {
        public const int MaxFonts = 4;
        const int Chunk = 1024;                    // glyphs drawn per renderer call

        static int ready;
        static int fontCount;
        static int screenHeight;
        static float[] advances;                   // [font * 256 + code]: the advance of each character, '?' for the ones the font lacks
        static float[] metrics;                    // [font * 4 ..]: ascent, descent, line height, size
        static float[] glyph;                      // scratch for GFX.FontGlyph
        static float[] fontBuf;                    // scratch for GFX.FontMetrics
        static float[] verts;                      // the quads of one Draw chunk

        /// <summary>Reads the fonts' numbers; the renderer (GFX.Init) must be up. Returns the number of fonts (0 if the renderer has none).</summary>
        public static int Init(int screenWidth, int screenH)
        {
            screenHeight = screenH;
            fontCount = GFX.FontCount();
            if (fontCount > MaxFonts) fontCount = MaxFonts;
            advances = new float[MaxFonts * 256];
            metrics = new float[MaxFonts * 4];
            glyph = new float[9];
            fontBuf = new float[4];
            verts = new float[Chunk * 6 * GFX.MeshVertexFloats];
            for (int f = 0; f < fontCount; f++)
            {
                GFX.FontMetrics(f, fontBuf);
                for (int k = 0; k < 4; k++) metrics[f * 4 + k] = fontBuf[k];
                for (int c = 0; c < 256; c++)
                {
                    GFX.FontGlyph(f, c, glyph);
                    advances[f * 256 + c] = glyph[8];
                }
            }
            ready = fontCount > 0 ? 1 : 0;
            return fontCount;
        }

        /// <summary>Points the renderer's camera at a screen of the given size, one world unit per pixel, y up (what Draw expects).</summary>
        public static void PixelCamera(int screenWidth, int screenH, float r, float g, float b)
        {
            screenHeight = screenH;
            GFX.Camera(screenWidth * 0.5f, screenH * 0.5f, screenH * 0.5f, r, g, b);
        }

        public static int FontCount() { return fontCount; }

        /// <summary>The baked font whose size is closest to `size` pixels.</summary>
        public static int PickFont(float size)
        {
            int best = 0;
            float bestD = 1000000f;
            for (int f = 0; f < fontCount; f++)
            {
                float d = metrics[f * 4 + 3] - size;
                if (d < 0f) d = -d;
                if (d < bestD) { bestD = d; best = f; }
            }
            return best;
        }

        public static float Size(int font) { return metrics[Clamp(font) * 4 + 3]; }
        public static float Ascent(int font) { return metrics[Clamp(font) * 4]; }
        public static float Descent(int font) { return metrics[Clamp(font) * 4 + 1]; }
        public static float LineHeight(int font) { return metrics[Clamp(font) * 4 + 2]; }

        static int Clamp(int font)
        {
            if (font < 0) return 0;
            if (font >= fontCount) return fontCount > 0 ? fontCount - 1 : 0;
            return font;
        }

        /// <summary>The advance of a character: control characters (a new line, a tab) have none.</summary>
        public static float Advance(int font, int code)
        {
            if (code < 32) return 0f;
            if (code > 255) code = 63;
            return advances[Clamp(font) * 256 + code];
        }


        /// <summary>
        /// Copies a string into `dst` as character codes: printable ASCII and the new line ("\n", code 10); any other character becomes '?' (keep non-ASCII out of the string: .NET sees one character where the C build sees its several UTF-8 bytes). Returns how many it wrote,
        /// at most dst.Length. (The C# to C translator has no `char`, so a string is read by comparing one-character pieces; for the rest of Latin-1 put the codes
        /// into the array yourself: `dst[n++] = 233;` is an e acute.)
        /// </summary>
        public static int Load(int[] dst, string s)
        {
            string printable = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";
            int n = s.Length;
            if (n > dst.Length) n = dst.Length;
            for (int i = 0; i < n; i++)
            {
                string piece = s.Substring(i, 1);
                int code = 63;
                if (piece == "\n") code = 10;
                else
                {
                    for (int k = 0; k < 95; k++)
                    {
                        if (piece == printable.Substring(k, 1)) { code = 32 + k; break; }
                    }
                }
                dst[i] = code;
            }
            return n;
        }

        /// <summary>The width in pixels of text[start .. start + count).</summary>
        public static float Measure(int font, int[] text, int start, int count)
        {
            float w = 0f;
            for (int i = 0; i < count; i++) w += Advance(font, text[start + i]);
            return w;
        }

        /// <summary>
        /// Breaks text[0 .. count) into lines no wider than maxWidth: at a new line (code 10), else at the last space that fits, else inside the word
        /// (never fewer than one character a line). The spaces a break falls on are left out. Writes where each line starts and how long it is;
        /// returns the number of lines (at most maxLines). A maxWidth of 0 or less means no wrapping but the new lines.
        /// </summary>
        public static int Wrap(int font, int[] text, int count, float maxWidth, int[] lineStart, int[] lineCount, int maxLines)
        {
            int lines = 0;
            int start = 0;
            while (start <= count && lines < maxLines)
            {
                float w = 0f;
                int i = start;
                int lastSpace = -1;                // the index of the last space that could end this line
                int end = -1;                      // set when the line is decided: it holds text[start .. end)
                int next = count + 1;              // where the following line starts
                while (i < count)
                {
                    int c = text[i];
                    if (c == 10) { end = i; next = i + 1; break; }
                    float a = Advance(font, c);
                    if (maxWidth > 0f && w + a > maxWidth && i > start)
                    {
                        if (c == 32)               // a space that does not fit ends the line; it is dropped
                        {
                            end = i; next = i + 1; break;
                        }
                        if (lastSpace >= 0) { end = lastSpace; next = lastSpace + 1; }
                        else { end = i; next = i; }
                        break;
                    }
                    if (c == 32) lastSpace = i;
                    w += a;
                    i++;
                }
                if (end < 0) { end = count; next = count + 1; }
                lineStart[lines] = start;
                lineCount[lines] = end - start;
                lines++;
                start = next;
            }
            return lines;
        }

        /// <summary>
        /// Writes the quads (two triangles each, 6 vertices of GFX.MeshVertexFloats floats) of text[start .. start + count) into `outVerts` from vertex
        /// `outVertex` on, for the pen at (x, baseline) in pixels from the top left, tinted (r, g, b, a). Returns how many vertices it wrote (fewer if
        /// outVerts is full). Draw them with GFX.Triangles and GFX.FontTexture(font).
        /// </summary>
        public static int Build(int font, int[] text, int start, int count, float x, float baseline, float r, float g, float b, float a, float[] outVerts, int outVertex)
        {
            if (ready == 0) return 0;
            int f = Clamp(font);
            int floats = GFX.MeshVertexFloats;
            int capacity = outVerts.Length / floats;
            int n = outVertex;
            float pen = x;
            float by0 = Floor(baseline + 0.5f);
            for (int i = 0; i < count; i++)
            {
                int c = text[start + i];
                if (c < 32) continue;
                if (c > 255) c = 63;
                GFX.FontGlyph(f, c, glyph);
                float w = glyph[4], h = glyph[5];
                if (w > 0f && h > 0f)
                {
                    if (n + 6 > capacity) break;
                    float left = Floor(pen + 0.5f) + glyph[6];
                    float top = by0 - glyph[7];                     // pixel row of the glyph's top edge
                    float wx0 = left, wx1 = left + w;
                    float wy1 = screenHeight - top, wy0 = wy1 - h; // world y is up
                    float u0 = glyph[0], v0 = glyph[1], u1 = glyph[2], v1 = glyph[3];
                    Vert(outVerts, n, wx0, wy0, u0, v1, r, g, b, a);
                    Vert(outVerts, n + 1, wx1, wy0, u1, v1, r, g, b, a);
                    Vert(outVerts, n + 2, wx1, wy1, u1, v0, r, g, b, a);
                    Vert(outVerts, n + 3, wx0, wy0, u0, v1, r, g, b, a);
                    Vert(outVerts, n + 4, wx1, wy1, u1, v0, r, g, b, a);
                    Vert(outVerts, n + 5, wx0, wy1, u0, v0, r, g, b, a);
                    n += 6;
                }
                pen += glyph[8];
            }
            return n - outVertex;
        }

        /// <summary>Draws text[start .. start + count) with the top left of its line at (x, top), in pixels. Returns the pen's x afterwards.</summary>
        public static float Draw(int font, int[] text, int start, int count, float x, float top, float r, float g, float b, float a)
        {
            if (ready == 0) return x;
            int f = Clamp(font);
            float baseline = top + metrics[f * 4];
            float pen = x;
            int done = 0;
            while (done < count)
            {
                int n = count - done;
                if (n > Chunk) n = Chunk;
                int nv = Build(f, text, start + done, n, pen, baseline, r, g, b, a, verts, 0);
                if (nv > 0) GFX.Triangles(verts, nv, GFX.FontTexture(f));
                pen += Measure(f, text, start + done, n);
                done += n;
            }
            return pen;
        }

        static void Vert(float[] o, int vertex, float x, float y, float u, float v, float r, float g, float b, float a)
        {
            int k = vertex * GFX.MeshVertexFloats;
            o[k] = x; o[k + 1] = y; o[k + 2] = u; o[k + 3] = v;
            o[k + 4] = r; o[k + 5] = g; o[k + 6] = b; o[k + 7] = a;
        }

        static float Floor(float v)
        {
            int i = (int)v;
            return (float)(v < (float)i ? i - 1 : i);
        }
    }
}
