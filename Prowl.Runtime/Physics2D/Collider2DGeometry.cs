// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime.Physics2D;

/// <summary>
/// The geometry behind Collider2D, with no engine types so it can be tested against real Box2D shapes.
/// A Box2D shape lives in the local space of its body (no scale), so each collider has to be expressed as
/// "offset + angle relative to the body", with the lossy scale already folded into the shape's dimensions.
/// </summary>
internal static class Collider2DGeometry
{
    /// <summary>Converts a world-space centre and angle into the space of a body at (bodyX, bodyY, bodyAngle).</summary>
    public static void ToBodySpace(float bodyX, float bodyY, float bodyAngle,
                                   float worldX, float worldY, float worldAngle,
                                   out float localX, out float localY, out float localAngle)
    {
        float dx = worldX - bodyX, dy = worldY - bodyY;
        float c = MathF.Cos(bodyAngle), s = MathF.Sin(bodyAngle);
        // rotate by -bodyAngle
        localX = c * dx + s * dy;
        localY = -s * dx + c * dy;
        localAngle = Angle2D.WrapPi(worldAngle - bodyAngle);
    }

    /// <summary>Scale, then rotate by <paramref name="angle"/>, then translate: a shape-local point into body space.</summary>
    public static void TransformPoint(float px, float py, float scaleX, float scaleY, float angle, float offsetX, float offsetY,
                                      out float x, out float y)
    {
        float sx = px * scaleX, sy = py * scaleY;
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        x = offsetX + c * sx - s * sy;
        y = offsetY + s * sx + c * sy;
    }

    /// <summary>
    /// The two cap centres of a capsule. <paramref name="radius"/> and <paramref name="halfLength"/> are the final
    /// (scaled) dimensions; <paramref name="halfLength"/> is the distance from the centre to a cap centre.
    /// </summary>
    public static void CapsuleEnds(float offsetX, float offsetY, float angle, float halfLength, bool vertical,
                                   out float x1, out float y1, out float x2, out float y2)
    {
        float ax = vertical ? 0f : halfLength, ay = vertical ? halfLength : 0f;
        TransformPoint(-ax, -ay, 1f, 1f, angle, offsetX, offsetY, out x1, out y1);
        TransformPoint(ax, ay, 1f, 1f, angle, offsetX, offsetY, out x2, out y2);
    }

    /// <summary>Magnitude of a scale component, with a floor so a collapsed object does not create a degenerate shape.</summary>
    public static float AbsScale(float s) => MathF.Max(MathF.Abs(s), 1e-4f);
}
