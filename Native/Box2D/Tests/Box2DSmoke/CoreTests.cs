// Tests for Prowl.Runtime/Physics2D (the engine-independent simulation core), run against the real native library.

using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.Runtime.Physics2D;
using Prowl.Runtime.Physics2D.Native;

internal static unsafe partial class Program
{
    // ---- test doubles --------------------------------------------------------------------

    private sealed class TBody : IBody2DHost
    {
        public PhysicsSimulation2D Sim = null!;
        public BodyPose2D Pose;
        public int Moves;
        public bool Asleep;
        public int Index;
        public uint Handle;

        public void OnMoved(float x, float y, float cos, float sin, bool fellAsleep)
        {
            Pose.Push(x, y, cos, sin, Sim.StepIndex);
            Moves++;
            if (fellAsleep) Asleep = true;
        }
    }

    private sealed class TCollider : ICollider2DHost
    {
        public string Name = "";
        public int Index = -1;
        public override string ToString() => Name;
    }

    private sealed class Rec : IPhysics2DEvents
    {
        public PhysicsSimulation2D Sim = null!;
        public readonly List<string> Log = new();
        public readonly List<(string self, string other, Contact2D c)> Begins = new();
        public Action<string, ICollider2DHost, ICollider2DHost>? OnFirstBegin;
        private bool _firedHook;

        private void Add(string kind, ICollider2DHost s, ICollider2DHost o) => Log.Add($"{kind}:{s}>{o}@{Sim.StepIndex}");

        public void CollisionBegin(ICollider2DHost self, ICollider2DHost other, Contact2D contact)
        {
            Add("Begin", self, other);
            Begins.Add((self.ToString()!, other.ToString()!, contact));
            if (OnFirstBegin != null && !_firedHook) { _firedHook = true; OnFirstBegin("Begin", self, other); }
        }
        public void CollisionEnd(ICollider2DHost self, ICollider2DHost other) => Add("End", self, other);
        public void TriggerEnter(ICollider2DHost self, ICollider2DHost other) => Add("Enter", self, other);
        public void TriggerStay(ICollider2DHost self, ICollider2DHost other) => Add("Stay", self, other);
        public void TriggerExit(ICollider2DHost self, ICollider2DHost other) => Add("Exit", self, other);

        public int Count(string kind, string self, string other) => Log.Count(l => l.StartsWith($"{kind}:{self}>{other}@"));
        public int FirstStep(string kind, string self, string other)
        {
            string? l = Log.FirstOrDefault(x => x.StartsWith($"{kind}:{self}>{other}@"));
            return l == null ? -1 : int.Parse(l[(l.LastIndexOf('@') + 1)..]);
        }
        public int LastStep(string kind, string self, string other)
        {
            string? l = Log.LastOrDefault(x => x.StartsWith($"{kind}:{self}>{other}@"));
            return l == null ? -1 : int.Parse(l[(l.LastIndexOf('@') + 1)..]);
        }
    }

    private static (PhysicsSimulation2D sim, Rec rec) NewSim()
    {
        var rec = new Rec();
        var sim = new PhysicsSimulation2D(rec) { };
        rec.Sim = sim;
        sim.SetGravity(0, -10);
        return (sim, rec);
    }

    private static TCollider StaticBoxC(PhysicsSimulation2D sim, string name, float x, float y, float hw, float hh, int layer = 0, bool sensor = false)
    {
        var c = new TCollider { Name = name };
        c.Index = sim.RegisterCollider(c);
        uint b = PB2.BodyCreate(PB2BodyType.Static, x, y, 0, -1, 1, 0, 0, 0);
        PB2.ShapeCreateBox(b, c.Index, layer, hw, hh, 0, 0, 0, 0, 1f, 0.6f, 0f, sensor ? (uint)PB2ShapeFlags.Sensor : 0u);
        return c;
    }

    private static (TCollider col, TBody body) BallC(PhysicsSimulation2D sim, string name, float x, float y, float r, int layer = 0)
    {
        var body = new TBody { Sim = sim };
        body.Index = sim.RegisterBody(body);
        body.Handle = PB2.BodyCreate(PB2BodyType.Dynamic, x, y, 0, body.Index, 1f, 0, 0, 0);
        body.Pose.Reset(x, y, 0, sim.StepIndex);
        var c = new TCollider { Name = name };
        c.Index = sim.RegisterCollider(c);
        PB2.ShapeCreateCircle(body.Handle, c.Index, layer, 0, 0, r, 1f, 0.6f, 0f, 0);
        return (c, body);
    }

    private static void RunSteps(PhysicsSimulation2D sim, int n)
    {
        for (int i = 0; i < n; i++) sim.Step(Dt);
    }

    private static void CoreTests()
    {
        Console.WriteLine("\n== core (Prowl.Runtime/Physics2D) ==");
        TestAngleAndPose();
        TestRegistry();
        TestGeometry();
        TestOutlinePrimitives();
        TestHullParity();
        TestOwnership();
        TestCoreContacts();
        TestCoreTriggers();
        TestHandlerDestroysDuringDispatch();
        TestTriggerPairCleanup();
        TestCoreQueries();
        TestSettingsSurviveEviction();
    }

    // ---- pure math -----------------------------------------------------------------------

    private static void TestAngleAndPose()
    {
        Console.WriteLine("angle + pose interpolation (pure math)");
        Check(Near(Angle2D.WrapPi(3 * MathF.PI), MathF.PI, 1e-4f) && Near(Angle2D.WrapPi(-MathF.PI / 2), -MathF.PI / 2, 1e-6f),
              "WrapPi maps into (-pi, pi]");
        Check(Near(Angle2D.Unwrap(-3.0f, 3.0f), 2 * MathF.PI - 3.0f, 1e-4f), $"Unwrap picks the representative nearest the reference ({Angle2D.Unwrap(-3.0f, 3.0f):F3})");

        bool roundTrip = true;
        foreach (float a in new[] { 0f, 0.3f, -0.3f, 1.5f, -1.5f, 3.0f, -3.0f })
        {
            Angle2D.ToQuaternionZ(a, out float z, out float w);
            roundTrip &= Near(Angle2D.FromQuaternionZ(0, 0, z, w), a, 1e-5f);
        }
        Check(roundTrip, "Z quaternion <-> angle round trip");

        // A body spinning 1 rad/step passes +-pi repeatedly. The unwrapped angle must keep increasing, not jump by 2pi.
        var pose = default(BodyPose2D);
        pose.Reset(0, 0, 0, 0);
        float prev = 0; bool monotonic = true; float maxJump = 0;
        for (int step = 1; step <= 20; step++)
        {
            float raw = step * 1.0f;
            pose.Push(step, 0, MathF.Cos(raw), MathF.Sin(raw), step);
            monotonic &= pose.CurAngle > prev;
            maxJump = MathF.Max(maxJump, pose.CurAngle - prev);
            prev = pose.CurAngle;
        }
        Check(monotonic && Near(maxJump, 1f, 1e-3f) && Near(pose.CurAngle, 20f, 1e-2f), $"spinning body stays continuous across +-pi (angle {pose.CurAngle:F2} after 20 rad)");

        pose.Sample(0f, 20, out float x0, out _, out float a0);
        pose.Sample(0.5f, 20, out float xh, out _, out float ah);
        pose.Sample(1f, 20, out float x1, out _, out float a1);
        Check(Near(x0, 19f, 1e-4f) && Near(xh, 19.5f, 1e-4f) && Near(x1, 20f, 1e-4f) && Near(ah, 19.5f, 1e-2f),
              $"interpolates between the last two poses (x {x0:F1}/{xh:F1}/{x1:F1})");

        pose.Sample(0.5f, 21, out float xr, out _, out float ar);
        Check(Near(xr, 20f, 1e-4f) && Near(ar, pose.CurAngle, 1e-4f), "a body that did not move last step rests at its current pose (no endless lerp)");

        var p2 = default(BodyPose2D);
        p2.Push(5, 5, 1, 0, 3);
        p2.Sample(0.5f, 3, out float sx, out _, out _);
        Check(Near(sx, 5f, 1e-6f), "first pose snaps (no lerp from the origin)");
    }

    private static void TestRegistry()
    {
        Console.WriteLine("slot registry");
        var r = new SlotRegistry<string>();
        int a = r.Add("a"), b = r.Add("b");
        Check(a == 0 && b == 1 && r.Get(a) == "a" && r.Count == 2, "dense indices from zero");
        Check(r.Remove(a) && r.Get(a) == null && !r.Remove(a) && r.Count == 1, "remove frees the slot; double remove is a no-op");
        int c = r.Add("c");
        Check(c == a, "a freed slot is reused immediately outside dispatch");

        r.Quarantining = true;
        r.Remove(c);
        int d = r.Add("d");
        Check(d != c && d == 2, $"during dispatch a freed slot is NOT reused (got {d}, freed {c})");
        r.Quarantining = false;
        r.Flush();
        Check(r.Add("e") == c, "after Flush the quarantined slot becomes available");
        Check(r.Get(-1) == null && r.Get(9999) == null, "out-of-range lookups return null");

        var big = new SlotRegistry<object>();
        for (int i = 0; i < 1000; i++) big.Add(new object());
        Check(big.Count == 1000 && big.Get(999) != null, "grows past its initial capacity");
    }

    private static void TestGeometry()
    {
        Console.WriteLine("collider geometry vs real Box2D shapes");
        float hp = MathF.PI / 2;

        Collider2DGeometry.ToBodySpace(10, 5, hp, 10, 7, hp, out float lx, out float ly, out float la);
        Check(Near(lx, 2f, 1e-4f) && Near(ly, 0f, 1e-4f) && Near(la, 0f, 1e-4f),
              $"world (10,7) on a body at (10,5) turned 90 deg is local (2,0) ({lx:F2},{ly:F2},{la:F2})");

        Collider2DGeometry.TransformPoint(1, 0, 2, 1, hp, 1, 1, out float tx, out float ty);
        Check(Near(tx, 1f, 1e-4f) && Near(ty, 3f, 1e-4f), $"scale(2,1) -> rotate 90 -> translate(1,1) maps (1,0) to (1,3) ({tx:F2},{ty:F2})");

        Collider2DGeometry.CapsuleEnds(0, 0, hp, 2f, vertical: false, out float x1, out float y1, out float x2, out float y2);
        Check(Near(x1, 0f, 1e-4f) && Near(y1, -2f, 1e-4f) && Near(x2, 0f, 1e-4f) && Near(y2, 2f, 1e-4f),
              $"a horizontal capsule turned 90 deg runs vertically ({x1:F1},{y1:F1}) - ({x2:F1},{y2:F1})");

        // Real shape: body at (10,5) turned 90deg, child collider centred at world (10,7), box 2x0.5 wide in the BODY's x.
        // In the world that box is therefore 0.5 wide and 2 tall.
        var (sim, _) = NewSim();
        var col = new TCollider { Name = "child" };
        col.Index = sim.RegisterCollider(col);
        uint body = PB2.BodyCreate(PB2BodyType.Static, 10, 5, hp, -1, 1, 0, 0, 0);
        Collider2DGeometry.ToBodySpace(10, 5, hp, 10, 7, hp, out lx, out ly, out la);
        PB2.ShapeCreateBox(body, col.Index, 0, 1f, 0.25f, lx, ly, la, 0, 1, 0.6f, 0, 0);
        sim.Step(Dt);
        var found = new List<ICollider2DHost>();
        bool Hits(float x, float y) { found.Clear(); return sim.OverlapPoint(x, y, uint.MaxValue, false, found) == 1; }
        Check(Hits(10f, 7f), "hit at the intended world centre (10,7)");
        Check(Hits(10f, 7.9f) && Hits(10f, 6.1f), "tall in world Y: reaches +-0.9 from centre");
        Check(!Hits(10.5f, 7f) && !Hits(9.5f, 7f), "narrow in world X: +-0.5 misses (0.25 half-width)");
        Check(!Hits(12f, 5f), "does NOT sit where an unrotated offset would put it (12,5)");
        sim.Dispose();
    }

    private static bool ClosedChain(List<float> s)
    {
        int n = s.Count / 4;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (MathF.Abs(s[4 * i + 2] - s[4 * j]) > 1e-3f || MathF.Abs(s[4 * i + 3] - s[4 * j + 1]) > 1e-3f) return false;
        }
        return n > 0;
    }

    private static void TestOutlinePrimitives()
    {
        Console.WriteLine("outline primitives (pure)");
        var s = new List<float>();
        Collider2DOutline.Circle(s, 3, 4, 2);
        bool onCircle = true;
        for (int i = 0; i < s.Count; i += 2) onCircle &= Near(MathF.Sqrt((s[i] - 3) * (s[i] - 3) + (s[i + 1] - 4) * (s[i + 1] - 4)), 2f, 1e-4f);
        Check(ClosedChain(s) && onCircle && s.Count / 4 == Collider2DOutline.CircleSegments, "circle: closed, every vertex at the radius");

        s.Clear();
        Collider2DOutline.Box(s, 0, 0, 2, 1, 0, 0);
        Check(s.Count == 16 && ClosedChain(s), "plain box: 4 closed edges");

        s.Clear();
        Collider2DOutline.Box(s, 10, 5, 2, 1, MathF.PI / 2, 0.5f);
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < s.Count; i += 2) { minX = MathF.Min(minX, s[i]); maxX = MathF.Max(maxX, s[i]); minY = MathF.Min(minY, s[i + 1]); maxY = MathF.Max(maxY, s[i + 1]); }
        Check(ClosedChain(s) && Near(maxX - minX, 2 * (1 + 0.5f), 1e-3f) && Near(maxY - minY, 2 * (2 + 0.5f), 1e-3f),
              $"rounded box turned 90 deg: closed, extents grown by the radius ({maxX - minX:F2} x {maxY - minY:F2})");

        s.Clear();
        Collider2DOutline.Capsule(s, 0, -1, 0, 1, 0.5f);
        minX = float.MaxValue; maxX = float.MinValue; minY = float.MaxValue; maxY = float.MinValue;
        for (int i = 0; i < s.Count; i += 2) { minX = MathF.Min(minX, s[i]); maxX = MathF.Max(maxX, s[i]); minY = MathF.Min(minY, s[i + 1]); maxY = MathF.Max(maxY, s[i + 1]); }
        Check(ClosedChain(s) && Near(maxX - minX, 1f, 1e-3f) && Near(maxY - minY, 3f, 1e-3f), $"capsule: closed, 1 wide x 3 tall ({maxX - minX:F2} x {maxY - minY:F2})");

        s.Clear();
        Collider2DOutline.Capsule(s, -1, 0, 1, 0, 0.25f);
        Check(ClosedChain(s), "capsule lying on its side is also a closed chain");

        s.Clear();
        float[] line = [0, 0, 1, 0, 2, 1];
        Collider2DOutline.Polyline(s, line, 3, loop: false);
        int open = s.Count / 4;
        s.Clear();
        Collider2DOutline.Polyline(s, line, 3, loop: true);
        Check(open == 2 && s.Count / 4 == 3 && ClosedChain(s), "polyline: n-1 segments open, n closed");
    }

    private static void TestHullParity()
    {
        Console.WriteLine("polygon hull: agrees with Box2D about what is valid, and describes the real shape");
        var (sim, _) = NewSim();
        var host = new TCollider { Name = "poly" };
        host.Index = sim.RegisterCollider(host);

        var rng = new Random(20260101);
        string[] families = ["uniform", "collinear", "exact duplicates", "near duplicates", "near collinear", "convex ring", "tiny cluster"];
        var tried = new int[families.Length]; var disagree = new int[families.Length]; var validCount = new int[families.Length];
        var validCases = new List<(float[] xy, int n)>();
        float[] xy = new float[16];
        int[] hull = new int[8];

        for (int trial = 0; trial < 4200; trial++)
        {
            int fam = trial % families.Length;
            int n = rng.Next(3, 9);
            float R() => (float)(rng.NextDouble() * 2 - 1);
            switch (fam)
            {
                case 0: for (int i = 0; i < n; i++) { xy[2 * i] = R() * 2; xy[2 * i + 1] = R() * 2; } break;
                case 1:
                {
                    float ax = R(), ay = R(), dx = R(), dy = R();
                    for (int i = 0; i < n; i++) { float t = R() * 2; xy[2 * i] = ax + dx * t; xy[2 * i + 1] = ay + dy * t; }
                    break;
                }
                case 2:
                    for (int i = 0; i < n; i++)
                    {
                        if (i > 0 && rng.Next(3) == 0) { int j = rng.Next(i); xy[2 * i] = xy[2 * j]; xy[2 * i + 1] = xy[2 * j + 1]; }
                        else { xy[2 * i] = R() * 2; xy[2 * i + 1] = R() * 2; }
                    }
                    break;
                case 3:
                {
                    int distinct = rng.Next(1, 4);
                    for (int i = 0; i < n; i++)
                    {
                        int b = rng.Next(distinct);
                        float bx = (b * 7 % 5) - 2f, by = (b * 3 % 5) - 2f;
                        xy[2 * i] = bx + R() * 0.002f; xy[2 * i + 1] = by + R() * 0.002f;
                    }
                    break;
                }
                case 4:
                {
                    float ax = R(), ay = R(), dx = R(), dy = R();
                    float len = MathF.Sqrt(dx * dx + dy * dy) + 1e-3f;
                    for (int i = 0; i < n; i++)
                    {
                        float t = R() * 2, off = R() * 0.003f;
                        xy[2 * i] = ax + dx * t - dy / len * off; xy[2 * i + 1] = ay + dy * t + dx / len * off;
                    }
                    break;
                }
                case 5:
                {
                    float rad = 0.1f + (float)rng.NextDouble() * 2f, a0 = R() * 3;
                    for (int i = 0; i < n; i++) { float a = a0 + i * MathF.PI * 2 / n; xy[2 * i] = rad * MathF.Cos(a); xy[2 * i + 1] = rad * MathF.Sin(a); }
                    break;
                }
                default: for (int i = 0; i < n; i++) { xy[2 * i] = R() * 0.004f; xy[2 * i + 1] = R() * 0.004f; } break;
            }

            int managed = Collider2DOutline.ComputeHull(xy, n, hull);

            uint body = PB2.BodyCreate(PB2BodyType.Static, 0, 0, 0, -1, 1, 0, 0, 0);
            uint shape;
            fixed (float* p = xy) shape = PB2.ShapeCreatePolygon(body, host.Index, 0, p, n, 0f, 1f, 0.6f, 0f, 0);
            bool native = shape != 0;
            PB2.BodyDestroy(body);

            tried[fam]++;
            if (native) validCount[fam]++;
            if ((managed > 0) != native) disagree[fam]++;
            if (fam == 0 && native && validCases.Count < 250) validCases.Add(((float[])xy.Clone(), n));
        }

        int totalDisagree = disagree.Sum();
        for (int f = 0; f < families.Length; f++)
            Console.WriteLine($"        {families[f],-17} {tried[f],4} cases, {validCount[f],4} valid natively, {disagree[f]} disagreements");
        Check(totalDisagree == 0, $"validity verdict matches Box2D on all {tried.Sum()} cases ({totalDisagree} disagreements)");

        // For valid polygons the hull is the real shape: edge midpoints touch it, points just outside them do not.
        var colliders = new List<TCollider>();
        var polys = new List<(float[] xy, int n)>();
        for (int i = 0; i < validCases.Count; i++)
        {
            var c = new TCollider { Name = $"p{i}" };
            c.Index = sim.RegisterCollider(c);
            uint body = PB2.BodyCreate(PB2BodyType.Static, i * 10f, 0, 0, -1, 1, 0, 0, 0);
            fixed (float* p = validCases[i].xy) PB2.ShapeCreatePolygon(body, c.Index, 0, p, validCases[i].n, 0f, 1f, 0.6f, 0f, 0);
            colliders.Add(c);
            polys.Add(validCases[i]);
        }
        sim.Step(Dt);

        int touchFail = 0, outsideFail = 0, centroidFail = 0, edges = 0;
        var found = new List<ICollider2DHost>();
        for (int i = 0; i < polys.Count; i++)
        {
            var (pxy, pn) = polys[i];
            int hn = Collider2DOutline.ComputeHull(pxy, pn, hull);
            float ox = i * 10f, cx = 0, cy = 0;
            for (int k = 0; k < hn; k++) { cx += pxy[2 * hull[k]]; cy += pxy[2 * hull[k] + 1]; }
            cx /= hn; cy /= hn;
            found.Clear();
            if (sim.OverlapPoint(ox + cx, cy, uint.MaxValue, false, found) == 0) centroidFail++;
            for (int k = 0; k < hn; k++)
            {
                int a = hull[k], b = hull[(k + 1) % hn];
                float ax = pxy[2 * a], ay = pxy[2 * a + 1], bx = pxy[2 * b], by = pxy[2 * b + 1];
                float mx = (ax + bx) / 2, my = (ay + by) / 2, ex = bx - ax, ey = by - ay, el = MathF.Sqrt(ex * ex + ey * ey);
                if (el < 0.05f) continue; // too short to probe reliably
                edges++;
                float nx = ey / el, ny = -ex / el; // outward for a counter-clockwise hull
                found.Clear();
                if (sim.OverlapCircle(ox + mx, my, 0.01f, uint.MaxValue, false, found) == 0) touchFail++;
                found.Clear();
                if (sim.OverlapPoint(ox + mx + nx * 0.03f, my + ny * 0.03f, uint.MaxValue, false, found) != 0) outsideFail++;
            }
        }
        Check(centroidFail == 0 && touchFail == 0 && outsideFail == 0,
              $"{polys.Count} real polygons, {edges} edges: hull edges lie on the shape (touch misses {touchFail}), nothing just outside them (hits {outsideFail}), centroids inside (misses {centroidFail})");
        sim.Dispose();
    }

    // ---- ownership -----------------------------------------------------------------------

    private static void TestOwnership()
    {
        Console.WriteLine("native world ownership + handoff");
        var (a, _) = NewSim();
        var (b, _) = NewSim();

        var ca = new TCollider { Name = "a" };
        int ia = a.RegisterCollider(ca);
        Check(ia >= 0 && a.IsAcquired && !b.IsAcquired, "first simulation acquires the world");

        var cb = new TCollider { Name = "b" };
        Check(b.RegisterCollider(cb) == -1 && !b.IsAcquired, "a second simulation is refused while the first is busy (returns -1)");

        a.UnregisterCollider(ia);
        Check(a.IsIdle && a.IsAcquired, "simulation keeps the world while idle (no create/destroy churn)");

        int ib = b.RegisterCollider(cb);
        Check(ib >= 0 && b.IsAcquired && !a.IsAcquired, "an idle owner is evicted when another simulation needs the world (scene swap)");

        // evicted simulation can come back once the other goes idle
        b.UnregisterCollider(ib);
        int ia2 = a.RegisterCollider(ca);
        Check(ia2 >= 0 && a.IsAcquired && !b.IsAcquired, "and the first can re-acquire it later");

        a.UnregisterCollider(ia2);
        a.Dispose();
        Check(!Box2DWorld.Exists, "Dispose releases the native world");

        // Registering during a step must not tear anything down.
        var (s, _) = NewSim();
        var host = new TCollider();
        int ih = s.RegisterCollider(host);
        s.Step(Dt);
        s.UnregisterCollider(ih);
        s.Step(Dt); // idle but still owning: stepping an empty world is fine
        Check(s.IsAcquired && s.IsIdle, "an idle simulation can keep stepping safely");
        s.Dispose();
    }

    // ---- contacts ------------------------------------------------------------------------

    private static void TestCoreContacts()
    {
        Console.WriteLine("contact events through the simulation");
        var (sim, rec) = NewSim();
        var ground = StaticBoxC(sim, "ground", 0, 0, 50, 0.5f);
        var (ball, body) = BallC(sim, "ball", 0, 3, 0.5f);

        RunSteps(sim, 200);
        Check(rec.Count("Begin", "ground", "ball") == 1 && rec.Count("Begin", "ball", "ground") == 1,
              $"begin delivered once to each side ({string.Join(", ", rec.Log.Where(l => l.StartsWith("Begin")))})");

        var g = rec.Begins.First(x => x.self == "ground").c;
        var bl = rec.Begins.First(x => x.self == "ball").c;
        Check(g.NY > 0.9f && bl.NY < -0.9f, $"normal points self->other on both sides (ground sees {g.NY:F2}, ball sees {bl.NY:F2})");
        Check(Near(g.X, 0f, 0.6f) && Near(g.Y, 0.5f, 0.15f) && g.Impulse > 0f, $"contact point + impulse intact through dispatch (y={g.Y:F2}, impulse={g.Impulse:F2})");
        Check(rec.FirstStep("Begin", "ground", "ball") == rec.FirstStep("Begin", "ball", "ground"), "both sides notified in the same step");

        body.Pose.Sample(1f, sim.StepIndex, out float x, out float y, out float ang);
        Check(Near(y, 1.0f, 0.05f) && body.Moves > 0 && body.Asleep, $"body host received its poses and the sleep notice (y={y:F3}, {body.Moves} moves)");
        int movesAtSleep = body.Moves;
        RunSteps(sim, 30);
        Check(body.Moves == movesAtSleep, "a sleeping body generates no further move callbacks");

        // launch it away: contact must end, delivered to both sides
        PB2.BodyApplyImpulse(body.Handle, 0, 20, 0, 0, 0);
        RunSteps(sim, 10);
        Check(rec.Count("End", "ground", "ball") == 1 && rec.Count("End", "ball", "ground") == 1, "end delivered once to each side");
        Check(rec.LastStep("End", "ground", "ball") > rec.FirstStep("Begin", "ground", "ball"), "end comes after begin");
        sim.Dispose();
    }

    // ---- triggers ------------------------------------------------------------------------

    private static void TestCoreTriggers()
    {
        Console.WriteLine("trigger Enter / Stay / Exit synthesis");
        var (sim, rec) = NewSim();
        var zone = StaticBoxC(sim, "zone", 0, 3, 5, 1, sensor: true);
        var (ball, body) = BallC(sim, "ball", 0, 8, 0.4f);
        RunSteps(sim, 120);

        Check(rec.Count("Enter", "zone", "ball") == 1 && rec.Count("Enter", "ball", "zone") == 1, "Enter delivered once to each side");
        Check(rec.Count("Exit", "zone", "ball") == 1 && rec.Count("Exit", "ball", "zone") == 1, "Exit delivered once to each side");
        int stayA = rec.Count("Stay", "zone", "ball"), stayB = rec.Count("Stay", "ball", "zone");
        Check(stayA >= 8 && stayA == stayB, $"Stay fires every step while overlapping, symmetric (zone side {stayA}, ball side {stayB})");

        int enter = rec.FirstStep("Enter", "zone", "ball"), firstStay = rec.FirstStep("Stay", "zone", "ball");
        int lastStay = rec.LastStep("Stay", "zone", "ball"), exit = rec.FirstStep("Exit", "zone", "ball");
        Check(firstStay == enter + 1, $"Stay never fires on the same step as Enter (enter@{enter}, first stay@{firstStay})");
        Check(lastStay < exit, $"Stay stops before Exit (last stay@{lastStay}, exit@{exit})");
        Check(stayA == exit - enter - 1, $"exactly one Stay per intervening step ({stayA} stays across steps {enter}..{exit})");

        body.Pose.Sample(1f, sim.StepIndex, out _, out float y, out _);
        Check(y < 0f, "the sensor did not block the ball");
        sim.Dispose();
    }

    private static void TestHandlerDestroysDuringDispatch()
    {
        Console.WriteLine("handler destroys objects mid-dispatch");
        var (sim, rec) = NewSim();
        var ground = StaticBoxC(sim, "ground", 0, 0, 50, 0.5f);
        var (ball, body) = BallC(sim, "ball", 0, 3, 0.5f);

        int removedIndex = -1, newcomerIndex = -1;
        rec.OnFirstBegin = (_, self, other) =>
        {
            var o = (TCollider)other;
            removedIndex = o.Index;
            sim.UnregisterCollider(o.Index);               // destroy the other side...
            var fresh = new TCollider { Name = "newcomer" };
            newcomerIndex = sim.RegisterCollider(fresh);   // ...and immediately create a replacement
        };

        RunSteps(sim, 200);
        int begins = rec.Log.Count(l => l.StartsWith("Begin:"));
        Check(begins == 1, $"the destroyed side gets no callback ({begins} Begin callback(s) total)");
        Check(newcomerIndex >= 0 && newcomerIndex != removedIndex, $"quarantine: the replacement did not inherit the destroyed index ({removedIndex} -> {newcomerIndex})");
        Check(!rec.Log.Any(l => l.Contains("newcomer")), "no event meant for the destroyed collider reached its replacement");
        sim.Dispose();
    }

    private static void TestTriggerPairCleanup()
    {
        Console.WriteLine("trigger pair cleanup when a collider goes away");
        var (sim, rec) = NewSim();
        var zone = StaticBoxC(sim, "zone", 0, 3, 5, 1, sensor: true);
        var (ball, body) = BallC(sim, "ball", 0, 8, 0.4f);
        while (rec.Count("Enter", "zone", "ball") == 0 && sim.StepIndex < 300) sim.Step(Dt);
        Check(rec.Count("Enter", "zone", "ball") == 1, "ball entered the zone");

        sim.UnregisterCollider(ball.Index); // the ball's managed side disappears while inside the sensor
        int staysBefore = rec.Log.Count(l => l.StartsWith("Stay:"));
        bool threw = false;
        try { RunSteps(sim, 60); } catch (Exception) { threw = true; }
        Check(!threw, "stepping on after the collider vanished does not throw");
        Check(rec.Log.Count(l => l.StartsWith("Stay:")) == staysBefore && rec.Count("Exit", "zone", "ball") == 0,
              "no Stay or Exit is delivered for the vanished collider (documented: destroyed colliders send no Exit)");
        sim.Dispose();
    }

    // ---- queries -------------------------------------------------------------------------

    private static void TestCoreQueries()
    {
        Console.WriteLine("queries through the simulation");
        var (sim, rec) = NewSim();
        var ground = StaticBoxC(sim, "ground", 0, 0, 50, 0.5f, layer: 0);
        var platform = StaticBoxC(sim, "platform", 0, 3, 2, 0.5f, layer: 2);
        var zone = StaticBoxC(sim, "zone", 0, 6, 2, 0.5f, layer: 0, sensor: true);
        sim.Step(Dt);
        uint all = uint.MaxValue;

        bool hit = sim.Raycast(0, 10, 0, -1, 20, all, false, out var h);
        Check(hit && ReferenceEquals(h.Collider, platform) && Near(h.Distance, 6.5f, 0.01f) && Near(h.Y, 3.5f, 0.01f),
              $"raycast resolves the collider object and distance ({h.Distance:F2})");

        hit = sim.Raycast(0, 10, 0, -1, float.MaxValue, all, false, out h);
        Check(hit && ReferenceEquals(h.Collider, platform) && Near(h.Distance, 6.5f, 0.05f),
              $"float.MaxValue max distance is clamped, not overflowed ({(hit ? h.Distance : -1):F2})");

        hit = sim.Raycast(0, 10, 0, -2, 20, all, false, out h);
        Check(hit && Near(h.Distance, 6.5f, 0.01f), $"direction need not be normalised (distance {h.Distance:F2})");

        var hits = new List<RayHit2D>();
        int n = sim.RaycastAll(0, 10, 0, -1, 20, all, false, hits);
        Check(n == 2 && ReferenceEquals(hits[0].Collider, platform) && ReferenceEquals(hits[1].Collider, ground) && hits[0].Distance < hits[1].Distance,
              "raycast all: nearest first, sensors skipped");

        var list = new List<ICollider2DHost>();
        Check(sim.OverlapCircle(0, 3, 1, all, false, list) == 1 && ReferenceEquals(list[0], platform), "overlap circle");
        list.Clear();
        Check(sim.OverlapPoint(0, 3, all, false, list) == 1, "overlap point");
        list.Clear();
        Check(sim.OverlapBox(0, 3, 1, 1, 0.5f, all, false, list) == 1, "overlap box");
        list.Clear();
        Check(sim.OverlapCircle(0, 3, 1, 1u << 0, false, list) == 0, "overlap respects the layer mask");

        // a collider built from two shapes is reported once
        var multi = new TCollider { Name = "multi" };
        multi.Index = sim.RegisterCollider(multi);
        uint mb = PB2.BodyCreate(PB2BodyType.Static, 200, 20, 0, -1, 1, 0, 0, 0);
        PB2.ShapeCreateBox(mb, multi.Index, 0, 0.5f, 0.5f, -1, 0, 0, 0, 1, 0.6f, 0, 0);
        PB2.ShapeCreateBox(mb, multi.Index, 0, 0.5f, 0.5f, 1, 0, 0, 0, 1, 0.6f, 0, 0);
        sim.Step(Dt);
        list.Clear();
        int m = sim.OverlapCircle(200, 20, 3, all, false, list);
        int times = list.Count(x => ReferenceEquals(x, multi));
        Check(m == 1 && times == 1, $"multi-shape collider (2 shapes, both inside the circle) reported once ({times}x, {m} result(s))");

        // many colliders: exercises buffer growth (>256) and the hash-set dedupe path (>64)
        var many = new List<TCollider>();
        for (int i = 0; i < 300; i++) many.Add(StaticBoxC(sim, $"m{i}", 100 + i * 1.0f, 50, 0.3f, 0.3f));
        sim.Step(Dt);
        list.Clear();
        int total = sim.OverlapBox(250, 50, 200, 5, 0, all, false, list);
        Check(total == 300 && list.Distinct().Count() == 300, $"300 overlaps survive buffer growth and dedupe ({total} distinct)");

        // raycast-all buffer growth: 100 thin walls along one ray
        var walls = new List<TCollider>();
        for (int i = 0; i < 100; i++) walls.Add(StaticBoxC(sim, $"w{i}", 1000 + i * 2f, 0, 0.5f, 5f));
        sim.Step(Dt);
        hits.Clear();
        int wn = sim.RaycastAll(990, 0, 1, 0, 300, all, false, hits);
        bool sorted = true;
        for (int i = 1; i < hits.Count; i++) sorted &= hits[i].Distance >= hits[i - 1].Distance;
        Check(wn == 100 && sorted, $"raycast all through 100 walls: {wn} hits, sorted (buffer grew past its initial 64)");
        sim.Dispose();
    }

    private static void TestSettingsSurviveEviction()
    {
        Console.WriteLine("settings survive the world being handed away and back");
        var (a, _) = NewSim();
        a.SetGravity(0, -20);
        var rows = new uint[32];
        Array.Fill(rows, uint.MaxValue);
        rows[0] &= ~(1u << 1); rows[1] &= ~(1u << 0);
        a.SetLayerMatrix(rows);

        var ca = new TCollider();
        int ia = a.RegisterCollider(ca);
        a.UnregisterCollider(ia);

        var (b, _) = NewSim();
        var cb = new TCollider();
        int ib = b.RegisterCollider(cb); // evicts a
        b.UnregisterCollider(ib);

        // a returns: its cached gravity and layer matrix must be pushed into the brand-new native world
        var (ground, ball) = (StaticBoxC(a, "ground", 0, 0, 50, 0.5f, layer: 0), BallC(a, "ball", 0, 20, 0.5f, layer: 1));
        RunSteps(a, 60);
        ball.body.Pose.Sample(1f, a.StepIndex, out _, out float y, out _);
        float expected = 20 - 0.5f * 20 * 1f * 1f; // ~10 after one second at g = 20
        Check(Near(y, expected, 0.6f), $"gravity (0,-20) re-applied after eviction (y = {y:F2}, expected ~{expected:F1})");

        RunSteps(a, 120);
        ball.body.Pose.Sample(1f, a.StepIndex, out _, out y, out _);
        Check(y < -5f, $"layer matrix re-applied after eviction: layers 0/1 do not collide (y = {y:F1})");
        a.Dispose();
    }
}
