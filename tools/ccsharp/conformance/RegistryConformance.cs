// Drives the arena registry (Registry2D with its BodyRecord and JointRecord arena classes) through long random operation sequences and prints
// integers only.
//
// Run twice by `python3 build.py ccsharp`: on real .NET, and translated by CCSharp to C (where the records live in statically allocated arenas
// and a reference between them is a pointer). The two outputs must be identical. Every table is also checked against a naive reference model
// written here, in the simplest way that could be right, so "mismatches 0" means the SEMANTICS are right and not just that C agrees with .NET.
//
// What it checks that the integer tables' conformance did not: that a recycled record is reset; that the pose a record holds is the one it was
// given; that the capacity is a hard limit which refuses cleanly (including when slots are tied up in a quarantine, the case that made the
// check have to come before the allocation); that a joint's two references name the right bodies; and that "which joints die with this body"
// finds exactly the live joints that touch it, with the world (null) never matching a body.
using System;
using System.Collections.Generic;
using Prowl.Runtime.Physics2D;

class RegistryConformance
{
    static int seed = 7770123;

    static int Next()
    {
        seed = seed * 1103515245 + 12345;
        return (seed >> 16) & 0x7fff;
    }

    static int Mix(int hash, int value)
    {
        return (hash * 31) ^ value;
    }

    static int Q(float v)
    {
        return (int)(v * 100f);
    }

    const int BodyCap = PhysicsLimits.Bodies;
    const int JointCap = PhysicsLimits.Joints;

    // ---- bodies and their records, against a model ------------------------------------------------------------------------------
    //
    // The model keeps, per slot: alive, generation, when it was last freed (for LIFO reuse), whether it is held by a quarantine, and the pose
    // the record should hold. "Reuse the most recently freed slot that is not held, else the lowest never used since the last Clear, else
    // the answer is -1" is the whole rule.

    static int BodiesRun(Registry2D reg)
    {
        reg.SetQuarantining(false);
        reg.Clear();          // a fresh start, in the SAME registry: see Main
        bool[] alive = new bool[BodyCap];
        int[] gen = new int[BodyCap];
        int[] freedAt = new int[BodyCap];
        bool[] held = new bool[BodyCap];
        int[] heldOrder = new int[BodyCap];
        int[] curX = new int[BodyCap];          // what Q(record.Pose.CurX) must be
        int[] prevX = new int[BodyCap];
        int[] pushes = new int[BodyCap];
        int[] marks = new int[BodyCap];         // what Native was set to
        int clock = 0, heldClock = 0, next = 0, live = 0;
        bool quarantine = false;
        int mismatches = 0, hash = 17, refused = 0, fullAt = 0;

        for (int step = 0; step < 14000; step++)
        {
            // the first phase fills the registry to its limit; later ones churn it
            int op = step < 1500 ? 0 : Next() % 100;
            if (op < 40)
            {
                int expected = -1;
                int best = 0;
                for (int i = 0; i < next; i++)
                    if (!alive[i] && !held[i] && freedAt[i] > best) { best = freedAt[i]; expected = i; }
                if (expected < 0 && next < BodyCap) { expected = next; next++; }

                int got = reg.AddBody();
                if (got != expected) mismatches++;
                if (got < 0) { refused++; if (fullAt == 0) fullAt = step; }
                else
                {
                    gen[got]++;
                    alive[got] = true;
                    freedAt[got] = 0;
                    live++;
                    pushes[got] = 0; curX[got] = 0; prevX[got] = 0; marks[got] = 0;
                    BodyRecord? rec = reg.Body(got);
                    if (rec == null) mismatches++;
                    else
                    {
                        if (rec.Index != got || rec.Native != 0u || rec.Asleep || rec.Pose.Valid) mismatches++;   // a recycled record starts clean
                        marks[got] = Next() + 1;
                        rec.Native = (uint)marks[got];
                    }
                }
                hash = Mix(hash, got);
            }
            else if (op < 62)
            {
                int index = Next() % (next + 3) - 1;
                bool expected = index >= 0 && index < next && alive[index];
                bool removed = reg.RemoveBody(index);
                if (removed != expected) mismatches++;
                if (expected)
                {
                    alive[index] = false;
                    gen[index]++;
                    live--;
                    if (quarantine) { held[index] = true; heldClock++; heldOrder[index] = heldClock; }
                    else { clock++; freedAt[index] = clock; }
                }
                hash = Mix(hash, removed ? 1 : 0);
            }
            else if (op < 80)
            {
                // a step moves a body: the record takes the pose, and keeps the one before it
                int index = Next() % (next + 1);
                BodyRecord? rec = reg.Body(index);
                bool isLive = index < next && alive[index];
                if ((rec != null) != isLive) mismatches++;
                if (rec != null)
                {
                    float x = (Next() % 4000) / 10f, y = (Next() % 4000) / 10f;
                    float ang = (Next() % 600) / 100f - 3f;
                    rec.Moved(x, y, MathF.Cos(ang), MathF.Sin(ang), (Next() & 1) == 1, 100 + step);
                    prevX[index] = pushes[index] == 0 ? Q(x) : curX[index];
                    curX[index] = Q(x);
                    pushes[index]++;
                    if (Q(rec.Pose.CurX) != curX[index] || Q(rec.Pose.PrevX) != prevX[index]) mismatches++;
                    if (rec.Native != (uint)marks[index]) mismatches++;
                    hash = Mix(hash, Q(rec.Pose.CurAngle) * 3 + Q(rec.Pose.PrevAngle));
                }
            }
            else if (op < 90)
            {
                quarantine = !quarantine;
                reg.SetQuarantining(quarantine);
                if (!quarantine)
                {
                    reg.Flush();
                    for (int order = 1; order <= heldClock; order++)
                        for (int i = 0; i < next; i++)
                            if (held[i] && heldOrder[i] == order) { held[i] = false; clock++; freedAt[i] = clock; }
                    heldClock = 0;
                }
            }
            else if (op < 99)
            {
                int index = Next() % (next + 2);
                bool isLive = index < next && alive[index];
                if (reg.BodyLive(index) != isLive) mismatches++;
                if (reg.BodyGeneration(index) != (isLive ? gen[index] : 0)) mismatches++;
            }
            else if (step > 4000)
            {
                reg.Clear();
                for (int i = 0; i < next; i++) { if (alive[i]) gen[i]++; alive[i] = false; held[i] = false; freedAt[i] = 0; }
                next = 0; live = 0; clock = 0; heldClock = 0;
            }
            if (reg.BodyCount != live) mismatches++;
        }

        Console.WriteLine($"bodies: live {reg.BodyCount} highwater {reg.BodyHighWater} refused {refused} first full at {fullAt} mismatches {mismatches} hash {hash}");
        return mismatches;
    }

    // ---- colliders: ids only -----------------------------------------------------------------------------------------------------

    static int CollidersRun(Registry2D reg)
    {
        reg.SetQuarantining(false);
        reg.Clear();          // a fresh start, in the SAME registry: see Main
        bool[] alive = new bool[600];
        int next = 0, live = 0, mismatches = 0, hash = 17;
        for (int step = 0; step < 4000; step++)
        {
            int op = Next() % 100;
            if (op < 50)
            {
                int index = reg.AddCollider();
                bool fresh = index >= next;
                if (index >= 600 || (index < next && alive[index])) { mismatches++; continue; }   // never a live one
                if (fresh) next = index + 1;
                alive[index] = true;
                live++;
                hash = Mix(hash, index);
            }
            else
            {
                int index = Next() % (next + 2);
                bool expected = index < next && alive[index];
                if (reg.RemoveCollider(index) != expected) mismatches++;
                if (expected) { alive[index] = false; live--; }
            }
            if (reg.ColliderCount != live) mismatches++;
            int probe = Next() % (next + 1);
            if (reg.ColliderLive(probe) != (probe < next && alive[probe])) mismatches++;
        }
        Console.WriteLine($"colliders: live {reg.ColliderCount} highwater {reg.ColliderHighWater} mismatches {mismatches} hash {hash}");
        return mismatches;
    }

    // ---- joints: references between two arenas ----------------------------------------------------------------------------------
    //
    // Behaves the way the simulation uses it: removing a body first asks which joints touch it, removes those, then removes the body.

    static int JointsRun(Registry2D reg)
    {
        reg.SetQuarantining(false);
        reg.Clear();          // a fresh start, in the SAME registry: see Main
        const int Bodies = 24;
        int[] bodyIndex = new int[Bodies];       // the slot each live body has, or -1
        bool[] bodyLive = new bool[Bodies];
        bool[] jLive = new bool[JointCap];
        int[] jA = new int[JointCap];            // body SLOTS, -1 = the world
        int[] jB = new int[JointCap];
        List<int> found = new List<int>();
        int mismatches = 0, hash = 17, jointsRefused = 0, lostTotal = 0, liveJoints = 0;

        for (int b = 0; b < Bodies; b++) { bodyIndex[b] = reg.AddBody(); bodyLive[b] = true; }

        for (int step = 0; step < 12000; step++)
        {
            int op = Next() % 100;
            if (op < 45)
            {
                // a joint between two live bodies, or one and the world; sometimes an end is a body that is gone (= the world)
                int a = Next() % (Bodies + 1) - 1;
                int b = Next() % Bodies;
                int sa = a >= 0 && bodyLive[a] ? bodyIndex[a] : -1;
                int sb = bodyLive[b] ? bodyIndex[b] : -1;
                int j = reg.AddJoint(sa, sb);          // the slots the model believes in; a dead body's old slot may belong to another body now
                // (no quarantine here, so a free slot is any slot that is not live: all this checks is the cap and liveness)
                if (liveJoints >= JointCap) { if (j != -1) mismatches++; jointsRefused++; }
                else
                {
                    if (j < 0 || j >= JointCap || jLive[j]) mismatches++;
                    else
                    {
                        jLive[j] = true; jA[j] = sa; jB[j] = sb; liveJoints++;
                        if (reg.JointBodyA(j) != sa || reg.JointBodyB(j) != sb) mismatches++;
                        hash = Mix(hash, j * 7 + sa * 3 + sb);
                    }
                }
            }
            else if (op < 65)
            {
                int j = Next() % (JointCap + 2) - 1;
                bool expected = j >= 0 && j < JointCap && jLive[j];
                if (reg.RemoveJoint(j) != expected) mismatches++;
                if (expected) { jLive[j] = false; liveJoints--; }
                if (reg.JointBodyA(j) != -1 || reg.JointBodyB(j) != -1) mismatches++;       // a dead joint names nobody
            }
            else if (op < 90)
            {
                // a body is removed: ask what touches it, remove those, then the body; or a body is made again in a free slot
                int b = Next() % Bodies;
                if (bodyLive[b])
                {
                    int slot = bodyIndex[b];
                    found.Clear();
                    int n = reg.JointsOfBody(slot, found);
                    int expected = 0;
                    for (int j = 0; j < JointCap; j++)
                        if (jLive[j] && (jA[j] == slot || jB[j] == slot)) expected++;
                    if (n != expected || found.Count != expected) mismatches++;
                    for (int i = 0; i < found.Count; i++)
                    {
                        int j = found[i];
                        if (!jLive[j] || !(jA[j] == slot || jB[j] == slot)) mismatches++;
                        reg.RemoveJoint(j);
                        jLive[j] = false; liveJoints--; lostTotal++;
                        hash = Mix(hash, j);
                    }
                    reg.RemoveBody(slot);
                    bodyLive[b] = false;
                }
                else
                {
                    int slot = reg.AddBody();
                    if (slot < 0) mismatches++;
                    else { bodyIndex[b] = slot; bodyLive[b] = true; }
                }
            }
            else
            {
                // the world is not a body: asking about slot -1, or a slot out of range, finds nothing
                found.Clear();
                if (reg.JointsOfBody(-1, found) != 0 || reg.JointsOfBody(BodyCap + 5, found) != 0 || found.Count != 0) mismatches++;
            }
            if (reg.JointCount != liveJoints) mismatches++;
        }
        Console.WriteLine($"joints: live {reg.JointCount} refused {jointsRefused} lost with bodies {lostTotal} mismatches {mismatches} hash {hash}");
        return mismatches;
    }

    public static int Main()
    {
        // ONE registry, reused. An arena belongs to its class, not to a registry: the 512 BodyRecords that BodiesRun fills are all there are, so a
        // second Registry2D would have none to make (and in C that is an abort). The C build has one registry for the process, as it has one
        // native world; a .NET program with several simulations has several, each with its own records.
        Registry2D reg = new Registry2D();
        int bad = 0;
        bad += BodiesRun(reg);
        bad += CollidersRun(reg);
        bad += JointsRun(reg);
        Console.WriteLine($"total mismatches {bad}");
        return bad == 0 ? 0 : 1;
    }
}
