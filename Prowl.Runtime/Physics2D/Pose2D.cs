// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime.Physics2D;

/// <summary>Angle helpers for the 2D physics layer. All angles are radians, counter-clockwise, about +Z.</summary>
internal static class Angle2D
{
    /// <summary>Wraps to (-pi, pi].</summary>
    public static float WrapPi(float a)
    {
        const float TwoPi = MathF.PI * 2f;
        a %= TwoPi;
        if (a > MathF.PI) a -= TwoPi;
        else if (a <= -MathF.PI) a += TwoPi;
        return a;
    }

    /// <summary>The angle equal to <paramref name="raw"/> (mod 2pi) that is closest to <paramref name="reference"/>.</summary>
    public static float Unwrap(float raw, float reference) => reference + WrapPi(raw - reference);

    /// <summary>Z rotation of a unit quaternion, for the case where the rotation is (nearly) about Z only.</summary>
    public static float FromQuaternionZ(float x, float y, float z, float w)
        => MathF.Atan2(2f * (w * z + x * y), 1f - 2f * (y * y + z * z));

    /// <summary>Z-rotation quaternion components (x = y = 0), so a caller can build its own Quaternion.</summary>
    public static void ToQuaternionZ(float angle, out float z, out float w)
    {
        float half = angle * 0.5f;
        z = MathF.Sin(half);
        w = MathF.Cos(half);
    }
}

/// <summary>
/// The last two simulated poses of a body, for rendering between fixed steps. Box2D only reports bodies that
/// moved, so a body at rest produces no events at all. <see cref="Sample"/> therefore treats "did not move in the
/// most recent step" as "rest at the current pose", instead of lerping toward a stale previous pose forever.
/// </summary>
internal struct BodyPose2D
{
    public float PrevX, PrevY, PrevAngle;
    public float CurX, CurY, CurAngle;
    public int MovedStep;
    public bool Valid;

    /// <summary>Snaps both poses (spawn, teleport) so nothing is smeared across the jump.</summary>
    public void Reset(float x, float y, float angle, int step)
    {
        PrevX = CurX = x;
        PrevY = CurY = y;
        PrevAngle = CurAngle = angle;
        MovedStep = step;
        Valid = true;
    }

    /// <summary>Records the pose a step produced. The angle is unwrapped so spinning bodies stay continuous.</summary>
    public void Push(float x, float y, float cos, float sin, int step)
    {
        float angle = MathF.Atan2(sin, cos);
        if (!Valid)
        {
            Reset(x, y, angle, step);
            return;
        }
        PrevX = CurX;
        PrevY = CurY;
        PrevAngle = CurAngle;
        CurX = x;
        CurY = y;
        CurAngle = Angle2D.Unwrap(angle, CurAngle);
        MovedStep = step;
    }

    /// <param name="alpha">0..1, how far the frame being drawn is into the next step (Time.FixedAlpha).</param>
    /// <param name="currentStep">The index of the most recently completed step.</param>
    public readonly void Sample(float alpha, int currentStep, out float x, out float y, out float angle)
    {
        if (MovedStep != currentStep)
        {
            x = CurX; y = CurY; angle = CurAngle;
            return;
        }
        alpha = Math.Clamp(alpha, 0f, 1f);
        x = PrevX + (CurX - PrevX) * alpha;
        y = PrevY + (CurY - PrevY) * alpha;
        angle = PrevAngle + (CurAngle - PrevAngle) * alpha;
    }
}
