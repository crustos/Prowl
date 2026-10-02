// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace Prowl.Runtime.Physics2D;

/// <summary>
/// Line-segment outlines of 2D collider shapes, for editor gizmos. No engine types: segments are appended to a flat
/// <c>List&lt;float&gt;</c> as (x1, y1, x2, y2) quads, so the geometry can be checked against real Box2D shapes.
/// <para/>
/// Written in the Crust C# subset (arrays and counts instead of Span, no tuples, no nested types, no local functions, no
/// optional arguments), so CCSharp can translate it to C unchanged. See tools/ccsharp.
/// <para/>
/// Gizmos run in the editor even when nothing is playing, so there is no native world to ask. These outlines therefore
/// have to be computed from the same resolved dimensions the physics uses, and the polygon hull has to agree with
/// Box2D's own about which shapes are valid at all (<see cref="ComputeHull"/>).
/// </summary>
internal static class Collider2DOutline
{
    public const int CircleSegments = 32;
    public const int ArcSegmentsPerHalfTurn = 12;

    /// <summary>Box2D's linear slop (0.005 m): points closer than this are welded, and vertices this close to a hull edge are dropped.</summary>
    public const float LinearSlop = 0.005f;

    public const int MaxPolygonPoints = 8;

    public static void AddSegment(List<float> segments, float x1, float y1, float x2, float y2)
    {
        segments.Add(x1); segments.Add(y1); segments.Add(x2); segments.Add(y2);
    }

    public static void Circle(List<float> segments, float cx, float cy, float radius)
    {
        Arc(segments, cx, cy, radius, 0f, MathF.PI * 2f, CircleSegments);
    }

    /// <summary>An arc of <paramref name="radius"/> about (cx, cy) from <paramref name="start"/> sweeping <paramref name="sweep"/> radians.</summary>
    public static void Arc(List<float> segments, float cx, float cy, float radius, float start, float sweep, int count)
    {
        float px = cx + radius * MathF.Cos(start), py = cy + radius * MathF.Sin(start);
        for (int i = 1; i <= count; i++)
        {
            float a = start + sweep * i / count;
            float x = cx + radius * MathF.Cos(a), y = cy + radius * MathF.Sin(a);
            AddSegment(segments, px, py, x, y);
            px = x; py = y;
        }
    }

    /// <summary>
    /// A box of half extents (hw, hh) turned by <paramref name="angle"/>. With a corner radius the outline is that box grown
    /// by the radius, with rounded corners: exactly what a Box2D polygon with a radius occupies.
    /// </summary>
    public static void Box(List<float> segments, float cx, float cy, float hw, float hh, float angle, float cornerRadius)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);

        if (cornerRadius <= 1e-6f)
        {
            float ax, ay, bx, by, dx, dy, ex, ey;
            BoxPoint(cx, cy, c, s, -hw, -hh, out ax, out ay);
            BoxPoint(cx, cy, c, s, hw, -hh, out bx, out by);
            BoxPoint(cx, cy, c, s, hw, hh, out dx, out dy);
            BoxPoint(cx, cy, c, s, -hw, hh, out ex, out ey);
            AddSegment(segments, ax, ay, bx, by); AddSegment(segments, bx, by, dx, dy);
            AddSegment(segments, dx, dy, ex, ey); AddSegment(segments, ex, ey, ax, ay);
            return;
        }

        float r = cornerRadius;
        int perCorner = ArcSegmentsPerHalfTurn / 2;

        // Four corner arcs about the un-grown corners, joined by the straight edges between them (offset outward by r).
        float[] cornerX = new float[4];
        float[] cornerY = new float[4];
        float[] cornerStart = new float[4];
        cornerX[0] = hw; cornerY[0] = hh; cornerStart[0] = 0f;
        cornerX[1] = -hw; cornerY[1] = hh; cornerStart[1] = MathF.PI / 2;
        cornerX[2] = -hw; cornerY[2] = -hh; cornerStart[2] = MathF.PI;
        cornerX[3] = hw; cornerY[3] = -hh; cornerStart[3] = MathF.PI * 3 / 2;

        for (int i = 0; i < 4; i++)
        {
            // Element reads are taken into locals before a call that also passes a list: cpprust leaves `v[i]` unlowered there.
            int next = (i + 1) % 4;
            float kcx = cornerX[i], kcy = cornerY[i], ncx = cornerX[next], ncy = cornerY[next];
            float startA = cornerStart[i] + angle;
            float nextA = cornerStart[next] + angle;

            float kx, ky;
            BoxPoint(cx, cy, c, s, kcx, kcy, out kx, out ky);
            Arc(segments, kx, ky, r, startA, MathF.PI / 2, perCorner);

            // straight edge from the end of this arc to the start of the next
            float nx, ny;
            BoxPoint(cx, cy, c, s, ncx, ncy, out nx, out ny);
            float a0 = startA + MathF.PI / 2;
            float a1 = nextA;
            AddSegment(segments, kx + r * MathF.Cos(a0), ky + r * MathF.Sin(a0), nx + r * MathF.Cos(a1), ny + r * MathF.Sin(a1));
        }
    }

    private static void BoxPoint(float cx, float cy, float c, float s, float lx, float ly, out float x, out float y)
    {
        x = cx + c * lx - s * ly;
        y = cy + s * lx + c * ly;
    }

    /// <summary>A capsule: two semicircular caps of <paramref name="radius"/> about (x1,y1) and (x2,y2), joined by straight sides.</summary>
    public static void Capsule(List<float> segments, float x1, float y1, float x2, float y2, float radius)
    {
        float axis = MathF.Atan2(y2 - y1, x2 - x1);
        // cap about p2 sweeps from the right of the axis round the far end; cap about p1 does the near end
        Arc(segments, x2, y2, radius, axis - MathF.PI / 2, MathF.PI, ArcSegmentsPerHalfTurn);
        float nx = -MathF.Sin(axis) * radius, ny = MathF.Cos(axis) * radius;
        AddSegment(segments, x2 + nx, y2 + ny, x1 + nx, y1 + ny);
        Arc(segments, x1, y1, radius, axis + MathF.PI / 2, MathF.PI, ArcSegmentsPerHalfTurn);
        AddSegment(segments, x1 - nx, y1 - ny, x2 - nx, y2 - ny);
    }

    /// <summary>A chain of points; <paramref name="loop"/> closes it.</summary>
    public static void Polyline(List<float> segments, float[] xy, int count, bool loop)
    {
        for (int i = 0; i + 1 < count; i++)
        {
            float x1 = xy[2 * i], y1 = xy[2 * i + 1], x2 = xy[2 * i + 2], y2 = xy[2 * i + 3];
            AddSegment(segments, x1, y1, x2, y2);
        }
        if (loop && count >= 3)
        {
            float lx = xy[2 * (count - 1)], ly = xy[2 * (count - 1) + 1], fx = xy[0], fy = xy[1];
            AddSegment(segments, lx, ly, fx, fy);
        }
    }

    /// <summary>list[index] = value, appending when index is the next free slot. The scratch lists are filled in order.</summary>
    private static void Put(List<HullPoint2D> list, int index, HullPoint2D value)
    {
        if (index < list.Count) list[index] = value;
        else list.Add(value);
    }

    private static float Cross(float ax, float ay, float bx, float by) { return ax * by - ay * bx; }

    private static void Normalize(float x, float y, out float nx, out float ny)
    {
        float length = MathF.Sqrt(x * x + y * y);
        if (length < float.Epsilon) { nx = 0f; ny = 0f; return; }
        float inv = 1f / length;
        nx = x * inv; ny = y * inv;
    }

    private static float DistSq(HullPoint2D a, HullPoint2D b) { float dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }

    /// <summary>Port of Box2D's b2RecurseHull: the points strictly right of p1 -> p2 that form the hull between them.</summary>
    private static int Recurse(HullPoint2D p1, HullPoint2D p2, List<HullPoint2D> ps, int count, List<HullPoint2D> output)
    {
        if (count == 0) return 0;

        float ex, ey;
        Normalize(p2.X - p1.X, p2.Y - p1.Y, out ex, out ey);

        // discard points left of e and find the point furthest to the right of e
        List<HullPoint2D> right = new List<HullPoint2D>();
        int rightCount = 0;
        int bestIndex = 0;
        float bestDistance = Cross(ps[0].X - p1.X, ps[0].Y - p1.Y, ex, ey);
        if (bestDistance > 0f) { { HullPoint2D _h0 = ps[0]; Put(right, rightCount, _h0); } rightCount++; }
        for (int i = 1; i < count; i++)
        {
            float distance = Cross(ps[i].X - p1.X, ps[i].Y - p1.Y, ex, ey);
            if (distance > bestDistance) { bestIndex = i; bestDistance = distance; }
            if (distance > 0f) { { HullPoint2D _h1 = ps[i]; Put(right, rightCount, _h1); } rightCount++; }
        }

        if (bestDistance < 2f * LinearSlop) return 0;

        HullPoint2D bestPoint = ps[bestIndex];
        List<HullPoint2D> hull1 = new List<HullPoint2D>();
        List<HullPoint2D> hull2 = new List<HullPoint2D>();
        int n1 = Recurse(p1, bestPoint, right, rightCount, hull1);
        int n2 = Recurse(bestPoint, p2, right, rightCount, hull2);

        int n = 0;
        for (int i = 0; i < n1; i++) { { HullPoint2D _h2 = hull1[i]; Put(output, n, _h2); } n++; }
        Put(output, n, bestPoint); n++;
        for (int i = 0; i < n2; i++) { { HullPoint2D _h3 = hull2[i]; Put(output, n, _h3); } n++; }
        return n;
    }

    /// <summary>
    /// The convex hull Box2D would build from up to <see cref="MaxPolygonPoints"/> points, in counter-clockwise order, as indices
    /// into <paramref name="xy"/>. Returns the number of vertices, or 0 when Box2D would reject the set.
    /// <para/>
    /// This is a line-for-line port of <c>b2ComputeHull</c> (quickhull with aggressive welding): points within 4x the linear slop
    /// are welded (the first of them survives), points within 2x the slop of a hull line are dropped, and collinear vertices are
    /// merged. It is ported rather than approximated because the editor draws what this returns, and a gizmo that disagrees with
    /// the physics about whether a shape is valid is worse than none. <c>TestHullParity</c> checks it against the real library.
    /// </summary>
    public static int ComputeHull(float[] xy, int count, int[] hull)
    {
        if (count < 3 || count > MaxPolygonPoints) return 0;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = -float.MaxValue, maxY = -float.MaxValue;

        // aggressive point welding; the first point always remains
        List<HullPoint2D> ps = new List<HullPoint2D>();
        int n = 0;
        float linearSlop = LinearSlop;
        float tolSqr = 16f * linearSlop * linearSlop;
        for (int i = 0; i < count; i++)
        {
            HullPoint2D vi = new HullPoint2D();
            vi.X = xy[2 * i]; vi.Y = xy[2 * i + 1]; vi.Index = i;
            minX = MathF.Min(minX, vi.X); minY = MathF.Min(minY, vi.Y);
            maxX = MathF.Max(maxX, vi.X); maxY = MathF.Max(maxY, vi.Y);

            bool unique = true;
            for (int j = 0; j < i; j++)
            {
                HullPoint2D vj = new HullPoint2D();
                vj.X = xy[2 * j]; vj.Y = xy[2 * j + 1];
                if (DistSq(vi, vj) < tolSqr) { unique = false; break; }
            }
            if (unique) { Put(ps, n, vi); n++; }
        }
        if (n < 3) return 0; // all points very close together

        // find an extreme point as the first point on the hull
        HullPoint2D c = new HullPoint2D();
        c.X = (minX + maxX) * 0.5f; c.Y = (minY + maxY) * 0.5f;
        int f1 = 0;
        float dsq1 = DistSq(c, ps[f1]);
        for (int i = 1; i < n; i++)
        {
            float dsq = DistSq(c, ps[i]);
            if (dsq > dsq1) { f1 = i; dsq1 = dsq; }
        }
        HullPoint2D p1 = ps[f1];
        { HullPoint2D _h4 = ps[n - 1]; Put(ps, f1, _h4); }
        n--;

        int f2 = 0;
        float dsq2 = DistSq(p1, ps[f2]);
        for (int i = 1; i < n; i++)
        {
            float dsq = DistSq(p1, ps[i]);
            if (dsq > dsq2) { f2 = i; dsq2 = dsq; }
        }
        HullPoint2D p2 = ps[f2];
        { HullPoint2D _h5 = ps[n - 1]; Put(ps, f2, _h5); }
        n--;

        // split the remaining points into those right and left of p1 -> p2
        List<HullPoint2D> rightPoints = new List<HullPoint2D>();
        List<HullPoint2D> leftPoints = new List<HullPoint2D>();
        int rightCount = 0, leftCount = 0;
        float ex, ey;
        Normalize(p2.X - p1.X, p2.Y - p1.Y, out ex, out ey);
        for (int i = 0; i < n; i++)
        {
            float d = Cross(ps[i].X - p1.X, ps[i].Y - p1.Y, ex, ey);
            if (d >= 2f * linearSlop) { { HullPoint2D _h6 = ps[i]; Put(rightPoints, rightCount, _h6); } rightCount++; }       // slop skips points very close to the line
            else if (d <= -2f * linearSlop) { { HullPoint2D _h7 = ps[i]; Put(leftPoints, leftCount, _h7); } leftCount++; }
        }

        List<HullPoint2D> hull1 = new List<HullPoint2D>();
        List<HullPoint2D> hull2 = new List<HullPoint2D>();
        int n1 = Recurse(p1, p2, rightPoints, rightCount, hull1);
        int n2 = Recurse(p2, p1, leftPoints, leftCount, hull2);
        if (n1 == 0 && n2 == 0) return 0; // all points collinear

        // stitch together, preserving counter-clockwise winding
        List<HullPoint2D> pts = new List<HullPoint2D>();
        int count2 = 0;
        Put(pts, count2, p1); count2++;
        for (int i = 0; i < n1; i++) { { HullPoint2D _h8 = hull1[i]; Put(pts, count2, _h8); } count2++; }
        Put(pts, count2, p2); count2++;
        for (int i = 0; i < n2; i++) { { HullPoint2D _h9 = hull2[i]; Put(pts, count2, _h9); } count2++; }

        // merge collinear
        bool searching = true;
        while (searching && count2 > 2)
        {
            searching = false;
            for (int i = 0; i < count2; i++)
            {
                int i2 = (i + 1) % count2, i3 = (i + 2) % count2;
                HullPoint2D s1 = pts[i], s2 = pts[i2], s3 = pts[i3];

                float rx, ry;
                Normalize(s3.X - s1.X, s3.Y - s1.Y, out rx, out ry);
                float distance = Cross(s2.X - s1.X, s2.Y - s1.Y, rx, ry);
                if (distance <= 2f * linearSlop)
                {
                    for (int j = i2; j < count2 - 1; j++) { HullPoint2D _h10 = pts[j + 1]; Put(pts, j, _h10); }
                    count2--;
                    searching = true;
                    break;
                }
            }
        }

        if (count2 < 3) return 0;
        for (int i = 0; i < count2; i++) hull[i] = pts[i].Index;
        return count2;
    }

    /// <summary>Outline of the polygon Box2D would build from these (already placed) points. Returns false if it would reject them.</summary>
    public static bool Polygon(List<float> segments, float[] xy, int count)
    {
        int[] hull = new int[MaxPolygonPoints];
        int n = ComputeHull(xy, count, hull);
        if (n == 0) return false;
        for (int i = 0; i < n; i++)
        {
            int a = hull[i], b = hull[(i + 1) % n];
            float ax = xy[2 * a], ay = xy[2 * a + 1], bx = xy[2 * b], by = xy[2 * b + 1];
            AddSegment(segments, ax, ay, bx, by);
        }
        return true;
    }
}

/// <summary>A hull working point: its position, and where it came from in the caller's array.</summary>
internal struct HullPoint2D
{
    public float X, Y;
    public int Index;
}
