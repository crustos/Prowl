// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>How a <see cref="Rigidbody2D"/> takes part in the simulation.</summary>
public enum BodyType2D
{
    /// <summary>Never moves. Cheapest; use for level geometry.</summary>
    Static = 0,
    /// <summary>Moves only when told to (velocity or <see cref="Rigidbody2D.MovePosition"/>); pushes dynamic bodies, is not pushed.</summary>
    Kinematic = 1,
    /// <summary>Fully simulated: gravity, forces and collisions move it.</summary>
    Dynamic = 2,
}

/// <summary>Which motion a <see cref="Rigidbody2D"/> is locked out of.</summary>
[Flags]
public enum RigidbodyConstraints2D
{
    None = 0,
    FreezePositionX = 1,
    FreezePositionY = 2,
    FreezePosition = FreezePositionX | FreezePositionY,
    FreezeRotation = 4,
    FreezeAll = FreezePosition | FreezeRotation,
}

/// <summary>
/// A contact reported to <see cref="MonoBehaviour.OnCollisionBegin2D"/> / <see cref="MonoBehaviour.OnCollisionEnd2D"/>.
/// Seen from the receiving object: <see cref="Collider"/> is the thing it touched.
/// </summary>
public readonly struct Collision2D
{
    /// <summary>The other collider's rigidbody, or null if it is static level geometry with no Rigidbody2D.</summary>
    public readonly Rigidbody2D? Rigidbody;

    /// <summary>The other collider.</summary>
    public readonly Collider2D Collider;

    /// <summary>World-space contact point. Zero on <see cref="MonoBehaviour.OnCollisionEnd2D"/>.</summary>
    public readonly Float2 Point;

    /// <summary>
    /// Contact normal pointing <b>from the other collider toward the receiver</b> (so standing on the ground gives
    /// roughly (0, 1), and <c>Normal.Y &gt; 0.5f</c> is a ground check). Zero on <see cref="MonoBehaviour.OnCollisionEnd2D"/>.
    /// </summary>
    public readonly Float2 Normal;

    /// <summary>Normal impulse the solver applied at the moment contact began. Zero on end.</summary>
    public readonly float ImpulseMagnitude;

    public GameObject? GameObject => Collider.IsValid() ? Collider.GameObject : null;

    public Transform? Transform => GameObject.IsValid() ? GameObject.Transform : null;

    internal Collision2D(Rigidbody2D? rigidbody, Collider2D collider, Float2 point, Float2 normal, float impulse)
    {
        Rigidbody = rigidbody;
        Collider = collider;
        Point = point;
        Normal = normal;
        ImpulseMagnitude = impulse;
    }
}

/// <summary>The result of a 2D ray cast.</summary>
public readonly struct RaycastHit2D
{
    public readonly Collider2D Collider;
    public readonly Rigidbody2D? Rigidbody;
    public readonly Float2 Point;
    /// <summary>Surface normal at the hit point, facing back toward the ray origin.</summary>
    public readonly Float2 Normal;
    /// <summary>Distance from the origin to <see cref="Point"/>, in world units.</summary>
    public readonly float Distance;
    /// <summary>Distance as a fraction of the cast's maximum distance.</summary>
    public readonly float Fraction;

    public GameObject? GameObject => Collider.IsValid() ? Collider.GameObject : null;

    internal RaycastHit2D(Collider2D collider, Rigidbody2D? rigidbody, Float2 point, Float2 normal, float distance, float fraction)
    {
        Collider = collider;
        Rigidbody = rigidbody;
        Point = point;
        Normal = normal;
        Distance = distance;
        Fraction = fraction;
    }
}

/// <summary>Restricts what a 2D query can hit.</summary>
public struct QueryFilter2D
{
    /// <summary>Which layers are hit. Everything by default.</summary>
    public LayerMask LayerMask;

    /// <summary>A rigidbody whose colliders are skipped (typically the caster itself).</summary>
    public Rigidbody2D? IgnoreRigidbody;

    /// <summary>A single collider to skip.</summary>
    public Collider2D? IgnoreCollider;

    /// <summary>When true, trigger colliders are skipped. The default (false) hits them, as Unity does.</summary>
    public bool ExcludeTriggers;

    public static readonly QueryFilter2D Default = new(LayerMask.Everything);

    public QueryFilter2D(LayerMask layerMask)
    {
        LayerMask = layerMask;
        IgnoreRigidbody = null;
        IgnoreCollider = null;
        ExcludeTriggers = false;
    }

    public readonly QueryFilter2D Ignoring(Rigidbody2D rigidbody)
    {
        QueryFilter2D f = this;
        f.IgnoreRigidbody = rigidbody;
        return f;
    }

    public readonly QueryFilter2D Ignoring(Collider2D collider)
    {
        QueryFilter2D f = this;
        f.IgnoreCollider = collider;
        return f;
    }

    internal readonly bool HasExclusions => IgnoreRigidbody.IsValid() || IgnoreCollider.IsValid();

    public static implicit operator QueryFilter2D(LayerMask layerMask) => new(layerMask);
}
