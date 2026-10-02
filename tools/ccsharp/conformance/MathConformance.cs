// Runs the engine-independent 2D math (Pose2D, Collider2DGeometry, Collider2DOutline) and prints integers only.
//
// The same file is run twice by `python3 build.py ccsharp-run`: once on real .NET, and once translated by CCSharp to C, built with
// gcc and executed natively. The two outputs must be identical. That is the proof that the translated C does what the C# does.
//
// Integers only, because a float cannot be printed in the Crust subset. A float result is compared by scaling it to an int.
using System;
using System.Collections.Generic;
using Prowl.Runtime.Physics2D;

class MathConformance
{
    static int seed = 12345;

    static int Next()
    {
        seed = seed * 1103515245 + 12345;
        return (seed >> 16) & 0x7fff;
    }

    static float Rand()
    {
        return (Next() / 16383.5f) - 1f;
    }

    static int Q(float v)
    {
        return (int)(v * 1000f);
    }

    public static int Main()
    {
        // ---- hull: the same families the Box2D parity test uses -------------------------------------------------------------
        float[] xy = new float[16];
        int[] hull = new int[8];
        int checksum = 0;
        for (int trial = 0; trial < 600; trial++)
        {
            int family = trial % 6;
            int n = 3 + (Next() % 6);
            for (int i = 0; i < n; i++)
            {
                if (family == 0) { xy[2 * i] = Rand() * 2f; xy[2 * i + 1] = Rand() * 2f; }
                else if (family == 1) { float t = Rand() * 2f; xy[2 * i] = 0.3f + 0.7f * t; xy[2 * i + 1] = -0.2f + 0.4f * t; }
                else if (family == 2) { xy[2 * i] = (Next() % 3) * 0.001f; xy[2 * i + 1] = (Next() % 3) * 0.001f; }
                else if (family == 3) { float a = i * 6.2831853f / n; xy[2 * i] = 1.5f * MathF.Cos(a); xy[2 * i + 1] = 1.5f * MathF.Sin(a); }
                else if (family == 4) { float t = Rand() * 2f; xy[2 * i] = t; xy[2 * i + 1] = 0.5f * t + Rand() * 0.003f; }
                else { xy[2 * i] = Rand() * 0.004f; xy[2 * i + 1] = Rand() * 0.004f; }
            }
            int count = Collider2DOutline.ComputeHull(xy, n, hull);
            checksum = checksum * 31 + count;
            for (int i = 0; i < count; i++) checksum = checksum * 31 + hull[i];
            if (trial % 60 == 0)
            {
                Console.WriteLine("hull " + trial + " family " + family + " n " + n + " -> " + count);
            }
        }
        Console.WriteLine("hull checksum " + checksum);

        // ---- outlines: segment counts and quantised geometry -------------------------------------------------------------------
        List<float> segments = new List<float>();
        Collider2DOutline.Circle(segments, 3f, 4f, 2f);
        Console.WriteLine("circle " + (segments.Count / 4) + " " + Q(segments[0]) + " " + Q(segments[1]));

        segments.Clear();
        Collider2DOutline.Box(segments, 10f, 5f, 2f, 1f, 1.5707963f, 0f);
        Console.WriteLine("box " + (segments.Count / 4) + " " + Q(segments[0]) + " " + Q(segments[1]) + " " + Q(segments[2]) + " " + Q(segments[3]));

        segments.Clear();
        Collider2DOutline.Box(segments, 10f, 5f, 2f, 1f, 0.6f, 0.5f);
        float sum = 0f;
        for (int i = 0; i < segments.Count; i++) sum = sum + segments[i];
        Console.WriteLine("rounded box " + (segments.Count / 4) + " " + Q(sum));

        segments.Clear();
        Collider2DOutline.Capsule(segments, 0f, -1f, 0f, 1f, 0.5f);
        sum = 0f;
        for (int i = 0; i < segments.Count; i++) sum = sum + segments[i];
        Console.WriteLine("capsule " + (segments.Count / 4) + " " + Q(sum));

        float[] pent = new float[10];
        for (int i = 0; i < 5; i++) { float a = i * 1.2566371f; pent[2 * i] = MathF.Cos(a); pent[2 * i + 1] = MathF.Sin(a); }
        segments.Clear();
        bool ok = Collider2DOutline.Polygon(segments, pent, 5);
        Console.WriteLine("pentagon " + ok + " " + (segments.Count / 4));

        float[] flat = new float[6];
        flat[0] = 0f; flat[1] = 0f; flat[2] = 1f; flat[3] = 0f; flat[4] = 2f; flat[5] = 0f;
        segments.Clear();
        Console.WriteLine("collinear " + Collider2DOutline.Polygon(segments, flat, 3));

        // ---- geometry and pose -----------------------------------------------------------------------------------------------
        float lx, ly, la;
        Collider2DGeometry.ToBodySpace(10f, 5f, 1.5707963f, 10f, 7f, 1.5707963f, out lx, out ly, out la);
        Console.WriteLine("to body space " + Q(lx) + " " + Q(ly) + " " + Q(la));
        float tx, ty;
        Collider2DGeometry.TransformPoint(1f, 0f, 2f, 1f, 1.5707963f, 1f, 1f, out tx, out ty);
        Console.WriteLine("transform point " + Q(tx) + " " + Q(ty));
        float x1, y1, x2, y2;
        Collider2DGeometry.CapsuleEnds(0f, 0f, 1.5707963f, 2f, false, out x1, out y1, out x2, out y2);
        Console.WriteLine("capsule ends " + Q(x1) + " " + Q(y1) + " " + Q(x2) + " " + Q(y2));

        BodyPose2D pose = new BodyPose2D();
        pose.Reset(0f, 0f, 0f, 0);
        for (int step = 1; step <= 20; step++) pose.Push(step, 0f, MathF.Cos(step * 1f), MathF.Sin(step * 1f), step);
        float sx, sy, sa;
        pose.Sample(0.5f, 20, out sx, out sy, out sa);
        Console.WriteLine("pose " + Q(pose.CurAngle) + " " + Q(sx) + " " + Q(sa));
        pose.Sample(0.5f, 21, out sx, out sy, out sa);
        Console.WriteLine("pose at rest " + Q(sx) + " " + Q(sa));
        Console.WriteLine("wrap " + Q(Angle2D.WrapPi(9.42477796f)) + " " + Q(Angle2D.Unwrap(-3f, 3f)));
        return 0;
    }
}
