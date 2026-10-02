// Editor-facing tests: gizmo geometry against real Box2D shapes, gizmo colours and handles, independence from the
// physics world, and a lint of the inspector metadata using the same lookup rules the editor's attribute handlers use.

// Reflection is the point of the inspector lint (it does what the editor's attribute handlers do), and this is test-only
// code, so the AOT/trim analyzers - which stay on for the engine files in this project - are silenced here only.
#pragma warning disable IL2070, IL2075

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

internal static unsafe partial class Program
{
    private static List<(Float3 A, Float3 B, Color Color, bool Dashed)> Capture(Collider2D c, bool selected = false)
    {
        Debug.Lines.Clear();
        if (selected) c.DrawGizmosSelected(); else c.DrawGizmos();
        return Debug.Lines.ToList();
    }

    private static GameObject Placed(Scene s, string name, float x, float y, float angleDeg = 0, float sx = 1, float sy = 1, GameObject? parent = null)
    {
        var go = s.Create(name, parent, active: parent != null);
        go.Transform.LocalPosition = new Float3(x, y, 0);
        go.Transform.LocalAngle = angleDeg * MathF.PI / 180f;
        go.Transform.LocalScale = new Float3(sx, sy, 1);
        return go;
    }

    private static void GizmoTests()
    {
        Console.WriteLine("\n== editor: gizmos + inspector ==");
        TestGizmoOutlinesMatchShapes();
        TestGizmoColorsAndHandles();
        TestGizmosIndependentOfPhysics();
        TestInspectorContract();
        TestInspectorEditsReachTheSimulation();
    }

    // ---- gizmo geometry ------------------------------------------------------------------

    private static void TestGizmoOutlinesMatchShapes()
    {
        Console.WriteLine("every drawn segment touches the real shape, and the space just outside it is empty");
        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, 0);
            var cases = new List<(string name, Collider2D col, bool open)>();

            var g = Placed(s, "circle", -30, 0, 30, 2, 3); var circle = g.AddComponent<CircleCollider2D>();
            circle.Radius = 0.7f; circle.Offset = new Float2(0.5f, 0.2f); cases.Add(("circle, scale (2,3), offset", circle, false));

            g = Placed(s, "box", -20, 0, 40, 2, 1); var box = g.AddComponent<BoxCollider2D>();
            box.Size = new Float2(1.5f, 0.8f); box.EdgeRadius = 0.2f; box.Offset = new Float2(-0.3f, 0.4f); box.Rotation = 15;
            cases.Add(("rounded box, scale (2,1), rotated twice, offset", box, false));

            var parent = Placed(s, "parent", -10, 0, 70, 1.2f, 1.2f); parent.AddComponent<Rigidbody2D>();
            var child = Placed(s, "child", 1, 0.5f, 25, 1.5f, 1, parent); var capsule = child.AddComponent<CapsuleCollider2D>();
            capsule.Size = new Float2(0.6f, 1.8f);
            cases.Add(("capsule on a child of a rotated + scaled rigidbody", capsule, false));

            g = Placed(s, "lying", 0, 0, -20); var lying = g.AddComponent<CapsuleCollider2D>();
            lying.Size = new Float2(2f, 0.5f); lying.Direction = CapsuleDirection2D.Horizontal; cases.Add(("horizontal capsule", lying, false));

            g = Placed(s, "stubby", 8, 0); var stubby = g.AddComponent<CapsuleCollider2D>();
            stubby.Size = new Float2(0.5f, 0.4f); cases.Add(("capsule too short to have a straight section (a circle)", stubby, false));

            g = Placed(s, "pentagon", 15, 0, 55, 1.3f, 0.7f); var pent = g.AddComponent<PolygonCollider2D>();
            pent.Points = Enumerable.Range(0, 5).Select(i => new Float2(MathF.Cos(i * 1.2566f), MathF.Sin(i * 1.2566f))).ToArray();
            pent.Offset = new Float2(0.4f, -0.2f); cases.Add(("pentagon, scale (1.3,0.7), rotated, offset", pent, false));

            g = Placed(s, "mirrored", 22, 0, 0, -1, 1); var mir = g.AddComponent<PolygonCollider2D>();
            mir.Points = [new Float2(-1, -0.5f), new Float2(1.5f, -0.5f), new Float2(0, 1)]; cases.Add(("triangle under a mirrored (negative) scale", mir, false));

            g = Placed(s, "edge", 30, 0, 20, 1.5f, 1); var edge = g.AddComponent<EdgeCollider2D>();
            edge.Points = [new Float2(-2, 0), new Float2(-1, 1), new Float2(0, 0), new Float2(1, 1.5f), new Float2(2, 0)];
            cases.Add(("open edge chain, scaled + rotated", edge, true));

            g = Placed(s, "loop", 40, 0, -35); var loop = g.AddComponent<EdgeCollider2D>();
            loop.Points = [new Float2(-1.5f, -1), new Float2(1.5f, -1), new Float2(1.5f, 1), new Float2(-1.5f, 1)]; loop.IsLoop = true;
            cases.Add(("closed edge loop", loop, true));

            g = Placed(s, "trigger", 50, 0, 10); var trig = g.AddComponent<BoxCollider2D>();
            trig.Size = new Float2(2, 1); trig.IsTrigger = true; cases.Add(("trigger box", trig, false));

            foreach (var go in s.AllForTests()) go.SetActive(true);
            Fixed(s, 2);

            var near = new List<Collider2D>();
            foreach (var (name, col, open) in cases)
            {
                var lines = Capture(col).Where(l => l.Color != Color.White).ToList();
                float minX = lines.Min(l => MathF.Min(l.A.X, l.B.X)), maxX = lines.Max(l => MathF.Max(l.A.X, l.B.X));
                float minY = lines.Min(l => MathF.Min(l.A.Y, l.B.Y)), maxY = lines.Max(l => MathF.Max(l.A.Y, l.B.Y));
                float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

                int touchMiss = 0, outsideHit = 0, probed = 0;
                bool Touches(float x, float y) { s.Physics2D.OverlapCircle(new Float2(x, y), 0.02f, near); return near.Contains(col); }
                bool Inside(float x, float y) { s.Physics2D.OverlapPoint(new Float2(x, y), near); return near.Contains(col); }

                foreach (var l in lines)
                {
                    float dx = l.B.X - l.A.X, dy = l.B.Y - l.A.Y, len = MathF.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-4f) continue;
                    float mx = (l.A.X + l.B.X) / 2, my = (l.A.Y + l.B.Y) / 2;
                    foreach (var (px, py) in new[] { (l.A.X, l.A.Y), (mx, my), (l.B.X, l.B.Y) })
                        if (!Touches(px, py)) touchMiss++;

                    float nx = dy / len, ny = -dx / len;
                    if (len < 0.03f) continue;      // degenerate chords only; arcs are smooth, so short ones are still fine to probe
                    probed++;
                    if (open)
                    {
                        if (Inside(mx + nx * 0.08f, my + ny * 0.08f) || Inside(mx - nx * 0.08f, my - ny * 0.08f)) outsideHit++;
                    }
                    else
                    {
                        if ((mx - cx) * nx + (my - cy) * ny < 0) { nx = -nx; ny = -ny; } // point away from the middle
                        if (Inside(mx + nx * 0.08f, my + ny * 0.08f)) outsideHit++;
                    }
                }
                Check(touchMiss == 0 && outsideHit == 0 && probed > 0, $"{name}: {lines.Count} segments, touch misses {touchMiss}, hits just outside {outsideHit}");
            }
        });
    }

    // ---- colours, handles, independence from physics --------------------------------------

    private static void TestGizmoColorsAndHandles()
    {
        Console.WriteLine("gizmo colours, handles and invalid shapes");
        InScene(s =>
        {
            var solid = Placed(s, "solid", 0, 0); var sc = solid.AddComponent<BoxCollider2D>();
            var trig = Placed(s, "trig", 10, 0); var tc = trig.AddComponent<CircleCollider2D>(); tc.IsTrigger = true;
            var flat = Placed(s, "flat", 20, 0); var fc = flat.AddComponent<PolygonCollider2D>();
            fc.Points = [new Float2(0, 0), new Float2(1, 0), new Float2(2, 0)];
            var many = Placed(s, "many", 30, 0); var mc = many.AddComponent<PolygonCollider2D>();
            mc.Points = Enumerable.Range(0, 9).Select(i => new Float2(MathF.Cos(i), MathF.Sin(i))).ToArray();
            var quad = Placed(s, "quad", 40, 0); var qc = quad.AddComponent<PolygonCollider2D>();
            var parent = Placed(s, "parent", 50, 5); parent.AddComponent<Rigidbody2D>();
            var kid = Placed(s, "kid", 1, 0, 0, 1, 1, parent); var kc = kid.AddComponent<BoxCollider2D>();
            foreach (var go in s.AllForTests()) go.SetActive(true);
            Debug.Errors.Clear();

            Check(Capture(sc).All(l => l.Color == Color.Green) && Capture(sc).Count == 4, "solid collider: green");
            Check(Capture(tc).All(l => l.Color == Color.Yellow), "trigger: yellow");
            var flatLines = Capture(fc);
            Check(flatLines.Count > 0 && flatLines.All(l => l.Color == Color.Red), $"collinear polygon: its raw points are drawn in red ({flatLines.Count} segments)");
            var manyLines = Capture(mc);
            Check(manyLines.Count == 9 && manyLines.All(l => l.Color == Color.Red), $"9-point polygon: raw loop in red ({manyLines.Count} segments)");

            var sel = Capture(qc, selected: true);
            int whites = sel.Count(l => l.Color == Color.White), links = sel.Count(l => l.Dashed);
            Check(whites == 2 + 4 * 2 && links == 0, $"selected polygon: a centre cross + a cross per vertex ({whites} white lines), no link ({links})");
            var selKid = Capture(kc, selected: true);
            Check(selKid.Count(l => l.Dashed && l.Color == Color.Cyan) == 1, "a collider on a child of a rigidbody draws a dashed link to it when selected");
            var selSame = Capture(sc, selected: true);
            Check(selSame.Count(l => l.Dashed) == 0, "no link when the collider is on the rigidbody's own GameObject (or there is none)");

            Check(Debug.Errors.Count == 0, "drawing gizmos logs no errors, even for invalid shapes");
        });

        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, 0);   // before anything steps, so the body stays exactly where it was put
            var go = s.Create("body", active: false);
            go.Transform.LocalPosition = new Float3(3, 4, 0);
            var rb = go.AddComponent<Rigidbody2D>();
            go.SetActive(true);
            Fixed(s, 1);
            rb.LinearVelocity = new Float2(8, 0);
            Debug.Lines.Clear();
            rb.DrawGizmosSelected();
            var l = Debug.Lines;
            Check(l.Count == 3 && Near(l[2].B.X, 3 + 8 * 0.25f, 1e-3f) && Near(l[2].B.Y, 4f, 1e-3f), $"rigidbody gizmo: origin cross + velocity line to where it'd be in 0.25 s ({l.Count} lines)");
            Debug.Lines.Clear();
            var idle = s.Create("idle", active: false);
            var rb2 = idle.AddComponent<Rigidbody2D>();
            rb2.DrawGizmosSelected();
            Check(Debug.Lines.Count == 2, "a body that is not simulated (edit mode) still draws its origin cross, without velocity");
        });
    }

    private static void TestGizmosIndependentOfPhysics()
    {
        Console.WriteLine("gizmos need no physics world (they draw in the editor with nothing playing)");
        InScene(s =>
        {
            var go = Placed(s, "g", 2, 3, 33, 1.5f, 0.8f);
            var col = go.AddComponent<BoxCollider2D>();
            col.Size = new Float2(2, 1); col.Offset = new Float2(0.5f, 0.2f); col.EdgeRadius = 0.1f;

            var before = Capture(col);
            Check(!col.IsInWorld && before.Count > 0, $"before enabling: not in the world, yet it draws ({before.Count} segments)");
            go.SetActive(true);
            Fixed(s, 2);
            var after = Capture(col);
            bool same = before.Count == after.Count && before.Zip(after).All(p => Near(p.First.A.X, p.Second.A.X, 1e-5f) && Near(p.First.A.Y, p.Second.A.Y, 1e-5f)
                                                                                 && Near(p.First.B.X, p.Second.B.X, 1e-5f) && Near(p.First.B.Y, p.Second.B.Y, 1e-5f));
            Check(col.IsInWorld && same, "the outline is identical whether or not the collider is in the world");
        });

        // A scene that cannot get the native world (a preview next to the running game) still draws its colliders.
        Debug.ResetForTests();
        var running = new Scene("running");
        var preview = new Scene("preview");
        try
        {
            MakeBall(running, "ball", 0, 0);
            var go = Placed(preview, "p", 5, 5);
            var col = go.AddComponent<CircleCollider2D>();
            go.SetActive(true);
            Check(!col.IsInWorld && Capture(col).Count > 0, "a collider kept inert by world ownership still draws its gizmo");
        }
        finally { running.Dispose(); preview.Dispose(); }
    }

    // ---- inspector -----------------------------------------------------------------------

    private static IEnumerable<FieldInfo> SerializedFields(Type type)
    {
        for (Type? t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (f.IsDefined(typeof(SerializeFieldAttribute), false)) yield return f;
    }

    // The exact lookup ShowIfAttributeHandler / EnableIfAttributeHandler perform (Prowl.Editor/GUI/AttributeHandlers.cs).
    private static bool? EditorCondition(Type concrete, object target, string member)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var f = concrete.GetField(member, flags);
        if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(target)!;
        var p = concrete.GetProperty(member, flags);
        if (p != null && p.PropertyType == typeof(bool)) return (bool)p.GetValue(target)!;
        return null; // the editor shows the field when the member is not found: a silent bug, so the lint rejects it
    }

    private static void SetField(object target, string name, object? value)
    {
        for (Type? t = target.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f != null) { f.SetValue(target, value); return; }
        }
        throw new MissingFieldException(target.GetType().Name, name);
    }

    private static void TestInspectorContract()
    {
        Console.WriteLine("inspector metadata: what the editor will actually do with it");
        Type[] types = [typeof(Rigidbody2D), typeof(CircleCollider2D), typeof(BoxCollider2D), typeof(CapsuleCollider2D), typeof(PolygonCollider2D), typeof(EdgeCollider2D)];

        var noTooltip = new List<string>(); var badCondition = new List<string>(); var badRange = new List<string>(); var badButton = new List<string>();
        int fields = 0;
        InScene(s =>
        {
            foreach (Type type in types)
            {
                var go = s.Create(type.Name, active: false);
                object target = type == typeof(Rigidbody2D) ? go.AddComponent<Rigidbody2D>()
                    : type == typeof(CircleCollider2D) ? go.AddComponent<CircleCollider2D>()
                    : type == typeof(BoxCollider2D) ? go.AddComponent<BoxCollider2D>()
                    : type == typeof(CapsuleCollider2D) ? go.AddComponent<CapsuleCollider2D>()
                    : type == typeof(PolygonCollider2D) ? go.AddComponent<PolygonCollider2D>()
                    : go.AddComponent<EdgeCollider2D>();

                foreach (FieldInfo f in SerializedFields(type))
                {
                    fields++;
                    string where = $"{type.Name}.{f.Name}";
                    if (string.IsNullOrWhiteSpace(f.GetCustomAttribute<TooltipAttribute>()?.Text)) noTooltip.Add(where);

                    foreach (string member in new[] { f.GetCustomAttribute<ShowIfAttribute>()?.ConditionMember, f.GetCustomAttribute<EnableIfAttribute>()?.ConditionMember }.OfType<string>())
                        if (EditorCondition(type, target, member) == null) badCondition.Add($"{where} -> {member}");

                    var range = f.GetCustomAttribute<RangeAttribute>();
                    if (range != null && ((f.FieldType != typeof(float) && f.FieldType != typeof(int)) || range.Min >= range.Max)) badRange.Add(where);
                }

                foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.GetCustomAttribute<ButtonAttribute>() != null && m.GetParameters().Length > 0) badButton.Add($"{type.Name}.{m.Name}");

                string? menu = type.GetCustomAttribute<AddComponentMenuAttribute>()?.Path;
                Check(menu != null && menu.StartsWith("Physics 2D/"), $"{type.Name}: Add Component menu path '{menu}'");
                Check(type.GetCustomAttribute<ComponentIconAttribute>(inherit: true) != null, $"{type.Name}: has a component icon");
            }
        });
        Check(noTooltip.Count == 0, $"all {fields} serialized fields have a tooltip" + (noTooltip.Count > 0 ? $" - missing: {string.Join(", ", noTooltip)}" : ""));
        Check(badCondition.Count == 0, "every [ShowIf]/[EnableIf] names a bool member the editor can actually find" + (badCondition.Count > 0 ? $" - broken: {string.Join(", ", badCondition)}" : ""));
        Check(badRange.Count == 0, "every [Range] is on a float/int field with min < max");
        Check(badButton.Count == 0, "every [Button] method is parameterless (the editor skips the rest)");

        // The conditions must show and hide the right fields as the body type changes.
        InScene(s =>
        {
            var go = s.Create("rb", active: false);
            var rb = go.AddComponent<Rigidbody2D>();
            HashSet<string> Visible()
            {
                var set = new HashSet<string>();
                foreach (FieldInfo f in SerializedFields(typeof(Rigidbody2D)))
                {
                    string? cond = f.GetCustomAttribute<ShowIfAttribute>()?.ConditionMember;
                    if (cond == null || EditorCondition(typeof(Rigidbody2D), rb, cond) == true) set.Add(f.Name);
                }
                return set;
            }
            string[] dynamicOnly = ["mass", "gravityScale", "linearDamping", "angularDamping", "constraints", "isBullet"];
            string[] movable = ["canSleep", "interpolation"];

            rb.BodyType = BodyType2D.Dynamic;
            var v = Visible();
            Check(dynamicOnly.Concat(movable).Append("bodyType").All(v.Contains), "Dynamic: every setting is shown");
            rb.BodyType = BodyType2D.Kinematic;
            v = Visible();
            Check(v.Contains("bodyType") && movable.All(v.Contains) && !dynamicOnly.Any(v.Contains), "Kinematic: sleep + interpolation shown; mass, gravity, damping, constraints, bullet hidden");
            rb.BodyType = BodyType2D.Static;
            v = Visible();
            Check(v.Count == 1 && v.Contains("bodyType"), $"Static: only the body type is shown ({string.Join(",", v)})");
        });

        // [Button] methods work when invoked the way the editor invokes them.
        InScene(s =>
        {
            var go = s.Create("poly", active: false);
            var poly = go.AddComponent<PolygonCollider2D>();
            poly.Points = [new Float2(0, 0), new Float2(1, 0), new Float2(0, 1)];
            typeof(PolygonCollider2D).GetMethods().First(m => m.GetCustomAttribute<ButtonAttribute>()?.Label == "Reset To Box").Invoke(poly, null);
            Check(poly.Points.Length == 4 && poly.Points[0].X == -0.5f, "Reset To Box button restores the 1x1 box");

            var go2 = s.Create("edge", active: false);
            var edge = go2.AddComponent<EdgeCollider2D>();
            edge.Points = [new Float2(0, 0), new Float2(1, 1), new Float2(2, 0), new Float2(3, 1)]; edge.IsLoop = true;
            typeof(EdgeCollider2D).GetMethods().First(m => m.GetCustomAttribute<ButtonAttribute>()?.Label == "Reset To Line").Invoke(edge, null);
            Check(edge.Points.Length == 2 && !edge.IsLoop, "Reset To Line button restores the single segment and opens the loop");
        });
    }

    /// <summary>
    /// The PropertyGrid writes the private serialized field directly and then calls OnValidate (EditorApplication.PropertyGridConfig
    /// .OnFieldChanged). The setters never run, so OnValidate is the only way an edit reaches a live simulation.
    /// </summary>
    private static void TestInspectorEditsReachTheSimulation()
    {
        Console.WriteLine("inspector edits (field write + OnValidate) reach the live simulation");
        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, -10);
            var ball = MakeBall(s, "ball", 0, 50);
            var rb = ball.GetComponent<Rigidbody2D>()!;
            Fixed(s, 10);
            SetField(rb, "gravityScale", 0f); rb.OnValidate();
            rb.LinearVelocity = new Float2(0, 0);
            float y0 = rb.Position.Y;
            Fixed(s, 30);
            Check(Near(rb.Position.Y, y0, 0.01f), "gravityScale edited to 0: the body floats");

            SetField(rb, "bodyType", BodyType2D.Static); rb.OnValidate();
            Check(rb.BodyType == BodyType2D.Static, "bodyType edited to Static takes effect");
            SetField(rb, "bodyType", BodyType2D.Dynamic); SetField(rb, "gravityScale", 1f); rb.OnValidate();
            Fixed(s, 30);
            Check(rb.Position.Y < y0 - 1f, "and back to Dynamic with gravity: it falls again");

            SetField(rb, "mass", -5f); SetField(rb, "linearDamping", -1f); rb.OnValidate();
            Check(rb.Mass > 0f && rb.LinearDamping >= 0f, $"nonsense values are clamped (mass {rb.Mass}, damping {rb.LinearDamping})");
        });

        InScene(s =>
        {
            s.Physics2D.Gravity = new Float2(0, 0);
            var go = Placed(s, "c", 0, 0); var circle = go.AddComponent<CircleCollider2D>(); go.SetActive(true);
            Fixed(s, 2);
            Check(!Hits(s, 1.5f, 0), "radius 0.5: (1.5,0) is outside");
            SetField(circle, "radius", 2f); circle.OnValidate();
            Fixed(s, 1);
            Check(Hits(s, 1.5f, 0, circle), "radius edited to 2: the live shape grew");

            var zone = MakeGround(s, "g", 0, 20, 10, 1);
            var zc = zone.GetComponent<BoxCollider2D>()!;
            var zp = zone.GetComponent<Probe>()!;
            var dropper = MakeBall(s, "d", 0, 24, 0.3f);
            s.Physics2D.Gravity = new Float2(0, -10);
            SetField(zc, "isTrigger", true); zc.OnValidate();
            Fixed(s, 150);
            Check(dropper.Transform.Position.Y < 15f && zp.Count("Enter:d") == 1, "isTrigger edited on: the ground stopped blocking and reports the overlap");

            Debug.Errors.Clear();
            SetField(zc, "friction", 3f); SetField(zc, "bounciness", 5f); zc.OnValidate();
            Check(zc.Bounciness <= 1f && Debug.Errors.Count == 0, "bounciness out of range is clamped; editing material rebuilds without errors");
        });
    }
}
