// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Echo;
using Prowl.Runtime.Physics2D;
using Prowl.Runtime.Physics2D.Native;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>A rectangle (in the collider's own space), optionally with rounded corners.</summary>
[AddComponentMenu("Physics 2D/Box Collider 2D")]
[ComponentIcon("\uf0c8")] // Square
public sealed class BoxCollider2D : Collider2D
{
    [SerializeField, Header("Shape"), Tooltip("Full width and height, before the GameObject's scale.")]
    private Float2 size = new(1f, 1f);

    [SerializeField, Tooltip("Rounds the corners. The rounding is added outside Size, so the box grows by this much on every side.")]
    private float edgeRadius;

    /// <summary>Full width and height, before the GameObject's scale.</summary>
    public Float2 Size
    {
        get => size;
        set { size = new Float2(MathF.Max(value.X, 0.001f), MathF.Max(value.Y, 0.001f)); Rebuild(); }
    }

    /// <summary>Rounds the corners. The rounding is added outside <see cref="Size"/>.</summary>
    public float EdgeRadius
    {
        get => edgeRadius;
        set { edgeRadius = MathF.Max(value, 0f); Rebuild(); }
    }

    private void Resolve(ShapeSpec2D spec, out float halfWidth, out float halfHeight, out float radius)
    {
        halfWidth = MathF.Max(size.X, 0.001f) * 0.5f * spec.AbsScaleX;
        halfHeight = MathF.Max(size.Y, 0.001f) * 0.5f * spec.AbsScaleY;
        radius = MathF.Max(edgeRadius, 0f) * MathF.Max(spec.AbsScaleX, spec.AbsScaleY);
    }

    private protected override void CreateShapes(uint body, ShapeSpec2D spec, List<uint> shapes)
    {
        float hw;
        float hh;
        float radius;
        Resolve(spec, out hw, out hh, out radius);
        uint id = PB2.ShapeCreateBox(body, spec.ColliderIndex, spec.Layer, hw, hh, spec.X, spec.Y, spec.Angle, radius,
                                     spec.Density, spec.Friction, spec.Bounciness, spec.Flags);
        if (id != 0) shapes.Add(id);
    }

    private protected override bool BuildOutline(ShapeSpec2D spec, List<float> segments)
    {
        float hw;
        float hh;
        float radius;
        Resolve(spec, out hw, out hh, out radius);
        Collider2DOutline.Box(segments, spec.X, spec.Y, hw, hh, spec.Angle, radius);
        return true;
    }

    public override void OnValidate()
    {
        size = new Float2(MathF.Max(size.X, 0.001f), MathF.Max(size.Y, 0.001f));
        edgeRadius = MathF.Max(edgeRadius, 0f);
        base.OnValidate();
    }
}
