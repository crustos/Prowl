// Drives the real Box2D shim (prowl_box2d) through the bindings generated from prowl_box2d.h, and prints integers only.
//
// The same file is run twice by `python3 build.py ccsharp`: on real .NET through [LibraryImport] (the "net" flavor of
// tools/ccsharp/gen_pb2.py), and translated by CCSharp to C and linked against the same native library (the "c" flavor, which spells each
// call as C). Both flavors are generated from one header with the same signatures, so this file is the same text for both, and the two
// outputs must be identical. A difference means one of the bindings is wrong: a pointer passed as the wrong kind of thing, a record field
// at the wrong name, an event array read with the wrong stride.
//
// It uses every kind of thing the generator produces: scalars, records by reference (in and out), scalar arrays, record lists, record
// arrays reached through a view accessor, and an array member read and written through accessors.
using System;
using System.Collections.Generic;
using Prowl.Native.Box2D;

class NativeConformance
{
    static int Q(float v)
    {
        return (int)(v * 1000f);
    }

    static int Mix(int hash, int value)
    {
        return (hash * 31) ^ value;
    }

    public static int Main()
    {
        // ---- the ABI the managed side was generated against must be the one in the library ------------------------------------
        int[] abi = new int[16];
        PB2.Abi(abi);
        Console.WriteLine($"abi version {abi[0]} joint record {abi[8]}");

        float[] layerCheck = new float[1];
        uint[] rows = new uint[32];
        for (int i = 0; i < 32; i++) rows[i] = 0xFFFFFFFFu;

        PB2.WorldCreate(0f, -10f, 1);
        PB2.WorldSetLayerMatrix(rows);

        // ---- a ground and a stack of boxes -------------------------------------------------------------------------------------
        uint[] bodies = new uint[16];
        bodies[0] = PB2.BodyCreate(PB2.BodyStatic, 0f, -0.5f, 0f, 0, 1f, 0f, 0f, 0u);
        PB2.ShapeCreateBox(bodies[0], 0, 0, 20f, 0.5f, 0f, 0f, 0f, 0f, 1f, 0.6f, 0f, 0u);
        for (int i = 1; i <= 5; i++)
        {
            bodies[i] = PB2.BodyCreate(PB2.BodyDynamic, 0f, 0.5f + (i - 1) * 1.05f, 0f, i, 1f, 0f, 0.05f, 0u);
            PB2.ShapeCreateBox(bodies[i], i, 0, 0.5f, 0.5f, 0f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        }

        // ---- a triangle from a float array: the scalar-array path --------------------------------------------------------------
        float[] tri = new float[6];
        tri[0] = 0f; tri[1] = 0f; tri[2] = 1f; tri[3] = 0f; tri[4] = 0.5f; tri[5] = 1f;
        bodies[6] = PB2.BodyCreate(PB2.BodyDynamic, 4f, 3f, 0f, 6, 1f, 0f, 0.05f, 0u);
        uint triShape = PB2.ShapeCreatePolygon(bodies[6], 6, 0, tri, 3, 0f, 1f, 0.4f, 0.1f, 0u);
        Console.WriteLine($"triangle shape made {(triShape != 0u ? 1 : 0)}");

        // ---- a pendulum: a revolute joint, filled in field by field; its parameters live in an array member ----------------------
        bodies[7] = PB2.BodyCreate(PB2.BodyStatic, 8f, 6f, 0f, 7, 1f, 0f, 0f, 0u);
        bodies[8] = PB2.BodyCreate(PB2.BodyDynamic, 10f, 6f, 0f, 8, 1f, 0f, 0f, 0u);
        PB2.ShapeCreateBox(bodies[8], 8, 0, 0.4f, 0.2f, 0f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);

        PB2JointDef def = new PB2JointDef();
        def.bodyA = bodies[7];
        def.bodyB = bodies[8];
        def.jointIndex = 3;
        def.type = PB2.JointRevolute;
        def.flags = PB2.JfLimit;
        def.ax = 0f; def.ay = 0f; def.aAngle = 0f;
        def.bx = -2f; def.by = 0f; def.bAngle = 0f;
        def.forceThreshold = 3.0e38f;
        def.torqueThreshold = 3.0e38f;
        PB2.JointDefSetP(ref def, 3, -0.6f);   // lower angle
        PB2.JointDefSetP(ref def, 4, 0.6f);    // upper angle
        Console.WriteLine($"joint params read back {Q(PB2.JointDefGetP(ref def, 3))} {Q(PB2.JointDefGetP(ref def, 4))}");
        uint joint = PB2.JointCreate(ref def);
        Console.WriteLine($"joint made {(joint != 0u ? 1 : 0)} valid {PB2.JointIsValid(joint)}");

        // ---- step, reading the event arrays the library fills in ---------------------------------------------------------------
        PB2StepInfo info = new PB2StepInfo();
        int[] lastY = new int[16];
        int moves = 0, begins = 0, hash = 17, awake = 0;
        List<PB2TransformSet> sets = new List<PB2TransformSet>();
        for (int step = 0; step < 150; step++)
        {
            if (step == 70)
            {
                // a batched teleport: the record-list path
                PB2TransformSet t = new PB2TransformSet();
                t.body = bodies[5];
                t.x = 0.3f; t.y = 9f; t.angle = 0f; t.mode = 0;
                sets.Add(t);
                PB2.BodiesSetTransforms(sets, 1, 0.0166667f);
            }
            PB2.Step(0.0166667f, 4, ref info);
            moves += info.moveCount;
            begins += info.contactBeginCount;
            awake = info.awakeBodyCount;
            for (int i = 0; i < info.moveCount; i++)
            {
                PB2BodyMove m = PB2.StepInfoMoves(ref info, i);
                lastY[m.bodyIndex] = Q(m.y);
                hash = Mix(hash, m.bodyIndex * 100003 + Q(m.y) + Q(m.x) * 7 + Q(m.s) * 13);
            }
            for (int i = 0; i < info.contactBeginCount; i++)
            {
                PB2ContactEvent c = PB2.StepInfoContacts(ref info, i);
                hash = Mix(hash, c.colliderA * 31 + c.colliderB + Q(c.ny));
            }
        }
        Console.WriteLine($"stepped: moves {moves} contact begins {begins} awake {awake} hash {hash}");
        for (int i = 1; i <= 8; i++) Console.WriteLine($"body {i} y {lastY[i]}");

        // ---- the joint's state, and the world's queries -------------------------------------------------------------------------
        float[] js = new float[8];
        PB2.JointGetState(joint, js);
        Console.WriteLine($"pendulum angle {Q(js[3])} force {Q(js[0])} {Q(js[1])}");

        PB2RayHit one = new PB2RayHit();
        int hitOne = PB2.Raycast(0.1f, 20f, 0f, -1f, 40f, 0xFFFFFFFFu, 0, ref one);
        Console.WriteLine($"ray: hit {hitOne} collider {one.collider} fraction {Q(one.fraction)}");

        List<PB2RayHit> hits = new List<PB2RayHit>();
        for (int i = 0; i < 8; i++)
        {
            PB2RayHit blank = new PB2RayHit();
            hits.Add(blank);
        }
        int n = PB2.RaycastAll(0.1f, 20f, 0f, -1f, 40f, 0xFFFFFFFFu, 0, hits, 8);
        int rayHash = 17;
        for (int i = 0; i < n; i++)
        {
            PB2RayHit h = hits[i];
            rayHash = Mix(rayHash, h.collider * 1009 + Q(h.fraction));
        }
        Console.WriteLine($"ray all: {n} hits hash {rayHash}");

        int[] overlap = new int[16];
        int overlaps = PB2.OverlapCircle(0f, 1.5f, 1.2f, 0xFFFFFFFFu, 0, overlap, 16);
        int overlapHash = 17;
        for (int i = 0; i < overlaps; i++) overlapHash = Mix(overlapHash, overlap[i]);
        Console.WriteLine($"overlap circle: {overlaps} colliders hash {overlapHash}");

        float[] state = new float[10];
        PB2.BodyGetState(bodies[6], state);
        Console.WriteLine($"triangle at {Q(state[0])} {Q(state[1])} awake {Q(state[9])}");

        PB2.JointDestroy(joint);
        Console.WriteLine($"joint valid after destroy {PB2.JointIsValid(joint)}");
        PB2.WorldDestroy();
        return 0;
    }
}
