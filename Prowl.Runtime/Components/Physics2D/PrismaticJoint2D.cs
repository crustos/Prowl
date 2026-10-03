// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.Echo;
using Prowl.Native.Box2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// A slider: this body may slide along one line and cannot rotate relative to the connected body. Pistons, lifts, sliding doors.
/// (Unity: SliderJoint2D.)
/// <para/>
/// <see cref="Angle"/> is the direction of the line, in degrees, in the space of the connected body (the world's when there is none),
/// so it turns with that body. The translation is how far this body has slid along the line from where it was when the joint was
/// created. A limit, a motor that drives the slide at a speed, and a spring that pulls it toward a target position are each optional.
/// </summary>
[AddComponentMenu("Physics 2D/Joints/Prismatic Joint 2D")]
[ComponentIcon("\uf07e")] // Arrows left-right
public sealed class PrismaticJoint2D : Joint2D
{
    [SerializeField, Header("Axis"), Tooltip("Direction of the sliding line in degrees, in the connected body's space (the world's if there is none). 0 slides along +X, 90 along +Y.")]
    private float angle;

    [SerializeField, Header("Limit"), Tooltip("Keep the slide between the lower and upper translation.")]
    private bool enableLimit;

    [SerializeField, ShowIf(nameof(enableLimit)), Tooltip("Furthest slide in the negative direction, from the starting position.")]
    private float lowerTranslation = -1f;

    [SerializeField, ShowIf(nameof(enableLimit)), Tooltip("Furthest slide in the positive direction, from the starting position.")]
    private float upperTranslation = 1f;

    [SerializeField, Header("Motor"), Tooltip("Drive the slide at Motor Speed, using at most Max Motor Force.")]
    private bool enableMotor;

    [SerializeField, ShowIf(nameof(enableMotor)), Tooltip("Target speed along the line in units per second. Positive is along the axis.")]
    private float motorSpeed = 1f;

    [SerializeField, ShowIf(nameof(enableMotor)), Tooltip("The most force the motor may use (newtons).")]
    private float maxMotorForce = 1000f;

    [SerializeField, Header("Spring"), Tooltip("Pull the translation toward Target Translation like a spring along the line.")]
    private bool enableSpring;

    [SerializeField, ShowIf(nameof(enableSpring)), Tooltip("Stiffness in cycles per second. Higher is stiffer.")]
    private float springFrequency = 2f;

    [SerializeField, ShowIf(nameof(enableSpring)), Tooltip("0 oscillates for a long time; 1 settles without overshoot.")]
    private float springDamping = 0.5f;

    [SerializeField, ShowIf(nameof(enableSpring)), Tooltip("The translation the spring rests at, from the starting position.")]
    private float targetTranslation;

    // ---- settings ------------------------------------------------------------------------

    /// <summary>Direction of the sliding line in degrees, in the connected body's space (the world's if there is none). Rebuilds the joint.</summary>
    public float Angle
    {
        get => angle;
        set { angle = value; Rebuild(); }
    }

    public bool EnableLimit
    {
        get => enableLimit;
        set { enableLimit = value; Apply(); }
    }

    /// <summary>Furthest slide in the negative direction, from the starting position.</summary>
    public float LowerTranslation
    {
        get => lowerTranslation;
        set { lowerTranslation = value; SanitizeValues(); Apply(); }
    }

    /// <summary>Furthest slide in the positive direction, from the starting position.</summary>
    public float UpperTranslation
    {
        get => upperTranslation;
        set { upperTranslation = value; SanitizeValues(); Apply(); }
    }

    public bool EnableMotor
    {
        get => enableMotor;
        set { enableMotor = value; Apply(); }
    }

    /// <summary>Target speed along the line in units per second.</summary>
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

    public bool EnableSpring
    {
        get => enableSpring;
        set { enableSpring = value; Apply(); }
    }

    /// <summary>Spring stiffness in cycles per second.</summary>
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

    /// <summary>The translation the spring rests at.</summary>
    public float TargetTranslation
    {
        get => targetTranslation;
        set { targetTranslation = value; Apply(); }
    }

    // ---- state ---------------------------------------------------------------------------

    /// <summary>How far this body has slid along the line from where it started, in units. 0 if the joint does not exist.</summary>
    public float Translation => JointPositionValue;

    /// <summary>How fast it is sliding along the line, in units per second. 0 if the joint does not exist.</summary>
    public float Speed => JointSpeedValue;

    /// <summary>The force the motor is using right now, in newtons.</summary>
    public float MotorForce => MotorLoadValue;

    // ---- Joint2D -------------------------------------------------------------------------

    private protected override int JointType => PB2.JointPrismatic;

    private protected override float FrameAngleRadians => angle * Maths.Deg2Rad;

    private protected override void SanitizeValues()
    {
        if (upperTranslation < lowerTranslation) upperTranslation = lowerTranslation;
        maxMotorForce = MathF.Max(maxMotorForce, 0f);
        springFrequency = MathF.Max(springFrequency, 0f);
        springDamping = MathF.Max(springDamping, 0f);
    }

    private protected override void FillTunables(ref PB2JointDef def)
    {
        if (enableSpring) def.flags |= PB2.JfSpring;
        if (enableLimit) def.flags |= PB2.JfLimit;
        if (enableMotor) def.flags |= PB2.JfMotor;

        float lower = lowerTranslation, upper = upperTranslation;
        if (upper < lower) upper = lower;

        PB2.JointDefSetP(ref def, 0, springFrequency);
        PB2.JointDefSetP(ref def, 1, springDamping);
        PB2.JointDefSetP(ref def, 2, targetTranslation);
        PB2.JointDefSetP(ref def, 3, lower);
        PB2.JointDefSetP(ref def, 4, upper);
        PB2.JointDefSetP(ref def, 5, maxMotorForce);
        PB2.JointDefSetP(ref def, 6, motorSpeed);
    }

    /// <summary>The sliding line through the anchor, with the limits marked where they are on.</summary>
    private protected override void DrawDetails(float z, float ax, float ay, float bx, float by)
    {
        float dirAngle = ConnectedWorldAngle() + angle * Maths.Deg2Rad;
        float dx = MathF.Cos(dirAngle), dy = MathF.Sin(dirAngle);
        float reach = enableLimit ? MathF.Max(MathF.Abs(lowerTranslation), MathF.Abs(upperTranslation)) + 0.25f : 1.5f;

        // The line runs through the connected anchor: that is the axis the body slides along.
        DrawLine(z, bx - dx * reach, by - dy * reach, bx + dx * reach, by + dy * reach, Color.Green);
        if (!enableLimit) return;

        // Limits are measured from where this body started, which is where the connected anchor sits at creation.
        const float tick = 0.12f;
        for (int i = 0; i < 2; i++)
        {
            float t = i == 0 ? lowerTranslation : upperTranslation;
            float cx = bx + dx * t, cy = by + dy * t;
            DrawLine(z, cx - dy * tick, cy + dx * tick, cx + dy * tick, cy - dx * tick, Color.Yellow);
        }
    }
}
