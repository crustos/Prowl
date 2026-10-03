// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.Echo;
using Prowl.Native.Box2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// Glues two bodies together at the pose they were in when the joint was created: no relative movement and no relative rotation.
/// (Unity: FixedJoint2D.)
/// <para/>
/// Box2D's solver is approximate, so a long chain of welds sags and is not as stiff as one solid body; for one rigid object prefer one
/// rigidbody with several colliders. With a frequency above zero the weld becomes springy, for breakable or squashy connections.
/// A frequency of 0 is as stiff as the solver can make it.
/// </summary>
[AddComponentMenu("Physics 2D/Joints/Weld Joint 2D")]
[ComponentIcon("\uf023")] // Lock
public sealed class WeldJoint2D : Joint2D
{
    [SerializeField, Header("Linear"), Tooltip("Stiffness of the position lock in cycles per second. 0 is as stiff as possible.")]
    private float linearFrequency;

    [SerializeField, ShowIf(nameof(IsLinearSoft)), Tooltip("0 oscillates for a long time; 1 settles without overshoot.")]
    private float linearDamping = 1f;

    [SerializeField, Header("Angular"), Tooltip("Stiffness of the rotation lock in cycles per second. 0 is as stiff as possible.")]
    private float angularFrequency;

    [SerializeField, ShowIf(nameof(IsAngularSoft)), Tooltip("0 oscillates for a long time; 1 settles without overshoot.")]
    private float angularDamping = 1f;

    // Conditions for [ShowIf]: the inspector looks them up by name.
    private bool IsLinearSoft => linearFrequency > 0f;
    private bool IsAngularSoft => angularFrequency > 0f;

    // ---- settings ------------------------------------------------------------------------

    /// <summary>Stiffness of the position lock in cycles per second. 0 is as stiff as possible.</summary>
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

    /// <summary>Stiffness of the rotation lock in cycles per second. 0 is as stiff as possible.</summary>
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

    // ---- Joint2D -------------------------------------------------------------------------

    private protected override int JointType => PB2.JointWeld;

    private protected override void SanitizeValues()
    {
        linearFrequency = MathF.Max(linearFrequency, 0f);
        linearDamping = MathF.Max(linearDamping, 0f);
        angularFrequency = MathF.Max(angularFrequency, 0f);
        angularDamping = MathF.Max(angularDamping, 0f);
    }

    private protected override void FillTunables(ref PB2JointDef def)
    {
        PB2.JointDefSetP(ref def, 0, linearFrequency);
        PB2.JointDefSetP(ref def, 1, angularFrequency);
        PB2.JointDefSetP(ref def, 2, linearDamping);
        PB2.JointDefSetP(ref def, 3, angularDamping);
    }
}
