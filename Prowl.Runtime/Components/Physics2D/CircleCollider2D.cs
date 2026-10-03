// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Echo;
using Prowl.Runtime.Physics2D;
using Prowl.Native.Box2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>A circle. With non-uniform scale it uses the larger axis, as a circle cannot become an ellipse.</summary>
[AddComponentMenu("Physics 2D/Circle Collider 2D")]
[ComponentIcon("\uf111")] // Circle
public sealed class CircleCollider2D : Collider2D
{
    [SerializeField, Header("Shape"), Tooltip("Radius before the GameObject's scale. Under non-uniform scale the larger axis wins.")]
    private float radius = 0.5f;

    public float Radius
    {
        get => radius;
        set { radius = MathF.Max(value, 0.001f); Rebuild(); }
    }

    private float Resolve(ShapeSpec2D spec) => MathF.Max(radius, 0.001f) * MathF.Max(spec.AbsScaleX, spec.AbsScaleY);

    private protected override void CreateShapes(uint body, ShapeSpec2D spec, List<uint> shapes)
    {
        uint id = PB2.ShapeCreateCircle(body, spec.ColliderIndex, spec.Layer, spec.X, spec.Y, Resolve(spec),
                                        spec.Density, spec.Friction, spec.Bounciness, spec.Flags);
        if (id != 0) shapes.Add(id);
    }

    private protected override bool BuildOutline(ShapeSpec2D spec, List<float> segments)
    {
        Collider2DOutline.Circle(segments, spec.X, spec.Y, Resolve(spec));
        return true;
    }

    public override void OnValidate()
    {
        radius = MathF.Max(radius, 0.001f);
        base.OnValidate();
    }
}
