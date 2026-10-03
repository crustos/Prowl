// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime.Physics2D;

/// <summary>
/// Index -> managed object: the part of the old slot registry that cannot be translated to C, because it holds references to objects the C
/// core does not have (the components and the event handlers). Which indices are alive, their generations, the quarantine and the records
/// are <see cref="Registry2D"/>'s; this only remembers who to call. A slot is cleared when its object is removed, so what it returns for
/// an index that is no longer live is null. In a C build this array is whatever the core keeps its callbacks in.
/// </summary>
internal sealed class HostTable<T> where T : class
{
    private T?[] _items = new T?[64];

    public void Set(int index, T host)
    {
        if (index >= _items.Length) Array.Resize(ref _items, Math.Max(_items.Length * 2, index + 1));
        _items[index] = host;
    }

    public T? Get(int index) => (uint)index < (uint)_items.Length ? _items[index] : null;

    public void Remove(int index)
    {
        if ((uint)index < (uint)_items.Length) _items[index] = null;
    }

    public void Clear() => Array.Clear(_items, 0, _items.Length);
}
