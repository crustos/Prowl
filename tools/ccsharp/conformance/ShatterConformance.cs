// destruction: randomized tests of the fracturing geometry (Fracturer): pieces of a convex polygon must tile it exactly, be convex, stay
// within the native vertex limit and be reproducible from a seed. Deterministic and integer-printing, so the same source runs on .NET and as C.
using System;
using System.Collections.Generic;
using Prowl.Runtime.Destruction2D;

static class Rng
{
    static uint s = 1u;
    public static void Seed(uint v) { s = v * 2654435761u + 1u; }
    public static int Next(int n) { s = s * 1664525u + 1013904223u; return (int)((s >> 8) % (uint)n); }
    public static float F() { s = s * 1664525u + 1013904223u; return (float)((s >> 8) & 0xFFFFFFu) / 16777216f; }
}

static class ShatterConformance
{
    static int failures = 0;

    static void Fail(string test, int caseNo, string what)
    {
        failures++;
        if (failures <= 12) Console.WriteLine("FAIL " + test + " case " + caseNo + ": " + what);
    }

    // a random convex polygon: points of an ellipse in angular order, found without trigonometry (x = (1 - t^2) / (1 + t^2), y = 2t / (1 + t^2))
    static void RandomConvex(List<float> poly, int k)
    {
        poly.Clear();
        float a = 0.5f + Rng.F() * 2.5f, b = 0.5f + Rng.F() * 2.5f, ox = Rng.F() * 6f - 3f, oy = Rng.F() * 6f - 3f;
        float[] ts = new float[k];
        for (int i = 0; i < k; i++) ts[i] = -6f + Rng.F() * 12f;
        for (int i = 1; i < k; i++)                                  // sort (k is small)
            for (int j = i; j > 0 && ts[j - 1] > ts[j]; j--)
            {
                float tmp = ts[j]; ts[j] = ts[j - 1]; ts[j - 1] = tmp;
            }
        for (int i = 0; i < k; i++)
        {
            float t = ts[i];
            poly.Add(ox + a * (1f - t * t) / (1f + t * t));
            poly.Add(oy + b * 2f * t / (1f + t * t));
        }
    }

    static bool Inside(List<float> pts, int start, int count, float x, float y, float margin)
    {
        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;
            float ax = pts[2 * (start + i)], ay = pts[2 * (start + i) + 1];
            float bx = pts[2 * (start + j)], by = pts[2 * (start + j) + 1];
            float len = (float)Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (((bx - ax) * (y - ay) - (by - ay) * (x - ax)) / len < margin) return false;
        }
        return true;
    }

    static uint Hash(PolySet set)
    {
        uint h = 2166136261u;
        for (int i = 0; i < set.Points.Count; i++) h = (h ^ (uint)(int)(set.Points[i] * 1000f)) * 16777619u;
        for (int i = 0; i < set.Counts.Count; i++) h = (h ^ (uint)set.Counts[i]) * 16777619u;
        return h;
    }

    static void TestShatter(int cases)
    {
        Fracturer f = new Fracturer(1u);
        List<float> poly = new List<float>();
        int totalPieces = 0, maxPieces = 0, triPieces = 0, vorPieces = 0, seedsDiffer = 0, seedsTried = 0;
        uint all = 2166136261u;
        for (int k = 0; k < cases; k++)
        {
            int n = 3 + Rng.Next(7);
            RandomConvex(poly, n);
            int mode = Rng.Next(2);
            int extra = Rng.Next(7);
            int sub = Rng.Next(3);
            f.Seed((uint)k + 1u);
            int count = f.Shatter(poly, mode, extra, sub);
            if (count <= 0) { Fail("shatter", k, "no pieces"); continue; }
            totalPieces += count;
            if (count > maxPieces) maxPieces = count;
            if (mode == Fracturer.Triangle) triPieces += count; else vorPieces += count;

            float src = Fracturer.SignedArea(poly, 0, n);
            if (src < 0f) src = -src;
            float sum = 0f;
            for (int p = 0; p < f.Pieces.Count; p++)
            {
                int c = f.Pieces.Counts[p];
                if (c < 3 || c > Fracturer.MaxVertices) { Fail("shatter", k, "a piece has " + c + " points"); break; }
                int ps = f.Pieces.Starts[p];
                float a = Fracturer.SignedArea(f.Pieces.Points, ps, c);
                if (a <= 0f) { Fail("shatter", k, "a piece is not counter-clockwise"); break; }
                sum += a;
                for (int i = 0; i < c; i++)
                {
                    int j = (i + 1) % c, m = (i + 2) % c;
                    float xi = f.Pieces.X(p, i), yi = f.Pieces.Y(p, i);
                    float xj = f.Pieces.X(p, j), yj = f.Pieces.Y(p, j);
                    float xm = f.Pieces.X(p, m), ym = f.Pieces.Y(p, m);
                    float ex = xj - xi, ey = yj - yi;
                    float gx = xm - xj, gy = ym - yj;
                    if (ex * gy - ey * gx < -1e-4f * src) { Fail("shatter", k, "a piece is not convex"); break; }
                }
            }
            float rel = (sum - src) / src;
            if (rel < 0f) rel = -rel;
            if (rel > 2e-3f) Fail("shatter", k, "the pieces' area differs from the polygon's by " + (int)(rel * 100000f) + " per 100000");

            // coverage: a point inside the polygon is inside one piece (two only on a border, which the margin skips)
            for (int s = 0; s < 60; s++)
            {
                float px = poly[0], py = poly[1];
                bool found = false;
                for (int tries = 0; tries < 50 && !found; tries++)
                {
                    float minX = 1e9f, maxX = -1e9f, minY = 1e9f, maxY = -1e9f;
                    for (int i = 0; i < n; i++)
                    {
                        if (poly[2 * i] < minX) minX = poly[2 * i];
                        if (poly[2 * i] > maxX) maxX = poly[2 * i];
                        if (poly[2 * i + 1] < minY) minY = poly[2 * i + 1];
                        if (poly[2 * i + 1] > maxY) maxY = poly[2 * i + 1];
                    }
                    px = minX + Rng.F() * (maxX - minX);
                    py = minY + Rng.F() * (maxY - minY);
                    found = Inside(poly, 0, n, px, py, 1e-3f);
                }
                if (!found) continue;
                int strictly = 0, near = 0;
                for (int p = 0; p < f.Pieces.Count; p++)
                {
                    int ps = f.Pieces.Starts[p];
                    int pc = f.Pieces.Counts[p];
                    if (Inside(f.Pieces.Points, ps, pc, px, py, 1e-3f)) strictly++;
                    if (Inside(f.Pieces.Points, ps, pc, px, py, -1e-3f)) near++;
                }
                if (strictly > 1 || near < 1) { Fail("coverage", k, "a point is in " + strictly + " pieces (near " + near + ")"); break; }
            }

            // the same seed gives the same pieces
            uint h1 = Hash(f.Pieces);
            f.Seed((uint)k + 1u);
            f.Shatter(poly, mode, extra, sub);
            if (Hash(f.Pieces) != h1) Fail("determinism", k, "the same seed gave other pieces");
            all = (all ^ h1) * 16777619u;
            if (extra > 0)
            {
                f.Seed((uint)k + 1000u);
                f.Shatter(poly, mode, extra, sub);
                seedsTried++;
                if (Hash(f.Pieces) != h1) seedsDiffer++;
            }
        }
        Console.WriteLine("shatter        " + cases + " cases, " + totalPieces + " pieces (most in one: " + maxPieces + "), triangle " + triPieces + ", voronoi " + vorPieces);
        Console.WriteLine("seeds          " + seedsDiffer + " of " + seedsTried + " other seeds gave other pieces");
        Console.WriteLine("checksum       " + all);
    }

    // with no sub-shatter the pieces come from one set of sites: check what makes them Delaunay triangles / Voronoi cells
    static void TestProperties(int cases)
    {
        Fracturer f = new Fracturer(1u);
        List<float> poly = new List<float>();
        int tris = 0, cellsChecked = 0;
        for (int k = 0; k < cases; k++)
        {
            int n = 3 + Rng.Next(7);
            RandomConvex(poly, n);
            int extra = Rng.Next(9);
            f.Seed((uint)k + 77u);
            double ext = 0.0;
            for (int q = 0; q < n; q++)
                for (int r = 0; r < n; r++)
                {
                    double ddx = poly[2 * q] - poly[2 * r], ddy = poly[2 * q + 1] - poly[2 * r + 1];
                    if (ddx * ddx + ddy * ddy > ext) ext = ddx * ddx + ddy * ddy;
                }
            double scale4 = ext * ext;                 // (the polygon's largest extent)^4: the size of the determinants

            // Delaunay: no vertex of any triangle is strictly inside another triangle's circumcircle
            int count = f.Shatter(poly, Fracturer.Triangle, extra, 0);
            int expected = 2 * f.SiteCount - 2 - n;                   // Euler: S sites with an n-point hull make 2S - 2 - n triangles
            if (count != expected) Fail("euler", k, count + " triangles for " + f.SiteCount + " sites on a " + n + "-gon, expected " + expected);
            for (int t = 0; t < f.Pieces.Count; t++)
            {
                if (f.Pieces.Counts[t] != 3) { Fail("delaunay", k, "a triangle piece has " + f.Pieces.Counts[t] + " points"); break; }
                tris++;
                double ax = f.Pieces.X(t, 0), ay = f.Pieces.Y(t, 0), bx = f.Pieces.X(t, 1), by = f.Pieces.Y(t, 1), cx = f.Pieces.X(t, 2), cy = f.Pieces.Y(t, 2);
                bool bad = false;
                for (int q = 0; q < f.Pieces.Points.Count / 2 && !bad; q++)
                {
                    double dx = f.Pieces.Points[2 * q], dy = f.Pieces.Points[2 * q + 1];
                    double adx = ax - dx, ady = ay - dy, bdx = bx - dx, bdy = by - dy, cdx = cx - dx, cdy = cy - dy;
                    double ad = adx * adx + ady * ady, bd = bdx * bdx + bdy * bdy, cd = cdx * cdx + cdy * cdy;
                    double det = adx * (bdy * cd - bd * cdy) - ady * (bdx * cd - bd * cdx) + ad * (bdx * cdy - bdy * cdx);
                    if (det > 1e-9 * scale4) bad = true;
                }
                if (bad)
                {
                    Fail("delaunay", k, "a point is inside a triangle's circumcircle");
                    string dump = "  polygon:";
                    for (int q = 0; q < n; q++) dump += " " + (int)(poly[2 * q] * 1000000f) + "," + (int)(poly[2 * q + 1] * 1000000f);
                    Console.WriteLine(dump);
                    string tr = "  triangles:";
                    for (int q = 0; q < f.Pieces.Count; q++) tr += " [" + (int)(f.Pieces.X(q,0)*1000000f) + "," + (int)(f.Pieces.Y(q,0)*1000000f) + " " + (int)(f.Pieces.X(q,1)*1000000f) + "," + (int)(f.Pieces.Y(q,1)*1000000f) + " " + (int)(f.Pieces.X(q,2)*1000000f) + "," + (int)(f.Pieces.Y(q,2)*1000000f) + "]";
                    Console.WriteLine(tr);
                    break;
                }
            }

            // Voronoi: every point of a piece has the same nearest site (a piece never straddles two cells)
            count = f.Shatter(poly, Fracturer.Voronoi, extra, 0);
            for (int p = 0; p < f.Pieces.Count; p++)
            {
                int c = f.Pieces.Counts[p];
                float mx = 0f, my = 0f;
                for (int i = 0; i < c; i++) { mx += f.Pieces.X(p, i); my += f.Pieces.Y(p, i); }
                mx /= c; my /= c;
                int nearest = -2;
                bool straddles = false;
                for (int i = -1; i < c && !straddles; i++)
                {
                    float x = mx, y = my;
                    if (i >= 0) { x = mx + 0.6f * (f.Pieces.X(p, i) - mx); y = my + 0.6f * (f.Pieces.Y(p, i) - my); }
                    int best = -1;
                    float bestD = 1e30f, second = 1e30f;
                    for (int s = 0; s < f.SiteCount; s++)
                    {
                        float ddx = f.SiteX(s) - x, ddy = f.SiteY(s) - y;
                        float d = ddx * ddx + ddy * ddy;
                        if (d < bestD) { second = bestD; bestD = d; best = s; }
                        else if (d < second) second = d;
                    }
                    if (second - bestD < 1e-5f) continue;          // too close to a bisector to say
                    if (nearest == -2) nearest = best;
                    else if (nearest != best) straddles = true;
                }
                cellsChecked++;
                if (straddles) { Fail("voronoi", k, "a piece has points whose nearest sites differ"); break; }
            }
        }
        Console.WriteLine("delaunay       " + tris + " triangles checked");
        Console.WriteLine("voronoi        " + cellsChecked + " cells checked");
    }

    static void TestKnown()
    {
        Fracturer f = new Fracturer(5u);
        List<float> box = new List<float>();
        box.Add(0f); box.Add(0f); box.Add(4f); box.Add(0f); box.Add(4f); box.Add(2f); box.Add(0f); box.Add(2f);

        int n = f.Shatter(box, Fracturer.Voronoi, 0, 0);
        bool quadrants = n == 4;
        for (int p = 0; quadrants && p < f.Pieces.Count; p++)
        {
            int ps = f.Pieces.Starts[p];
            int pc = f.Pieces.Counts[p];
            float a = Fracturer.SignedArea(f.Pieces.Points, ps, pc);
            if (a < 1.999f || a > 2.001f) quadrants = false;          // wh / 4 = 2
        }
        if (!quadrants) Fail("known", 0, "a 4 x 2 box with no extra points is not four quadrants of area 2 (" + n + " pieces)");
        Console.WriteLine("box voronoi    " + n + " pieces");

        n = f.Shatter(box, Fracturer.Triangle, 0, 0);
        if (n != 2) Fail("known", 1, "a box in triangle mode with no extra points is not two triangles (" + n + ")");
        Console.WriteLine("box triangles  " + n + " pieces");

        List<float> tri = new List<float>();
        tri.Add(0f); tri.Add(0f); tri.Add(3f); tri.Add(0f); tri.Add(0f); tri.Add(3f);
        n = f.Shatter(tri, Fracturer.Triangle, 0, 0);
        if (n != 3) Fail("known", 2, "a triangle is broken at its centre into three (" + n + ")");
        Console.WriteLine("triangle       " + n + " pieces");

        List<float> cw = new List<float>();                           // the box, clockwise
        cw.Add(0f); cw.Add(2f); cw.Add(4f); cw.Add(2f); cw.Add(4f); cw.Add(0f); cw.Add(0f); cw.Add(0f);
        n = f.Shatter(cw, Fracturer.Voronoi, 0, 0);
        if (n != 4) Fail("known", 3, "a clockwise box gave " + n + " pieces");

        List<float> ell = new List<float>();                          // an L: concave, refused
        ell.Add(0f); ell.Add(0f); ell.Add(4f); ell.Add(0f); ell.Add(4f); ell.Add(1f); ell.Add(1f); ell.Add(1f); ell.Add(1f); ell.Add(4f); ell.Add(0f); ell.Add(4f);
        n = f.Shatter(ell, Fracturer.Voronoi, 0, 0);
        if (n != 0) Fail("known", 4, "a concave polygon was not refused");

        List<float> big = new List<float>();                          // 12 points on a circle: pieces must not exceed 8 points
        for (int i = 0; i < 12; i++)
        {
            float t = -5f + 10f * i / 11f;
            big.Add((1f - t * t) / (1f + t * t) * 2f);
            big.Add(2f * t / (1f + t * t) * 2f);
        }
        n = f.Shatter(big, Fracturer.Voronoi, 0, 0);
        bool small = n > 0;
        for (int p = 0; p < f.Pieces.Count; p++) if (f.Pieces.Counts[p] > Fracturer.MaxVertices) small = false;
        if (!small) Fail("known", 5, "a piece has more than the native limit of points");
        Console.WriteLine("12-gon         " + n + " pieces");
    }

    public static int Main()
    {
        Rng.Seed(7u);
        TestShatter(400);
        TestProperties(300);
        TestKnown();
        if (failures == 0) Console.WriteLine("all tests passed");
        else Console.WriteLine("FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
