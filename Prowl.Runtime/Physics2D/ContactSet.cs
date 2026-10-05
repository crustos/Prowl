// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace Prowl.Runtime.Physics2D;

/// <summary>
/// The pairs of colliders that are touching right now, each with HOW MANY pairs of their shapes touch. The simulation uses it to turn the native
/// layer's contact events, which are one per pair of shapes, into one Begin and one End per pair of colliders, as Unity delivers them.
/// <para/>
/// Without it a collider of several shapes (an edge collider, a terrain chunk of boxes or chains) reports a Begin each time something touches one more
/// of its shapes, and an End each time it leaves one, so a ball rolling across the seam between two of them is told it stopped touching and began again.
/// <para/>
/// A pair is unordered: the contact (a, b) and the contact (b, a) are the same pair. Integers only, so it translates to C: a dense list of keys with their
/// counts beside it, and a map from key to position, as <see cref="TriggerSet"/> is made.
/// </summary>
internal sealed class ContactSet
{
    private List<long> _keys;                 // only the first _count entries are live
    private List<int> _touching;              // how many pairs of shapes touch, for each key
    private int _count;
    private Dictionary<long, int> _position;  // key -> index in _keys

    public ContactSet()
    {
        _keys = new List<long>();
        _touching = new List<int>();
        _position = new Dictionary<long, int>();
    }

    /// <summary>The key of the unordered pair: the smaller collider index in the high half, the larger in the low.</summary>
    public static long Pair(int a, int b)
    {
        if (a > b)
        {
            int t = a;
            a = b;
            b = t;
        }
        return ((long)a << 32) | (uint)b;
    }

    public static int Low(long key)
    {
        return (int)(key >> 32);
    }

    public static int High(long key)
    {
        return (int)key;
    }

    /// <summary>The number of pairs of colliders in contact.</summary>
    public int Count => _count;

    /// <summary>How many pairs of shapes of this pair of colliders touch; 0 if they are not in contact.</summary>
    public int Touching(long key)
    {
        if (!_position.ContainsKey(key)) return 0;
        return _touching[_position[key]];
    }

    /// <summary>The key at <paramref name="i"/>, for walking the set. Removing a key moves the last one into its place.</summary>
    public long KeyAt(int i)
    {
        return _keys[i];
    }

    /// <summary>Forgets the pair whatever its count. Returns false if it was not there.</summary>
    public bool Remove(long key)
    {
        if (!_position.ContainsKey(key)) return false;
        RemoveAt(_position[key], key);
        return true;
    }

    /// <summary>One more pair of shapes touches. Returns how many do now: 1 means the colliders began touching.</summary>
    public int Increment(long key)
    {
        if (_position.ContainsKey(key))
        {
            int at = _position[key];
            _touching[at] = _touching[at] + 1;
            return _touching[at];
        }
        if (_count < _keys.Count)
        {
            _keys[_count] = key;
            _touching[_count] = 1;
        }
        else
        {
            _keys.Add(key);
            _touching.Add(1);
        }
        _position[key] = _count;
        _count++;
        return 1;
    }

    /// <summary>
    /// One pair of shapes stopped touching. Returns how many still do: 0 means the colliders stopped touching (and the pair is forgotten), and -1
    /// that the pair was not in contact at all (a contact that ended after its collider was forgotten).
    /// </summary>
    public int Decrement(long key)
    {
        if (!_position.ContainsKey(key)) return -1;
        int at = _position[key];
        int left = _touching[at] - 1;
        if (left > 0)
        {
            _touching[at] = left;
            return left;
        }
        RemoveAt(at, key);
        return 0;
    }

    /// <summary>Forgets every pair that has <paramref name="collider"/> on either side. Returns how many.</summary>
    public int RemoveInvolving(int collider)
    {
        int removed = 0;
        // Backwards: a removal moves the last key into the hole, and every key after position i has already been looked at.
        for (int i = _count - 1; i >= 0; i--)
        {
            long key = _keys[i];
            if (Low(key) == collider || High(key) == collider)
            {
                RemoveAt(i, key);
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

    private void RemoveAt(int at, long key)
    {
        _count--;
        long last = _keys[_count];
        _keys[at] = last;
        _touching[at] = _touching[_count];
        _position[last] = at;   // when key is the last one this writes it back, and the next line removes it again
        _position.Remove(key);
    }
}
