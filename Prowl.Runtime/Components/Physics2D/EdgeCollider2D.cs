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
/// An open or closed chain of line segments with no thickness, for terrain and level boundaries. Two-sided: things
/// collide with it from either side. It has no volume, so a dynamic body cannot be built from it.
/// </summary>
[AddComponentMenu("Physics 2D/Edge Collider 2D")]
[ComponentIcon("\uf0c8")]
public sealed class EdgeCollider2D : Collider2D
{
    [SerializeField, Header("Shape"), Tooltip("Vertices in the collider's local space, before scale. At least 2 (3 for a loop).")]
    private Float2[] points = { new(-0.5f, 0f), new(0.5f, 0f) };

    [SerializeField, Tooltip("Joins the last point back to the first.")]
    private bool isLoop;

    /// <summary>Vertices in the collider's local space, before scale. At least 2 (3 for a loop).</summary>
    public Float2[] Points
    {
        get => points;
        set { points = value ?? Array.Empty<Float2>(); Rebuild(); }
    }

    /// <summary>Joins the last point back to the first.</summary>
    public bool IsLoop
    {
        get => isLoop;
        set { isLoop = value; Rebuild(); }
    }

    /// <summary>Replaces the points with a single horizontal unit segment.</summary>
    [Button("Reset To Line")]
    public void ResetToLine()
    {
        isLoop = false;
        Points = new[] { new Float2(-0.5f, 0f), new Float2(0.5f, 0f) };
    }

    private int MinPoints => isLoop ? 3 : 2;

    private float[] Place(ShapeSpec2D spec, out int n)
    {
        n = points?.Length ?? 0;
        float[] xy = new float[Math.Max(n, 1) * 2];
        for (int i = 0; i < n; i++)
            Collider2DGeometry.TransformPoint(points![i].X, points[i].Y, spec.ScaleX, spec.ScaleY, spec.Angle, spec.X, spec.Y,
                                              out xy[2 * i], out xy[2 * i + 1]);
        return xy;
    }

    private protected override unsafe void CreateShapes(uint body, ShapeSpec2D spec, List<uint> shapes)
    {
        int n;
        float[] xy = Place(spec, out n);
        if (n < MinPoints)
        {
            Debug.LogError($"[{Name}] EdgeCollider2D needs at least {MinPoints} points (it has {n}).");
            return;
        }

        int segments = isLoop ? n : n - 1;
        uint[] ids = new uint[segments];
        int made;
        fixed (float* p = xy)
        fixed (uint* o = ids)
            made = PB2.SegmentsCreate(body, spec.ColliderIndex, spec.Layer, p, n, isLoop ? 1 : 0, spec.Friction, spec.Bounciness, o);

        for (int i = 0; i < made; i++) shapes.Add(ids[i]);
    }

    private protected override bool BuildOutline(ShapeSpec2D spec, List<float> segments)
    {
        int n;
        float[] xy = Place(spec, out n);
        Collider2DOutline.Polyline(segments, xy, n, isLoop);
        return n >= MinPoints;
    }

    private protected override void CollectHandles(ShapeSpec2D spec, List<float> handles)
    {
        int n;
        float[] xy = Place(spec, out n);
        for (int i = 0; i < n * 2; i++) handles.Add(xy[i]);
    }
}
