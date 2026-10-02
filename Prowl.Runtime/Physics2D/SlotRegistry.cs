// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace Prowl.Runtime.Physics2D;

/// <summary>
/// Dense index -> object table. The native layer identifies bodies and colliders by these small integers, so an
/// event can be turned back into a managed object with one array read.
/// <para/>
/// A slot released while events are being dispatched is <b>quarantined</b>: it is not handed out again until
/// <see cref="Flush"/>. Without that, a handler that destroys an object and creates another in the same callback
/// would give the newcomer the old index, and the remaining events about the destroyed object would be delivered to it.
/// </summary>
internal sealed class SlotRegistry<T> where T : class
{
    private T?[] _items = new T?[64];
    private readonly Stack<int> _free = new();
    private readonly List<int> _quarantine = new();
    private int _next;

    /// <summary>Number of live objects.</summary>
    public int Count { get; private set; }

    /// <summary>While true, released slots wait for <see cref="Flush"/> instead of being reused immediately.</summary>
    public bool Quarantining { get; set; }

    /// <summary>Largest index ever handed out + 1. The native side packs collider indices into 24 bits.</summary>
    public int HighWater => _next;

    public int Add(T item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));

        int index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
        }
        else
        {
            index = _next++;
            if (index == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        }
        _items[index] = item;
        Count++;
        return index;
    }

    /// <summary>Returns the object at <paramref name="index"/>, or null if it is out of range, free or quarantined.</summary>
    public T? Get(int index) => (uint)index < (uint)_next ? _items[index] : null;

    public bool Remove(int index)
    {
        if ((uint)index >= (uint)_next || _items[index] is null) return false;
        _items[index] = null;
        Count--;
        if (Quarantining) _quarantine.Add(index);
        else _free.Push(index);
        return true;
    }

    /// <summary>Makes quarantined slots reusable. Call when dispatch is over.</summary>
    public void Flush()
    {
        foreach (int i in _quarantine) _free.Push(i);
        _quarantine.Clear();
    }

    public void Clear()
    {
        Array.Clear(_items, 0, _next);
        _free.Clear();
        _quarantine.Clear();
        _next = 0;
        Count = 0;
    }
}
