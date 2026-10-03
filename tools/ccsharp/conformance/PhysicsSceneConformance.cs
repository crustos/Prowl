// Tier 3 of the 2D runtime: physics components, and the collision and trigger messages they send to scripts. Run on .NET and as translated C
// against the real Box2D library; the outputs must be identical.
//
// A small scene is built through the public API. The numbers it must reach are worked out by hand and asserted here, so a match between
// the flavors cannot mean "identically wrong":
//   * the ground's top is at y = 0, so a ball of radius 0.5 comes to rest at y = 0.5, a box of height 1 likewise, and a body whose collider is on a
//     child node 0.5 below it comes to rest at y = 1.0 (the offset from the child's node to the body's);
//   * a trigger band is passed through once by ball A and (after being teleported) by ball C: Enter once each, Stay while inside, Exit once each,
//     every message both ways (the band hears about the ball, the ball hears about the band);
//   * ball B has its collider added BEFORE its rigidbody, which must move onto the body when it appears; it destroys its own node in the middle of the
//     message about its first contact, and what is left must be sound;
//   * ball C is moved by a script after it has come to rest and fallen asleep: it must wake, fall, land again (the second contact) and the first
//     one must have ended. That is the case the shim's teleport-wakes-the-body fix is for.
// Every message must reach a component of the collider's own node, and the scene must stay sound (Validate) after every step.
using System;
using System.Collections.Generic;
using Prowl.Core2D;
using Prowl.Native.Box2D;
using Prowl.Runtime.Physics2D;

static class Probe2
{
    public static List<int> Trace;
    public static int Bad;
    public static int Frame;

    public static void Add(int who, int kind) { Trace.Add(who * 10 + kind); }
}

[Script, MaxInstances(16)]
class Ball
{
    public Component Self;
    public int Who;
    public int Begins, Ends, Enters, Stays, Exits;
    public float LastNY, LastImpulse;

    public void Reset() { Who = 0; Begins = 0; Ends = 0; Enters = 0; Stays = 0; Exits = 0; LastNY = 0f; LastImpulse = 0f; }

    private void Check(Collider2D mine)
    {
        // a message goes to the components of the node of the collider that hears it
        if (mine == null || mine.Self == null || mine.Self.Node != Self.Node || Self.Destroyed || !Self.EnabledInHierarchy) Probe2.Bad++;
    }

    public void OnCollisionBegin2D(Collision2D c) { Check(c.Self); Begins++; LastNY = c.NY; LastImpulse = c.Impulse; Probe2.Add(Who, 1); }
    public void OnCollisionEnd2D(Collision2D c) { Check(c.Self); Ends++; Probe2.Add(Who, 2); }
    public void OnTriggerEnter2D(Collider2D other) { if (other == null || other.Self.Node == Self.Node) Probe2.Bad++; Enters++; Probe2.Add(Who, 3); }
    public void OnTriggerStay2D(Collider2D other) { if (other == null || other.Self.Node == Self.Node) Probe2.Bad++; Stays++; }
    public void OnTriggerExit2D(Collider2D other) { if (other == null || other.Self.Node == Self.Node) Probe2.Bad++; Exits++; Probe2.Add(Who, 4); }
}

[Script, MaxInstances(4)]
class Popper
{
    public Component Self;
    public int Popped;

    public void Reset() { Popped = 0; }

    // destroys its own node in the middle of the message about its first contact
    public void OnCollisionBegin2D(Collision2D c)
    {
        Popped++;
        Probe2.Add(9, 1);
        Scene2D.Current.Destroy(Self.Node);
    }
}

[Script, MaxInstances(4)]
class Mover
{
    public Component Self;
    public int Frames;
    public float ToX, ToY;
    public int At;
    public bool Moved;

    public void Reset() { Frames = 0; ToX = 0f; ToY = 0f; At = 0; Moved = false; }

    // moves its node after it has come to rest and fallen asleep
    public void Update()
    {
        Frames++;
        if (Frames == At && !Moved)
        {
            Moved = true;
            Self.Node.SetPosition(ToX, ToY);
        }
    }
}

[Script, MaxInstances(4)]
class Dropper
{
    public Component Self;
    public int Told;

    public void Reset() { Told = 0; }

    // destroys its own node the moment it enters a trigger: its collider dies while overlapping the trigger
    public void OnTriggerEnter2D(Collider2D other)
    {
        Told++;
        Probe2.Add(8, 3);
        Scene2D.Current.Destroy(Self.Node);
    }
}

class PhysicsSceneConformance
{
    static int Q(float v) { return (int)(v * 1000f); }

    static void Pump(Scene2D s) { while (s.NextCall()) Scripts.Invoke(s); }
    static void Fixed(Scene2D s) { s.BeginFixedStep(1f / 60f); Pump(s); }
    static void Frame(Scene2D s) { s.BeginFrame(1f / 60f, 1f); Pump(s); }

    static int scenariosOk, scenariosBad;

    static void Expect(bool ok, string what)
    {
        if (ok) scenariosOk++;
        else { scenariosBad++; Console.WriteLine($"  FAIL {what}"); }
    }

    static bool Near(float a, float b, float tol) { return MathF.Abs(a - b) <= tol; }

    public static int Main()
    {
        bool ok;                       // each check is computed on its own line: a string built beside a call would change the order C# evaluates in
        Probe2.Trace = new List<int>();
        Scripts.Init();
        Scene2D scene = new Scene2D();

        // ---- the scene ---------------------------------------------------------------------------------------------------------
        Node ground = scene.NewNode(null);
        ground.SetPosition(0f, -0.5f);
        scene.AddBoxCollider(ground, 100f, 1f);                       // no rigidbody: static, on a body of its own

        Node zone = scene.NewNode(null);
        zone.SetPosition(0f, 3f);
        Collider2D zc = scene.NewBoxCollider(zone, 4f, 1f);
        zc.IsTrigger = true;
        scene.Finish(zc.Self);
        Ball zoneBall = Scripts.AddBall(zone);
        zoneBall.Who = 5;

        Node a = scene.NewNode(null);                                  // A: falls through the band, lands on the ground
        a.SetPosition(0f, 6f);
        scene.AddRigidbody(a, PB2.BodyDynamic);
        scene.AddCircleCollider(a, 0.5f);
        Ball aBall = Scripts.AddBall(a);
        aBall.Who = 1;

        Node b = scene.NewNode(null);                                  // B: its collider first, its rigidbody after; destroys itself on contact
        b.SetPosition(3f, 6f);
        scene.AddCircleCollider(b, 0.5f);
        scene.AddRigidbody(b, PB2.BodyDynamic);
        Popper popper = Scripts.AddPopper(b);

        Node c = scene.NewNode(null);                                  // C: lands, sleeps, is moved by a script above the band, falls through it
        c.SetPosition(-3f, 6f);
        scene.AddRigidbody(c, PB2.BodyDynamic);
        scene.AddBoxCollider(c, 1f, 1f);
        Ball cBall = Scripts.AddBall(c);
        cBall.Who = 3;
        Mover mover = Scripts.AddMover(c);
        mover.At = 200;
        mover.ToX = -1.5f;
        mover.ToY = 9f;

        Node d = scene.NewNode(null);                                  // D: the body on one node, its collider on a child 0.5 below it
        d.SetPosition(9f, 4f);
        scene.AddRigidbody(d, PB2.BodyDynamic);
        Node dChild = scene.NewNode(d);
        dChild.SetPosition(0f, -0.5f);
        scene.AddBoxCollider(dChild, 1f, 1f);
        Ball dBall = Scripts.AddBall(dChild);
        dBall.Who = 4;

        Node boxE = scene.NewNode(null);                                  // E: moved SIDEWAYS by a script while it is still FALLING (an edit of a moving body)
        boxE.SetPosition(-6f, 8f);
        scene.AddRigidbody(boxE, PB2.BodyDynamic);
        scene.AddBoxCollider(boxE, 1f, 1f);
        Ball eBall = Scripts.AddBall(boxE);
        eBall.Who = 2;
        Mover eMover = Scripts.AddMover(boxE);
        eMover.At = 20;
        eMover.ToX = 6f;
        eMover.ToY = 8f;

        Node zone2 = scene.NewNode(null);                              // a second band, far from the rest, with a ball that dies inside it
        zone2.SetPosition(20f, 3f);
        Collider2D z2c = scene.NewBoxCollider(zone2, 4f, 1f);
        z2c.IsTrigger = true;
        scene.Finish(z2c.Self);
        Ball zone2Ball = Scripts.AddBall(zone2);
        zone2Ball.Who = 6;
        Node ballF = scene.NewNode(null);                                  // F: falls into the second band and destroys itself on entering
        ballF.SetPosition(20f, 6f);
        scene.AddRigidbody(ballF, PB2.BodyDynamic);
        scene.AddCircleCollider(ballF, 0.5f);
        Dropper dropper = Scripts.AddDropper(ballF);
        Node ballG = null;                                                  // G: made after F is gone, far from the band, and takes F's collider slot
        bool fGone = false;                                             // (a Node is an arena reference: once F's slot is recycled for G, `ballF` names G, so the fact is kept here)

        // ---- run ---------------------------------------------------------------------------------------------------------------
        int unsound = 0;
        for (int frame = 0; frame < 480; frame++)
        {
            Probe2.Frame = frame;
            Fixed(scene);
            Frame(scene);
            if (scene.Validate() != 0) unsound++;
            if (ballG == null && !scene.IsLive(ballF))
            {
                fGone = true;
                ballG = scene.NewNode(null);
                ballG.SetPosition(30f, 6f);
                scene.AddRigidbody(ballG, PB2.BodyDynamic);
                scene.AddCircleCollider(ballG, 0.5f);
            }
        }

        // ---- what it must have done --------------------------------------------------------------------------------------------
        Console.WriteLine("results");
        Console.WriteLine($"  A at {Q(a.WorldX())} {Q(a.WorldY())}   C at {Q(c.WorldX())} {Q(c.WorldY())}   D at {Q(d.WorldX())} {Q(d.WorldY())}   E at {Q(boxE.WorldX())} {Q(boxE.WorldY())}");
        ok = Near(a.WorldY(), 0.5f, 0.03f);
        Expect(ok, "ball A rests at y = 0.5");
        ok = Near(c.WorldY(), 0.5f, 0.03f) && Near(c.WorldX(), -1.5f, 0.2f);
        Expect(ok, "box C, moved above the band, rests at y = 0.5 where it was dropped");
        ok = Near(d.WorldY(), 1.0f, 0.03f);
        Expect(ok, "body D rests at y = 1.0: its collider's offset from the body was honoured");
        ok = !scene.IsLive(b);
        Expect(ok, "ball B destroyed itself");
        ok = popper.Popped == 1;
        Expect(ok, "B was told of its first contact once");
        ok = Near(boxE.WorldX(), 6f, 0.2f) && Near(boxE.WorldY(), 0.5f, 0.03f);
        Expect(ok, "box E, moved sideways by a script while falling, landed at the new place (the write-back did not undo the edit)");
        ok = eBall.Begins == 1;
        Expect(ok, "E touched the ground once, at the new place");
        ok = fGone && dropper.Told == 1;
        Expect(ok, "F destroyed itself on entering the second band, told once");
        ok = zone2Ball.Enters == 1 && zone2Ball.Exits == 0;
        Expect(ok, "the second band heard F enter, and no Exit, as F died inside it");
        ok = ballG != null && Near(ballG.WorldY(), 0.5f, 0.03f);
        Expect(ok, "G, made after F, took its place and fell to the ground");
        ok = zone2Ball.Stays == 0;
        Expect(ok, "G, reusing the slot of F's collider, was never mistaken for F by the band: no Stay or Exit for a pair that never was");
        Console.WriteLine($"  A: begin {aBall.Begins} end {aBall.Ends} enter {aBall.Enters} stay>0 {(aBall.Stays > 0 ? 1 : 0)} exit {aBall.Exits}");
        Console.WriteLine($"  C: begin {cBall.Begins} end {cBall.Ends} enter {cBall.Enters} stay>0 {(cBall.Stays > 0 ? 1 : 0)} exit {cBall.Exits}");
        Console.WriteLine($"  D: begin {dBall.Begins} end {dBall.Ends}   band: enter {zoneBall.Enters} stay>0 {(zoneBall.Stays > 0 ? 1 : 0)} exit {zoneBall.Exits}");
        ok = aBall.Begins == 1 && aBall.Ends == 0;
        Expect(ok, "A touched the ground once and stayed");
        ok = aBall.Enters == 1 && aBall.Exits == 1 && aBall.Stays > 0;
        Expect(ok, "A went through the band: Enter, Stay, Exit");
        ok = cBall.Begins == 2 && cBall.Ends == 1;
        Expect(ok, "C landed twice: the first contact ended when it was moved (it woke up)");
        ok = cBall.Enters == 1 && cBall.Exits == 1;
        Expect(ok, "C went through the band once, after it was moved");
        ok = dBall.Begins == 1;
        Expect(ok, "D's collider on its child node sent its message to the child's script");
        ok = zoneBall.Enters == 2 && zoneBall.Exits == 2 && zoneBall.Stays > 0;
        Expect(ok, "the band heard of both balls, in and out");
        ok = aBall.LastNY < -0.9f && aBall.LastImpulse > 0f;
        Expect(ok, "the contact normal points from the ball down to the ground, with an impulse");
        ok = Probe2.Bad == 0;
        Expect(ok, "every message reached a component of the collider's own node");
        ok = unsound == 0;
        Expect(ok, "the scene stayed sound after every step");

        ok = scene.Physics.Sim.Registry.BodyCount == 5;
        Expect(ok, "five bodies are left (A, C, D, E, G): B's and F's went with their nodes");
        ok = scene.Physics.Sim.Registry.ColliderCount == 8;
        Expect(ok, "eight colliders are left: ground, two bands, A, C, D's child, E, G");

        int hash = 17;
        for (int i = 0; i < Probe2.Trace.Count; i++) hash = (hash * 31) ^ Probe2.Trace[i];
        Console.WriteLine($"  messages {Probe2.Trace.Count} hash {hash}");

        // ---- and it all goes away ------------------------------------------------------------------------------------------------
        for (int i = 0; i < scene.NodeHighWater; i++)
        {
            Node n = scene.NodeAt(i);
            if (n != null && n.Parent == null) scene.Destroy(n);
        }
        Frame(scene);
        Frame(scene);
        ok = scene.NodeCount == 0 && scene.ComponentCount == 0;
        Expect(ok, "everything was freed");
        ok = !scene.Physics.Sim.HasWorld;
        Expect(ok, "the native world was given back when nothing was left");
        ok = scene.Validate() == 0;
        Expect(ok, "and the scene is sound");

        Console.WriteLine($"checks ok {scenariosOk} bad {scenariosBad}");
        return scenariosBad == 0 ? 0 : 1;
    }
}
