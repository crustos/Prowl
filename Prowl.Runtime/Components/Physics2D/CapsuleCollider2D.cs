// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Echo;
using Prowl.Runtime.Physics2D;
using Prowl.Native.Box2D;
using Prowl.Vector;

namespace Prowl.Runtime;

public enum CapsuleDirection2D
{
    /// <summary>The long axis runs along Y.</summary>
    Vertical,
    /// <summary>The long axis runs along X.</summary>
    Horizontal,
}

/// <summary>A capsule: a rectangle with semicircular ends. A standard character shape, since it slides over seams.</summary>
[AddComponentMenu("Physics 2D/Capsule Collider 2D")]
[ComponentIcon("\uf0c8")]
public sealed class CapsuleCollider2D : Collider2D
{
    [SerializeField, Header("Shape"), Tooltip("Overall width and height. The rounded ends use the shorter side as their diameter.")]
    private Float2 size = new(0.5f, 1f);

    [SerializeField, Tooltip("Which axis the capsule is long along.")]
    private CapsuleDirection2D direction = CapsuleDirection2D.Vertical;

    /// <summary>Overall width and height. The rounded ends use the shorter side as their diameter.</summary>
    public Float2 Size
    {
        get => size;
        set { size = new Float2(MathF.Max(value.X, 0.001f), MathF.Max(value.Y, 0.001f)); Rebuild(); }
    }

    public CapsuleDirection2D Direction
    {
        get => direction;
        set { direction = value; Rebuild(); }
    }

    /// <summary>
    /// Resolves the capsule. When it is no longer than it is wide there is no straight section and it is a circle,
    /// reported as <paramref name="halfLength"/> = 0 with <paramref name="radius"/> the circle's.
    /// </summary>
    private void Resolve(ShapeSpec2D spec, out float radius, out float halfLength, out bool vertical)
    {
        vertical = direction == CapsuleDirection2D.Vertical;
        float width = MathF.Max(size.X, 0.001f) * spec.AbsScaleX;
        float height = MathF.Max(size.Y, 0.001f) * spec.AbsScaleY;

        float cap = (vertical ? width : height) * 0.5f;
        float length = vertical ? height : width;
        halfLength = length * 0.5f - cap;
        if (halfLength <= 1e-4f)
        {
            halfLength = 0f;
            radius = MathF.Min(length, vertical ? width : height) * 0.5f;
        }
        else
        {
            radius = cap;
        }
    }

    private protected override void CreateShapes(uint body, ShapeSpec2D spec, List<uint> shapes)
    {
        float radius;
        float halfLength;
        bool vertical;
        Resolve(spec, out radius, out halfLength, out vertical);
        uint id;
        if (halfLength == 0f)
        {
            id = PB2.ShapeCreateCircle(body, spec.ColliderIndex, spec.Layer, spec.X, spec.Y, radius,
                                       spec.Density, spec.Friction, spec.Bounciness, spec.Flags);
        }
        else
        {
            float x1;
            float y1;
            float x2;
            float y2;
            Collider2DGeometry.CapsuleEnds(spec.X, spec.Y, spec.Angle, halfLength, vertical, out x1, out y1, out x2, out y2);
            id = PB2.ShapeCreateCapsule(body, spec.ColliderIndex, spec.Layer, x1, y1, x2, y2, radius,
                                        spec.Density, spec.Friction, spec.Bounciness, spec.Flags);
        }
        if (id != 0) shapes.Add(id);
    }

    private protected override bool BuildOutline(ShapeSpec2D spec, List<float> segments)
    {
        float radius;
        float halfLength;
        bool vertical;
        Resolve(spec, out radius, out halfLength, out vertical);
        if (halfLength == 0f)
        {
            Collider2DOutline.Circle(segments, spec.X, spec.Y, radius);
            return true;
        }
        float x1;
        float y1;
        float x2;
        float y2;
        Collider2DGeometry.CapsuleEnds(spec.X, spec.Y, spec.Angle, halfLength, vertical, out x1, out y1, out x2, out y2);
        Collider2DOutline.Capsule(segments, x1, y1, x2, y2, radius);
        return true;
    }

    public override void OnValidate()
    {
        size = new Float2(MathF.Max(size.X, 0.001f), MathF.Max(size.Y, 0.001f));
        base.OnValidate();
    }
}
