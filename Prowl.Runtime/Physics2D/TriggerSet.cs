// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace Prowl.Runtime.Physics2D;

/// <summary>
/// The sensor / visitor pairs that are overlapping right now, as a set of 64-bit keys (sensor collider index in the high half, visitor in
/// the low). The simulation uses it to turn the native layer's begin and end events into Enter, Stay and Exit.
/// <para/>
/// Integers only, so it translates to C. It is a dense list plus a map from key to position, which makes add and remove constant time
/// and lets the keys be walked by position; <c>HashSet&lt;long&gt;</c> is not something the C# subset has, and a walk over a hash set
/// has no order to rely on.
/// </summary>
internal sealed class TriggerSet
{
    private List<long> _keys;                 // only the first _count entries are live
    private int _count;
    private Dictionary<long, int> _position;  // key -> index in _keys

    public TriggerSet()
    {
        _keys = new List<long>();
        _position = new Dictionary<long, int>();
    }

    public static long Pair(int sensor, int visitor)
    {
        return ((long)sensor << 32) | (uint)visitor;
    }

    public static int Sensor(long key)
    {
        return (int)(key >> 32);
    }

    public static int Visitor(long key)
    {
        return (int)key;
    }

    public int Count => _count;

    public bool Contains(long key)
    {
        return _position.ContainsKey(key);
    }

    /// <summary>The key at <paramref name="i"/>, for walking the set. Removing a key moves the last one into its place.</summary>
    public long KeyAt(int i)
    {
        return _keys[i];
    }

    /// <summary>Returns false if the pair was already in the set.</summary>
    public bool Add(long key)
    {
        if (_position.ContainsKey(key)) return false;
        if (_count < _keys.Count) _keys[_count] = key;
        else _keys.Add(key);
        _position[key] = _count;
        _count++;
        return true;
    }

    /// <summary>Returns false if the pair was not in the set.</summary>
    public bool Remove(long key)
    {
        if (!_position.ContainsKey(key)) return false;
        int at = _position[key];
        _count--;
        long last = _keys[_count];
        _keys[at] = last;
        _position[last] = at;   // when key is the last one this writes it back, and the next line removes it again
        _position.Remove(key);
        return true;
    }

    /// <summary>Removes every pair that has <paramref name="collider"/> on either side. Returns how many.</summary>
    public int RemoveInvolving(int collider)
    {
        int removed = 0;
        // Backwards: a removal moves the last key into the hole, and every key after position i has already been looked at.
        for (int i = _count - 1; i >= 0; i--)
        {
            long key = _keys[i];
            if (Sensor(key) == collider || Visitor(key) == collider)
            {
                Remove(key);
                removed++;
            }
        }
        return removed;
    }

    public void Clear()
    {
        _position.Clear();
        _count = 0;
    }
}
