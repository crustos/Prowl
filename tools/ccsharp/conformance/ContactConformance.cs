// The contact events of a collider that has SEVERAL shapes, against the real Box2D library, and the table behind them (ContactSet).
//
// Run twice by `python3 build.py ccsharp`: on real .NET and translated to C. The two outputs must be identical; and every line that says "FAIL"
// is a semantic failure, not only a translation one (a wrong rule would agree with itself in both).
//
// Box2D reports a contact for every pair of SHAPES. The game is to be told once per pair of COLLIDERS: a Begin when the first pair of shapes touches,
// an End when the last one stops. The cases:
//
//   seam      one collider made of two boxes side by side (what an edge collider or a chunk of terrain is); a ball rolls across the join and off the end.
//             One Begin, one End. Counting the end of the first box before the begin of the second would give End, Begin at the join.
//   bounce    a ball bouncing on ONE shape. Every bounce is a Begin and an End: nothing a collider of one shape did is lost.
//   fast seam the same, but the ball crosses the join in ONE step: the end of the left box and the begin of the right box come together, and must
//             not read as End, Begin (the step's begins are counted before its ends).
//   rebuild   the shapes of the collider are destroyed and made again (a terrain chunk after a dig) with the ball resting on it: no event at all, and when
//             the ball leaves afterwards, one End (the native layer reports no end for a destroyed shape, so the core is told: ShapesVanishing).
//   dig       the same, but the new shapes are lower: the ball is no longer touched, and is told so once, with no native event to say it.
//   reuse     a collider is removed while touching, and its slot handed to a new collider that touches the same body: that is a Begin. The old
//             contact must not be remembered under the reused slot.
using System;
using System.Collections.Generic;
using Prowl.Native.Box2D;
using Prowl.Runtime.Physics2D;

class ContactConformance
{
    static int failures = 0;

    static void Check(bool ok, string what)
    {
        if (ok) return;
        failures++;
        Console.WriteLine("FAIL " + what);
    }

    static int Mix(int hash, int value) { return (hash * 31) ^ value; }

    // ---- ContactSet against a naive model ------------------------------------------------------------------------------------------

    static uint seed = 12345u;
    static int Next(int n) { seed = seed * 1664525u + 1013904223u; return (int)((seed >> 8) % (uint)n); }

    static void TestSet()
    {
        ContactSet set = new ContactSet();
        int[] model = new int[64];                    // model[lo * 8 + hi]: how many shape pairs touch
        int wrong = 0, ops = 0, hash = 0;
        for (int i = 0; i < 20000; i++)
        {
            int a = Next(8), b = Next(8);
            int lo = a < b ? a : b, hi = a < b ? b : a;
            long key = ContactSet.Pair(a, b);
            if (ContactSet.Pair(b, a) != key) wrong++;
            int kind = Next(10);
            if (kind < 5)
            {
                model[lo * 8 + hi]++;
                if (set.Increment(key) != model[lo * 8 + hi]) wrong++;
            }
            else if (kind < 9)
            {
                int expect = model[lo * 8 + hi] == 0 ? -1 : model[lo * 8 + hi] - 1;
                if (model[lo * 8 + hi] > 0) model[lo * 8 + hi]--;
                if (set.Decrement(key) != expect) wrong++;
            }
            else
            {
                int c = Next(8), expectRemoved = 0;
                for (int x = 0; x < 8; x++)
                    for (int y = x; y < 8; y++)
                        if ((x == c || y == c) && model[x * 8 + y] > 0)
                        {
                            expectRemoved++;
                            model[x * 8 + y] = 0;
                        }
                if (set.RemoveInvolving(c) != expectRemoved) wrong++;
            }
            int pairs = 0;
            for (int x = 0; x < 8; x++)
                for (int y = x; y < 8; y++)
                {
                    if (model[x * 8 + y] > 0) pairs++;
                    if (set.Touching(ContactSet.Pair(x, y)) != model[x * 8 + y]) wrong++;
                }
            if (set.Count != pairs) wrong++;
            hash = Mix(hash, pairs);
            ops++;
        }
        Console.WriteLine("contact set    " + ops + " operations, " + wrong + " wrong, hash " + hash);
        Check(wrong == 0, "ContactSet disagrees with the naive model");
    }

    // ---- scenes on the real native world -------------------------------------------------------------------------------------------

    static SimCore2D core = null;
    static uint[] nativeBody;

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

    static int begins = 0, ends = 0, others = 0;     // what the ball's collider was told about the ground's, in one direction

    // steps once and counts the events between the ball's collider and the ground's
    static void StepAndCount(int colBall, int colGround)
    {
        core.Step(0.0166667f, 4);
        while (core.NextEvent())
        {
            if (core.EventSelf == colBall && core.EventOther == colGround)
            {
                if (core.EventKind == SimEvent.CollisionBegin) begins++;
                else if (core.EventKind == SimEvent.CollisionEnd) ends++;
                else others++;
            }
        }
        core.EndStep();
    }

    static void NewWorld()
    {
        if (core == null) core = new SimCore2D();      // an arena class: one instance, in C; the world is given back at the end of each scene and taken again here
        core.Acquire();
        begins = 0;
        ends = 0;
        others = 0;
    }

    static void TestSeam()
    {
        NewWorld();
        int ground = NewBody(PB2.BodyStatic, 0f, -0.5f);
        int colGround = core.Registry.AddCollider();
        PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, -2f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);   // left half: x -4 .. 0
        PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, 2f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);    // right half: x 0 .. 4
        int ball = NewBody(PB2.BodyDynamic, -3f, 0.26f);
        int colBall = core.Registry.AddCollider();
        PB2.ShapeCreateCircle(nativeBody[ball], colBall, 0, 0f, 0f, 0.25f, 1f, 0.5f, 0f, 0u);
        PB2.BodySetVelocity(nativeBody[ball], 2f, 0f, 0f);
        for (int step = 0; step < 420; step++) StepAndCount(colBall, colGround);
        BodyRecord r = core.Registry.Body(ball);
        Console.WriteLine("seam           begins " + begins + ", ends " + ends + ", other " + others + ", ball ended at y*1000 " + (int)(r.Pose.CurY * 1000f));
        Check(r.Pose.CurY < -1f, "seam: the ball should have rolled off the end and fallen");
        Check(begins == 1, "seam: " + begins + " Begins for one landing (a ball rolling across the join of two shapes is still touching)");
        Check(ends == 1, "seam: " + ends + " Ends for one take-off");
        core.Release();
    }

    static void TestSeamFast()
    {
        NewWorld();
        int ground = NewBody(PB2.BodyStatic, 0f, -0.5f);
        int colGround = core.Registry.AddCollider();
        PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, -2f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, 2f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        int ball = NewBody(PB2.BodyDynamic, -3.9f, 0.26f);
        int colBall = core.Registry.AddCollider();
        PB2.ShapeCreateCircle(nativeBody[ball], colBall, 0, 0f, 0f, 0.25f, 1f, 0.5f, 0f, 0u);
        PB2.BodySetVelocity(nativeBody[ball], 25f, 0f, 0f);                    // 0.42 m a step: the left box ends and the right one begins in the same step
        for (int step = 0; step < 90; step++) StepAndCount(colBall, colGround);
        Console.WriteLine("fast seam      begins " + begins + ", ends " + ends);
        Check(begins == 1, "fast seam: " + begins + " Begins (a ball crossing the join in one step is still touching)");
        Check(ends == 1, "fast seam: " + ends + " Ends");
        core.Release();
    }

    static void TestDig()
    {
        NewWorld();
        int ground = NewBody(PB2.BodyStatic, 0f, -0.5f);
        int colGround = core.Registry.AddCollider();
        uint s1 = PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, -2f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        uint s2 = PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, 2f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        int ball = NewBody(PB2.BodyDynamic, -1f, 1f);
        int colBall = core.Registry.AddCollider();
        PB2.ShapeCreateCircle(nativeBody[ball], colBall, 0, 0f, 0f, 0.25f, 1f, 0.5f, 0f, 0u);
        for (int step = 0; step < 120; step++) StepAndCount(colBall, colGround);       // it lands and settles
        int beginsBefore = begins, endsBefore = ends;
        core.ShapesVanishing(colGround);                                                // the dig: the ground is made again, three units lower
        PB2.ShapeDestroy(s1);
        PB2.ShapeDestroy(s2);
        PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, -2f, -3f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, 2f, -3f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        for (int step = 0; step < 150; step++) StepAndCount(colBall, colGround);       // it falls, and lands on the lower ground
        float y = core.Registry.Body(ball).Pose.CurY;
        Console.WriteLine("dig            landed " + beginsBefore + ", then ends +" + (ends - endsBefore) + ", begins +" + (begins - beginsBefore) + ", ball at y*1000 " + (int)(y * 1000f));
        Check(y < -2.5f && y > -3.1f, "dig: the ball should rest on the lower ground (y " + (int)(y * 1000f) + ")");
        Check(ends - endsBefore == 1, "dig: the ground left from under the ball and it was told " + (ends - endsBefore) + " times (it must be once)");
        Check(begins - beginsBefore == 1, "dig: it landed on the lower ground and was told " + (begins - beginsBefore) + " times (it must be once)");
        core.Release();
    }

    static void TestBounce()
    {
        NewWorld();
        int ground = NewBody(PB2.BodyStatic, 0f, -0.5f);
        int colGround = core.Registry.AddCollider();
        PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 20f, 0.5f, 0f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        int ball = NewBody(PB2.BodyDynamic, 0f, 4f);
        int colBall = core.Registry.AddCollider();
        PB2.ShapeCreateCircle(nativeBody[ball], colBall, 0, 0f, 0f, 0.25f, 1f, 0.5f, 0.8f, 0u);
        // the oracle: every bounce is the bottom of an arc, a local minimum of the ball's height; it does not use the events
        float y2 = 1e9f, y1 = 1e9f;
        int bounces = 0;
        for (int step = 0; step < 420; step++)
        {
            StepAndCount(colBall, colGround);
            float y = core.Registry.Body(ball).Pose.CurY;
            if (y1 < y2 && y1 < y && y1 < 1f) bounces++;
            y2 = y1;
            y1 = y;
        }
        Console.WriteLine("bounce         begins " + begins + ", ends " + ends + ", bounces counted from the height " + bounces);
        Check(bounces >= 4, "bounce: the oracle saw only " + bounces + " bounces, the scene proves little");
        int diff = begins - bounces;
        Check(diff >= -1 && diff <= 1, "bounce: " + begins + " Begins for " + bounces + " bounces: a bouncing ball must be told of every bounce, and of no more");
        Check(ends == begins || ends == begins - 1, "bounce: " + begins + " Begins but " + ends + " Ends");
        core.Release();
    }

    static void TestRebuild()
    {
        NewWorld();
        int ground = NewBody(PB2.BodyStatic, 0f, -0.5f);
        int colGround = core.Registry.AddCollider();
        uint s1 = PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, -2f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        uint s2 = PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, 2f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        int ball = NewBody(PB2.BodyDynamic, -1f, 1f);
        int colBall = core.Registry.AddCollider();
        PB2.ShapeCreateCircle(nativeBody[ball], colBall, 0, 0f, 0f, 0.25f, 1f, 0.5f, 0f, 0u);
        for (int step = 0; step < 120; step++) StepAndCount(colBall, colGround);       // it lands and settles on the left half
        int beginsBefore = begins, endsBefore = ends;
        for (int round = 0; round < 3; round++)                                         // three digs: every shape destroyed and made again
        {
            core.ShapesVanishing(colGround);
            PB2.ShapeDestroy(s1);
            PB2.ShapeDestroy(s2);
            s1 = PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, -2f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
            s2 = PB2.ShapeCreateBox(nativeBody[ground], colGround, 0, 2f, 0.5f, 2f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
            for (int step = 0; step < 30; step++) StepAndCount(colBall, colGround);
        }
        Console.WriteLine("rebuild        begins " + beginsBefore + " then +" + (begins - beginsBefore) + ", ends " + endsBefore + " then +" + (ends - endsBefore));
        Check(beginsBefore == 1, "rebuild: the ball should have landed once (" + beginsBefore + ")");
        bool silent = begins == beginsBefore && ends == endsBefore;
        int extraBegins = begins - beginsBefore;
        int extraEnds = ends - endsBefore;
        string why = "rebuild: rebuilding the shapes under a resting ball must produce no event (+" + extraBegins + " Begins, +" + extraEnds + " Ends)";
        Check(silent, why);
        // and when the ball then leaves, the collider must be told it stopped touching: once
        PB2.BodySetAwake(nativeBody[ball], 1);
        PB2.BodySetVelocity(nativeBody[ball], 0f, 6f, 0f);
        for (int step = 0; step < 90; step++) StepAndCount(colBall, colGround);
        Console.WriteLine("rebuild        the ball leaves: ends " + ends + " (was " + endsBefore + ")");
        Check(ends == endsBefore + 1, "rebuild: after three rebuilds the ball left and the collider was told " + (ends - endsBefore) + " times that it stopped touching (it must be once)");
        core.Release();
    }

    static void TestReuse()
    {
        NewWorld();
        int ground = NewBody(PB2.BodyStatic, 0f, -0.5f);
        int colOld = core.Registry.AddCollider();
        PB2.ShapeCreateBox(nativeBody[ground], colOld, 0, 5f, 0.5f, 0f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        int ball = NewBody(PB2.BodyDynamic, 0f, 0.8f);
        int colBall = core.Registry.AddCollider();
        PB2.ShapeCreateCircle(nativeBody[ball], colBall, 0, 0f, 0f, 0.25f, 1f, 0.5f, 0f, 0u);
        for (int step = 0; step < 60; step++) StepAndCount(colBall, colOld);
        int landed = begins;
        // the old collider goes (the game's handler removed it), and a new one takes its slot and touches the same ball
        core.Registry.RemoveCollider(colOld);
        core.ForgetCollider(colOld);
        int colNew = core.Registry.AddCollider();
        string same = "is";
        if (colNew != colOld) same = "is NOT";
        Console.WriteLine("reuse          the new collider's slot " + same + " the old one's");
        Check(colNew == colOld, "reuse: the slot was not reused, so this case proves nothing");
        int ground2 = NewBody(PB2.BodyStatic, 0f, -0.5f);
        PB2.ShapeCreateBox(nativeBody[ground2], colNew, 0, 5f, 0.5f, 0f, 0f, 0f, 0f, 1f, 0.5f, 0f, 0u);
        begins = 0;
        ends = 0;
        PB2.BodySetAwake(nativeBody[ball], 1);      // it has been resting: asleep, and a shape made under a sleeping body makes no contact event until it wakes
        for (int step = 0; step < 60; step++) StepAndCount(colBall, colNew);
        Console.WriteLine("reuse          landed " + landed + ", then the new collider: begins " + begins + ", ends " + ends);
        Check(landed == 1, "reuse: the ball should have landed once on the old collider (" + landed + ")");
        Check(begins == 1, "reuse: the ball touches the new collider but was never told (the old contact was remembered under the reused slot)");
        core.Release();
    }

    public static int Main()
    {
        nativeBody = new uint[16];
        TestSet();
        TestSeam();
        TestSeamFast();
        TestBounce();
        TestRebuild();
        TestDig();
        TestReuse();
        if (failures == 0) Console.WriteLine("all checks passed");
        else Console.WriteLine("FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
