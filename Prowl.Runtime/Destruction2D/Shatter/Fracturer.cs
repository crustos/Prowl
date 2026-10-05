// Prowl.Runtime.Destruction2D. The shattering idea is from Unity-2D-Destruction (MIT, (c) 2016 Matthew Holtzem); see LICENSE.md.
using System.Collections.Generic;

namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// Breaks a convex polygon into convex pieces, the way Unity-2D-Destruction's SpriteExploder does: the sites are the polygon's own
    /// vertices plus any number of random extra points; <see cref="Voronoi"/> makes one piece per site (the part of the polygon nearer to
    /// that site than to any other), <see cref="Triangle"/> makes the Delaunay triangles of the sites. Every sub-shatter step breaks each piece again.
    /// <para/>
    /// The original gets its Voronoi diagram from a port of Fortune's sweep (about 2,100 lines) and clips it to the outline with the Clipper
    /// library (about 4,800). The outline is convex here, so neither is needed: a Voronoi cell is the polygon cut by one half-plane per other site,
    /// and the Delaunay triangulation is a fan of the polygon made Delaunay by flips. The pieces are the same pieces.
    /// <para/>
    /// Differences from the original: extra points are chosen inside the polygon (the original chooses in its bounding box, which for a
    /// triangle or a rotated shape can be outside it, and its triangle mode centres that box differently from its Voronoi mode); a piece with more than
    /// <see cref="MaxVertices"/> vertices is split into several, because a native polygon shape has at most that many; the random numbers are this
    /// class's own, so a seed always gives the same pieces. A concave outline is refused.
    /// </summary>
    internal sealed class Fracturer
    {
        public const int Triangle = 0;
        public const int Voronoi = 1;
        public const int MaxVertices = 8;

        public int MaxPoints = MaxVertices;    // the most points a piece may have: the native limit unless lowered or (to compare with other tools) raised
        public PolySet Pieces;                 // the result of the last Shatter: convex, counter-clockwise, at most MaxVertices points each

        private PolySet _lvlA;
        private PolySet _lvlB;
        private List<float> _src;              // the source polygon, counter-clockwise
        private List<float> _sites;            // x, y pairs: the polygon's vertices first
        private List<float> _bufA;
        private List<float> _bufB;
        private List<int> _ta;
        private List<int> _tb;
        private List<int> _tc;
        private uint _rng;
        private float _scale;                  // the extent of the source polygon

        /// <summary>The sites of the polygon last broken (the last one, after a sub-shatter): its vertices first, then any centre and extra points.</summary>
        public int SiteCount { get { return _sites.Count / 2; } }
        public float SiteX(int i) { return _sites[2 * i]; }
        public float SiteY(int i) { return _sites[2 * i + 1]; }

        public Fracturer()
        {
            Init(1u);
        }

        public Fracturer(uint seed)
        {
            Init(seed);
        }

        private void Init(uint seed)
        {
            Pieces = new PolySet();
            _lvlA = new PolySet();
            _lvlB = new PolySet();
            _src = new List<float>();
            _sites = new List<float>();
            _bufA = new List<float>();
            _bufB = new List<float>();
            _ta = new List<int>();
            _tb = new List<int>();
            _tc = new List<int>();
            Seed(seed);
        }

        public void Seed(uint seed) { _rng = seed * 2654435761u + 1u; }

        private float Next01()
        {
            _rng = _rng * 1664525u + 1013904223u;
            return (float)((_rng >> 8) & 0xFFFFFFu) / 16777216f;
        }

        // ---- the entry point -------------------------------------------------------------------

        /// <summary>
        /// Shatters the polygon (x, y pairs, either winding). The pieces are in <see cref="Pieces"/>.
        /// </summary>
        /// <param name="poly">The outline, as x, y pairs, in either winding; it must be convex</param>
        /// <param name="mode"><see cref="Triangle"/> or <see cref="Voronoi"/></param>
        /// <param name="extraPoints">Random sites added inside the polygon, on top of its vertices</param>
        /// <param name="subshatterSteps">How many more times every piece is broken again</param>
        /// <returns>The number of pieces; 0 if the polygon is degenerate or concave</returns>
        public int Shatter(List<float> poly, int mode, int extraPoints, int subshatterSteps)
        {
            Pieces.Clear();
            int n = poly.Count / 2;
            if (n < 3) return 0;
            float area = SignedArea(poly, 0, n);
            if (area == 0f) return 0;
            _src.Clear();
            if (area > 0f)
            {
                for (int i = 0; i < poly.Count; i++) _src.Add(poly[i]);
            }
            else
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    _src.Add(poly[2 * i]);
                    _src.Add(poly[2 * i + 1]);
                }
            }
            float minX = _src[0], maxX = _src[0], minY = _src[1], maxY = _src[1];
            for (int i = 1; i < n; i++)
            {
                if (_src[2 * i] < minX) minX = _src[2 * i];
                if (_src[2 * i] > maxX) maxX = _src[2 * i];
                if (_src[2 * i + 1] < minY) minY = _src[2 * i + 1];
                if (_src[2 * i + 1] > maxY) maxY = _src[2 * i + 1];
            }
            _scale = maxX - minX;
            if (maxY - minY > _scale) _scale = maxY - minY;
            if (_scale <= 0f) return 0;
            if (!IsConvex(_src, 0, n)) return 0;

            _lvlA.Clear();
            ShatterOne(_src, 0, n, mode, extraPoints, _lvlA);
            for (int s = 0; s < subshatterSteps; s++)
            {
                _lvlB.Clear();
                for (int k = 0; k < _lvlA.Count; k++)
                {
                    int st = _lvlA.Starts[k];          // (read into locals first: a list element straight into a call with a list is not translated)
                    int ct = _lvlA.Counts[k];
                    ShatterOne(_lvlA.Points, st, ct, mode, extraPoints, _lvlB);
                }
                _lvlA.CopyFrom(_lvlB);
            }
            for (int k = 0; k < _lvlA.Count; k++)
            {
                int st = _lvlA.Starts[k];
                int ct = _lvlA.Counts[k];
                SplitConvex(_lvlA.Points, st, ct, Pieces);
            }
            return Pieces.Count;
        }

        // ---- one polygon into pieces -----------------------------------------------------------

        private void ShatterOne(List<float> src, int start, int count, int mode, int extra, PolySet dst)
        {
            int before = dst.Count;
            float minX = src[2 * start], maxX = minX, minY = src[2 * start + 1], maxY = minY;
            float cx = 0f, cy = 0f;
            _sites.Clear();
            for (int k = 0; k < count; k++)
            {
                float x = src[2 * (start + k)];
                float y = src[2 * (start + k) + 1];
                _sites.Add(x);
                _sites.Add(y);
                cx += x;
                cy += y;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            if (mode == Triangle && count == 3)         // a triangle is one triangle: add its centre, as the original does
            {
                _sites.Add(cx / count);
                _sites.Add(cy / count);
            }
            for (int e = 0; e < extra; e++)
            {
                for (int tries = 0; tries < 16; tries++)
                {
                    float x = minX + Next01() * (maxX - minX);
                    float y = minY + Next01() * (maxY - minY);
                    if (InsideConvex(src, start, count, x, y))
                    {
                        _sites.Add(x);
                        _sites.Add(y);
                        break;
                    }
                }
            }
            if (mode == Voronoi) Cells(src, start, count, dst);
            else Triangulate(src, start, count, dst);
            if (dst.Count == before) AddPolygon(dst, src, start, count);     // nothing came out: the piece stays whole
        }

        // ---- Voronoi: the polygon cut by the half-plane of every other site -------------------

        private void Cells(List<float> src, int start, int count, PolySet dst)
        {
            int ns = _sites.Count / 2;
            float total = SignedArea(src, start, count);
            float eps = _scale * 1e-6f;
            float eps2 = eps * eps;
            for (int i = 0; i < ns; i++)
            {
                float sx = _sites[2 * i];
                float sy = _sites[2 * i + 1];
                bool duplicate = false;
                for (int j = 0; j < i; j++)
                {
                    float ddx = _sites[2 * j] - sx;
                    float ddy = _sites[2 * j + 1] - sy;
                    if (ddx * ddx + ddy * ddy < eps2) duplicate = true;   // the site before it owns the cell
                }
                if (duplicate) continue;

                _bufA.Clear();
                for (int k = 0; k < count; k++)
                {
                    _bufA.Add(src[2 * (start + k)]);
                    _bufA.Add(src[2 * (start + k) + 1]);
                }
                int cnt = count;
                bool inA = true;
                for (int j = 0; j < ns && cnt >= 3; j++)
                {
                    if (j == i) continue;
                    float tx = _sites[2 * j];
                    float ty = _sites[2 * j + 1];
                    float dx = tx - sx;
                    float dy = ty - sy;
                    if (dx * dx + dy * dy < eps2) continue;               // the same place as this site
                    float c = dx * (sx + tx) * 0.5f + dy * (sy + ty) * 0.5f;   // nearer to this site: n . p <= n . midpoint
                    if (inA) cnt = ClipHalfPlane(_bufA, 0, cnt, dx, dy, c, _bufB);
                    else cnt = ClipHalfPlane(_bufB, 0, cnt, dx, dy, c, _bufA);
                    inA = !inA;
                }
                if (cnt < 3) continue;
                if (inA)
                {
                    if (SignedArea(_bufA, 0, cnt) > total * 1e-6f) AddPolygon(dst, _bufA, 0, cnt);
                }
                else
                {
                    if (SignedArea(_bufB, 0, cnt) > total * 1e-6f) AddPolygon(dst, _bufB, 0, cnt);
                }
            }
        }

        /// <summary>
        /// Clips the polygon to the points with nx * x + ny * y &lt;= c, into dst (cleared first). Returns the number of points.
        /// </summary>
        private int ClipHalfPlane(List<float> src, int start, int count, float nx, float ny, float c, List<float> dst)
        {
            dst.Clear();
            for (int i = 0; i < count; i++)
            {
                int prev = (i + count - 1) % count;
                float cxp = src[2 * (start + i)];
                float cyp = src[2 * (start + i) + 1];
                float pxp = src[2 * (start + prev)];
                float pyp = src[2 * (start + prev) + 1];
                float dcur = nx * cxp + ny * cyp - c;
                float dprev = nx * pxp + ny * pyp - c;
                if (dcur <= 0f)
                {
                    if (dprev > 0f)
                    {
                        float t = dprev / (dprev - dcur);
                        dst.Add(pxp + t * (cxp - pxp));
                        dst.Add(pyp + t * (cyp - pyp));
                    }
                    dst.Add(cxp);
                    dst.Add(cyp);
                }
                else if (dprev <= 0f)
                {
                    float t = dprev / (dprev - dcur);
                    dst.Add(pxp + t * (cxp - pxp));
                    dst.Add(pyp + t * (cyp - pyp));
                }
            }
            return dst.Count / 2;
        }

        // ---- Delaunay: a fan of the polygon, the other sites inserted (a site on an edge splits it), then flips until Delaunay -

        private void Triangulate(List<float> src, int start, int count, PolySet dst)
        {
            int ns = _sites.Count / 2;
            float tiny = _scale * _scale * 1e-6f;
            _ta.Clear();
            _tb.Clear();
            _tc.Clear();
            for (int i = 1; i < count - 1; i++)
            {
                _ta.Add(0);
                _tb.Add(i);
                _tc.Add(i + 1);
            }
            for (int p = count; p < ns; p++)
            {
                float px = _sites[2 * p];
                float py = _sites[2 * p + 1];
                // the triangle that holds the point best: the one whose nearest edge is farthest from it
                int hit = -1;
                float bestMin = -1e30f;
                int edge = 0;
                for (int t = 0; t < _ta.Count; t++)
                {
                    float c1 = Cross(_ta[t], _tb[t], px, py);
                    float c2 = Cross(_tb[t], _tc[t], px, py);
                    float c3 = Cross(_tc[t], _ta[t], px, py);
                    float m = c1;
                    int e = 0;
                    if (c2 < m) { m = c2; e = 1; }
                    if (c3 < m) { m = c3; e = 2; }
                    if (m > bestMin) { bestMin = m; hit = t; edge = e; }
                }
                if (hit < 0 || bestMin < -tiny) continue;                 // not inside the polygon
                int a = _ta[hit], b = _tb[hit], c = _tc[hit];
                if (bestMin > tiny)                                         // inside: three triangles
                {
                    _tc[hit] = p;
                    _ta.Add(b); _tb.Add(c); _tc.Add(p);
                    _ta.Add(c); _tb.Add(a); _tc.Add(p);
                    continue;
                }
                // on an edge u -> v of the triangle (u, v, w): split it, and the triangle on the other side of the edge too
                int u = a, v = b, w = c;
                if (edge == 1) { u = b; v = c; w = a; }
                else if (edge == 2) { u = c; v = a; w = b; }
                int other = -1, x = -1;
                for (int t = 0; t < _ta.Count && other < 0; t++)
                {
                    if (t == hit) continue;
                    int ja = _ta[t], jb = _tb[t], jc = _tc[t];
                    if (ja == v && jb == u) { other = t; x = jc; }
                    else if (jb == v && jc == u) { other = t; x = ja; }
                    else if (jc == v && ja == u) { other = t; x = jb; }
                }
                _ta[hit] = u; _tb[hit] = p; _tc[hit] = w;
                _ta.Add(p); _tb.Add(v); _tc.Add(w);
                if (other >= 0)
                {
                    _ta[other] = v; _tb[other] = p; _tc[other] = x;
                    _ta.Add(p); _tb.Add(u); _tc.Add(x);
                }
            }
            for (int pass = 0; pass < 200; pass++)
            {
                bool flipped = false;
                for (int i = 0; i < _ta.Count && !flipped; i++)
                    for (int j = i + 1; j < _ta.Count && !flipped; j++)
                        if (TryFlip(i, j)) flipped = true;
                if (!flipped) break;
            }
            for (int t = 0; t < _ta.Count; t++)
            {
                _bufA.Clear();
                _bufA.Add(_sites[2 * _ta[t]]); _bufA.Add(_sites[2 * _ta[t] + 1]);
                _bufA.Add(_sites[2 * _tb[t]]); _bufA.Add(_sites[2 * _tb[t] + 1]);
                _bufA.Add(_sites[2 * _tc[t]]); _bufA.Add(_sites[2 * _tc[t] + 1]);
                AddPolygon(dst, _bufA, 0, 3);
            }
        }

        // twice the signed area of (site a, site b, point p): positive when p is to the left of a -> b
        private float Cross(int a, int b, float px, float py)
        {
            float ax = _sites[2 * a], ay = _sites[2 * a + 1];
            float bx = _sites[2 * b], by = _sites[2 * b + 1];
            return (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        }

        // the same, in double: the flip decisions depend on small differences between large numbers, so they are made with room to spare
        private double CrossSites(int a, int b, int c)
        {
            double ax = _sites[2 * a], ay = _sites[2 * a + 1];
            double bx = _sites[2 * b], by = _sites[2 * b + 1];
            double cx = _sites[2 * c], cy = _sites[2 * c + 1];
            return (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        }

        // positive when site d is inside the circle through a, b, c (counter-clockwise)
        private double InCircle(int a, int b, int c, int d)
        {
            double dx = _sites[2 * d], dy = _sites[2 * d + 1];
            double adx = _sites[2 * a] - dx, ady = _sites[2 * a + 1] - dy;
            double bdx = _sites[2 * b] - dx, bdy = _sites[2 * b + 1] - dy;
            double cdx = _sites[2 * c] - dx, cdy = _sites[2 * c + 1] - dy;
            double ad = adx * adx + ady * ady;
            double bd = bdx * bdx + bdy * bdy;
            double cd = cdx * cdx + cdy * cdy;
            return adx * (bdy * cd - bd * cdy) - ady * (bdx * cd - bd * cdx) + ad * (bdx * cdy - bdy * cdx);
        }

        /// <summary>If triangles i and j share an edge and the far vertex of one is inside the other's circle, swaps the edge. True if it did.</summary>
        private bool TryFlip(int i, int j)
        {
            int a = _ta[i], b = _tb[i], c = _tc[i];
            int p = _ta[j], q = _tb[j], r = _tc[j];
            int shared = 0;
            int s1 = -1, s2 = -1, oi = -1, oj = -1;
            if (a == p || a == q || a == r) { shared++; s1 = a; } else oi = a;
            if (b == p || b == q || b == r) { shared++; if (s1 < 0) s1 = b; else s2 = b; } else oi = b;
            if (c == p || c == q || c == r) { shared++; if (s1 < 0) s1 = c; else s2 = c; } else oi = c;
            if (shared != 2 || oi < 0) return false;
            if (p != s1 && p != s2) oj = p;
            else if (q != s1 && q != s2) oj = q;
            else oj = r;
            double l2 = (double)_scale * (double)_scale;
            if (InCircle(a, b, c, oj) <= 1e-12 * l2 * l2) return false;      // Delaunay already (or cocircular)
            double minArea = l2 * 1e-12;
            double k1 = CrossSites(oi, s1, oj), k2 = CrossSites(s1, oj, s2), k3 = CrossSites(oj, s2, oi), k4 = CrossSites(s2, oi, s1);
            if (k1 < 0.0 && k2 < 0.0 && k3 < 0.0 && k4 < 0.0)
            {
                int tmp = s1; s1 = s2; s2 = tmp;
                k1 = -k1; k2 = -k2; k3 = -k3; k4 = -k4;
            }
            if (k1 <= minArea || k2 <= minArea || k3 <= minArea || k4 <= minArea) return false;   // not a convex quadrilateral: the edge cannot flip
            _ta[i] = oi; _tb[i] = s1; _tc[i] = oj;
            _ta[j] = oi; _tb[j] = oj; _tc[j] = s2;
            return true;
        }

        // ---- polygon helpers -------------------------------------------------------------------

        /// <summary>Shoelace area: positive for a counter-clockwise polygon.</summary>
        public static float SignedArea(List<float> pts, int start, int count)
        {
            float a = 0f;
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                a += pts[2 * (start + i)] * pts[2 * (start + j) + 1] - pts[2 * (start + j)] * pts[2 * (start + i) + 1];
            }
            return a * 0.5f;
        }

        private bool IsConvex(List<float> pts, int start, int count)
        {
            float tol = -_scale * _scale * 1e-6f;
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                int k = (i + 2) % count;
                float ex = pts[2 * (start + j)] - pts[2 * (start + i)];
                float ey = pts[2 * (start + j) + 1] - pts[2 * (start + i) + 1];
                float fx = pts[2 * (start + k)] - pts[2 * (start + j)];
                float fy = pts[2 * (start + k) + 1] - pts[2 * (start + j) + 1];
                if (ex * fy - ey * fx < tol) return false;
            }
            return true;
        }

        private bool InsideConvex(List<float> pts, int start, int count, float x, float y)
        {
            float margin = _scale * _scale * 1e-4f;
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                float ax = pts[2 * (start + i)], ay = pts[2 * (start + i) + 1];
                float bx = pts[2 * (start + j)], by = pts[2 * (start + j) + 1];
                if ((bx - ax) * (y - ay) - (by - ay) * (x - ax) < margin) return false;
            }
            return true;
        }

        /// <summary>Adds a polygon to dst, welding points that sit on each other. Dropped if fewer than three points or no area remain.</summary>
        private void AddPolygon(PolySet dst, List<float> pts, int start, int count)
        {
            float weld = _scale * 1e-5f;
            float weld2 = weld * weld;
            dst.Begin();
            for (int i = 0; i < count; i++)
            {
                float x = pts[2 * (start + i)];
                float y = pts[2 * (start + i) + 1];
                int have = dst.Counts[dst.Counts.Count - 1];
                if (have > 0)
                {
                    float dx = x - dst.X(dst.Count - 1, have - 1);
                    float dy = y - dst.Y(dst.Count - 1, have - 1);
                    if (dx * dx + dy * dy < weld2) continue;
                }
                dst.AddPoint(x, y);
            }
            int n = dst.Counts[dst.Counts.Count - 1];
            if (n > 1)
            {
                float dx = dst.X(dst.Count - 1, 0) - dst.X(dst.Count - 1, n - 1);
                float dy = dst.Y(dst.Count - 1, 0) - dst.Y(dst.Count - 1, n - 1);
                if (dx * dx + dy * dy < weld2)
                {
                    dst.Points.RemoveAt(dst.Points.Count - 1);
                    dst.Points.RemoveAt(dst.Points.Count - 1);
                    dst.Counts[dst.Counts.Count - 1] = n - 1;
                    n--;
                }
            }
            if (n < 3 || SignedArea(dst.Points, dst.Starts[dst.Count - 1], n) <= _scale * _scale * 1e-9f) dst.DropLast();
        }

        /// <summary>Adds a convex polygon to dst, as several if it has more than MaxVertices points: a fan from its first vertex.</summary>
        private void SplitConvex(List<float> src, int start, int count, PolySet dst)
        {
            if (count <= MaxPoints)
            {
                AddPolygon(dst, src, start, count);
                return;
            }
            int first = 1;
            while (first < count - 1)
            {
                int n = count - first;
                if (n > MaxPoints - 1) n = MaxPoints - 1;
                _bufA.Clear();
                _bufA.Add(src[2 * start]);
                _bufA.Add(src[2 * start + 1]);
                for (int k = 0; k < n; k++)
                {
                    _bufA.Add(src[2 * (start + first + k)]);
                    _bufA.Add(src[2 * (start + first + k) + 1]);
                }
                AddPolygon(dst, _bufA, 0, n + 1);
                first += n - 1;
            }
        }
    }
}
