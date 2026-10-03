// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.Echo;
using Prowl.Native.Box2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// Controls how two bodies move relative to each other: a soft hold on their relative pose, and a drive on their relative velocity.
/// (Unity: RelativeJoint2D and FrictionJoint2D.)
/// <para/>
/// <b>Hold.</b> The spring settings pull this body toward the pose it had relative to the connected body (or the world) when the
/// joint was created, limited by <see cref="MaxSpringForce"/> and <see cref="MaxSpringTorque"/>. Set a frequency to 0 to switch that
/// part off.
/// <para/>
/// <b>Drive.</b> <see cref="LinearVelocity"/> and <see cref="AngularVelocity"/> are the relative velocity the joint tries to maintain,
/// using at most <see cref="MaxVelocityForce"/> and <see cref="MaxVelocityTorque"/>. With a velocity of zero and a small maximum it acts
/// like top-down friction, which is how to make a body on a flat floor slow itself down.
/// </summary>
[AddComponentMenu("Physics 2D/Joints/Motor Joint 2D")]
[ComponentIcon("\uf085")] // Gears
public sealed class MotorJoint2D : Joint2D
{
    [SerializeField, Header("Velocity drive"), Tooltip("Relative linear velocity to maintain, in units per second, in the connected body's space (the world's if there is none).")]
    private Float2 linearVelocity;

    [SerializeField, Tooltip("The most force the linear drive may use (newtons). 0 switches it off; with a zero velocity a small value is friction.")]
    private float maxVelocityForce;

    [SerializeField, Tooltip("Relative spin to maintain, in degrees per second.")]
    private float angularVelocity;

    [SerializeField, Tooltip("The most torque the angular drive may use (newton-metres). 0 switches it off; with a zero spin a small value is friction.")]
    private float maxVelocityTorque;

    [SerializeField, Header("Linear hold"), Tooltip("Stiffness of the pull toward the relative position, in cycles per second. 0 switches it off.")]
    private float linearFrequency = 5f;

    [SerializeField, Tooltip("0 oscillates for a long time; 1 settles without overshoot.")]
    private float linearDamping = 0.7f;

    [SerializeField, Tooltip("The most force the position hold may use (newtons).")]
    private float maxSpringForce = 1000f;

    [SerializeField, Header("Angular hold"), Tooltip("Stiffness of the pull toward the relative rotation, in cycles per second. 0 switches it off.")]
    private float angularFrequency = 5f;

    [SerializeField, Tooltip("0 oscillates for a long time; 1 settles without overshoot.")]
    private float angularDamping = 0.7f;

    [SerializeField, Tooltip("The most torque the rotation hold may use (newton-metres).")]
    private float maxSpringTorque = 1000f;

    // ---- settings ------------------------------------------------------------------------

    /// <summary>Relative linear velocity to maintain, in units per second.</summary>
    public Float2 LinearVelocity
    {
        get => linearVelocity;
        set { linearVelocity = value; Apply(); }
    }

    /// <summary>The most force the linear drive may use, in newtons. 0 switches it off.</summary>
    public float MaxVelocityForce
    {
        get => maxVelocityForce;
        set { maxVelocityForce = MathF.Max(value, 0f); Apply(); }
    }

    /// <summary>Relative spin to maintain, in degrees per second.</summary>
    public float AngularVelocity
    {
        get => angularVelocity;
        set { angularVelocity = value; Apply(); }
    }

    /// <summary>The most torque the angular drive may use, in newton-metres. 0 switches it off.</summary>
    public float MaxVelocityTorque
    {
        get => maxVelocityTorque;
        set { maxVelocityTorque = MathF.Max(value, 0f); Apply(); }
    }

    /// <summary>Stiffness of the position hold in cycles per second. 0 switches it off.</summary>
    public float LinearFrequency
    {
        get => linearFrequency;
        set { linearFrequency = MathF.Max(value, 0f); Apply(); }
    }

    public float LinearDamping
    {
        get => linearDamping;
        set { linearDamping = MathF.Max(value, 0f); Apply(); }
    }

    /// <summary>The most force the position hold may use, in newtons.</summary>
    public float MaxSpringForce
    {
        get => maxSpringForce;
        set { maxSpringForce = MathF.Max(value, 0f); Apply(); }
    }

    /// <summary>Stiffness of the rotation hold in cycles per second. 0 switches it off.</summary>
    public float AngularFrequency
    {
        get => angularFrequency;
        set { angularFrequency = MathF.Max(value, 0f); Apply(); }
    }

    public float AngularDamping
    {
        get => angularDamping;
        set { angularDamping = MathF.Max(value, 0f); Apply(); }
    }

    /// <summary>The most torque the rotation hold may use, in newton-metres.</summary>
    public float MaxSpringTorque
    {
        get => maxSpringTorque;
        set { maxSpringTorque = MathF.Max(value, 0f); Apply(); }
    }

    // ---- Joint2D -------------------------------------------------------------------------

    private protected override int JointType => PB2.JointMotor;

    private protected override void SanitizeValues()
    {
        maxVelocityForce = MathF.Max(maxVelocityForce, 0f);
        maxVelocityTorque = MathF.Max(maxVelocityTorque, 0f);
        linearFrequency = MathF.Max(linearFrequency, 0f);
        linearDamping = MathF.Max(linearDamping, 0f);
        maxSpringForce = MathF.Max(maxSpringForce, 0f);
        angularFrequency = MathF.Max(angularFrequency, 0f);
        angularDamping = MathF.Max(angularDamping, 0f);
        maxSpringTorque = MathF.Max(maxSpringTorque, 0f);
    }

    private protected override void FillTunables(ref PB2JointDef def)
    {
        PB2.JointDefSetP(ref def, 0, linearVelocity.X);
        PB2.JointDefSetP(ref def, 1, linearVelocity.Y);
        PB2.JointDefSetP(ref def, 2, maxVelocityForce);
        PB2.JointDefSetP(ref def, 3, angularVelocity * Maths.Deg2Rad);
        PB2.JointDefSetP(ref def, 4, maxVelocityTorque);
        PB2.JointDefSetP(ref def, 5, linearFrequency);
        PB2.JointDefSetP(ref def, 6, linearDamping);
        PB2.JointDefSetP(ref def, 7, maxSpringForce);
        PB2.JointDefSetP(ref def, 8, angularFrequency);
        PB2.JointDefSetP(ref def, 9, angularDamping);
        PB2.JointDefSetP(ref def, 10, maxSpringTorque);
    }
}
