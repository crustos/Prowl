// Runs the integer-only core tables (HandleTable, TriggerSet) through long random operation sequences and prints integers only.
//
// The same file is run twice by `python3 build.py ccsharp`: once on real .NET, and once translated by CCSharp to C, built with gcc and
// executed natively. The two outputs must be identical, which is what shows the translation preserves behaviour.
//
// Each table is also checked against a naive reference model written here, in the simplest way that could be right, so a line that reads
// "mismatches 0" means the SEMANTICS are right as well as the translation (a translation of a wrong table would agree with itself).
using System;
using System.Collections.Generic;
using Prowl.Runtime.Physics2D;

class HandleConformance
{
    static int seed = 20240611;

    static int Next()
    {
        seed = seed * 1103515245 + 12345;
        return (seed >> 16) & 0x7fff;
    }

    static int Mix(int hash, int value)
    {
        return (hash * 31) ^ value;
    }

    // ---- HandleTable vs a model ------------------------------------------------------------------------------------------------
    //
    // The model keeps, per slot: alive, generation, and the time it was last freed. "Reuse the most recently freed slot that is not
    // quarantined, else the lowest slot never used since the last Clear" is the whole rule, and it needs no stack.

    const int Slots = 192;

    static int HandleTableRun()
    {
        HandleTable table = new HandleTable();
        bool[] alive = new bool[Slots];
        int[] gen = new int[Slots];
        int[] freedAt = new int[Slots];      // 0 = not in the free pool
        bool[] held = new bool[Slots];       // freed during quarantine, not yet reusable
        int[] heldOrder = new int[Slots];
        int[] issuedIndex = new int[64];
        int[] issuedGen = new int[64];
        int issued = 0;
        int clock = 0;
        int next = 0;                        // lowest never-used slot since the last Clear
        int live = 0;
        bool quarantine = false;
        int heldClock = 0;

        int mismatches = 0;
        int hash = 17;

        for (int step = 0; step < 6000; step++)
        {
            int op = Next() % 100;
            if (op < 38)
            {
                // Add
                int expected = -1;
                int best = 0;
                for (int i = 0; i < next; i++)
                    if (!alive[i] && !held[i] && freedAt[i] > best) { best = freedAt[i]; expected = i; }
                if (expected < 0)
                {
                    if (next >= Slots) continue; // the model's own capacity
                    expected = next;
                    next++;
                }
                gen[expected]++;
                alive[expected] = true;
                freedAt[expected] = 0;
                live++;

                Handle2D h = table.Alloc();
                if (h.Index != expected || h.Generation != gen[expected]) mismatches++;
                hash = Mix(hash, h.Index * 1000 + h.Generation);
                if (issued < 64) { issuedIndex[issued] = h.Index; issuedGen[issued] = h.Generation; issued++; }
                else { int at = Next() % 64; issuedIndex[at] = h.Index; issuedGen[at] = h.Generation; }
            }
            else if (op < 76)
            {
                // Remove a random index: often a live one, sometimes a free one or one far out of range
                int index = Next() % (next + 4) - 1;
                bool expected = index >= 0 && index < next && alive[index];
                bool got = table.Remove(index);
                if (got != expected) mismatches++;
                hash = Mix(hash, got ? 1 : 0);
                if (expected)
                {
                    alive[index] = false;
                    gen[index]++;
                    live--;
                    if (quarantine) { held[index] = true; heldClock++; heldOrder[index] = heldClock; }
                    else { clock++; freedAt[index] = clock; }
                }
            }
            else if (op < 88)
            {
                // Toggle quarantine; leaving it is a Flush
                quarantine = !quarantine;
                table.Quarantining = quarantine;
                if (!quarantine)
                {
                    table.Flush();
                    // held slots go back in the order they were held, so the last one held is reused first
                    for (int order = 1; order <= heldClock; order++)
                        for (int i = 0; i < next; i++)
                            if (held[i] && heldOrder[i] == order) { held[i] = false; clock++; freedAt[i] = clock; }
                    heldClock = 0;
                }
            }
            else if (op < 97)
            {
                // Is an old handle still good?
                if (issued > 0)
                {
                    int k = Next() % issued;
                    int hi = issuedIndex[k];
                    bool expected = hi < next && alive[hi] && gen[hi] == issuedGen[k];
                    bool got = table.IsHandleLive(new Handle2D(hi, issuedGen[k]));
                    if (got != expected) mismatches++;
                    hash = Mix(hash, got ? 1 : 0);
                }
            }
            else
            {
                // Clear: every handle issued so far must now be dead, and generations keep counting
                table.Clear();
                for (int i = 0; i < next; i++)
                {
                    if (alive[i]) gen[i]++;
                    alive[i] = false;
                    held[i] = false;
                    freedAt[i] = 0;
                }
                next = 0; live = 0; clock = 0; heldClock = 0;
                for (int k = 0; k < issued; k++)
                    if (table.IsHandleLive(new Handle2D(issuedIndex[k], issuedGen[k]))) mismatches++;
            }

            if (table.Count != live) mismatches++;
            for (int k = 0; k < issued && k < 3; k++)
            {
                int hi = issuedIndex[k];
                bool expected = hi < next && alive[hi] && gen[hi] == issuedGen[k];
                if (table.IsHandleLive(new Handle2D(hi, issuedGen[k])) != expected) mismatches++;
            }
            if (table.GenerationOf(step % Slots) != (step % Slots < next && alive[step % Slots] ? gen[step % Slots] : 0)) mismatches++;
        }

        Console.WriteLine($"handle table: live {table.Count} highwater {table.HighWater} issued {issued} mismatches {mismatches} hash {hash}");
        return mismatches;
    }

    // ---- TriggerSet vs a model --------------------------------------------------------------------------------------------------

    static int TriggerSetRun()
    {
        TriggerSet set = new TriggerSet();
        long[] model = new long[200];
        int count = 0;
        int mismatches = 0;
        int hash = 17;

        for (int step = 0; step < 5000; step++)
        {
            int op = Next() % 100;
            int sensor = Next() % 9;
            int visitor = Next() % 9;
            long key = TriggerSet.Pair(sensor, visitor);
            int at = -1;
            for (int i = 0; i < count; i++) if (model[i] == key) at = i;

            if (op < 45)
            {
                bool got = set.Add(key);
                if (got != (at < 0)) mismatches++;
                if (at < 0) { model[count] = key; count++; }
                hash = Mix(hash, got ? 1 : 0);
            }
            else if (op < 85)
            {
                bool got = set.Remove(key);
                if (got != (at >= 0)) mismatches++;
                if (at >= 0) { count--; model[at] = model[count]; }
                hash = Mix(hash, got ? 1 : 0);
            }
            else if (op < 97)
            {
                int collider = Next() % 9;
                int removed = set.RemoveInvolving(collider);
                int expected = 0;
                int w = 0;
                for (int i = 0; i < count; i++)
                {
                    if (TriggerSet.Sensor(model[i]) == collider || TriggerSet.Visitor(model[i]) == collider) expected++;
                    else { model[w] = model[i]; w++; }
                }
                count = w;
                if (removed != expected) mismatches++;
                hash = Mix(hash, removed);
            }
            else
            {
                set.Clear();
                count = 0;
            }

            if (set.Count != count) mismatches++;
            // Same members, whatever the order: every key the set walks to is in the model, and the sizes agree.
            for (int i = 0; i < set.Count; i++)
            {
                long k = set.KeyAt(i);
                bool found = false;
                for (int j = 0; j < count; j++) if (model[j] == k) found = true;
                if (!found) mismatches++;
                if (!set.Contains(k)) mismatches++;
                if (TriggerSet.Pair(TriggerSet.Sensor(k), TriggerSet.Visitor(k)) != k) mismatches++;
            }
            // The walk's order is the translation's to get right: fold it in.
            if (step % 50 == 0)
                for (int i = 0; i < set.Count; i++) hash = Mix(hash, (int)(set.KeyAt(i) % 1000003));
        }

        // A key whose halves have the high bit set must round-trip, which is what 64-bit packing is for.
        long wide = TriggerSet.Pair(2000000000, -5);
        if (TriggerSet.Sensor(wide) != 2000000000 || TriggerSet.Visitor(wide) != -5) mismatches++;

        Console.WriteLine($"trigger set: count {set.Count} mismatches {mismatches} hash {hash}");
        return mismatches;
    }

    public static int Main()
    {
        int bad = 0;
        bad += HandleTableRun();
        bad += TriggerSetRun();
        Console.WriteLine($"total mismatches {bad}");
        return bad == 0 ? 0 : 1;
    }
}
