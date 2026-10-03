// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.Echo;
using Prowl.Native.Box2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// Keeps the two anchors a set distance apart. Ropes, rods, springs, tethers. (Unity: DistanceJoint2D and SpringJoint2D.)
/// <para/>
/// With <see cref="EnableSpring"/> off the joint is a <b>rigid rod</b> of length <see cref="Distance"/>; the limit and motor are ignored.
/// Turn the spring on and the distance becomes a rest length the bodies are pulled toward with the given stiffness. For a
/// <b>rope</b> that only pulls when stretched, turn the spring on, set <see cref="SpringFrequency"/> to 0 (no spring force at all) and turn the limit
/// on: the anchors can come as close as <see cref="MinDistance"/> but not further apart than <see cref="MaxDistance"/>.
/// </summary>
[AddComponentMenu("Physics 2D/Joints/Distance Joint 2D")]
[ComponentIcon("\uf545")] // Ruler
public sealed class DistanceJoint2D : Joint2D
{
    // Box2D clamps lengths to a small positive value.
    private const float MinLength = 0.005f;

    [SerializeField, Header("Distance"), Tooltip("The length to hold between the anchors. A rigid rod unless the spring is on, in which case it is the spring's rest length.")]
    private float distance = 1f;

    [SerializeField, Tooltip("Measure Distance from how far apart the anchors are when the joint is created.")]
    private bool autoConfigureDistance = true;

    [SerializeField, Header("Spring"), Tooltip("Make the distance soft. Off holds it rigidly, and ignores the limit and motor.")]
    private bool enableSpring;

    [SerializeField, ShowIf(nameof(enableSpring)), Tooltip("Stiffness in cycles per second. 0 applies no spring force, which with the limit on makes a rope.")]
    private float springFrequency = 2f;

    [SerializeField, ShowIf(nameof(enableSpring)), Tooltip("0 oscillates for a long time; 1 settles without overshoot.")]
    private float springDamping = 0.5f;

    [SerializeField, Header("Limit"), Tooltip("Keep the distance between Min and Max. Only applies with the spring on.")]
    private bool enableLimit;

    [SerializeField, ShowIf(nameof(enableLimit)), Tooltip("The closest the anchors may come.")]
    private float minDistance;

    [SerializeField, ShowIf(nameof(enableLimit)), Tooltip("The furthest the anchors may part.")]
    private float maxDistance = 10f;

    [SerializeField, Header("Motor"), Tooltip("Reel the distance in or out at Motor Speed, using at most Max Motor Force. Only applies with the spring on.")]
    private bool enableMotor;

    [SerializeField, ShowIf(nameof(enableMotor)), Tooltip("How fast the anchors move apart, in units per second. Negative reels them in.")]
    private float motorSpeed = 1f;

    [SerializeField, ShowIf(nameof(enableMotor)), Tooltip("The most force the motor may use (newtons).")]
    private float maxMotorForce = 100f;

    // ---- settings ------------------------------------------------------------------------

    /// <summary>The length to hold between the anchors: a rigid rod, or the spring's rest length when the spring is on.</summary>
    public float Distance
    {
        get => distance;
        set { distance = MathF.Max(value, MinLength); Apply(); }
    }

    /// <summary>Measure <see cref="Distance"/> from how far apart the anchors are when the joint is created. Does not rebuild the joint.</summary>
    public bool AutoConfigureDistance
    {
        get => autoConfigureDistance;
        set => autoConfigureDistance = value;
    }

    /// <summary>Soft distance. Off holds <see cref="Distance"/> rigidly and ignores the limit and motor.</summary>
    public bool EnableSpring
    {
        get => enableSpring;
        set { enableSpring = value; Apply(); }
    }

    /// <summary>Spring stiffness in cycles per second. 0 applies no spring force.</summary>
    public float SpringFrequency
    {
        get => springFrequency;
        set { springFrequency = MathF.Max(value, 0f); Apply(); }
    }

    /// <summary>Spring damping ratio: 0 oscillates, 1 settles without overshoot.</summary>
    public float SpringDamping
    {
        get => springDamping;
        set { springDamping = MathF.Max(value, 0f); Apply(); }
    }

    /// <summary>Keep the distance between <see cref="MinDistance"/> and <see cref="MaxDistance"/>. Only applies with the spring on.</summary>
    public bool EnableLimit
    {
        get => enableLimit;
        set { enableLimit = value; Apply(); }
    }

    public float MinDistance
    {
        get => minDistance;
        set { minDistance = MathF.Max(value, 0f); SanitizeValues(); Apply(); }
    }

    public float MaxDistance
    {
        get => maxDistance;
        set { maxDistance = MathF.Max(value, 0f); SanitizeValues(); Apply(); }
    }

    /// <summary>Reel the distance in or out at <see cref="MotorSpeed"/>. Only applies with the spring on.</summary>
    public bool EnableMotor
    {
        get => enableMotor;
        set { enableMotor = value; Apply(); }
    }

    /// <summary>How fast the anchors move apart, in units per second. Negative reels them in.</summary>
    public float MotorSpeed
    {
        get => motorSpeed;
        set { motorSpeed = value; Apply(); }
    }

    /// <summary>The most force the motor may use, in newtons.</summary>
    public float MaxMotorForce
    {
        get => maxMotorForce;
        set { maxMotorForce = MathF.Max(value, 0f); Apply(); }
    }

    // ---- state ---------------------------------------------------------------------------

    /// <summary>How far apart the anchors are right now. 0 if the joint does not exist.</summary>
    public float CurrentDistance => JointPositionValue;

    /// <summary>The force the motor is using right now, in newtons.</summary>
    public float MotorForce => MotorLoadValue;

    // ---- Joint2D -------------------------------------------------------------------------

    private protected override int JointType => PB2.JointDistance;

    private protected override void BeforeCreate(float worldAx, float worldAy, float worldBx, float worldBy)
    {
        if (!autoConfigureDistance) return;
        float dx = worldBx - worldAx, dy = worldBy - worldAy;
        distance = MathF.Max(MathF.Sqrt(dx * dx + dy * dy), MinLength);
    }

    private protected override void SanitizeValues()
    {
        distance = MathF.Max(distance, MinLength);
        minDistance = MathF.Max(minDistance, 0f);
        maxDistance = MathF.Max(maxDistance, 0f);
        if (maxDistance < minDistance) maxDistance = minDistance;
        springFrequency = MathF.Max(springFrequency, 0f);
        springDamping = MathF.Max(springDamping, 0f);
        maxMotorForce = MathF.Max(maxMotorForce, 0f);
    }

    private protected override void FillTunables(ref PB2JointDef def)
    {
        if (enableSpring) def.flags |= PB2.JfSpring;
        if (enableLimit) def.flags |= PB2.JfLimit;
        if (enableMotor) def.flags |= PB2.JfMotor;

        float min = MathF.Max(minDistance, MinLength);
        float max = MathF.Max(maxDistance, min);

        PB2.JointDefSetP(ref def, 0, MathF.Max(distance, MinLength));
        PB2.JointDefSetP(ref def, 1, min);
        PB2.JointDefSetP(ref def, 2, max);
        PB2.JointDefSetP(ref def, 3, springFrequency);
        PB2.JointDefSetP(ref def, 4, springDamping);
        PB2.JointDefSetP(ref def, 5, maxMotorForce);
        PB2.JointDefSetP(ref def, 6, motorSpeed);
    }

    /// <summary>Circles of the minimum and maximum distance around the connected anchor, when the limit is on.</summary>
    private protected override void DrawDetails(float z, float ax, float ay, float bx, float by)
    {
        if (!enableLimit) return;
        DrawRing(z, bx, by, minDistance);
        DrawRing(z, bx, by, maxDistance);
    }

    private static void DrawRing(float z, float cx, float cy, float radius)
    {
        if (radius < 0.01f) return;
        const int segments = 24;
        float px = cx + radius, py = cy;
        for (int i = 1; i <= segments; i++)
        {
            float a = i * (MathF.PI * 2f / segments);
            float x = cx + MathF.Cos(a) * radius, y = cy + MathF.Sin(a) * radius;
            DrawLine(z, px, py, x, y, Color.Yellow);
            px = x; py = y;
        }
    }
}
