// Tier 2 of the 2D runtime: the scene, its components, and the order in which the game's callbacks run. Run on .NET and as translated C; the
// outputs must be identical. (The script call sink is generated from the [Script] classes below by tools/ccsharp/gen_scripts.py, as a game's
// build does it.)
//
// Two kinds of check. First, SCENARIOS with the exact sequence of calls written out beforehand: execution order, a parent deactivated in the
// middle of an Update, a node destroyed in the middle of an Update, a component added during Update (including the engine's own quirk that it can
// get LateUpdate before its Start), a component disabled and re-enabled, the scene made inactive and active again, and a script slot recycled.
// Second, a STRESS run in which scripts create, destroy, enable, disable and reparent things in the middle of dispatch, with invariants checked
// on every call (never a callback to something disabled, destroyed or freed; OnEnable and OnDisable strictly alternate; Start at most once) and
// the scene's own Validate() after every step.
using System;
using System.Collections.Generic;
using Prowl.Core2D;
using Prowl.Runtime.Physics2D;

static class Log
{
    public static List<int> Trace;
    public static int Bad;
    public static int[] BadBy;
    public static int Rng;
    public static Node Target;

    public static int Next()
    {
        Rng = Rng * 1103515245 + 12345;
        return (Rng >> 16) & 0x7fff;
    }

    public static void Add(int id, int kind) { Trace.Add(id * 100 + kind); }

    public static void Violation(int code)
    {
        Bad++;
        BadBy[code]++;
    }
}

[Script, MaxInstances(48)]
class Probe
{
    public Component Self;
    public int Id;
    public int Plan;
    public bool On;
    public int Starts;

    public void Reset() { Id = 0; Plan = 0; On = false; Starts = 0; }

    public void OnEnable() { Log.Add(Id, 1); if (On) Log.Violation(1); On = true; }
    public void OnDisable() { Log.Add(Id, 2); if (!On) Log.Violation(2); On = false; }
    public void Start() { Log.Add(Id, 3); Starts++; if (Starts > 1) Log.Violation(3); Check(); }
    public void FixedUpdate() { Log.Add(Id, 4); Check(); }
    public void Update() { Log.Add(Id, 5); Check(); Act(); }
    public void LateUpdate() { Log.Add(Id, 6); Check(); }

    // Whatever called it, a callback must find its component alive and enabled, and the script must have been told OnEnable first.
    private void Check()
    {
        if (!On || Self.Node == null || Self.Destroyed || !Self.EnabledInHierarchy || !Self.Node.ActiveInHierarchy) Log.Violation(4);
    }

    private void Act()
    {
        Scene2D scene = Scene2D.Current;
        if (Plan == 1) { Plan = 0; scene.SetActive(Self.Node, false); }
        else if (Plan == 2) { Plan = 0; scene.Destroy(Log.Target); }
        else if (Plan == 3) { Plan = 0; Probe q = Scripts.AddProbe(Self.Node); if (q != null) q.Id = 9; }
        else if (Plan == 5) Chaos();
    }

    // Mutates the scene in the middle of dispatch, in every way a game can.
    private void Chaos()
    {
        Scene2D scene = Scene2D.Current;
        int r = Log.Next() % 100;
        Node n = scene.NodeAt(Log.Next() % (scene.NodeHighWater + 1));
        if (n == null) return;
        if (r < 25) scene.SetActive(n, (Log.Next() & 1) == 1);
        else if (r < 35) scene.Destroy(n);
        else if (r < 55)
        {
            Probe q = Scripts.AddProbe(n);
            if (q != null)
            {
                q.Id = 10 + Log.Next() % 80;
                q.Plan = Log.Next() % 4 == 0 ? 5 : 0;
            }
        }
        else if (r < 70)
        {
            Component c = scene.FirstComponent(n);
            if (c != null) scene.SetEnabled(c, (Log.Next() & 1) == 1);
        }
        else if (r < 80) scene.NewNode(n);
        else if (r < 90)
        {
            Node m = scene.NodeAt(Log.Next() % (scene.NodeHighWater + 1));
            if (m != null) scene.SetParent(n, m, false);
        }
        else if (r < 95)
        {
            Early e = Scripts.AddEarly(n);
            if (e != null) e.Id = 10 + Log.Next() % 80;
        }
    }
}

[Script(Order = -5), MaxInstances(16)]
class Early
{
    public Component Self;
    public int Id;
    public bool On;
    public int Starts;

    public void Reset() { Id = 0; On = false; Starts = 0; }

    public void OnEnable() { Log.Add(Id, 1); if (On) Log.Violation(1); On = true; }
    public void OnDisable() { Log.Add(Id, 2); if (!On) Log.Violation(2); On = false; }
    public void Start() { Log.Add(Id, 3); Starts++; if (Starts > 1) Log.Violation(3); }
    public void FixedUpdate() { Log.Add(Id, 4); }
    public void Update() { Log.Add(Id, 5); if (!On || Self.Destroyed || !Self.EnabledInHierarchy) Log.Violation(4); }
    public void LateUpdate() { Log.Add(Id, 6); }
}

class LifecycleConformance
{
    static List<int> exp;
    static int scenariosOk, scenariosBad;

    static void Pump(Scene2D s)
    {
        while (s.NextCall()) Scripts.Invoke(s);
    }

    static void Frame(Scene2D s) { s.BeginFrame(0.016f, 0.5f); Pump(s); }
    static void Fixed(Scene2D s) { s.BeginFixedStep(0.02f); Pump(s); }

    static void X(int v) { exp.Add(v); }

    // Compares the trace with what the scenario said it must be, and clears both.
    static void Match(string name)
    {
        bool same = exp.Count == Log.Trace.Count;
        int at = -1;
        for (int i = 0; same && i < exp.Count; i++)
            if (exp[i] != Log.Trace[i]) { same = false; at = i; }
        if (same)
        {
            scenariosOk++;
            Console.WriteLine($"  ok   {name}");
        }
        else
        {
            scenariosBad++;
            int n = exp.Count < Log.Trace.Count ? exp.Count : Log.Trace.Count;
            if (at < 0) at = n;
            Console.WriteLine($"  FAIL {name}: first difference at call {at}: expected {(at < exp.Count ? exp[at] : -1)}, got {(at < Log.Trace.Count ? Log.Trace[at] : -1)} ({exp.Count} expected, {Log.Trace.Count} made)");
        }
        exp.Clear();
        Log.Trace.Clear();
    }

    static void Sound(Scene2D s, string name)
    {
        int v = s.Validate();
        if (v == 0) scenariosOk++;
        else { scenariosBad++; Console.WriteLine($"  FAIL {name}: the scene's bookkeeping has {v} problem(s)"); }
    }

    // Destroys everything and runs the end of the frame twice, so the next scenario starts from an empty scene and every script slot is back.
    static void Clean(Scene2D s)
    {
        for (int i = 0; i < s.NodeHighWater; i++)
        {
            Node n = s.NodeAt(i);
            if (n != null && n.Parent == null) s.Destroy(n);
        }
        Frame(s);
        Frame(s);
        if (s.NodeCount != 0 || s.ComponentCount != 0 || s.Validate() != 0)
        {
            scenariosBad++;
            Console.WriteLine($"  FAIL cleanup left {s.NodeCount} nodes, {s.ComponentCount} components, {s.Validate()} problems");
        }
        Log.Trace.Clear();
    }

    // R (Probe 1, Early 2) with a child C (Probe 3).
    static Node Tree(Scene2D s, out Node child)
    {
        Node r = s.NewNode(null);
        Probe p1 = Scripts.AddProbe(r); p1.Id = 1;
        Early e2 = Scripts.AddEarly(r); e2.Id = 2;
        child = s.NewNode(r);
        Probe p3 = Scripts.AddProbe(child); p3.Id = 3;
        return r;
    }

    public static int Main()
    {
        Log.Trace = new List<int>();
        Log.BadBy = new int[8];
        Log.Rng = 20260929;
        exp = new List<int>();
        Scripts.Init();
        Scene2D scene = new Scene2D();

        Console.WriteLine("scenarios");
        Node c, r;

        // S1: OnEnable in the order components were added, then Start / Update / LateUpdate by execution order (Early is -5), ties by registration
        r = Tree(scene, out c);
        Frame(scene);
        X(101); X(201); X(301);
        X(203); X(103); X(303);
        X(205); X(105); X(305);
        X(206); X(106); X(306);
        Match("enable, then Start / Update / LateUpdate by execution order");
        Fixed(scene);
        X(204); X(104); X(304);
        Match("a fixed step runs FixedUpdate only, Start having run");
        Clean(scene);

        // S2: a parent deactivated in the middle of an Update
        r = Tree(scene, out c);
        Scripts.AsProbe(scene.FirstComponent(r)).Plan = 1;
        Frame(scene);
        X(101); X(201); X(301); X(203); X(103); X(303);
        X(205); X(105);
        X(102); X(202); X(302);                       // OnDisable for R's components in the order added, then the child's, right after the Update that did it
        Match("a node deactivated in Update: OnDisable follows at once, the child's Update never runs, LateUpdate has no one");
        scene.SetActive(r, true);
        Frame(scene);
        X(101); X(201); X(301);                       // OnEnable again; no second Start
        X(205); X(105); X(305);
        X(206); X(106); X(306);
        Match("reactivated: OnEnable again, no second Start");
        Sound(scene, "bookkeeping after deactivate / reactivate");
        Clean(scene);

        // S3: a node destroyed in the middle of an Update
        r = Tree(scene, out c);
        Probe first = Scripts.AsProbe(scene.FirstComponent(r));
        Probe third = Scripts.AsProbe(scene.FirstComponent(c));
        int slot3 = third.Self.Slot;
        first.Plan = 2;
        Log.Target = c;
        Frame(scene);
        X(101); X(201); X(301); X(203); X(103); X(303);
        X(205); X(105);
        X(302);                                        // the destroyed child is told OnDisable; its Update is skipped; its LateUpdate never comes
        X(206); X(106);
        Match("a node destroyed in Update: OnDisable, no further callbacks");
        bool freed = scene.NodeCount == 1 && scene.ComponentCount == 2 && scene.NodeAt(c.Index) == null;
        Probe again = Scripts.AddProbe(r);
        bool recycled = again != null && again.Self.Slot == slot3 && again.Id == 0 && !again.On && again.Starts == 0;
        if (freed && recycled && scene.Validate() == 0) { scenariosOk++; Console.WriteLine("  ok   freed at the end of the frame; the script slot is recycled and reset"); }
        else { scenariosBad++; Console.WriteLine($"  FAIL freed {(freed ? 1 : 0)} recycled {(recycled ? 1 : 0)}"); }
        Clean(scene);

        // S4: a component added during Update
        r = scene.NewNode(null);
        Probe q1 = Scripts.AddProbe(r); q1.Id = 1; q1.Plan = 3;
        Frame(scene);
        X(101); X(103); X(105);
        X(901);                                        // OnEnable straight after the Update that added it
        X(106); X(906);                                // LateUpdate's channel is built after Update: the new component is in it, before its Start
        Match("a component added in Update: OnEnable at once, not in this frame's Update, LateUpdate before its Start (as in the .NET engine)");
        Frame(scene);
        X(903);
        X(105); X(905);
        X(106); X(906);
        Match("... and its Start comes with the next frame");
        Clean(scene);

        // S5: a component disabled and enabled; then the whole scene made inactive and active
        r = scene.NewNode(null);
        Probe a = Scripts.AddProbe(r); a.Id = 1;
        Frame(scene);
        X(101); X(103); X(105); X(106);
        Match("a lone component");
        scene.SetEnabled(a.Self, false);
        Frame(scene);
        X(102);
        Match("a component disabled: OnDisable, then silence");
        scene.SetEnabled(a.Self, true);
        Frame(scene);
        X(101); X(105); X(106);
        Match("enabled again: OnEnable, no second Start");
        scene.SetSceneActive(false);
        Frame(scene);
        X(102);
        Match("an inactive scene: OnDisable, then silence");
        Probe b = Scripts.AddProbe(r); b.Id = 2;
        Frame(scene);
        Match("a component added while the scene is inactive gets nothing");
        scene.SetSceneActive(true);
        Frame(scene);
        X(101); X(201);                                // everything enabled in hierarchy is enabled, oldest first
        X(203);
        X(105); X(205);
        X(106); X(206);
        Match("the scene active again: OnEnable for all, Start only for the one never started");
        Sound(scene, "bookkeeping after scene activation");
        Clean(scene);

        // ---- stress: scripts mutate the scene in the middle of dispatch -----------------------------------------------------------
        Console.WriteLine("stress");
        int steps = 0, fixedSteps = 0, framesRun = 0, soundBad = 0, stateBad = 0, created = 0, maxNodes = 0, maxComps = 0;
        for (int step = 0; step < 900; step++)
        {
            int op = Log.Next() % 100;
            Node n = scene.NodeAt(Log.Next() % (scene.NodeHighWater + 1));
            if (op < 22)
            {
                Node made = scene.NewNode(Log.Next() % 3 == 0 ? null : n);
                if (made != null)
                {
                    created++;
                    int k = Log.Next() % 3;
                    for (int j = 0; j < k; j++)
                    {
                        Probe q = Scripts.AddProbe(made);
                        if (q != null) { q.Id = 10 + Log.Next() % 80; q.Plan = Log.Next() % 3 == 0 ? 5 : 0; }
                    }
                    if (Log.Next() % 4 == 0)
                    {
                        Early e = Scripts.AddEarly(made);
                        if (e != null) e.Id = 10 + Log.Next() % 80;
                    }
                }
            }
            else if (op < 34 && n != null) scene.SetActive(n, (Log.Next() & 1) == 1);
            else if (op < 40 && n != null) scene.Destroy(n);
            else if (op < 46 && n != null)
            {
                Component k = scene.FirstComponent(n);
                if (k != null) scene.SetEnabled(k, (Log.Next() & 1) == 1);
            }
            else if (op < 52 && n != null)
            {
                Node m = scene.NodeAt(Log.Next() % (scene.NodeHighWater + 1));
                if (m != null) scene.SetParent(n, m, false);
            }
            else if (op < 54) scene.SetSceneActive(Log.Next() % 5 != 0);
            else if (op < 58 && n != null) scene.DestroyComponent(scene.FirstComponent(n));

            if (Log.Next() % 3 == 0) { Fixed(scene); fixedSteps++; }
            else { Frame(scene); framesRun++; }
            steps++;

            // everything has been delivered and the frame is over: what the scripts were told must match what the scene says
            if (scene.Validate() != 0) soundBad++;
            for (int i = 0; i < scene.ComponentHighWater; i++)
            {
                Component k = scene.ComponentAt(i);
                if (k == null) continue;
                bool on;
                if (k.Kind == ScriptKind.Probe) on = Scripts.AsProbe(k).On;
                else on = Scripts.AsEarly(k).On;
                if (on != (k.EnabledInHierarchy && scene.IsActive)) stateBad++;
            }
            if (scene.NodeCount > maxNodes) maxNodes = scene.NodeCount;
            if (scene.ComponentCount > maxComps) maxComps = scene.ComponentCount;
        }

        int[] byKind = new int[8];
        int hash = 17;
        for (int i = 0; i < Log.Trace.Count; i++)
        {
            int v = Log.Trace[i];
            byKind[v % 100]++;
            hash = (hash * 31) ^ v;
        }
        Console.WriteLine($"  steps {steps} (fixed {fixedSteps}, frames {framesRun}) nodes made {created} peak nodes {maxNodes} peak components {maxComps}");
        Console.WriteLine($"  calls: enable {byKind[1]} disable {byKind[2]} start {byKind[3]} fixed {byKind[4]} update {byKind[5]} late {byKind[6]}");
        Console.WriteLine($"  trace {Log.Trace.Count} hash {hash}");
        Console.WriteLine($"  violations {Log.Bad} (by kind {Log.BadBy[1]} {Log.BadBy[2]} {Log.BadBy[3]} {Log.BadBy[4]}) unsound steps {soundBad} delivered-state mismatches {stateBad}");

        // after all that, everything must be freeable and every script slot must be back in its pool
        scene.SetSceneActive(true);
        Log.Trace.Clear();
        Clean(scene);
        Node holder = scene.NewNode(null);
        int got = 0;
        for (int i = 0; i < 60; i++)
            if (Scripts.AddProbe(holder) != null) got++;
        int gotEarly = 0;
        for (int i = 0; i < 20; i++)
            if (Scripts.AddEarly(holder) != null) gotEarly++;
        Console.WriteLine($"  pools after the run: {got} of 48 Probe slots and {gotEarly} of 16 Early slots are free");
        Clean(scene);

        bool stressOk = Log.Bad == 0 && soundBad == 0 && stateBad == 0 && got == 48 && gotEarly == 16;
        string verdict = "ok";
        if (!stressOk) verdict = "BAD";
        Console.WriteLine($"scenarios ok {scenariosOk} bad {scenariosBad}; stress {verdict}");
        return scenariosBad == 0 && stressOk ? 0 : 1;
    }
}
