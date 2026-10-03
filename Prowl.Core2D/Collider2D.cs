// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Physics2D;

namespace Prowl.Core2D;

/// <summary>
/// The data of a BoxCollider2D or CircleCollider2D component. Its shape is made when the component is enabled, on the nearest enabled rigidbody
/// at or above its node, or on a static body of its own when there is none, and destroyed when it is disabled. Its values are read then.
/// A collider with no rigidbody is static: it is placed where its node is when it is enabled, and does not follow the node afterwards.
/// <para/>
/// This is also what a script is given in <c>OnTriggerEnter2D(Collider2D other)</c>: <see cref="Self"/> leads to the node and its other components.
/// </summary>
[MaxInstances(CoreLimits.Colliders)]
internal sealed class Collider2D
{
    public const int Box = 0;
    public const int Circle = 1;

    public Component Self;
    public int ShapeKind;                // Box or Circle
    public float SizeX, SizeY;           // a box's full width and height (the node's scale multiplies them)
    public float Radius;                 // a circle's
    public float OffsetX, OffsetY;       // the shape's centre, in the node's space
    public float Density;
    public float Friction;
    public float Bounciness;
    public bool IsTrigger;

    public int ColliderIndex;            // the slot in the simulation's registry, -1 while there is no shape
    public uint Shape;                   // the native shape
    public uint OwnBody;                 // the static body made for a collider with no rigidbody, else 0
    public Rigidbody2D Attached;         // the body the shape is on, null for an own static body

    public void Reset(int shapeKind)
    {
        Self = null;
        ShapeKind = shapeKind;
        SizeX = 1f;
        SizeY = 1f;
        Radius = 0.5f;
        OffsetX = 0f;
        OffsetY = 0f;
        Density = 1f;
        Friction = 0.4f;
        Bounciness = 0f;
        IsTrigger = false;
        ColliderIndex = -1;
        Shape = 0;
        OwnBody = 0;
        Attached = null;
    }
}

/// <summary>
/// The payload of the collision or trigger message being delivered. One object, reused: it is valid for the duration of the callback and must
/// not be kept. <see cref="Self"/> is the collider that is hearing it, <see cref="Other"/> the one it touched; the normal points from self to
/// other. For trigger messages only the colliders are set.
/// </summary>
[MaxInstances(1)]
internal sealed class Collision2D
{
    public Collider2D Self;
    public Collider2D Other;
    public float X, Y, NX, NY, Impulse;
}
