// Drives the 2D SIMULATION CORE (SimCore2D) against the real Box2D library, through the generated bindings, and prints integers only.
//
// Run twice by `python3 build.py ccsharp`: on real .NET (through [LibraryImport]), and translated by CCSharp to C and linked against the same
// library. The two outputs must be identical. This is the check that the whole step runs as C: the native step, the pose writes into the body
// arena, and the event stream with its ordering rules, and not only the tables underneath it.
//
// The program plays the part of the engine layer: it builds bodies and colliders (what the components do), then reads the step's events with the
// cursor, `while (core.NextEvent())`, and acts on them. Two of the things it does are the point:
//
//   * a handler REMOVES a collider in the middle of a pair's events (a ball's collider, on its trigger-enter; the second ground, on the first
//     contact with it). The reverse direction of that pair, and every later event that names the removed collider, must then never be delivered.
//     `leaks` counts events that name a collider after it was removed: it has to be 0.
//   * a handler destroys a joint on its break event, and no event about it follows.
//
// A normal pair (a ball that passes through the sensor, untouched) must still produce Enter, Stay and Exit, each in both directions.
using System;
using System.Collections.Generic;
using Prowl.Native.Box2D;
using Prowl.Runtime.Physics2D;

class SimConformance
{
    static int Q(float v) { return (int)(v * 1000f); }
    static int Mix(int hash, int value) { return (hash * 31) ^ value; }

    static SimCore2D core = null;
    static uint[] nativeBody;                // the native body of each body slot
    static uint[] nativeJoint;

    static int NewBody(int type, float x, float y)
    {
        int slot = core.Registry.AddBody();
        uint handle = PB2.BodyCreate(type, x, y, 0f, slot, 1f, 0f, 0.05f, 0u);
        nativeBody[slot] = handle;
        BodyRecord record = core.Registry.Body(slot);
        record.Native = handle;
        record.Pose.Reset(x, y, 0f, core.StepIndex);
        return slot;
    }

    static int NewBox(int body, float halfW, float halfH, uint flags)
    {
        int collider = core.Registry.AddCollider();
        PB2.ShapeCreateBox(nativeBody[body], collider, 0, halfW, halfH, 0f, 0f, 0f, 0f, 1f, 0.5f, 0f, flags);
        return collider;
    }

    public static int Main()
    {
        nativeBody = new uint[32];
        nativeJoint = new uint[8];
        core = new SimCore2D();
        core.Acquire();

        // ---- the scene -----------------------------------------------------------------------------------------------------------
        int groundA = NewBody(PB2.BodyStatic, -5f, -0.5f);
        int colGroundA = NewBox(groundA, 5f, 0.5f, 0u);
        int groundB = NewBody(PB2.BodyStatic, 5f, -0.5f);
        int colGroundB = NewBox(groundB, 5f, 0.5f, 0u);                 // the one a handler will remove
        int sensorBody = NewBody(PB2.BodyStatic, 0f, 3f);
        int colSensor = NewBox(sensorBody, 8f, 0.5f, PB2.SfSensor);     // a trigger band every ball falls through

        int ballA = NewBody(PB2.BodyDynamic, -3f, 7f);                  // untouched: a normal pair
        int colBallA = NewBox(ballA, 0.4f, 0.4f, 0u);
        int ballB = NewBody(PB2.BodyDynamic, -1f, 7f);                  // its collider is removed when it enters the sensor
        int colBallB = NewBox(ballB, 0.4f, 0.4f, 0u);
        int ballC = NewBody(PB2.BodyDynamic, 4f, 5f);                   // lands on groundB, which is removed on first contact
        int colBallC = NewBox(ballC, 0.4f, 0.4f, 0u);

        // a pendulum whose joint breaks at once (its force threshold is far below what holding the bob takes)
        int anchor = NewBody(PB2.BodyStatic, 20f, 6f);
        int bob = NewBody(PB2.BodyDynamic, 22f, 6f);
        NewBox(bob, 0.3f, 0.3f, 0u);
        int joint = core.Registry.AddJoint(anchor, bob);
        PB2JointDef def = new PB2JointDef();
        def.bodyA = nativeBody[anchor];
        def.bodyB = nativeBody[bob];
        def.jointIndex = joint;
        def.type = PB2.JointRevolute;
        def.flags = 0u;
        def.ax = 0f; def.ay = 0f; def.bx = -2f; def.by = 0f;
        def.forceThreshold = 1.0f;
        def.torqueThreshold = 3.0e38f;
        PB2.JointDefSetP(ref def, 3, -3.0f);
        PB2.JointDefSetP(ref def, 4, 3.0f);
        nativeJoint[joint] = PB2.JointCreate(ref def);

        // ---- the engine layer's side: read the event stream, act on it -----------------------------------------------------------
        int[] counts = new int[8];
        int hash = 17;
        bool removedBall = false, removedGround = false, brokeJoint = false;
        int leaks = 0, jointEventsAfterBreak = 0, bothDirections = 0;
        int ballAEntered = 0, ballAStayed = 0, ballAExited = 0;
        int lastKind = 0, lastSelf = -1, lastOther = -1;

        for (int step = 0; step < 260; step++)
        {
            if (step == 120) core.QueueTransform(nativeBody[ballA], -3f, 9f, 0f, false);   // a batched teleport: ball A falls through the band again

            core.Step(0.0166667f, 4);
            while (core.NextEvent())
            {
                int kind = core.EventKind;
                int self = core.EventSelf;
                int other = core.EventOther;
                counts[kind]++;
                hash = Mix(hash, kind * 1009 + self * 31 + other + step * 7 + Q(core.EventNY));

                // events that name something a handler already removed are the bug this stream exists to prevent
                if (removedBall && kind != SimEvent.JointBreak && (self == colBallB || other == colBallB)) leaks++;
                if (removedGround && kind != SimEvent.JointBreak && (self == colGroundB || other == colGroundB)) leaks++;
                if (brokeJoint && kind == SimEvent.JointBreak) jointEventsAfterBreak++;

                // the reverse direction of the pair just delivered arrives straight after it, with the roles swapped
                if (lastKind == kind && lastSelf == other && lastOther == self) bothDirections++;
                lastKind = kind; lastSelf = self; lastOther = other;

                if (kind == SimEvent.TriggerEnter && other == colBallA) ballAEntered++;
                if (kind == SimEvent.TriggerStay && other == colBallA) ballAStayed++;
                if (kind == SimEvent.TriggerExit && other == colBallA) ballAExited++;

                // the handlers that destroy things in the middle of the stream
                if (kind == SimEvent.TriggerEnter && other == colBallB && !removedBall)
                {
                    core.Registry.RemoveCollider(colBallB);
                    core.ForgetCollider(colBallB);
                    removedBall = true;
                }
                if (kind == SimEvent.CollisionBegin && self == colGroundB && other == colBallC && !removedGround)
                {
                    core.Registry.RemoveCollider(colGroundB);
                    core.ForgetCollider(colGroundB);
                    removedGround = true;
                }
                if (kind == SimEvent.JointBreak && !brokeJoint)
                {
                    PB2.JointDestroy(nativeJoint[core.EventJoint]);
                    core.Registry.RemoveJoint(core.EventJoint);
                    brokeJoint = true;
                }
            }
            core.EndStep();
        }

        Console.WriteLine($"steps {core.StepIndex} bodies {core.Registry.BodyCount} colliders {core.Registry.ColliderCount} joints {core.Registry.JointCount}");
        Console.WriteLine($"events end {counts[SimEvent.CollisionEnd]} begin {counts[SimEvent.CollisionBegin]} exit {counts[SimEvent.TriggerExit]} stay {counts[SimEvent.TriggerStay]} enter {counts[SimEvent.TriggerEnter]} break {counts[SimEvent.JointBreak]}");
        Console.WriteLine($"stream hash {hash}");
        Console.WriteLine($"leaks {leaks} jointEventsAfterBreak {jointEventsAfterBreak} removed ball {(removedBall ? 1 : 0)} ground {(removedGround ? 1 : 0)} joint {(brokeJoint ? 1 : 0)}");
        Console.WriteLine($"normal pair: enter {ballAEntered} stay>0 {(ballAStayed > 0 ? 1 : 0)} exit {ballAExited} reverse directions seen {(bothDirections > 0 ? 1 : 0)}");

        // the poses the step wrote into the body arena
        for (int b = 0; b < 4; b++)
        {
            int slot = b == 0 ? ballA : b == 1 ? ballB : b == 2 ? ballC : bob;
            BodyRecord r = core.Registry.Body(slot);
            Console.WriteLine($"body {slot} pose {Q(r.Pose.CurX)} {Q(r.Pose.CurY)} asleep {(r.Asleep ? 1 : 0)}");
        }

        // ---- the queries ---------------------------------------------------------------------------------------------------------
        int hit = core.Raycast(-3f, 20f, 0f, -1f, 40f, 0xFFFFFFFFu, false);
        Console.WriteLine($"ray: collider {hit} distance {Q(core.HitDistance)}");
        int n = core.RaycastAll(-3f, 20f, 0f, -1f, 40f, 0xFFFFFFFFu, true);
        int rayHash = 17;
        for (int i = 0; i < n; i++) rayHash = Mix(rayHash, core.RayHitCollider(i) * 1009 + Q(core.RayHitFraction(i)));
        Console.WriteLine($"ray all: {n} hash {rayHash}");
        int overlaps = core.OverlapCircle(-3f, 0.6f, 3f, 0xFFFFFFFFu, false);
        int overlapHash = 17;
        for (int i = 0; i < overlaps; i++) overlapHash = Mix(overlapHash, core.OverlapResult(i));
        Console.WriteLine($"overlap circle: {overlaps} hash {overlapHash}");
        Console.WriteLine($"overlap point on the removed ground: {core.OverlapPoint(5f, -0.5f, 0xFFFFFFFFu, false)}");

        core.Release();
        Console.WriteLine($"released: world {(core.HasWorld ? 1 : 0)} bodies {core.Registry.BodyCount}");
        return leaks == 0 && jointEventsAfterBreak == 0 ? 0 : 1;
    }
}
