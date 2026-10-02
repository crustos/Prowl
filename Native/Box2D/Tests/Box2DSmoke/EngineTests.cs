// Integration tests: the REAL Rigidbody2D / Collider2D / PhysicsWorld2D (compiled unmodified from Prowl.Runtime) running
// in the headless mini-engine (EngineStubs/MiniEngine.cs) on top of the real native Box2D.

using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

internal static unsafe partial class Program
{
    // ---- helpers -------------------------------------------------------------------------

    private sealed class Probe : MonoBehaviour
    {
        public readonly List<string> Log = new();
        public readonly List<Collision2D> Begins = new();
        public Action<Collision2D>? OnBegin;

        public override void OnCollisionBegin2D(Collision2D c) { Log.Add("Begin"); Begins.Add(c); OnBegin?.Invoke(c); }
        public override void OnCollisionEnd2D(Collision2D c) => Log.Add("End");
        public override void OnTriggerEnter2D(Collider2D o) => Log.Add("Enter:" + o.GameObject.Name);
        public override void OnTriggerStay2D(Collider2D o) => Log.Add("Stay:" + o.GameObject.Name);
        public override void OnTriggerExit2D(Collider2D o) => Log.Add("Exit:" + o.GameObject.Name);
        public int Count(string prefix) => Log.Count(l => l.StartsWith(prefix));
    }

    private static void InScene(Action<Scene> body)
    {
        Debug.ResetForTests();
        CollisionMatrix.Reset();
        Time.FixedAccumulator = 0f;
        var scene = new Scene("test");
        try { body(scene); }
        finally { scene.Dispose(); CollisionMatrix.Reset(); }
    }

    /// <summary>Runs <paramref name="n"/> fixed steps, each followed by a frame drawn <paramref name="alpha"/> of the way into the next.</summary>
    private static void Fixed(Scene s, int n, float alpha = 1f)
    {
        for (int i = 0; i < n; i++)
        {
            s.FixedUpdate();
            Time.FixedAccumulator = alpha * Time.FixedDeltaTime;
            s.Update();
        }
    }

    private static GameObject MakeGround(Scene s, string name = "ground", float x = 0, float y = 0, float w = 100, float h = 1, int layer = 0)
    {
        var go = s.Create(name, active: false);
        go.LayerIndex = layer;
        go.Transform.LocalPosition = new Float3(x, y, 0);
        go.AddComponent<Probe>();
        go.AddComponent<BoxCollider2D>().Size = new Float2(w, h);
        go.SetActive(true);
        return go;
    }

    private static GameObject MakeBox(Scene s, string name, float x, float y, float w = 1, float h = 1, float z = 0, int layer = 0,
                                      RigidbodyInterpolation interp = RigidbodyInterpolation.None)
    {
        var go = s.Create(name, active: false);
        go.LayerIndex = layer;
        go.Transform.LocalPosition = new Float3(x, y, z);
        go.AddComponent<Probe>();
        go.AddComponent<Rigidbody2D>().Interpolation = interp;
        go.AddComponent<BoxCollider2D>().Size = new Float2(w, h);
        go.SetActive(true);
        return go;
    }

    private static GameObject MakeBall(Scene s, string name, float x, float y, float r = 0.5f, int layer = 0,
                                       RigidbodyInterpolation interp = RigidbodyInterpolation.None)
    {
        var go = s.Create(name, active: false);
        go.LayerIndex = layer;
        go.Transform.LocalPosition = new Float3(x, y, 0);
        go.AddComponent<Probe>();
        go.AddComponent<Rigidbody2D>().Interpolation = interp;
        go.AddComponent<CircleCollider2D>().Radius = r;
        go.SetActive(true);
        return go;
    }

    private static float AngleDeg(GameObject go)
    {
        Quaternion q = go.Transform.Rotation;
        return MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z)) * 180f / MathF.PI;
    }

    private static bool Hits(Scene s, float x, float y, Collider2D? expect = null)
    {
        var list = new List<Collider2D>();
        s.Physics2D.OverlapPoint(new Float2(x, y), list);
        return expect == null ? list.Count > 0 : list.Contains(expect);
    }

    private static void EngineTests()
    {
        Console.WriteLine("\n== engine-facing components (real Rigidbody2D / Collider2D / PhysicsWorld2D in the mini-engine) ==");
        TestFallEventsAndZ();
        TestInterpolation();
        TestChildColliderOnRotatedBody();
        TestScaleAndHierarchy();
        TestTriggers();
        TestLayersViaCollisionMatrix();
        TestMovingStaticCollider();
        TestTransformEdits();
        TestEnableDisableReattach();
        TestDestroyInsideHandler();
        TestConstraints();
        TestKinematicCarry();
        TestForceModes();
        TestTeleportAndMove();
        TestSceneOwnership();
        TestEngineQueries();
        TestShapes();
    }

    // ---- tests ---------------------------------------------------------------------------

    private static void TestFallEventsAndZ()
    {
        Console.WriteLine("falling box: rest height, Z preserved, collision payloads from both sides");
        InScene(s =>
        {
            var ground = MakeGround(s);
            var box = MakeBox(s, "box", 0, 3, z: 7);
            Fixed(s, 200);

            Float3 p = box.Transform.Position;
            Check(Near(p.Y, 1.0f, 0.05f) && Near(p.X, 0f, 0.05f), $"box rests on the ground (y = {p.Y:F3})");
            Check(p.Z == 7f, "Z (drawing depth) is never touched by physics");
            Check(Near(AngleDeg(box), 0f, 0.5f), $"landed flat (angle = {AngleDeg(box):F2} deg)");

            var bp = box.GetComponent<Probe>()!;
            var gp = ground.GetComponent<Probe>()!;
            Check(bp.Count("Begin") == 1 && gp.Count("Begin") == 1, $"exactly one begin per side ({bp.Count("Begin")}/{gp.Count("Begin")})");

            Collision2D onBox = bp.Begins[0], onGround = gp.Begins[0];
            Check(onBox.Collider.GameObject.Name == "ground" && onBox.Rigidbody == null,
                  "the box is told it hit 'ground', which has no Rigidbody2D");
            Check(onGround.Collider.GameObject.Name == "box" && ReferenceEquals(onGround.Rigidbody, box.GetComponent<Rigidbody2D>()),
                  "the ground is told it was hit by 'box' and gets that box's Rigidbody2D");
            Check(onBox.Normal.Y > 0.9f && onGround.Normal.Y < -0.9f,
                  $"Normal points from the OTHER collider toward the receiver (box sees y={onBox.Normal.Y:F2}: a ground check works)");
            Check(Near(onBox.Point.Y, 0.5f, 0.15f) && onBox.ImpulseMagnitude > 0f, $"point + impulse reach the component (y={onBox.Point.Y:F2}, impulse={onBox.ImpulseMagnitude:F2})");
            Check(Debug.Errors.Count == 0, "no errors logged");
        });
    }

    private static void TestInterpolation()
    {
        Console.WriteLine("interpolation between fixed steps");
        foreach (var mode in new[] { RigidbodyInterpolation.Interpolate, RigidbodyInterpolation.None })
        {
            InScene(s =>
            {
                var ball = MakeBall(s, "ball", 0, 100, interp: mode);
                var rb = ball.GetComponent<Rigidbody2D>()!;
                Fixed(s, 10, alpha: 1f);

                s.FixedUpdate(); // one more step, then draw frames at different points inside the next interval
                float Frame(float a) { Time.FixedAccumulator = a * Time.FixedDeltaTime; s.Update(); return ball.Transform.Position.Y; }
                float y0 = Frame(0f), yh = Frame(0.5f), y1 = Frame(1f);
                float sim = rb.Position.Y;

                if (mode == RigidbodyInterpolation.Interpolate)
                {
                    Check(y0 > yh && yh > y1 && Near(yh, (y0 + y1) / 2f, 1e-4f), $"Interpolate: alpha 0 / 0.5 / 1 -> {y0:F4} / {yh:F4} / {y1:F4}");
                    Check(Near(y1, sim, 1e-4f), "alpha 1 equals the true simulated position");
                }
                else
                {
                    Check(Near(y0, sim, 1e-5f) && Near(yh, sim, 1e-5f) && Near(y1, sim, 1e-5f), "None: Transform always shows the simulated position");
                }
            });
        }

        InScene(s =>
        {
            var ground = MakeGround(s);
            var box = MakeBox(s, "box", 0, 1.001f, interp: RigidbodyInterpolation.Interpolate);
            Fixed(s, 200, alpha: 0.5f);
            var t = box.Transform;
            uint v = t.Version;
            Fixed(s, 20, alpha: 0.5f);
            Check(t.Version == v, "a resting body does not dirty its Transform every frame");
        });
    }

    private static void TestChildColliderOnRotatedBody()
    {
        Console.WriteLine("child collider on a rotated rigidbody");
        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, 0);
            var rbGo = s.Create("rb", active: false);
            rbGo.Transform.LocalPosition = new Float3(10, 5, 0);
            rbGo.Transform.LocalAngle = MathF.PI / 2;
            var rb = rbGo.AddComponent<Rigidbody2D>();
            var child = s.Create("child", rbGo);
            child.Transform.LocalPosition = new Float3(2, 0, 0);
            var bc = child.AddComponent<BoxCollider2D>();
            bc.Size = new Float2(2f, 0.5f);
            rbGo.SetActive(true);
            Fixed(s, 3);

            Check(ReferenceEquals(bc.AttachedRigidbody, rb) && bc.IsInWorld, "child collider belongs to the parent's rigidbody");
            Check(Hits(s, 10f, 7f, bc), "centred at world (10,7): child offset (2,0) rotated by the body's 90 deg");
            Check(Hits(s, 10f, 7.9f, bc) && Hits(s, 10f, 6.1f, bc), "2 long along the body's X = tall in world Y");
            Check(!Hits(s, 10.5f, 7f) && !Hits(s, 12f, 5f), "0.25 half-width in world X; and not where an unrotated offset would put it");
        });
    }

    private static void TestScaleAndHierarchy()
    {
        Console.WriteLine("lossy scale folds into shapes");
        InScene(s =>
        {
            var go = s.Create("scaled", active: false);
            go.Transform.LocalScale = new Float3(2, 3, 1);
            var box = go.AddComponent<BoxCollider2D>();
            box.Size = new Float2(1, 1);
            go.SetActive(true);
            Fixed(s, 2);
            Check(Hits(s, 0.9f, 1.4f, box) && !Hits(s, 1.1f, 0f) && !Hits(s, 0f, 1.6f), "1x1 box scaled (2,3) is 2 wide and 3 tall");

            var parent = s.Create("parent", active: false);
            parent.Transform.LocalScale = new Float3(2, 2, 1);
            parent.Transform.LocalPosition = new Float3(20, 0, 0);
            var kid = s.Create("kid", parent);
            kid.Transform.LocalPosition = new Float3(1, 0, 0);
            kid.Transform.LocalScale = new Float3(0.5f, 0.5f, 1);
            var circle = kid.AddComponent<CircleCollider2D>();
            circle.Radius = 1f;
            parent.SetActive(true);
            Fixed(s, 2);
            // world centre (22,0); lossy scale 2*0.5 = 1, so radius 1
            Check(Hits(s, 22.9f, 0f, circle) && !Hits(s, 23.1f, 0f), "a child under a scaled parent: lossy scale = product of the chain");

            go.Transform.LocalScale = new Float3(4, 3, 1); // rescale at runtime -> the collider rebuilds on the next frame
            Fixed(s, 2);
            Check(Hits(s, 1.9f, 0f, box) && !Hits(s, 2.1f, 0f), "rescaling at runtime rebuilds the shape");
        });
    }

    private static void TestTriggers()
    {
        Console.WriteLine("triggers through the components");
        InScene(s =>
        {
            var zone = s.Create("zone", active: false);
            zone.Transform.LocalPosition = new Float3(0, 3, 0);
            zone.AddComponent<Probe>();
            var zc = zone.AddComponent<BoxCollider2D>();
            zc.Size = new Float2(10, 2);
            zc.IsTrigger = true;
            zone.SetActive(true);
            var ball = MakeBall(s, "ball", 0, 8, 0.4f);
            Fixed(s, 120);

            var zp = zone.GetComponent<Probe>()!;
            var bp = ball.GetComponent<Probe>()!;
            Check(zp.Count("Enter:ball") == 1 && bp.Count("Enter:zone") == 1, "Enter: each side told who it met");
            Check(zp.Count("Exit:ball") == 1 && bp.Count("Exit:zone") == 1, "Exit: each side told who left");
            Check(zp.Count("Stay:ball") >= 8 && zp.Count("Stay:ball") == bp.Count("Stay:zone"), $"Stay: symmetric ({zp.Count("Stay:ball")})");
            int e = zp.Log.IndexOf("Enter:ball"), x = zp.Log.IndexOf("Exit:ball");
            Check(e < zp.Log.IndexOf("Stay:ball") && zp.Log.LastIndexOf("Stay:ball") < x, "order is Enter, Stay..., Exit");
            Check(ball.Transform.Position.Y < 0f, "the trigger did not block the ball");
        });
    }

    private static void TestLayersViaCollisionMatrix()
    {
        Console.WriteLine("layers: the real CollisionMatrix reaches the native filter");
        InScene(s =>
        {
            MakeGround(s, layer: 3);
            var ball = MakeBall(s, "ball", 0, 3, layer: 28); // above layer 15: the fork's own 16-bit filter could not express this
            CollisionMatrix.SetLayerCollision(3, 28, false);
            Fixed(s, 120);
            Check(ball.Transform.Position.Y < -5f, $"layers 3/28 disabled in the matrix: falls through (y = {ball.Transform.Position.Y:F1})");
        });
        InScene(s =>
        {
            MakeGround(s, layer: 3);
            var ball = MakeBall(s, "ball", 0, 3, layer: 28);
            Fixed(s, 120);
            Check(Near(ball.Transform.Position.Y, 1.0f, 0.1f), $"default matrix: rests (y = {ball.Transform.Position.Y:F2})");
        });
        InScene(s =>
        {
            MakeGround(s, layer: 3);
            var ball = MakeBall(s, "ball", 0, 3, layer: 28);
            Fixed(s, 120);
            ball.LayerIndex = 4;               // changing the layer of a live collider rebuilds it with the new layer
            CollisionMatrix.SetLayerCollision(3, 4, false);
            ball.Transform.LocalPosition = new Float3(0, 1.0f, 0);
            Fixed(s, 120);
            Check(ball.Transform.Position.Y < -5f, "changing a live collider's layer takes effect");
        });
    }

    private static void TestMovingStaticCollider()
    {
        Console.WriteLine("moving a body-less collider moves its static body, and wakes what it affects");

        // The ball settles and falls asleep. A sleeping body is only correct to leave alone if nothing changed under it.
        InScene(s =>
        {
            var ground = MakeGround(s);
            var ball = MakeBall(s, "ball", 0, 2);
            var rb = ball.GetComponent<Rigidbody2D>()!;
            Fixed(s, 200);
            Check(Near(ball.Transform.Position.Y, 1.0f, 0.05f) && !rb.IsAwake, $"setup: the ball rests and has gone to sleep (y = {ball.Transform.Position.Y:F3}, awake = {rb.IsAwake})");
            ground.Transform.Position = new Float3(0, 0.3f, 0);          // rises 0.3 into the ball
            Fixed(s, 120);
            Check(Near(ball.Transform.Position.Y, 1.3f, 0.05f), $"ground rising into a sleeping ball pushes it up to rest on the new surface (y = {ball.Transform.Position.Y:F3})");
        });

        InScene(s =>
        {
            var ground = MakeGround(s);
            var ball = MakeBall(s, "ball", 0, 2);
            Fixed(s, 200);
            for (int i = 1; i <= 60; i++)
            {
                ground.Transform.Position = new Float3(0, 0.02f * i, 0);  // an elevator: 1.2 m over a second
                Fixed(s, 1);
            }
            Fixed(s, 30);
            Check(Near(ball.Transform.Position.Y, 1.0f + 1.2f, 0.1f), $"an elevator carries the ball up with it (y = {ball.Transform.Position.Y:F3})");
        });

        InScene(s =>
        {
            var ground = MakeGround(s);
            var ball = MakeBall(s, "ball", 0, 2);
            Fixed(s, 200);
            ground.Transform.Position = new Float3(0, -1, 0);             // lowered out from under the sleeping ball
            Fixed(s, 120);
            Check(Near(ball.Transform.Position.Y, 0.0f, 0.05f), $"ground lowered beneath a sleeping ball: it wakes and drops onto it (y = {ball.Transform.Position.Y:F3})");
        });

        InScene(s =>
        {
            var ground = MakeGround(s);
            var ball = MakeBall(s, "ball", 0, 2);
            Fixed(s, 200);
            ground.Transform.Position = new Float3(100, 0, 0);            // slid away entirely
            Fixed(s, 120);
            Check(ball.Transform.Position.Y < -5f, $"ground slid away: the sleeping ball wakes and falls (y = {ball.Transform.Position.Y:F1})");
        });
    }

    private static void TestTransformEdits()
    {
        Console.WriteLine("editing the Transform from game code");
        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, 0);
            var ball = MakeBall(s, "ball", 0, 10);
            var rb = ball.GetComponent<Rigidbody2D>()!;
            Fixed(s, 2);
            ball.Transform.Position = new Float3(5, 5, 0);
            Fixed(s, 1);
            Check(Near(rb.Position.X, 5f, 1e-3f) && Near(rb.Position.Y, 5f, 1e-3f), $"moving the Transform teleports the body ({rb.Position})");
            Check(Near(ball.Transform.Position.X, 5f, 1e-3f), "and the Transform stays where it was put");

            ball.Transform.LocalAngle = MathF.PI / 4;
            Fixed(s, 1);
            Check(Near(rb.Rotation, 45f, 0.1f), $"rotating the Transform rotates the body ({rb.Rotation:F1} deg)");
        });

        InScene(s =>
        {
            MakeGround(s, y: -1000);
            var ball = MakeBall(s, "ball", 0, 500, interp: RigidbodyInterpolation.Interpolate);
            var rb = ball.GetComponent<Rigidbody2D>()!;
            Fixed(s, 30, alpha: 0.5f);                  // Transform now trails the simulation by half a step
            float before = rb.Position.Y;
            Fixed(s, 1, alpha: 0.5f);
            float step = before - rb.Position.Y;        // how far one step moves it
            before = rb.Position.Y;
            ball.Transform.LocalScale = new Float3(1.5f, 1.5f, 1); // a scale edit bumps Transform.Version but is not a request to move
            s.FixedUpdate();
            float moved = before - rb.Position.Y;
            Check(Near(moved, step, step * 0.05f), $"a scale-only edit does not rewind an interpolated body (moved {moved:F4}, expected {step:F4})");
        });
    }

    private static void TestEnableDisableReattach()
    {
        Console.WriteLine("enable / disable moves colliders between a rigidbody and a static body");
        foreach (bool colliderFirst in new[] { false, true })
        {
            InScene(s =>
            {
                s.Physics2D.Gravity = new Float2(0, 0);
                var go = s.Create("go", active: false);
                go.Transform.LocalPosition = new Float3(0, 5, 0);
                Rigidbody2D rb;
                BoxCollider2D col;
                if (colliderFirst) { col = go.AddComponent<BoxCollider2D>(); rb = go.AddComponent<Rigidbody2D>(); }
                else { rb = go.AddComponent<Rigidbody2D>(); col = go.AddComponent<BoxCollider2D>(); }
                go.SetActive(true);
                Fixed(s, 2);

                string order = colliderFirst ? "collider added first" : "rigidbody added first";
                Check(ReferenceEquals(col.AttachedRigidbody, rb) && rb.IsSimulated, $"({order}) collider ends up on the rigidbody");

                rb.Enabled = false;
                Check(col.AttachedRigidbody == null && col.IsInWorld && !rb.IsSimulated && Hits(s, 0, 5, col),
                      $"({order}) disabling the rigidbody leaves the collider in the world as static geometry");

                rb.Enabled = true;
                Fixed(s, 1);
                Check(ReferenceEquals(col.AttachedRigidbody, rb) && rb.IsSimulated && Hits(s, 0, 5, col),
                      $"({order}) re-enabling reattaches it");

                col.Enabled = false;
                Check(!col.IsInWorld && !Hits(s, 0, 5), $"({order}) a disabled collider is out of the world");
                col.Enabled = true;
                Check(col.IsInWorld && Hits(s, 0, 5, col), $"({order}) and back in when enabled");

                go.SetActive(false);
                Check(!col.IsInWorld && !rb.IsSimulated && s.Physics2D.Simulation.BodyCount == 0 && s.Physics2D.Simulation.ColliderCount == 0,
                      $"({order}) deactivating the GameObject removes everything");
            });
        }

        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, 0);
            var outer = s.Create("outer", active: false);
            var outerRb = outer.AddComponent<Rigidbody2D>();
            var inner = s.Create("inner", outer);
            var innerRb = inner.AddComponent<Rigidbody2D>();
            var col = inner.AddComponent<CircleCollider2D>();
            outer.SetActive(true);
            Fixed(s, 1);
            Check(ReferenceEquals(col.AttachedRigidbody, innerRb), "a collider belongs to its NEAREST rigidbody");
            innerRb.Enabled = false;
            Fixed(s, 1);
            Check(ReferenceEquals(col.AttachedRigidbody, outerRb), "when the nearest one goes away it falls back to the next one up");
        });
    }

    private static void TestDestroyInsideHandler()
    {
        Console.WriteLine("destroying GameObjects from inside collision handlers");
        InScene(s =>
        {
            var ground = MakeGround(s);
            var ball = MakeBall(s, "ball", 0, 3);
            ground.GetComponent<Probe>()!.OnBegin = c => c.GameObject!.Dispose(); // the ground destroys what hit it
            bool threw = false;
            try { Fixed(s, 150); } catch (Exception ex) { threw = true; Console.WriteLine("    " + ex.Message); }
            Check(!threw && ball.IsDisposed, "destroying the other object in OnCollisionBegin2D is safe");
            Check(s.Physics2D.Simulation.BodyCount == 0 && s.Physics2D.Simulation.ColliderCount == 1, "its body and collider are gone; the ground remains");
        });
        InScene(s =>
        {
            var ground = MakeGround(s);
            var ball = MakeBall(s, "ball", 0, 3);
            ball.GetComponent<Probe>()!.OnBegin = _ => ball.Dispose(); // an object destroying itself
            bool threw = false;
            try { Fixed(s, 150); } catch (Exception) { threw = true; }
            Check(!threw && ball.IsDisposed && s.Physics2D.Simulation.BodyCount == 0, "an object destroying itself in its own handler is safe");
        });
        InScene(s =>
        {
            MakeGround(s);
            var balls = new List<GameObject>();
            for (int i = 0; i < 50; i++) balls.Add(MakeBall(s, $"b{i}", i * 0.3f, 2 + i * 0.1f, 0.15f));
            foreach (var b in balls) b.GetComponent<Probe>()!.OnBegin = _ => { foreach (var o in balls.Where(x => !x.IsDisposed).Take(3)) o.Dispose(); };
            bool threw = false;
            try { Fixed(s, 200); } catch (Exception) { threw = true; }
            Check(!threw, "a chain of handlers each destroying several others does not break the step");
        });
    }

    private static void TestConstraints()
    {
        Console.WriteLine("constraints");
        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, 0);
            var free = MakeBox(s, "free", -10, 0);
            var locked = MakeBox(s, "locked", 10, 0);
            locked.GetComponent<Rigidbody2D>()!.Constraints = RigidbodyConstraints2D.FreezeRotation;
            foreach (var go in new[] { free, locked })
                go.GetComponent<Rigidbody2D>()!.AddForceAtPosition(new Float2(5, 0), new Float2(go.Transform.Position.X, 0.5f), ForceMode.Impulse);
            Fixed(s, 30);
            Check(MathF.Abs(AngleDeg(free)) > 5f, $"control: an off-centre impulse spins a free body ({AngleDeg(free):F1} deg)");
            Check(Near(AngleDeg(locked), 0f, 0.01f) && locked.Transform.Position.X > 10.5f, $"FreezeRotation: no spin, still translates (x = {locked.Transform.Position.X:F2})");
        });
        InScene(s =>
        {
            var box = MakeBox(s, "box", 0, 5);
            box.GetComponent<Rigidbody2D>()!.Constraints = RigidbodyConstraints2D.FreezePositionY;
            box.GetComponent<Rigidbody2D>()!.LinearVelocity = new Float2(3, 0);
            Fixed(s, 60);
            Check(Near(box.Transform.Position.Y, 5f, 0.01f) && box.Transform.Position.X > 2f, $"FreezePositionY: held in Y against gravity, free in X ({box.Transform.Position})");
        });
        InScene(s =>
        {
            var box = MakeBox(s, "box", 0, 5);
            var rb = box.GetComponent<Rigidbody2D>()!;
            rb.Constraints = RigidbodyConstraints2D.FreezeAll;
            Fixed(s, 30);
            Check(Near(box.Transform.Position.Y, 5f, 0.01f), "FreezeAll");
            rb.Constraints = RigidbodyConstraints2D.None;
            Fixed(s, 30);
            Check(box.Transform.Position.Y < 4.5f, "constraints can be lifted at runtime");
        });
    }

    private static void TestKinematicCarry()
    {
        Console.WriteLine("kinematic MovePosition carries what rides on it");
        InScene(s =>
        {
            var plat = s.Create("plat", active: false);
            var prb = plat.AddComponent<Rigidbody2D>();
            prb.BodyType = BodyType2D.Kinematic;
            plat.AddComponent<BoxCollider2D>().Size = new Float2(10, 0.5f);
            plat.SetActive(true);
            var cargo = MakeBox(s, "cargo", 0, 0.76f);
            Fixed(s, 30);

            float x = 0;
            for (int i = 0; i < 60; i++)
            {
                x += 2f * Time.FixedDeltaTime;
                prb.MovePosition(new Float2(x, 0));
                Fixed(s, 1);
            }
            float platX = prb.Position.X, cargoX = cargo.Transform.Position.X;
            Check(Near(platX, 2f, 0.05f), $"platform glided to its targets (x = {platX:F2})");
            Check(cargoX > 1.0f && cargoX < platX + 0.1f && Near(cargo.Transform.Position.Y, 0.75f, 0.05f),
                  $"cargo was carried by friction (x = {cargoX:F2}) and stayed on top (y = {cargo.Transform.Position.Y:F2})");

            var dyn = MakeBox(s, "dyn", 50, 50);
            dyn.GetComponent<Rigidbody2D>()!.MovePosition(new Float2(60, 60));
            Check(Near(dyn.GetComponent<Rigidbody2D>()!.Position.X, 60f, 1e-3f), "MovePosition on a dynamic body teleports");
        });
    }

    private static void TestForceModes()
    {
        Console.WriteLine("force modes + units");
        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, 0);
            var go = MakeBall(s, "ball", 0, 0);
            var rb = go.GetComponent<Rigidbody2D>()!;
            rb.Mass = 2f;
            rb.AngularDamping = 0f;
            Fixed(s, 1);

            rb.AddForce(new Float2(4, 0), ForceMode.Impulse);
            Check(Near(rb.LinearVelocity.X, 2f, 1e-3f), $"Impulse 4 on mass 2 -> 2 m/s ({rb.LinearVelocity.X:F3})");
            rb.AddForce(new Float2(3, 0), ForceMode.VelocityChange);
            Check(Near(rb.LinearVelocity.X, 5f, 1e-3f), $"VelocityChange ignores mass -> 5 m/s ({rb.LinearVelocity.X:F3})");

            float dt = Time.FixedDeltaTime;
            rb.AddForce(new Float2(10, 0), ForceMode.Force);
            s.FixedUpdate();
            Check(Near(rb.LinearVelocity.X, 5f + 10f / 2f * dt, 0.01f), $"Force 10 on mass 2 for one step -> +{10f / 2f * dt:F3} ({rb.LinearVelocity.X:F3})");
            float v = rb.LinearVelocity.X;
            rb.AddForce(new Float2(6, 0), ForceMode.Acceleration);
            s.FixedUpdate();
            Check(Near(rb.LinearVelocity.X, v + 6f * dt, 0.01f), $"Acceleration 6 ignores mass -> +{6f * dt:F3} ({rb.LinearVelocity.X:F3})");

            rb.AddTorque(90f, ForceMode.VelocityChange);
            Check(Near(rb.AngularVelocity, 90f, 0.5f), $"torque VelocityChange 90 deg/s -> {rb.AngularVelocity:F1} deg/s");
            rb.AngularVelocity = -45f;
            Check(Near(rb.AngularVelocity, -45f, 0.01f), "AngularVelocity set/get is in degrees per second");
            rb.AddTorque(5f, ForceMode.Impulse);
            Check(rb.AngularVelocity > -45f, "positive torque impulse speeds up counter-clockwise rotation");
        });
    }

    private static void TestTeleportAndMove()
    {
        Console.WriteLine("Position / Rotation setters");
        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, 0);
            var go = MakeBall(s, "ball", 0, 0);
            var rb = go.GetComponent<Rigidbody2D>()!;
            rb.LinearVelocity = new Float2(1, 0);
            Fixed(s, 1);
            rb.Position = new Float2(3, 4);
            Check(Near(go.Transform.Position.X, 3f, 1e-4f) && Near(go.Transform.Position.Y, 4f, 1e-4f), "setting Position moves the Transform immediately");
            Check(Near(rb.LinearVelocity.X, 1f, 1e-3f), "and keeps the velocity");
            rb.Rotation = 90f;
            Check(Near(AngleDeg(go), 90f, 0.01f) && Near(rb.Rotation, 90f, 0.01f), "setting Rotation (degrees) turns it");
            Fixed(s, 1);
            Check(Near(rb.Position.X, 3f + Time.FixedDeltaTime, 0.01f), $"and the body carries on from the new place ({rb.Position})");
        });
    }

    private static void TestSceneOwnership()
    {
        Console.WriteLine("one scene at a time owns 2D physics");
        Debug.ResetForTests();
        var a = new Scene("A");
        var b = new Scene("B");
        try
        {
            var ballA = MakeBall(a, "a", 0, 10);
            var ballB = MakeBall(b, "b", 0, 10);
            var rbA = ballA.GetComponent<Rigidbody2D>()!;
            var rbB = ballB.GetComponent<Rigidbody2D>()!;
            Check(rbA.IsSimulated && !rbB.IsSimulated, "the first scene is simulated; the second scene's body stays inert");
            Check(!ballB.GetComponent<CircleCollider2D>()!.IsInWorld, "and so does its collider");
            Check(Debug.Errors.Count(e => e.Contains("Another scene already owns")) == 1, "one clear error is logged, once");

            a.Dispose();
            b.FixedUpdate();
            var ballC = MakeBall(b, "c", 0, 10);
            var rbC = ballC.GetComponent<Rigidbody2D>()!;
            Check(rbC.IsSimulated, "once the first scene is disposed, the second can take the world");
            Fixed(b, 30);
            Check(ballC.Transform.Position.Y < 9f, "and simulates normally");
        }
        finally { a.Dispose(); b.Dispose(); }

        // Replacing the running scene: the old one is torn down before the new one enables.
        InScene(old =>
        {
            MakeBall(old, "x", 0, 10);
            old.Dispose();
            var next = new Scene("next");
            try
            {
                var ball = MakeBall(next, "y", 0, 10);
                Check(ball.GetComponent<Rigidbody2D>()!.IsSimulated, "scene swap: the incoming scene gets the world");
            }
            finally { next.Dispose(); }
        });
    }

    private static void TestEngineQueries()
    {
        Console.WriteLine("scene queries");
        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, 0);
            MakeGround(s);
            var platform = MakeGround(s, "platform", 0, 3, 4, 1, layer: 2);
            var ball = MakeBall(s, "ball", 0, 8);
            var zone = s.Create("zone", active: false);
            zone.Transform.LocalPosition = new Float3(0, 10, 0);
            var zc = zone.AddComponent<BoxCollider2D>();
            zc.Size = new Float2(4, 1); zc.IsTrigger = true;
            zone.SetActive(true);
            Fixed(s, 2);
            var rb = ball.GetComponent<Rigidbody2D>()!;
            var W = s.Physics2D;

            Check(W.Raycast(new Float2(0, 14), new Float2(0, -1), out var h) && h.Collider == zc && Near(h.Distance, 3.5f, 0.01f),
                  $"raycast hits the trigger first by default ({h.Collider?.GameObject.Name}, {h.Distance:F2})");
            Check(W.Raycast(new Float2(0, 14), new Float2(0, -1), out h, 100f, new QueryFilter2D { ExcludeTriggers = true }) && h.Rigidbody == rb
                  && Near(h.Distance, 5.5f, 0.01f), $"ExcludeTriggers skips it -> the ball ({h.Collider?.GameObject.Name}, {h.Distance:F2})");
            Check(W.Raycast(new Float2(0, 14), new Float2(0, -1), out h, 100f, QueryFilter2D.Default.Ignoring(rb).Ignoring(zc)) && h.Collider.GameObject == platform,
                  $"Ignoring(collider) + Ignoring(rigidbody) falls through to the platform ({h.Collider?.GameObject.Name})");
            Check(W.Raycast(new Float2(0, 14), new Float2(0, -1), out h, 100f, new QueryFilter2D(LayerMask.FromMask(~(1u << 2)) ) { ExcludeTriggers = true, IgnoreRigidbody = rb })
                  && h.Collider.GameObject.Name == "ground", "layer mask excluding layer 2 skips the platform");
            Check(Near(h.Normal.Y, 1f, 0.01f) && Near(h.Point.Y, 0.5f, 0.01f), "hit point and normal");
            Check(!W.Raycast(new Float2(0, 14), new Float2(0, 1), out _), "ray pointing away hits nothing");
            Check(W.Raycast(new Float2(0, 14), new Float2(0, -1), out h) && h.Distance < 100000f, "default maxDistance (float.MaxValue) works");

            var hits = new List<RaycastHit2D>();
            int n = W.RaycastAll(new Float2(0, 14), new Float2(0, -1), 100f, hits);
            Check(n == 4 && hits.Select(x => x.Collider.GameObject.Name).SequenceEqual(new[] { "zone", "ball", "platform", "ground" })
                  && hits.Zip(hits.Skip(1)).All(p => p.First.Distance <= p.Second.Distance),
                  $"RaycastAll: nearest first ({string.Join(",", hits.Select(x => x.Collider.GameObject.Name))})");

            var found = new List<Collider2D>();
            Check(W.OverlapCircle(new Float2(0, 8), 1f, found) == 1 && found[0].AttachedRigidbody == rb, "OverlapCircle finds the ball");
            Check(W.OverlapCircle(new Float2(0, 8), 1f, found, QueryFilter2D.Default.Ignoring(rb)) == 0 && found.Count == 0, "and Ignoring the rigidbody finds nothing");
            Check(W.OverlapBox(new Float2(0, 3), new Float2(1, 1), 45f, found) == 1 && found[0].GameObject == platform, "OverlapBox (rotated) finds the platform");
            Check(W.OverlapPoint(new Float2(0, 10), found, new QueryFilter2D { ExcludeTriggers = true }) == 0, "OverlapPoint with ExcludeTriggers skips the zone");
            Check(W.OverlapPoint(new Float2(0, 10), found) == 1 && found[0] == zc, "OverlapPoint finds the zone otherwise");
        });
    }

    private static void TestShapes()
    {
        Console.WriteLine("every shape type");
        InScene(s =>
        {
            MakeGround(s);
            var cap = s.Create("capsule", active: false);
            cap.Transform.LocalPosition = new Float3(-10, 1.2f, 0);
            cap.AddComponent<Rigidbody2D>();
            cap.AddComponent<CapsuleCollider2D>().Size = new Float2(0.5f, 1f);
            cap.SetActive(true);

            var lying = s.Create("lying", active: false);
            lying.Transform.LocalPosition = new Float3(-5, 1.5f, 0);
            lying.AddComponent<Rigidbody2D>();
            var lc = lying.AddComponent<CapsuleCollider2D>();
            lc.Size = new Float2(0.5f, 1f);
            lc.Rotation = 90f;
            lying.SetActive(true);

            var tri = s.Create("tri", active: false);
            tri.Transform.LocalPosition = new Float3(5, 1.5f, 0);
            tri.AddComponent<Rigidbody2D>();
            tri.AddComponent<PolygonCollider2D>().Points = [new Float2(-0.5f, -0.5f), new Float2(0.5f, -0.5f), new Float2(0, 0.5f)];
            tri.SetActive(true);

            var rounded = s.Create("rounded", active: false);
            rounded.Transform.LocalPosition = new Float3(10, 1.5f, 0);
            rounded.AddComponent<Rigidbody2D>();
            var bc = rounded.AddComponent<BoxCollider2D>();
            bc.Size = new Float2(1, 1); bc.EdgeRadius = 0.25f;
            rounded.SetActive(true);

            Fixed(s, 300);
            Check(Near(cap.Transform.Position.Y, 1.0f, 0.06f), $"standing capsule 0.5x1 rests with its base on the ground (y = {cap.Transform.Position.Y:F3})");
            Check(Near(lying.Transform.Position.Y, 0.75f, 0.06f), $"capsule rotated 90 deg lies on its side (y = {lying.Transform.Position.Y:F3})");
            Check(Near(tri.Transform.Position.Y, 1.0f, 0.06f), $"triangle rests on its flat edge (y = {tri.Transform.Position.Y:F3})");
            Check(Near(rounded.Transform.Position.Y, 1.25f, 0.06f), $"EdgeRadius rounds outward: 1x1 box + 0.25 rests at y = {rounded.Transform.Position.Y:F3}");
        });

        foreach (bool reversed in new[] { false, true })
        {
            InScene(s =>
            {
                var terrain = s.Create("terrain", active: false);
                var edge = terrain.AddComponent<EdgeCollider2D>();
                edge.Points = reversed ? [new Float2(10, 0), new Float2(0, 0), new Float2(-10, 0)] : [new Float2(-10, 0), new Float2(0, 0), new Float2(10, 0)];
                terrain.SetActive(true);
                var ball = MakeBall(s, "ball", 0, 3);
                Fixed(s, 200);
                Check(Near(ball.Transform.Position.Y, 0.5f, 0.05f), $"EdgeCollider2D is two-sided, points {(reversed ? "right->left" : "left->right")} (y = {ball.Transform.Position.Y:F3})");
            });
        }

        InScene(s =>
        {
            var loop = s.Create("loop", active: false);
            var e = loop.AddComponent<EdgeCollider2D>();
            e.Points = [new Float2(-3, 0), new Float2(3, 0), new Float2(3, 6), new Float2(-3, 6)];
            e.IsLoop = true;
            loop.SetActive(true);
            var ball = MakeBall(s, "ball", 0, 3);
            ball.GetComponent<Rigidbody2D>()!.LinearVelocity = new Float2(30, 40);
            Fixed(s, 240);
            var p = ball.Transform.Position;
            Check(p.X > -3.01f && p.X < 3.01f && p.Y > -0.01f && p.Y < 6.01f, $"a looped edge contains a fast ball ({p})");
        });

        InScene(s =>
        {
            var bad = s.Create("bad", active: false);
            bad.AddComponent<PolygonCollider2D>().Points = Enumerable.Range(0, 9).Select(i => new Float2(MathF.Cos(i), MathF.Sin(i))).ToArray();
            bad.SetActive(true);
            Check(Debug.Errors.Any(m => m.Contains("needs 3 to 8 points")), "a polygon with 9 points logs a clear error instead of failing silently");

            Debug.ResetForTests();
            var flat = s.Create("flat", active: false);
            flat.AddComponent<PolygonCollider2D>().Points = [new Float2(0, 0), new Float2(1, 0), new Float2(2, 0)];
            flat.SetActive(true);
            Check(Debug.Errors.Any(m => m.Contains("valid convex polygon")), "collinear points log a clear error");
        });
    }
}
