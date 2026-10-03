// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.Echo;
using Prowl.Native.Box2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// A hinge: the anchors are pinned together and the bodies may rotate about that point. Doors, pendulums, wheels, chains.
/// (Unity: HingeJoint2D.)
/// <para/>
/// The joint angle is the rotation of this body relative to the connected one (or the world), counter-clockwise positive, and it is
/// zero at the pose the bodies were in when the joint was created. A limit, a motor that drives the angle at a speed, and a spring that
/// pulls it toward a target are each optional. Angles are in degrees.
/// </summary>
[AddComponentMenu("Physics 2D/Joints/Revolute Joint 2D")]
[ComponentIcon("\ue4bb")] // Arrows spin
public sealed class RevoluteJoint2D : Joint2D
{
    // Box2D keeps limits inside (-pi, pi) with a little room, so that is the most a limit can ask for.
    private const float MaxLimitDegrees = 178f;

    [SerializeField, Header("Limit"), Tooltip("Keep the angle between the lower and upper limits.")]
    private bool enableLimit;

    [SerializeField, ShowIf(nameof(enableLimit)), Tooltip("Lowest angle in degrees, from the pose at creation. At least -178.")]
    private float lowerAngle = -45f;

    [SerializeField, ShowIf(nameof(enableLimit)), Tooltip("Highest angle in degrees, from the pose at creation. At most 178.")]
    private float upperAngle = 45f;

    [SerializeField, Header("Motor"), Tooltip("Drive the joint at Motor Speed, using at most Max Motor Torque.")]
    private bool enableMotor;

    [SerializeField, ShowIf(nameof(enableMotor)), Tooltip("Target angular speed in degrees per second. Positive turns counter-clockwise.")]
    private float motorSpeed = 90f;

    [SerializeField, ShowIf(nameof(enableMotor)), Tooltip("The most torque the motor may use (newton-metres). Less than the load needs and the motor stalls.")]
    private float maxMotorTorque = 1000f;

    [SerializeField, Header("Spring"), Tooltip("Pull the angle toward Target Angle like a torsion spring.")]
    private bool enableSpring;

    [SerializeField, ShowIf(nameof(enableSpring)), Tooltip("Stiffness in cycles per second. Higher is stiffer.")]
    private float springFrequency = 2f;

    [SerializeField, ShowIf(nameof(enableSpring)), Tooltip("0 oscillates for a long time; 1 settles without overshoot.")]
    private float springDamping = 0.5f;

    [SerializeField, ShowIf(nameof(enableSpring)), Tooltip("The angle in degrees the spring rests at, from the pose at creation.")]
    private float targetAngle;

    // ---- settings ------------------------------------------------------------------------

    /// <summary>Keep the angle between <see cref="LowerAngle"/> and <see cref="UpperAngle"/>.</summary>
    public bool EnableLimit
    {
        get => enableLimit;
        set { enableLimit = value; Apply(); }
    }

    /// <summary>Lowest angle in degrees, from the pose at creation (at least -178).</summary>
    public float LowerAngle
    {
        get => lowerAngle;
        set { lowerAngle = value; SanitizeValues(); Apply(); }
    }

    /// <summary>Highest angle in degrees, from the pose at creation (at most 178).</summary>
    public float UpperAngle
    {
        get => upperAngle;
        set { upperAngle = value; SanitizeValues(); Apply(); }
    }

    /// <summary>Drive the joint at <see cref="MotorSpeed"/>, using at most <see cref="MaxMotorTorque"/>.</summary>
    public bool EnableMotor
    {
        get => enableMotor;
        set { enableMotor = value; Apply(); }
    }

    /// <summary>Target angular speed in degrees per second; positive is counter-clockwise.</summary>
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

    /// <summary>Pull the angle toward <see cref="TargetAngle"/> like a torsion spring.</summary>
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

    /// <summary>The angle in degrees the spring rests at.</summary>
    public float TargetAngle
    {
        get => targetAngle;
        set { targetAngle = value; Apply(); }
    }

    // ---- state ---------------------------------------------------------------------------

    /// <summary>The current angle in degrees, counter-clockwise positive, zero at the pose the joint was created in. 0 if it does not exist.</summary>
    public float JointAngle => JointPositionValue * Maths.Rad2Deg;

    /// <summary>How fast the angle is changing, in degrees per second. 0 if the joint does not exist.</summary>
    public float JointSpeed
    {
        get
        {
            Rigidbody2D? self = AttachedBody;
            if (self == null) return 0f;
            Rigidbody2D? other = ConnectedBodyInUse;
            return self.AngularVelocity - (other != null ? other.AngularVelocity : 0f);
        }
    }

    /// <summary>The torque the motor is using right now, in newton-metres.</summary>
    public float MotorTorque => MotorLoadValue;

    // ---- Joint2D -------------------------------------------------------------------------

    private protected override int JointType => PB2.JointRevolute;

    private protected override void SanitizeValues()
    {
        lowerAngle = Math.Clamp(lowerAngle, -MaxLimitDegrees, MaxLimitDegrees);
        upperAngle = Math.Clamp(upperAngle, -MaxLimitDegrees, MaxLimitDegrees);
        if (upperAngle < lowerAngle) upperAngle = lowerAngle;
        maxMotorTorque = MathF.Max(maxMotorTorque, 0f);
        springFrequency = MathF.Max(springFrequency, 0f);
        springDamping = MathF.Max(springDamping, 0f);
    }

    private protected override void FillTunables(ref PB2JointDef def)
    {
        if (enableSpring) def.flags |= PB2.JfSpring;
        if (enableLimit) def.flags |= PB2.JfLimit;
        if (enableMotor) def.flags |= PB2.JfMotor;

        float lower = Math.Clamp(lowerAngle, -MaxLimitDegrees, MaxLimitDegrees) * Maths.Deg2Rad;
        float upper = Math.Clamp(upperAngle, -MaxLimitDegrees, MaxLimitDegrees) * Maths.Deg2Rad;
        if (upper < lower) upper = lower;

        PB2.JointDefSetP(ref def, 0, targetAngle * Maths.Deg2Rad);
        PB2.JointDefSetP(ref def, 1, springFrequency);
        PB2.JointDefSetP(ref def, 2, springDamping);
        PB2.JointDefSetP(ref def, 3, lower);
        PB2.JointDefSetP(ref def, 4, upper);
        PB2.JointDefSetP(ref def, 5, maxMotorTorque);
        PB2.JointDefSetP(ref def, 6, motorSpeed * Maths.Deg2Rad);
    }

    /// <summary>Two rays from the anchor marking the limits, drawn from this body's anchor, when the limit is on.</summary>
    private protected override void DrawDetails(float z, float ax, float ay, float bx, float by)
    {
        if (!enableLimit) return;
        // Joint angle zero is where the two bodies' frames line up; the connected body's orientation is the reference.
        float reference = ConnectedWorldAngle();
        const float length = 0.6f;
        float lo = reference + lowerAngle * Maths.Deg2Rad, hi = reference + upperAngle * Maths.Deg2Rad;
        DrawLine(z, bx, by, bx + MathF.Cos(lo) * length, by + MathF.Sin(lo) * length, Color.Yellow);
        DrawLine(z, bx, by, bx + MathF.Cos(hi) * length, by + MathF.Sin(hi) * length, Color.Yellow);
    }
}
