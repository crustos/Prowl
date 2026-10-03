// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace Prowl.Runtime.Physics2D;

/// <summary>
/// A reference to a slot in a <see cref="HandleTable"/>: an index and the generation the slot had when it was handed out.
/// Plain integers, so it can be stored, compared and sent across the native boundary without keeping anything alive.
/// </summary>
internal struct Handle2D
{
    public int Index;
    public int Generation;

    public Handle2D(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }
}

/// <summary>
/// Hands out small dense indices and says which of them are alive. It holds no objects, only integers, which is what lets it be
/// translated to C (see tools/ccsharp): <see cref="Registry2D"/> builds the registry on it, and the engine layer keeps its own table of
/// managed objects indexed by what it returns (<see cref="HostTable{T}"/>), which in a C build is whatever the core keeps its callbacks in.
/// <para/>
/// <b>Generations.</b> Every slot carries a counter that goes up each time the slot is freed or reused. Odd means alive, even means
/// free. A <see cref="Handle2D"/> remembers the generation it was issued at, so a handle to a freed slot is an integer mismatch, whether
/// or not the slot has been handed out again. That replaces the liveness checks that compared object references. Generations are
/// never reset, not even by <see cref="Clear"/>, so a handle from before a world was released can never alias something made after.
/// <para/>
/// <b>Quarantine.</b> Generations cannot help with a bare index. The native layer names bodies and colliders by index alone, and its
/// events are all gathered before the first handler runs; a handler that destroys an object and creates another in the same callback
/// would otherwise be handed the old index, and the remaining events about the destroyed object would reach the newcomer. While
/// <see cref="Quarantining"/> is on, a freed slot waits and is not reused until <see cref="Flush"/>.
/// </summary>
internal sealed class HandleTable
{
    private List<int> _generations;   // per slot, never shrinks: odd = alive, even = free
    private List<int> _free;          // a stack: only the first _freeCount entries are live
    private int _freeCount;
    private List<int> _held;          // freed during quarantine, waiting for Flush: only the first _heldCount
    private int _heldCount;
    private int _next;                // slots handed out since the last Clear
    private int _count;

    public HandleTable()
    {
        _generations = new List<int>();
        _free = new List<int>();
        _held = new List<int>();
    }

    /// <summary>Number of live slots.</summary>
    public int Count => _count;

    /// <summary>While true, a freed slot waits for <see cref="Flush"/> instead of being reused immediately.</summary>
    public bool Quarantining { get; set; }

    /// <summary>Largest index ever handed out since the last <see cref="Clear"/>, plus one. Every live index is below it.</summary>
    public int HighWater => _next;

    /// <summary>Takes a slot and returns its index. Reuses the most recently freed one, else the next unused.</summary>
    public int Add()
    {
        int index;
        if (_freeCount > 0)
        {
            _freeCount--;
            index = _free[_freeCount];
            _generations[index] = _generations[index] + 1;
        }
        else
        {
            index = _next;
            _next++;
            if (index < _generations.Count) _generations[index] = _generations[index] + 1; // a slot from before a Clear: keeps counting
            else _generations.Add(1);
        }
        _count++;
        return index;
    }

    /// <summary>
    /// <see cref="Add"/>, unless that would hand out an index of <paramref name="capacity"/> or more: then -1, and nothing has changed. The check
    /// has to come first. Taking the slot and giving it back when it is too high would leave that index on top of the free stack, to be
    /// picked again, and refused again, by every later call, even with real slots free below it. (A slot waiting out a quarantine is not
    /// free, so the index can reach the capacity while fewer than <paramref name="capacity"/> slots are live.)
    /// </summary>
    public int AddWithin(int capacity)
    {
        if (_freeCount == 0 && _next >= capacity) return -1;
        return Add();
    }

    /// <summary>Takes a slot and returns a handle that stays valid until the slot is freed.</summary>
    public Handle2D Alloc()
    {
        int index = Add();
        return new Handle2D(index, _generations[index]);
    }

    /// <summary>The generation a live slot currently has, or 0 when the slot is free or out of range.</summary>
    public int GenerationOf(int index)
    {
        return IsLive(index) ? _generations[index] : 0;
    }

    /// <summary>The handle for a live index (generation 0, which no live handle has, when it is not).</summary>
    public Handle2D HandleOf(int index)
    {
        return new Handle2D(index, GenerationOf(index));
    }

    /// <summary>Whether <paramref name="index"/> is a slot that has been handed out and not freed.</summary>
    public bool IsLive(int index)
    {
        return index >= 0 && index < _next && (_generations[index] & 1) == 1;
    }

    /// <summary>
    /// Whether <paramref name="handle"/> still refers to the slot it was issued for. (A different name from <see cref="IsLive(int)"/> because
    /// the C# subset resolves overloads by argument count alone.)
    /// </summary>
    public bool IsHandleLive(Handle2D handle)
    {
        return handle.Index >= 0 && handle.Index < _next && _generations[handle.Index] == handle.Generation;
    }

    /// <summary>Frees a live slot. Returns false, and changes nothing, if it was not alive.</summary>
    public bool Remove(int index)
    {
        if (!IsLive(index)) return false;
        _generations[index] = _generations[index] + 1;
        _count--;
        if (Quarantining) Push(_held, ref _heldCount, index);
        else Push(_free, ref _freeCount, index);
        return true;
    }

    /// <summary>Makes every quarantined slot reusable. Call when dispatch is over.</summary>
    public void Flush()
    {
        for (int i = 0; i < _heldCount; i++)
        {
            int index = _held[i]; // read into a local: Crust leaves `list[i]` unlowered as an argument beside another list
            Push(_free, ref _freeCount, index);
        }
        _heldCount = 0;
    }

    /// <summary>Frees every slot and starts handing out indices from zero again. Handles issued before this are all dead.</summary>
    public void Clear()
    {
        for (int i = 0; i < _next; i++)
            if ((_generations[i] & 1) == 1) _generations[i] = _generations[i] + 1;
        _next = 0;
        _count = 0;
        _freeCount = 0;
        _heldCount = 0;
    }

    // A stack kept in a list that never shrinks: `count` is its size, and entries past it are stale and get overwritten.
    private static void Push(List<int> stack, ref int count, int value)
    {
        if (count < stack.Count) stack[count] = value;
        else stack.Add(value);
        count++;
    }
}
