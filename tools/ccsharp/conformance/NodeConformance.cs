// Tier 1 of the 2D runtime: the node hierarchy and its transforms. Run on .NET and as translated C; the outputs must be identical.
//
// Random edits, reparenting (with and without keeping the world pose, and attempts that would make a cycle), detaches and reads, on 48 nodes,
// checked against a naive model written here: parents, child order and local values are kept in plain arrays and every world transform is
// recomputed from the root each time it is asked, with no cache. "mismatches 0" therefore means the cache, the invalidation and the child links
// are right, not only that C agrees with .NET.
//
// The version property is checked too: after an edit of node i, the world version of i and of everything below it must have risen, and the world
// version of every other node must be exactly what it was. That is what lets a body tell its own write-back from somebody else's edit.
using System;
using Prowl.Core2D;
using Prowl.Runtime.Physics2D;

class NodeConformance
{
    const int N = 48;

    static int seed = 424242;
    static int Next() { seed = seed * 1103515245 + 12345; return (seed >> 16) & 0x7fff; }
    static int Mix(int hash, int value) { return (hash * 31) ^ value; }
    static int Q(float v) { return (int)(v * 100f); }

    static float Rand(float lo, float hi) { return lo + (hi - lo) * (Next() / 32767f); }

    // ---- the model ---------------------------------------------------------------------------------------------------------------
    static int[] mParent;
    static float[] mx, my, ma, msx, msy;
    static int[] kids;          // kids[p * N + k]: the k-th child of p, in order
    static int[] kc;

    static bool MIsDesc(int i, int of)
    {
        for (int p = mParent[i]; p >= 0; p = mParent[p])
            if (p == of) return true;
        return false;
    }

    // The same arithmetic as Node.EnsureWorld, in the same order, but with no cache: from the root, every time.
    static void MWorld(int i, out float a, out float b, out float c, out float d, out float tx, out float ty)
    {
        float cs = MathF.Cos(ma[i]), sn = MathF.Sin(ma[i]);
        float la = cs * msx[i], lb = sn * msx[i], lc = -sn * msy[i], ld = cs * msy[i];
        if (mParent[i] < 0)
        {
            a = la; b = lb; c = lc; d = ld; tx = mx[i]; ty = my[i];
            return;
        }
        float pa, pb, pc, pd, ptx, pty;
        MWorld(mParent[i], out pa, out pb, out pc, out pd, out ptx, out pty);
        a = pa * la + pc * lb;
        b = pb * la + pd * lb;
        c = pa * lc + pc * ld;
        d = pb * lc + pd * ld;
        tx = pa * mx[i] + pc * my[i] + ptx;
        ty = pb * mx[i] + pd * my[i] + pty;
    }

    static void MRemoveChild(int parent, int child)
    {
        int at = -1;
        for (int k = 0; k < kc[parent]; k++)
            if (kids[parent * N + k] == child) { at = k; break; }
        for (int k = at; k < kc[parent] - 1; k++) kids[parent * N + k] = kids[parent * N + k + 1];
        kc[parent]--;
    }

    // Reparenting without keeping the world pose moves a node under a new parent's scale; do that enough times and positions grow without limit
    // (to infinity, and then NaN, which fails every comparison). A real scene would not; so a node whose world position has run away is given a
    // sane local pose, applied to the model and to the node alike, shallowest first so a parent is settled before its children are judged.
    static int Tame(Node[] nodes)
    {
        int fixedUp = 0;
        for (int depth = 0; depth < N; depth++)
        {
            bool any = false;
            for (int j = 0; j < N; j++)
            {
                if (Depth(j) != depth) continue;
                any = true;
                float a, b, c, d, tx, ty;
                MWorld(j, out a, out b, out c, out d, out tx, out ty);
                float magnitude = MathF.Abs(tx) + MathF.Abs(ty);
                if (!(magnitude < 500f))        // also true for NaN
                {
                    float x = Rand(-20f, 20f), y = Rand(-20f, 20f), an = Rand(-3f, 3f);
                    nodes[j].SetLocal(x, y, an, 1f, 1f);
                    mx[j] = x; my[j] = y; ma[j] = an; msx[j] = 1f; msy[j] = 1f;
                    fixedUp++;
                }
            }
            if (!any) break;
        }
        return fixedUp;
    }

    static int Depth(int i)
    {
        int d = 0;
        for (int p = mParent[i]; p >= 0; p = mParent[p]) d++;
        return d;
    }

    static int bad;
    static int checks;
    static int[] badBy;          // which check failed: see the ids at each Expect

    static void Expect(bool ok, int what)
    {
        checks++;
        if (!ok) { bad++; badBy[what]++; }
    }

    static bool Near(float x, float y)
    {
        float t = 0.0005f + 0.0001f * (MathF.Abs(x) + MathF.Abs(y));
        return MathF.Abs(x - y) <= t;
    }

    static void CheckNode(Node[] nodes, int i)
    {
        float a, b, c, d, tx, ty;
        MWorld(i, out a, out b, out c, out d, out tx, out ty);
        Node n = nodes[i];
        Expect(Near(n.WorldX(), tx) && Near(n.WorldY(), ty), 0);
        Expect(Near(n.WorldAngle(), MathF.Atan2(b, a)), 1);
        Expect(Near(n.LossyScaleX(), MathF.Sqrt(a * a + b * b)), 2);
        float sy = MathF.Sqrt(c * c + d * d);
        Expect(Near(n.LossyScaleY(), a * d - b * c < 0f ? -sy : sy), 3);
        // the world point of a local point, and back
        float wx, wy, lx, ly;
        n.ToWorld(1.5f, -0.5f, out wx, out wy);
        Expect(Near(wx, a * 1.5f + c * -0.5f + tx) && Near(wy, b * 1.5f + d * -0.5f + ty), 4);
        float det = a * d - b * c;
        if (det > 0.05f || det < -0.05f)
        {
            n.ToLocal(wx, wy, out lx, out ly);
            Expect(Near(lx, 1.5f) && Near(ly, -0.5f), 5);
        }
    }

    static void CheckLinks(Node[] nodes, int i)
    {
        Node n = nodes[i];
        Expect(n.ChildCount == kc[i], 6);
        Node c = n.FirstChild;
        Node last = null;
        int k = 0;
        bool order = true;
        while (c != null && k < N)
        {
            if (k >= kc[i] || c != nodes[kids[i * N + k]]) order = false;
            if (c.Parent != n) order = false;
            if (c.PrevSibling != last) order = false;
            last = c;
            c = c.NextSibling;
            k++;
        }
        Expect(order && k == kc[i] && n.LastChild == last, 7);
        Expect(n.Parent == (mParent[i] < 0 ? null : nodes[mParent[i]]), 8);
    }

    public static int Main()
    {
        badBy = new int[64];
        mParent = new int[N];
        mx = new float[N]; my = new float[N]; ma = new float[N]; msx = new float[N]; msy = new float[N];
        kids = new int[N * N];
        kc = new int[N];
        Node[] nodes = new Node[N];
        int[] lastWV = new int[N];
        for (int i = 0; i < N; i++)
        {
            nodes[i] = new Node();
            nodes[i].Reset(i);
            mParent[i] = -1;
            mx[i] = 0f; my[i] = 0f; ma[i] = 0f; msx[i] = 1f; msy[i] = 1f;
        }
        for (int i = 0; i < N; i++) lastWV[i] = nodes[i].WorldVersion();

        float[] scales = new float[6];
        scales[0] = 0.8f; scales[1] = 1f; scales[2] = 1.25f; scales[3] = 1.5f; scales[4] = -1f; scales[5] = -0.8f;

        int accepted = 0, rejected = 0, detaches = 0, edits = 0, keepWorld = 0, tamed = 0;
        int hash = 17;

        for (int step = 0; step < 20000; step++)
        {
            int op = Next() % 100;
            int i = Next() % N;
            bool changed = false;          // the subtree of i was edited, so every world version in it must have risen
            bool subtreeAll = false;       // taming may have edited nodes elsewhere: then only 'did not go back' can be said of the rest

            if (op < 40)
            {
                float x = Rand(-50f, 50f), y = Rand(-50f, 50f), an = Rand(-3f, 3f);
                float sx = scales[Next() % 6], sy = scales[Next() % 6];
                int which = Next() % 4;
                if (which == 0) { nodes[i].SetLocal(x, y, an, sx, sy); mx[i] = x; my[i] = y; ma[i] = an; msx[i] = sx; msy[i] = sy; }
                else if (which == 1) { nodes[i].SetPosition(x, y); mx[i] = x; my[i] = y; }
                else if (which == 2) { nodes[i].SetAngle(an); ma[i] = an; }
                else { nodes[i].SetScale(sx, sy); msx[i] = sx; msy[i] = sy; }
                changed = true;
                edits++;
            }
            else if (op < 80)
            {
                int p = Next() % (N + 1) - 1;
                bool keep = (Next() & 1) == 1;
                bool expectOk = !(p == i || (p >= 0 && MIsDesc(p, i)));
                float wa0 = 0f, wx0 = 0f, wy0 = 0f;
                if (expectOk && keep)
                {
                    float a, b, c, d;
                    MWorld(i, out a, out b, out c, out d, out wx0, out wy0);
                    wa0 = MathF.Atan2(b, a);
                }
                bool ok = nodes[i].SetParent(p < 0 ? null : nodes[p], keep);
                Expect(ok == expectOk, 9);
                if (ok)
                {
                    accepted++;
                    if (keep) keepWorld++;
                    if (mParent[i] >= 0) MRemoveChild(mParent[i], i);
                    mParent[i] = p;
                    if (p >= 0) { kids[p * N + kc[p]] = i; kc[p]++; }
                    if (keep)
                    {
                        // the model of "keep the world pose": solve the local pose under the new parent, as SetWorldPose does
                        if (p < 0) { mx[i] = wx0; my[i] = wy0; ma[i] = wa0; }
                        else
                        {
                            float pa, pb, pc, pd, ptx, pty;
                            MWorld(p, out pa, out pb, out pc, out pd, out ptx, out pty);
                            float dx = wx0 - ptx, dy = wy0 - pty;
                            float det = pa * pd - pb * pc;
                            if (det > -1e-12f && det < 1e-12f) { mx[i] = dx; my[i] = dy; }
                            else
                            {
                                float inv = 1f / det;
                                mx[i] = (pd * dx - pc * dy) * inv;
                                my[i] = (-pb * dx + pa * dy) * inv;
                            }
                            ma[i] = Angle2D.WrapPi(wa0 - MathF.Atan2(pb, pa));
                        }
                    }
                    changed = true;
                    if (Tame(nodes) > 0) { changed = true; tamed++; subtreeAll = true; }
                }
                else rejected++;
            }
            else if (op < 90)
            {
                nodes[i].Detach();
                if (mParent[i] >= 0) { MRemoveChild(mParent[i], i); mParent[i] = -1; changed = true; }
                detaches++;
            }
            // else: just the reads below

            // the version property: the edited subtree rose, nothing else moved
            for (int j = 0; j < N; j++)
            {
                int wv = nodes[j].WorldVersion();
                bool inSubtree = j == i || MIsDesc(j, i);
                if (subtreeAll) Expect(wv >= lastWV[j], 10);
                else if (changed && inSubtree) Expect(wv > lastWV[j], 10);
                else Expect(wv == lastWV[j], 11);
                lastWV[j] = wv;
            }

            // a few nodes' worlds and links, against the model
            CheckNode(nodes, i);
            int r = Next() % N;
            CheckNode(nodes, r);
            CheckLinks(nodes, i);
            CheckLinks(nodes, r);
            int u = Next() % N, v = Next() % N;
            Expect(nodes[u].IsDescendantOf(nodes[v]) == MIsDesc(u, v), 12);

            hash = Mix(hash, Q(nodes[r].WorldX()) * 3 + Q(nodes[r].WorldY()) + Q(nodes[r].WorldAngle()) * 7);

            if (step % 500 == 0)
            {
                for (int j = 0; j < N; j++) { CheckNode(nodes, j); CheckLinks(nodes, j); }
            }
        }
        for (int j = 0; j < N; j++) { CheckNode(nodes, j); CheckLinks(nodes, j); }

        int roots = 0, deepest = 0;
        for (int j = 0; j < N; j++)
        {
            if (mParent[j] < 0) roots++;
            int depth = 0;
            for (int p = mParent[j]; p >= 0; p = mParent[p]) depth++;
            if (depth > deepest) deepest = depth;
        }
        Console.WriteLine($"edits {edits} reparents accepted {accepted} (keeping world {keepWorld}) rejected as cycles {rejected} detaches {detaches} tamed {tamed}");
        string by = "";
        for (int k = 0; k < 64; k++) if (badBy[k] != 0) by += " #" + k + ":" + badBy[k];
        if (by.Length == 0) by = " none";
        Console.WriteLine("failed by check:" + by);
        Console.WriteLine($"roots {roots} deepest {deepest} checks {checks} mismatches {bad} hash {hash}");
        return bad == 0 ? 0 : 1;
    }
}
