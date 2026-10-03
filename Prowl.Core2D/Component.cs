// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Physics2D;

namespace Prowl.Core2D;

/// <summary>
/// The callbacks a component can have, one bit each. A component's set (<see cref="Component.Callbacks"/>) is what the engine resolves by reflection
/// for a MonoBehaviour; here it is known when the program is translated, and written into the generated script table. The same numbers say what a
/// call from <see cref="Scene2D.NextCall"/> is for.
/// </summary>
internal static class Callbacks
{
    public const int OnEnable = 1 << 0;
    public const int OnDisable = 1 << 1;
    public const int Start = 1 << 2;
    public const int FixedUpdate = 1 << 3;
    public const int Update = 1 << 4;
    public const int LateUpdate = 1 << 5;
    public const int CollisionBegin2D = 1 << 6;
    public const int CollisionEnd2D = 1 << 7;
    public const int TriggerEnter2D = 1 << 8;
    public const int TriggerStay2D = 1 << 9;
    public const int TriggerExit2D = 1 << 10;

    /// <summary>The ones the per-frame loops dispatch: a component with none of these is never registered.</summary>
    public const int AnyFrame = Start | FixedUpdate | Update | LateUpdate;

    /// <summary>Not a callback a script writes: said once to a script's pool when its component is finally freed, so the slot can be recycled.</summary>
    public const int Release = 1 << 20;
}

/// <summary>What a component is. Built-in kinds are handled by the runtime itself; a script kind is a number the script generator assigns.</summary>
internal static class ComponentKind
{
    public const int None = 0;
    public const int Rigidbody2D = 1;
    public const int BoxCollider2D = 2;
    public const int CircleCollider2D = 3;
    public const int SpriteRenderer2D = 4;
    public const int FirstScript = 100;
}

/// <summary>
/// One component of a node: the record the lifecycle works on, whatever the component is. The component's own data is elsewhere: a built-in kind
/// has a payload object (<see cref="Body"/>, <see cref="Collider"/>); a script is an object in the generated pool of its kind, found by
/// <see cref="Kind"/> and <see cref="Slot"/>. Nothing here is virtual: the scene says which callback to run on which component, and a sink
/// (generated for scripts) calls it with a <c>switch</c>.
/// </summary>
[MaxInstances(CoreLimits.Components)]
internal sealed class Component
{
    public int Index;
    public int Kind;
    public int Slot;                     // a script's object, in the generated pool of its kind
    public Node Node;
    public Component NextOnNode;         // the node's components, in the order they were added

    public Rigidbody2D Body;             // the payload of a Rigidbody2D, else null
    public Collider2D Collider;          // the payload of a collider, else null
    public SpriteRenderer2D Sprite;      // the payload of a SpriteRenderer2D, else null

    public bool Enabled;                 // set by the game
    public bool EnabledInHierarchy;      // Enabled, and the node active all the way up, and not destroyed: what callbacks follow
    public bool HasStarted;
    public bool Destroyed;
    public int Callbacks;                // the set of callbacks this component has
    public int Order;                    // execution order: lower first
    public int Sequence;                 // registration order, to break ties; not the position in any array
    public int DispatchSlot;             // 1 + its place in the scene's registration array, or 0 when not registered

    public void Reset(int index)
    {
        Index = index;
        Kind = ComponentKind.None;
        Slot = 0;
        Node = null;
        NextOnNode = null;
        Body = null;
        Collider = null;
        Sprite = null;
        Enabled = true;
        EnabledInHierarchy = false;
        HasStarted = false;
        Destroyed = false;
        Callbacks = 0;
        Order = 0;
        Sequence = 0;
        DispatchSlot = 0;
    }
}
