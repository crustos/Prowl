// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Echo;
using Prowl.Runtime.Physics2D;
using Prowl.Runtime.Physics2D.Native;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// A convex polygon of at most 8 points (Box2D's limit). Concave outlines are not supported: split them into several
/// colliders or use an <see cref="EdgeCollider2D"/>. The gizmo is drawn red when the points cannot form a polygon.
/// </summary>
[AddComponentMenu("Physics 2D/Polygon Collider 2D")]
[ComponentIcon("\uf0c8")]
public sealed class PolygonCollider2D : Collider2D
{
    /// <summary>The most vertices Box2D accepts in one convex polygon.</summary>
    public const int MaxPoints = Collider2DOutline.MaxPolygonPoints;

    [SerializeField, Header("Shape"), Tooltip("Outline in the collider's local space, before scale. 3 to 8 points, convex; order does not matter.")]
    private Float2[] points = { new(-0.5f, -0.5f), new(0.5f, -0.5f), new(0.5f, 0.5f), new(-0.5f, 0.5f) };

    /// <summary>Outline points in the collider's local space, before scale. Order does not matter; the hull is computed.</summary>
    public Float2[] Points
    {
        get => points;
        set { points = value ?? Array.Empty<Float2>(); Rebuild(); }
    }

    /// <summary>Replaces the outline with a 1x1 box.</summary>
    [Button("Reset To Box")]
    public void ResetToBox() => Points = new[] { new Float2(-0.5f, -0.5f), new Float2(0.5f, -0.5f), new Float2(0.5f, 0.5f), new Float2(-0.5f, 0.5f) };

    /// <summary>Places the points (scale, rotation and centre applied) into <paramref name="xy"/>. Returns the count.</summary>
    private int Place(ShapeSpec2D spec, float[] xy)
    {
        int n = Math.Min(points?.Length ?? 0, xy.Length / 2);
        for (int i = 0; i < n; i++)
            Collider2DGeometry.TransformPoint(points![i].X, points[i].Y, spec.ScaleX, spec.ScaleY, spec.Angle, spec.X, spec.Y,
                                              out xy[2 * i], out xy[2 * i + 1]);
        return n;
    }

    private protected override unsafe void CreateShapes(uint body, ShapeSpec2D spec, List<uint> shapes)
    {
        int count = points?.Length ?? 0;
        if (count < 3 || count > MaxPoints)
        {
            Debug.LogError($"[{Name}] PolygonCollider2D needs 3 to {MaxPoints} points (it has {count}). Split larger shapes into several convex colliders.");
            return;
        }

        float[] xy = new float[MaxPoints * 2];
        int n = Place(spec, xy);
        uint id;
        fixed (float* p = xy)
            id = PB2.ShapeCreatePolygon(body, spec.ColliderIndex, spec.Layer, p, n, 0f, spec.Density, spec.Friction, spec.Bounciness, spec.Flags);

        if (id == 0)
        {
            Debug.LogError($"[{Name}] PolygonCollider2D points do not form a valid convex polygon (collinear or degenerate).");
            return;
        }
        shapes.Add(id);
    }

    private protected override bool BuildOutline(ShapeSpec2D spec, List<float> segments)
    {
        int count = points?.Length ?? 0;
        // A bad point count still draws, so the user can see what they have; only up to 8 fit on the stack.
        float[] xy = new float[Math.Max(count, 1) * 2];
        int n = Place(spec, xy);
        if (Collider2DOutline.Polygon(segments, xy, n)) return true;
        Collider2DOutline.Polyline(segments, xy, n, loop: true); // the raw points, in red
        return false;
    }

    private protected override void CollectHandles(ShapeSpec2D spec, List<float> handles)
    {
        float[] xy = new float[Math.Max(points?.Length ?? 0, 1) * 2];
        int n = Place(spec, xy);
        for (int i = 0; i < n * 2; i++) handles.Add(xy[i]);
    }
}
