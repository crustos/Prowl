// Standalone verification of the prowl_box2d shim and its C# bindings.
//   dotnet run -c Release                 correctness tests, then the benchmark
//   dotnet run -c Release -- --no-bench   tests only
// Exit code is non-zero if any check fails.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Prowl.Runtime.Physics2D.Native;

internal static unsafe partial class Program
{
    private const float Dt = 1f / 60f;
    private static int s_pass, s_fail;

    // Same entry point as PB2.BodyGetState but WITHOUT SuppressGCTransition, to measure what that attribute buys.
    [LibraryImport("prowl_box2d", EntryPoint = "pb2_body_get_state")]
    private static partial void BodyGetStateSlow(uint body, float* out10);

    [LibraryImport("prowl_box2d", EntryPoint = "pb2_body_set_gravity_scale")]
    private static partial void SetGravityScaleSlow(uint body, float scale);

    private static void Micro()
    {
        Console.WriteLine("\n== micro: cost of one managed->native call (trivial setter, 20M calls) ==");
        using var w = Box2DWorld.Create(0, -10);
        uint b = DynamicBox(0, 0, 0.5f, 0.5f, 1, 1);
        const int N = 20_000_000;
        for (int rep = 0; rep < 2; rep++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) PB2.BodySetGravityScale(b, 1f);
            double fast = sw.Elapsed.TotalMilliseconds * 1e6 / N;
            sw.Restart();
            for (int i = 0; i < N; i++) SetGravityScaleSlow(b, 1f);
            double slow = sw.Elapsed.TotalMilliseconds * 1e6 / N;
            Console.WriteLine($"  SuppressGCTransition {fast,6:F1} ns/call    normal transition {slow,6:F1} ns/call");
        }
    }

    private static void Check(bool ok, string what)
    {
        if (ok) { s_pass++; Console.WriteLine($"  PASS  {what}"); }
        else { s_fail++; Console.WriteLine($"  FAIL  {what}"); }
    }

    private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

    private static int Main(string[] args)
    {
        Console.WriteLine("== correctness ==");
        TestRestAndContacts();
        TestLayerMatrix();
        TestSensors();
        TestQueries();
        TestChain();
        TestRotationLock();
        TestKinematicTarget();
        TestAbiMismatchIsCaught();
        CoreTests();
        EngineTests();
        GizmoTests();

        Console.WriteLine($"\n{s_pass} passed, {s_fail} failed");
        if (Array.Exists(args, a => a == "--micro")) { Micro(); return 0; }
        if (s_fail == 0 && !Array.Exists(args, a => a == "--no-bench"))
            Benchmark();
        return s_fail == 0 ? 0 : 1;
    }

    // ---- helpers -------------------------------------------------------------------------

    private static uint StaticBox(float x, float y, float hw, float hh, int collider, int layer = 0, PB2ShapeFlags flags = 0)
    {
        uint b = PB2.BodyCreate(PB2BodyType.Static, x, y, 0, -1, 1f, 0, 0, 0);
        PB2.ShapeCreateBox(b, collider, layer, hw, hh, 0, 0, 0, 0, 1f, 0.6f, 0f, (uint)flags);
        return b;
    }

    private static uint DynamicBox(float x, float y, float hw, float hh, int bodyIndex, int collider, int layer = 0, PB2BodyFlags bf = 0)
    {
        uint b = PB2.BodyCreate(PB2BodyType.Dynamic, x, y, 0, bodyIndex, 1f, 0, 0, (uint)bf);
        PB2.ShapeCreateBox(b, collider, layer, hw, hh, 0, 0, 0, 0, 1f, 0.6f, 0f, 0);
        return b;
    }

    private static uint DynamicBall(float x, float y, float r, int bodyIndex, int collider, int layer = 0)
    {
        uint b = PB2.BodyCreate(PB2BodyType.Dynamic, x, y, 0, bodyIndex, 1f, 0, 0, 0);
        PB2.ShapeCreateCircle(b, collider, layer, 0, 0, r, 1f, 0.6f, 0f, 0);
        return b;
    }

    private static float[] State(uint body)
    {
        var s = new float[10];
        fixed (float* p = s) PB2.BodyGetState(body, p);
        return s;
    }

    private static uint[] MatrixWithout(int a, int b)
    {
        var rows = new uint[32];
        Array.Fill(rows, 0xFFFFFFFFu);
        rows[a] &= ~(1u << b);
        rows[b] &= ~(1u << a);
        return rows;
    }

    // ---- tests ---------------------------------------------------------------------------

    private static void TestRestAndContacts()
    {
        Console.WriteLine("rest height, contact manifold, sleep");
        using var w = Box2DWorld.Create(0, -10);
        StaticBox(0, 0, 50, 0.5f, collider: 0);
        uint box = DynamicBox(0, 5, 0.5f, 0.5f, bodyIndex: 7, collider: 1);

        int begins = 0, moveSeen = 0;
        PB2ContactEvent begin = default;
        bool asleep = false;
        for (int i = 0; i < 400; i++)
        {
            var ev = w.Step(Dt);
            foreach (var m in ev.Moves) { if (m.BodyIndex == 7) moveSeen++; if (m.BodyIndex == 7 && m.FellAsleep != 0) asleep = true; }
            foreach (var c in ev.ContactBegins) { begins++; begin = c; }
        }
        var s = State(box);
        Check(Near(s[1], 1.0f, 0.05f), $"box rests on ground (y = {s[1]:F3}, expected 1.000)");
        Check(moveSeen > 10, $"move events carry the managed body index ({moveSeen} events for body 7)");
        Check(begins == 1, $"exactly one contact begin event ({begins})");
        Check((begin.ColliderA, begin.ColliderB) is (0, 1) or (1, 0), $"contact collider indices resolved natively (A={begin.ColliderA}, B={begin.ColliderB})");
        Check(MathF.Abs(begin.NY) > 0.9f && MathF.Abs(begin.NX) < 0.1f, $"contact normal is vertical ({begin.NX:F2}, {begin.NY:F2})");
        Check(Near(begin.PY, 0.5f, 0.15f), $"contact point is on the ground surface (y = {begin.PY:F3})");
        Check(begin.Impulse > 0f, $"contact carries a normal impulse ({begin.Impulse:F3})");
        Check(begin.BodyA >= -1 && (begin.BodyA == 7 || begin.BodyB == 7), $"contact carries managed body indices (A={begin.BodyA}, B={begin.BodyB})");
        Check(asleep, "resting body fell asleep and said so");
    }

    private static void TestLayerMatrix()
    {
        Console.WriteLine("32-layer collision matrix (evaluated natively)");
        // Layers above 15 prove the fork's 16-bit filter limit is not what applies.
        foreach (var (la, lb) in new[] { (0, 1), (3, 28) })
        {
            using var w = Box2DWorld.Create(0, -10);
            w.SetLayerMatrix(MatrixWithout(la, lb));
            StaticBox(0, 0, 50, 0.5f, 0, layer: la);
            uint ball = DynamicBall(0, 3, 0.5f, 1, 1, layer: lb);
            for (int i = 0; i < 120; i++) w.Step(Dt);
            Check(State(ball)[1] < -5f, $"layers {la}/{lb} excluded: ball falls through (y = {State(ball)[1]:F1})");
        }
        {
            using var w = Box2DWorld.Create(0, -10);
            StaticBox(0, 0, 50, 0.5f, 0, layer: 3);
            uint ball = DynamicBall(0, 3, 0.5f, 1, 1, layer: 28);
            for (int i = 0; i < 120; i++) w.Step(Dt);
            Check(Near(State(ball)[1], 1.0f, 0.1f), $"layers 3/28 default matrix: ball rests (y = {State(ball)[1]:F2})");
        }
    }

    private static void TestSensors()
    {
        Console.WriteLine("sensor events");
        foreach (bool excluded in new[] { false, true })
        {
            using var w = Box2DWorld.Create(0, -10);
            if (excluded) w.SetLayerMatrix(MatrixWithout(0, 5));
            StaticBox(0, 3, 5, 1, collider: 10, layer: 0, flags: PB2ShapeFlags.Sensor);
            uint ball = DynamicBall(0, 8, 0.4f, 1, collider: 11, layer: 5);
            int begin = 0, end = 0;
            bool orderOk = true;
            for (int i = 0; i < 120; i++)
            {
                var ev = w.Step(Dt);
                foreach (var e in ev.Sensors)
                {
                    if (e.SensorCollider != 10 || e.VisitorCollider != 11) { orderOk = false; continue; }
                    if (e.Flags == PB2EventFlags.Begin) begin++;
                    else { if (begin == 0) orderOk = false; end++; }
                }
            }
            bool passedThrough = State(ball)[1] < 0f;
            if (!excluded)
                Check(begin == 1 && end == 1 && orderOk && passedThrough, $"sensor: one enter, one exit, ball not blocked (begin={begin}, end={end})");
            else
                Check(begin == 0 && end == 0, $"sensor: layer matrix suppresses events (begin={begin}, end={end})");
        }
    }

    private static void TestQueries()
    {
        Console.WriteLine("queries");
        using var w = Box2DWorld.Create(0, -10);
        StaticBox(0, 0, 50, 0.5f, collider: 100, layer: 0);          // top at y = 0.5
        StaticBox(0, 3, 2, 0.5f, collider: 101, layer: 2);           // platform, spans y 2.5..3.5
        StaticBox(0, 6, 2, 0.5f, collider: 102, layer: 0, flags: PB2ShapeFlags.Sensor);
        w.Step(Dt); // queries need the broad phase populated

        uint all = 0xFFFFFFFFu;
        bool hit = w.Raycast(0, 10, 0, -1, 20, all, hitSensors: false, out var h);
        Check(hit && h.Collider == 101, $"raycast skips sensor, hits platform first (collider {h.Collider})");
        Check(hit && Near(h.Fraction, (10f - 3.5f) / 20f, 0.01f) && Near(h.NY, 1f, 0.01f) && Near(h.PY, 3.5f, 0.01f),
              $"raycast fraction/point/normal (f={h.Fraction:F3}, y={h.PY:F2}, ny={h.NY:F2})");

        hit = w.Raycast(0, 10, 0, -1, 20, all & ~(1u << 2), false, out h);
        Check(hit && h.Collider == 100 && Near(h.PY, 0.5f, 0.01f), $"layer mask excludes the platform, hits ground (collider {h.Collider})");

        hit = w.Raycast(0, 10, 0, -1, 20, all, hitSensors: true, out h);
        Check(hit && h.Collider == 102, $"hitSensors includes the sensor (collider {h.Collider})");

        Check(!w.Raycast(0, 10, 0, -1, 2, all, false, out _), "raycast respects max distance");

        Span<PB2RayHit> hits = stackalloc PB2RayHit[8];
        int n = w.RaycastAll(0, 10, 0, -1, 20, all, false, hits);
        Check(n == 2 && hits[0].Collider == 101 && hits[1].Collider == 100 && hits[0].Fraction < hits[1].Fraction,
              $"raycast all: platform then ground, sorted ({n} hits)");

        Span<int> o = stackalloc int[8];
        Check(w.OverlapCircle(0, 1, 1, all, false, o) >= 1 && o[0] == 100, "overlap circle hits ground");
        Check(w.OverlapCircle(0, 20, 1, all, false, o) == 0, "overlap circle in empty space hits nothing");
        Check(w.OverlapPoint(0, 3, all, false, o) == 1 && o[0] == 101, "overlap point inside platform");
        Check(w.OverlapBox(0, 3, 1, 1, 0.7f, all, false, o) >= 1, "overlap rotated box hits platform");
        Check(w.OverlapCircle(0, 3, 1, 1u << 0, false, o) == 0, "overlap respects layer mask");
    }

    private static float DropBallOnEdges(bool segments, bool leftToRight, int groundLayer, int ballLayer, uint[]? matrix)
    {
        using var w = Box2DWorld.Create(0, -10);
        if (matrix != null) w.SetLayerMatrix(matrix);
        float[] pts = leftToRight ? [-10, 0, 0, 0, 10, 0] : [10, 0, 0, 0, -10, 0];
        uint ground = PB2.BodyCreate(PB2BodyType.Static, 0, 0, 0, -1, 1, 0, 0, 0);
        uint* ids = stackalloc uint[4];
        fixed (float* p = pts)
        {
            if (segments) Check(PB2.SegmentsCreate(ground, 20, groundLayer, p, 3, 0, 0.6f, 0f, ids) == 2, "segments: 3 points -> 2 shapes");
            else PB2.ChainCreate(ground, 20, groundLayer, p, 3, 0, 0.6f, 0f);
        }
        uint ball = DynamicBall(0, 3, 0.5f, 1, 21, layer: ballLayer);
        for (int i = 0; i < 120; i++) w.Step(Dt);
        return State(ball)[1];
    }

    private static void TestChain()
    {
        Console.WriteLine("edge colliders: two-sided segments vs one-sided chain");
        // Segments are two-sided: the ball rests whichever way the points are ordered.
        float a = DropBallOnEdges(true, true, 4, 4, null), b = DropBallOnEdges(true, false, 4, 4, null);
        Check(Near(a, 0.5f, 0.05f) && Near(b, 0.5f, 0.05f), $"segments are two-sided (left->right y = {a:F2}, right->left y = {b:F2})");

        // Chains are one-sided: winding decides the solid side. This is Box2D behaviour we expose deliberately, so pin it.
        float c = DropBallOnEdges(false, true, 4, 4, null), d = DropBallOnEdges(false, false, 4, 4, null);
        Check(c < -5f && Near(d, 0.5f, 0.05f), $"chain is one-sided: left->right falls through (y = {c:F1}), right->left holds (y = {d:F2})");

        // Both must honour the 32-layer matrix, which only works because every segment/chain shape carries the layer.
        var m = MatrixWithout(4, 6);
        float e = DropBallOnEdges(true, true, 4, 6, m), f = DropBallOnEdges(false, false, 4, 6, m);
        Check(e < -5f && f < -5f, $"layer matrix applies to segments (y = {e:F1}) and chain segments (y = {f:F1})");

        // Segment ids are real, independently destroyable shapes.
        using var w = Box2DWorld.Create(0, -10);
        uint ground = PB2.BodyCreate(PB2BodyType.Static, 0, 0, 0, -1, 1, 0, 0, 0);
        float[] pts = [-10, 0, 0, 0, 10, 0];
        uint* ids = stackalloc uint[2];
        fixed (float* p = pts) PB2.SegmentsCreate(ground, 20, 0, p, 3, 0, 0.6f, 0f, ids);
        PB2.ShapeDestroy(ids[0]); PB2.ShapeDestroy(ids[1]);
        uint ball = DynamicBall(0, 3, 0.5f, 1, 21);
        for (int i = 0; i < 120; i++) w.Step(Dt);
        Check(State(ball)[1] < -5f, "destroying every segment shape removes the collider");
    }

    private static void TestRotationLock()
    {
        Console.WriteLine("motion locks");
        foreach (bool locked in new[] { false, true })
        {
            using var w = Box2DWorld.Create(0, 0);
            uint b = DynamicBox(0, 0, 0.5f, 0.5f, 1, 1, bf: locked ? PB2BodyFlags.LockRotation : 0);
            PB2.BodyApplyImpulse(b, 5, 0, 1, 0, 0.5f); // off-centre impulse
            for (int i = 0; i < 30; i++) w.Step(Dt);
            var s = State(b);
            float angle = MathF.Atan2(s[3], s[2]);
            if (locked) Check(Near(angle, 0f, 1e-4f) && s[0] > 1f, $"rotation locked: angle = {angle:F4}, still translates (x = {s[0]:F2})");
            else Check(MathF.Abs(angle) > 0.1f, $"control: unlocked body spins (angle = {angle:F2})");
        }
    }

    private static void TestKinematicTarget()
    {
        Console.WriteLine("batched kinematic move");
        using var w = Box2DWorld.Create(0, 0);
        uint k = PB2.BodyCreate(PB2BodyType.Kinematic, 0, 0, 0, 3, 1, 0, 0, 0);
        PB2.ShapeCreateBox(k, 0, 0, 0.5f, 0.5f, 0, 0, 0, 0, 1, 0.6f, 0, 0);
        w.QueueTransform(k, 5, 2, 0.5f, kinematicTarget: true);
        var ev = w.Step(Dt);
        var s = State(k);
        Check(Near(s[0], 5f, 0.3f) && Near(s[1], 2f, 0.3f), $"kinematic body reaches target within one step ({s[0]:F2}, {s[1]:F2})");

        uint d = DynamicBall(0, 20, 0.5f, 4, 1);
        w.QueueTransform(d, -7, 9, 0f); // teleport
        w.Step(Dt);
        Check(Near(State(d)[0], -7f, 0.01f), "queued teleport applied");
    }

    private static void TestAbiMismatchIsCaught()
    {
        Console.WriteLine("abi");
        int* a = stackalloc int[8];
        PB2.Abi(a);
        Check(a[0] == PB2.AbiVersion && a[7] == IntPtr.Size, $"native ABI v{a[0]}, pointer size {a[7]}");
    }

    // ---- benchmark -----------------------------------------------------------------------

    private static void Benchmark()
    {
        float* st = stackalloc float[10];
        Console.WriteLine("\n== benchmark: boxes pyramid (all bodies managed-owned) ==");
        Console.WriteLine($"   {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} core(s), {RuntimeInformation.FrameworkDescription}");

        foreach (int baseCount in new[] { 40, 100, 140 })
        {
            using var w = Box2DWorld.Create(0, -10);
            StaticBox(0, -0.5f, 400, 0.5f, collider: 0);

            int count = 0;
            var ids = new System.Collections.Generic.List<uint>();
            for (int row = 0; row < baseCount; row++)
                for (int col = 0; col < baseCount - row; col++)
                {
                    float x = (col - (baseCount - row) * 0.5f) * 1.05f;
                    float y = 0.5f + row * 1.0f;
                    ids.Add(DynamicBox(x, y, 0.5f, 0.5f, count, count + 1));
                    count++;
                }

            var posX = new float[count]; var posY = new float[count]; var rot = new float[count];

            const int Warm = 30, Frames = 270;
            double stepMs = 0, batchedMs = 0, naiveMs = 0, naiveSlowMs = 0;
            long awakeSum = 0, moveSum = 0;
            var sw = new Stopwatch();

            for (int f = 0; f < Warm + Frames; f++)
            {
                sw.Restart();
                var ev = w.Step(Dt, 4);
                double s = sw.Elapsed.TotalMilliseconds;

                // (a) batched: consume the move-event span (what Prowl does)
                sw.Restart();
                foreach (ref readonly var m in ev.Moves) { posX[m.BodyIndex] = m.X; posY[m.BodyIndex] = m.Y; rot[m.BodyIndex] = m.Angle; }
                double a = sw.Elapsed.TotalMilliseconds;
                int moves = ev.Moves.Length, awake = ev.AwakeBodyCount;

                if (f < Warm) continue;
                stepMs += s; batchedMs += a; awakeSum += awake; moveSum += moves;

                // (b) naive per-body P/Invoke, every body, with SuppressGCTransition
                sw.Restart();
                for (int i = 0; i < count; i++) { PB2.BodyGetState(ids[i], st); posX[i] = st[0]; posY[i] = st[1]; rot[i] = MathF.Atan2(st[3], st[2]); }
                naiveMs += sw.Elapsed.TotalMilliseconds;

                // (c) naive per-body P/Invoke, regular GC transition
                sw.Restart();
                for (int i = 0; i < count; i++) { BodyGetStateSlow(ids[i], st); posX[i] = st[0]; posY[i] = st[1]; rot[i] = MathF.Atan2(st[3], st[2]); }
                naiveSlowMs += sw.Elapsed.TotalMilliseconds;
            }

            double n = Frames;
            Console.WriteLine($"\n{count,6} bodies  (avg {awakeSum / n,7:F0} awake, {moveSum / n,7:F0} move events / frame)");
            Console.WriteLine($"  native step + event gather           {stepMs / n,8:F3} ms/frame");
            Console.WriteLine($"  read transforms: batched span        {batchedMs / n,8:F3} ms/frame   <- the design");
            Console.WriteLine($"  read transforms: per-body, no GC tr. {naiveMs / n,8:F3} ms/frame   ({naiveMs / Math.Max(batchedMs, 1e-9),5:F1}x batched)");
            Console.WriteLine($"  read transforms: per-body, GC trans. {naiveSlowMs / n,8:F3} ms/frame   ({naiveSlowMs / Math.Max(batchedMs, 1e-9),5:F1}x batched)");
        }
    }
}
