// terrain_model: randomized tests of the terrain model (PixelRange, PixelColumn, StampShape, PixelRectMerge, PixelQuadTree, PixelChainTrace) against a plain
// bitmap oracle. Deterministic (own generator, integer math, no strings but printing), so the same source runs on .NET and as C.
using System;
using System.Collections.Generic;
using Prowl.Runtime.Destruction2D;

static class Rng
{
    static uint s = 12345u;
    public static void Seed(uint v) { s = v * 2654435761u + 1u; }
    public static int Next(int n) { s = s * 1664525u + 1013904223u; return (int)((s >> 8) % (uint)n); }
}

static class PixelTerrainConformance
{
    static int failures = 0;

    static void Fail(string test, int caseNo, string what)
    {
        failures++;
        if (failures <= 12) Console.WriteLine("FAIL " + test + " case " + caseNo + ": " + what);
    }

    // ---- the oracle: a bitmap, column-major (cell x * h + y) -------------------------------------------------

    static bool[] RandomGrid(int w, int h)
    {
        bool[] g = new bool[w * h];
        int mode = Rng.Next(6);
        int density = mode == 0 ? 5 : (mode == 1 ? 95 : 20 + Rng.Next(60));
        for (int i = 0; i < g.Length; i++) g[i] = Rng.Next(100) < density;
        int passes = Rng.Next(4);                       // smooth into blobs: thin spans, single pixels and diagonal touches all occur
        for (int p = 0; p < passes; p++)
        {
            bool[] n = new bool[g.Length];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    int c = 0;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx >= 0 && xx < w && yy >= 0 && yy < h && g[xx * h + yy]) c++;
                        }
                    n[x * h + y] = c >= 5;
                }
            g = n;
        }
        return g;
    }

    // what CollidableChunk.PrepareColumns makes of a texture: one range per run of ground
    static List<PixelColumn> ColumnsOf(bool[] g, int w, int h)
    {
        List<PixelColumn> cols = new List<PixelColumn>();
        for (int x = 0; x < w; x++)
        {
            PixelColumn c = new PixelColumn(x);
            for (int y = 0; y < h; y++)
            {
                int min = y;
                int max = y - 1;
                while (y < h && g[x * h + y]) { y++; max++; }
                if (min <= max) c.AddRange(min, max);
            }
            cols.Add(c);
        }
        return cols;
    }

    static void FillChunk(PixelChunk chunk, bool[] g, int w, int h)
    {
        chunk.Width = w;
        chunk.Height = h;
        chunk.Columns.Clear();
        for (int x = 0; x < w; x++)
        {
            chunk.Columns.Add(new PixelColumn(x));
            for (int y = 0; y < h; y++)
            {
                int min = y;
                int max = y - 1;
                while (y < h && g[x * h + y]) { y++; max++; }
                if (min <= max) chunk.Columns[x].AddRange(min, max);
            }
        }
    }

    static int Mismatches(List<PixelColumn> cols, bool[] g, int w, int h)
    {
        int bad = 0;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                if (cols[x].isWithin(y) != g[x * h + y]) bad++;
        return bad;
    }

    static bool Normalized(List<PixelColumn> cols)       // ranges inside a column are disjoint
    {
        for (int x = 0; x < cols.Count; x++)
        {
            int n = cols[x].Ranges.Count;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (cols[x].Ranges[i].Min <= cols[x].Ranges[j].Max && cols[x].Ranges[j].Min <= cols[x].Ranges[i].Max) return false;
        }
        return true;
    }

    // ---- 1. the columns hold exactly the bitmap -------------------------------------------------------------

    static void TestColumns(int cases)
    {
        for (int k = 0; k < cases; k++)
        {
            int w = 1 + Rng.Next(24), h = 1 + Rng.Next(24);
            bool[] g = RandomGrid(w, h);
            List<PixelColumn> cols = ColumnsOf(g, w, h);
            if (Mismatches(cols, g, w, h) != 0) Fail("columns", k, "isWithin differs from the bitmap");
            if (!Normalized(cols)) Fail("columns", k, "ranges overlap");
            for (int x = 0; x < w; x++)
                for (int y0 = 0; y0 < h; y0++)
                    for (int y1 = y0; y1 < h; y1++)
                    {
                        bool all = true, any = false;
                        for (int y = y0; y <= y1; y++) { if (g[x * h + y]) any = true; else all = false; }
                        if (cols[x].Covers(y0, y1) != all) Fail("covers", k, "Covers(" + y0 + "," + y1 + ") at column " + x);
                        if (cols[x].Touches(y0, y1) != any) Fail("touches", k, "Touches(" + y0 + "," + y1 + ") at column " + x);
                    }
        }
        Console.WriteLine("columns        " + cases + " cases");
    }

    // ---- 2. rectangles: they tile exactly the ground ---------------------------------------------------------

    static int CheckTiling(List<PixelRect> rects, bool[] g, int w, int h, bool exact)
    {
        int[] hits = new int[w * h];
        for (int i = 0; i < rects.Count; i++)
        {
            PixelRect r = rects[i];
            if (r.W <= 0 || r.H <= 0 || r.X < 0 || r.Y < 0 || r.X + r.W > w || r.Y + r.H > h) return 1;
            for (int x = r.X; x < r.X + r.W; x++)
                for (int y = r.Y; y < r.Y + r.H; y++) hits[x * h + y]++;
        }
        for (int i = 0; i < hits.Length; i++)
        {
            if (g[i] && hits[i] != 1) return 1;
            if (!g[i] && hits[i] != 0) return 1;
        }
        return 0;
    }

    static void TestRects(int cases)
    {
        int rectSum = 0, mergeOnPow2 = 0, quadSum = 0;
        for (int k = 0; k < cases; k++)
        {
            int w = 1 + Rng.Next(32), h = 1 + Rng.Next(32);
            bool[] g = RandomGrid(w, h);
            List<PixelColumn> cols = ColumnsOf(g, w, h);
            List<PixelRect> rects = new List<PixelRect>();
            PixelRectMerge.FromColumns(cols, rects);
            if (CheckTiling(rects, g, w, h, true) != 0) Fail("rectmerge", k, "does not tile the ground exactly");
            rectSum += rects.Count;

            int s = 4 << Rng.Next(4);                                   // quadtree: power-of-two squares
            bool[] g2 = RandomGrid(s, s);
            List<PixelColumn> cols2 = ColumnsOf(g2, s, s);
            List<PixelRect> quads = new List<PixelRect>();
            PixelQuadTree.Build(cols2, 0, 0, s, s, quads);
            if (CheckTiling(quads, g2, s, s, true) != 0) Fail("quadtree", k, "does not tile the ground exactly");
            List<PixelRect> merged = new List<PixelRect>();
            PixelRectMerge.FromColumns(cols2, merged);
            if (CheckTiling(merged, g2, s, s, true) != 0) Fail("rectmerge", k, "does not tile the ground exactly (power-of-two grid)");
            quadSum += quads.Count;
            mergeOnPow2 += merged.Count;
        }
        Console.WriteLine("rectmerge      " + cases + " cases; on the same " + cases + " power-of-two grids: rectmerge " + mergeOnPow2 + " rectangles, quadtree " + quadSum);
    }

    // ---- 3. chains: the boundary, once each, ground on the left ---------------------------------------------

    static bool Solid(bool[] g, int w, int h, int x, int y) { return x >= 0 && x < w && y >= 0 && y < h && g[x * h + y]; }

    static void TestChains(int cases)
    {
        int loops = 0, opens = 0;
        PixelChunk chunk = new PixelChunk();                 // one chunk, refilled for every case (an arena class has a fixed number of slots)
        for (int k = 0; k < cases; k++)
        {
            int w = 1 + Rng.Next(24), h = 1 + Rng.Next(24);
            bool[] g = RandomGrid(w, h);
            FillChunk(chunk, g, w, h);
            PixelChainTrace tr = new PixelChainTrace(chunk);
            PixelChainSet set = new PixelChainSet();
            tr.Trace(set);

            // expected unit edges: (x, y, dir) with the ground on the left of the way
            int[] expect = new int[(w + 2) * (h + 2) * 4];
            int expectedCount = 0;
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    if (!g[x * h + y]) continue;
                    if (!Solid(g, w, h, x, y - 1)) { expect[((x + 1) * (h + 2) + (y + 1)) * 4 + 0]++; expectedCount++; }
                    if (!Solid(g, w, h, x + 1, y)) { expect[((x + 2) * (h + 2) + (y + 1)) * 4 + 1]++; expectedCount++; }
                    if (!Solid(g, w, h, x, y + 1)) { expect[((x + 2) * (h + 2) + (y + 2)) * 4 + 2]++; expectedCount++; }
                    if (!Solid(g, w, h, x - 1, y)) { expect[((x + 1) * (h + 2) + (y + 2)) * 4 + 3]++; expectedCount++; }
                }
            int seen = 0;
            bool ok = true;
            for (int c = 0; c < set.ChainCount && ok; c++)
            {
                int n = set.Counts[c];
                int start = set.Starts[c];
                int segs = set.Loop[c] == 1 ? n : n - 1;
                if (set.Loop[c] == 1) loops++; else opens++;
                for (int i = 0; i < segs && ok; i++)
                {
                    int ax = set.Points[2 * (start + i)], ay = set.Points[2 * (start + i) + 1];
                    int bx = set.Points[2 * (start + (i + 1) % n)], by = set.Points[2 * (start + (i + 1) % n) + 1];
                    int dx = bx > ax ? 1 : (bx < ax ? -1 : 0), dy = by > ay ? 1 : (by < ay ? -1 : 0);
                    if ((dx != 0) == (dy != 0)) { ok = false; break; }          // not axis aligned
                    int dir = dx == 1 ? 0 : (dy == 1 ? 1 : (dx == -1 ? 2 : 3));
                    int len = (bx - ax) + (by - ay);
                    if (len < 0) len = -len;
                    int cx = ax, cy = ay;
                    for (int u = 0; u < len; u++)
                    {
                        int slot = ((cx + 1) * (h + 2) + (cy + 1)) * 4 + dir;
                        if (expect[slot] <= 0) { ok = false; break; }
                        expect[slot]--;
                        seen++;
                        cx += dx; cy += dy;
                    }
                }
            }
            if (!ok) Fail("chains", k, "a chain edge is not a boundary edge, or is traced twice");
            else if (seen != expectedCount) Fail("chains", k, "traced " + seen + " of " + expectedCount + " boundary edges");
        }
        Console.WriteLine("chains         " + cases + " cases, " + loops + " loops, " + opens + " open chains");
    }

    // ---- 3b. one column: every way a cut or an addition can meet the existing ranges ------------------------------

    static void TestColumnOps(int cases)
    {
        int h = 12;
        for (int k = 0; k < cases; k++)
        {
            bool[] g = new bool[h];
            for (int i = 0; i < h; i++) g[i] = Rng.Next(100) < 50;
            List<PixelColumn> one = ColumnsOf(g, 1, h);
            int ops = 1 + Rng.Next(4);
            for (int o = 0; o < ops; o++)
            {
                int y0 = Rng.Next(h), y1 = y0 + Rng.Next(h - y0);
                bool destroy = Rng.Next(2) == 0;
                for (int y = y0; y <= y1; y++) g[y] = !destroy;
                bool changedExpected = false;
                for (int y = y0; y <= y1; y++) if (one[0].isWithin(y) == destroy) changedExpected = true;
                bool changed = destroy ? one[0].ClearRows(y0, y1) : one[0].SumRange(new PixelRange(y0, y1));
                string op = "SumRange";
                if (destroy) op = "ClearRows";
                if (Mismatches(one, g, 1, h) != 0) { Fail("column ops", k, op + " " + y0 + ".." + y1 + " gave the wrong rows"); break; }
                if (!Normalized(one)) { Fail("column ops", k, "ranges overlap after " + op); break; }
                if (changed != changedExpected) { Fail("column ops", k, op + " reported the wrong changed flag"); break; }
            }
        }
        Console.WriteLine("column ops     " + cases + " cases");
    }

    // ---- 4. destroying and building, the way CollidableChunk does it -----------------------------------------

    static void Stamp(List<PixelColumn> cols, bool[] g, int w, int h, StampShape s, int px, int py, bool destroy, bool useChunkCalls)
    {
        for (int k = 0; k < s.Ranges.Count; k++)
        {
            int x = px + s.OffsetX + k;
            if (x < 0 || x >= w) continue;
            PixelRange r = s.Ranges[k];
            int y = py + r.Min;
            int height = r.Length + 1;                      // PaintableLayer.PaintColumn: RectInt(x, y, 1, Length + 1)
            int y0 = y < 0 ? 0 : y;
            int y1 = y + height - 1;
            if (y1 > h - 1) y1 = h - 1;
            if (y1 < y0) continue;
            for (int yy = y0; yy <= y1; yy++) g[x * h + yy] = !destroy;
            if (destroy) cols[x].DelRange(new PixelRange(y0 - 1, y1 + 1));              // CollidableChunk.DeleteFromColumns
            else cols[x].SumRange(new PixelRange(y0, y1));                              // CollidableChunk.AddToColumns
        }
    }

    // a random circle or rectangle at a random place, partly off the chunk now and then
    static void StampRandom(List<PixelColumn> cols, bool[] g, int w, int h, bool destroy, bool circle)
    {
        if (circle)
        {
            StampShape s = StampShape.GenerateShapeCircle(1 + Rng.Next(6));
            int px = Rng.Next(w + 6) - 3;
            int py = Rng.Next(h + 6) - 3;
            Stamp(cols, g, w, h, s, px, py, destroy, true);
        }
        else
        {
            StampShape s = StampShape.GenerateShapeRect(1 + Rng.Next(8), 1 + Rng.Next(8));
            int px = Rng.Next(w + 6) - 3;
            int py = Rng.Next(h + 6) - 3;
            Stamp(cols, g, w, h, s, px, py, destroy, true);
        }
    }

    static void TestEdits(int cases)
    {
        int wrong = 0, unnormal = 0, total = 0;
        for (int k = 0; k < cases; k++)
        {
            int w = 8 + Rng.Next(24), h = 8 + Rng.Next(24);
            bool[] g = RandomGrid(w, h);
            List<PixelColumn> cols = ColumnsOf(g, w, h);
            int ops = 1 + Rng.Next(6);
            bool bad = false;
            for (int o = 0; o < ops; o++)
            {
                bool destroy = Rng.Next(3) != 0;
                StampRandom(cols, g, w, h, destroy, Rng.Next(2) == 0);
                if (Mismatches(cols, g, w, h) != 0) { bad = true; break; }
            }
            total++;
            if (bad) wrong++;
            else if (!Normalized(cols)) unnormal++;
            if (bad) Fail("edits", k, "columns differ from the bitmap after a stamp");
        }
        Console.WriteLine("edits          " + total + " cases: " + wrong + " wrong, " + unnormal + " with overlapping ranges");
    }

    public static int Main()
    {
        Rng.Seed(1u);
        TestColumns(300);
        TestRects(300);
        TestChains(300);
        TestColumnOps(20000);
        TestEdits(300);
        if (failures == 0) Console.WriteLine("all tests passed");
        else Console.WriteLine("FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
