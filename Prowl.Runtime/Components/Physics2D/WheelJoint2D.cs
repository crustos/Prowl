// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.Echo;
using Prowl.Native.Box2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// A wheel on a suspension: this body spins freely and may slide along one line, sprung against the connected body. Put it on the
/// <i>wheel</i> and connect it to the chassis. (Unity: WheelJoint2D.)
/// <para/>
/// <see cref="Angle"/> is the direction of the suspension in degrees in the chassis's space (90 is straight up and down). The motor
/// spins the wheel; nothing drives the suspension except the spring, which is on by default.
/// </summary>
[AddComponentMenu("Physics 2D/Joints/Wheel Joint 2D")]
[ComponentIcon("\uf655")] // Dharmachakra (wheel)
public sealed class WheelJoint2D : Joint2D
{
    [SerializeField, Header("Suspension"), Tooltip("Direction of the suspension in degrees, in the connected body's space (the world's if there is none). 90 is vertical.")]
    private float angle = 90f;

    [SerializeField, Tooltip("Spring the wheel against the chassis along the suspension.")]
    private bool enableSpring = true;

    [SerializeField, ShowIf(nameof(enableSpring)), Tooltip("Suspension stiffness in cycles per second. Higher is stiffer.")]
    private float springFrequency = 1f;

    [SerializeField, ShowIf(nameof(enableSpring)), Tooltip("0 bounces for a long time; 1 settles without overshoot.")]
    private float springDamping = 0.7f;

    [SerializeField, Header("Limit"), Tooltip("Keep the suspension travel between the lower and upper translation.")]
    private bool enableLimit;

    [SerializeField, ShowIf(nameof(enableLimit)), Tooltip("Furthest travel in the negative direction along the suspension, from the starting position.")]
    private float lowerTranslation = -0.5f;

    [SerializeField, ShowIf(nameof(enableLimit)), Tooltip("Furthest travel in the positive direction along the suspension, from the starting position.")]
    private float upperTranslation = 0.5f;

    [SerializeField, Header("Motor"), Tooltip("Spin the wheel at Motor Speed, using at most Max Motor Torque.")]
    private bool enableMotor;

    [SerializeField, ShowIf(nameof(enableMotor)), Tooltip("Target spin in degrees per second. Positive is counter-clockwise.")]
    private float motorSpeed = 360f;

    [SerializeField, ShowIf(nameof(enableMotor)), Tooltip("The most torque the motor may use (newton-metres).")]
    private float maxMotorTorque = 100f;

    // ---- settings ------------------------------------------------------------------------

    /// <summary>Direction of the suspension in degrees, in the connected body's space (the world's if there is none). Rebuilds the joint.</summary>
    public float Angle
    {
        get => angle;
        set { angle = value; Rebuild(); }
    }

    public bool EnableSpring
    {
        get => enableSpring;
        set { enableSpring = value; Apply(); }
    }

    /// <summary>Suspension stiffness in cycles per second.</summary>
    public float SpringFrequency
    {
        get => springFrequency;
        set { springFrequency = MathF.Max(value, 0f); Apply(); }
    }

    /// <summary>Suspension damping ratio: 0 bounces, 1 settles without overshoot.</summary>
    public float SpringDamping
    {
        get => springDamping;
        set { springDamping = MathF.Max(value, 0f); Apply(); }
    }

    public bool EnableLimit
    {
        get => enableLimit;
        set { enableLimit = value; Apply(); }
    }

    public float LowerTranslation
    {
        get => lowerTranslation;
        set { lowerTranslation = value; SanitizeValues(); Apply(); }
    }

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

    /// <summary>Target spin in degrees per second; positive is counter-clockwise.</summary>
    public float MotorSpeed
    {
        get => motorSpeed;
        set { motorSpeed = value; Apply(); }
    }

    /// <summary>The most torque the motor may use, in newton-metres.</summary>
    public float MaxMotorTorque
    {
        get => maxMotorTorque;
        set { maxMotorTorque = MathF.Max(value, 0f); Apply(); }
    }

    // ---- state ---------------------------------------------------------------------------

    /// <summary>How far the wheel has travelled along the suspension from where it started. 0 if the joint does not exist.</summary>
    public float Translation => JointPositionValue;

    /// <summary>The torque the motor is using right now, in newton-metres.</summary>
    public float MotorTorque => MotorLoadValue;

    // ---- Joint2D -------------------------------------------------------------------------

    private protected override int JointType => PB2.JointWheel;

    private protected override float FrameAngleRadians => angle * Maths.Deg2Rad;

    private protected override void SanitizeValues()
    {
        if (upperTranslation < lowerTranslation) upperTranslation = lowerTranslation;
        maxMotorTorque = MathF.Max(maxMotorTorque, 0f);
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
        PB2.JointDefSetP(ref def, 2, lower);
        PB2.JointDefSetP(ref def, 3, upper);
        PB2.JointDefSetP(ref def, 4, maxMotorTorque);
        PB2.JointDefSetP(ref def, 5, motorSpeed * Maths.Deg2Rad);
    }

    /// <summary>The suspension line through the chassis-side anchor, with the travel limits marked where they are on.</summary>
    private protected override void DrawDetails(float z, float ax, float ay, float bx, float by)
    {
        float dirAngle = ConnectedWorldAngle() + angle * Maths.Deg2Rad;
        float dx = MathF.Cos(dirAngle), dy = MathF.Sin(dirAngle);
        float reach = enableLimit ? MathF.Max(MathF.Abs(lowerTranslation), MathF.Abs(upperTranslation)) + 0.25f : 1f;

        DrawLine(z, bx - dx * reach, by - dy * reach, bx + dx * reach, by + dy * reach, Color.Green);
        if (!enableLimit) return;

        const float tick = 0.12f;
        for (int i = 0; i < 2; i++)
        {
            float t = i == 0 ? lowerTranslation : upperTranslation;
            float cx = bx + dx * t, cy = by + dy * t;
            DrawLine(z, cx - dy * tick, cy + dx * tick, cx + dy * tick, cy - dx * tick, Color.Yellow);
        }
    }
}
