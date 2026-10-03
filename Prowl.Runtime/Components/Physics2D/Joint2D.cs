// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.Echo;
using Prowl.Runtime.Physics2D;
using Prowl.Native.Box2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// Base class of the 2D joints (Box2D-Packed). A joint ties the <see cref="Rigidbody2D"/> that owns it (the nearest enabled one on
/// this GameObject or above) to another rigidbody, or to the world when <see cref="ConnectedBody"/> is empty.
/// <para/>
/// <b>Anchors.</b> <see cref="Anchor"/> is a point on this body and <see cref="ConnectedAnchor"/> a point on the connected body
/// (a world position when there is none), both in the body's local space and scaled with it. With
/// <see cref="AutoConfigureConnectedAnchor"/> on, the connected anchor is worked out when the joint is created so that the two
/// coincide and the joint starts at rest.
/// <para/>
/// <b>Reference pose.</b> A joint starts relaxed: the relative rotation of the two bodies at the moment it is created is the
/// zero of its angle, so a hinge's limits and a weld's rotation lock are measured from how the bodies were placed.
/// <para/>
/// <b>Lifetime.</b> The joint exists while the component is enabled and both bodies are simulated. If a body is disabled or
/// destroyed the joint goes with it, and comes back (re-measured from the new poses) when both bodies are simulated again. A
/// joint pushed past <see cref="BreakForce"/> or <see cref="BreakTorque"/> is destroyed and reported to
/// <see cref="MonoBehaviour.OnJointBreak2D"/>; <see cref="Rebuild"/> or re-enabling the component brings it back.
/// <para/>
/// Scale is read when the joint is created; call <see cref="Rebuild"/> after rescaling a body at runtime.
/// </summary>
[ComponentIcon("\uf0c1")] // Link
public abstract class Joint2D : MonoBehaviour, IJoint2DHost
{
    [SerializeField, Header("Connection"), Tooltip("The rigidbody this joint ties to. Empty anchors the joint to the world.")]
    private Rigidbody2D? connectedBody;

    [SerializeField, Tooltip("Where the joint attaches to this rigidbody, in its local space (scaled with it).")]
    private Float2 anchor;

    [SerializeField, Tooltip("Work the connected anchor out from Anchor when the joint is created, so both start at the same point.")]
    private bool autoConfigureConnectedAnchor = true;

    [SerializeField, ShowIf(nameof(ManualConnectedAnchor)), Tooltip("Where the joint attaches to the connected body, in its local space. A world position when there is no connected body.")]
    private Float2 connectedAnchor;

    [SerializeField, Tooltip("Whether the two bodies still collide with each other.")]
    private bool collideConnected;

    [SerializeField, Header("Breaking"), Tooltip("The joint breaks when the force it exerts goes over this (newtons). Infinity never breaks.")]
    private float breakForce = float.PositiveInfinity;

    [SerializeField, Tooltip("The joint breaks when the torque it exerts goes over this (newton-metres). Infinity never breaks.")]
    private float breakTorque = float.PositiveInfinity;

    // Condition for [ShowIf]: the inspector looks it up by name.
    private bool ManualConnectedAnchor => !autoConfigureConnectedAnchor;

    // ---- settings ------------------------------------------------------------------------

    /// <summary>The rigidbody this joint ties to, or null to anchor it to the world. Rebuilds the joint.</summary>
    public Rigidbody2D? ConnectedBody
    {
        get => connectedBody;
        set
        {
            if (ReferenceEquals(connectedBody, value)) return;
            connectedBody = value;
            Rebuild();
        }
    }

    /// <summary>Where the joint attaches to this rigidbody, in its local space (scaled with it). Rebuilds the joint.</summary>
    public Float2 Anchor
    {
        get => anchor;
        set { anchor = value; Rebuild(); }
    }

    /// <summary>Works <see cref="ConnectedAnchor"/> out when the joint is created, so the joint starts at rest. Rebuilds the joint.</summary>
    public bool AutoConfigureConnectedAnchor
    {
        get => autoConfigureConnectedAnchor;
        set { autoConfigureConnectedAnchor = value; Rebuild(); }
    }

    /// <summary>
    /// Where the joint attaches to the connected body, in its local space; a world position with no connected body. Once the
    /// joint exists with <see cref="AutoConfigureConnectedAnchor"/> on, this holds the value that was worked out. Setting it
    /// rebuilds the joint, and has no effect on a joint that works the anchor out itself.
    /// </summary>
    public Float2 ConnectedAnchor
    {
        get => connectedAnchor;
        set { connectedAnchor = value; Rebuild(); }
    }

    /// <summary>Whether the two connected bodies still collide with each other.</summary>
    public bool CollideConnected
    {
        get => collideConnected;
        set { collideConnected = value; Apply(); }
    }

    /// <summary>The joint breaks above this force, in newtons. <see cref="float.PositiveInfinity"/> never breaks.</summary>
    public float BreakForce
    {
        get => breakForce;
        set { breakForce = MathF.Max(value, 0f); Apply(); }
    }

    /// <summary>The joint breaks above this torque, in newton-metres. <see cref="float.PositiveInfinity"/> never breaks.</summary>
    public float BreakTorque
    {
        get => breakTorque;
        set { breakTorque = MathF.Max(value, 0f); Apply(); }
    }

    // ---- what a joint type provides ------------------------------------------------------

    /// <summary>One of <see cref="PB2JointType"/>.</summary>
    private protected abstract int JointType { get; }

    /// <summary>
    /// Writes this joint's tunable values into <paramref name="def"/>: ORs its spring / limit / motor bits into
    /// <c>Flags</c> and fills the <c>P</c> slots as the table in prowl_box2d.h says. Runs when the joint is created and again
    /// whenever a value changes, so it must not depend on anything but the component's own fields.
    /// </summary>
    private protected abstract void FillTunables(ref PB2JointDef def);

    /// <summary>The angle, in radians, of the joint's axis in the connected body's space (the world's, when there is none). Slide joints use it.</summary>
    private protected virtual float FrameAngleRadians => 0f;

    /// <summary>
    /// Called while a joint is being created, before <see cref="FillTunables"/>, with the world positions of the two anchors.
    /// Lets a joint measure something from how it was placed (the distance joint's rest length).
    /// </summary>
    private protected virtual void BeforeCreate(float worldAx, float worldAy, float worldBx, float worldBy) { }

    /// <summary>Clamps the subclass's serialized values to what Box2D accepts. Runs from OnValidate.</summary>
    private protected virtual void SanitizeValues() { }

    /// <summary>
    /// Draws what is specific to the joint type (axis, limits) while selected. The first point is the anchor on this body and the
    /// second the anchor on the connected body, both in world space.
    /// </summary>
    private protected virtual void DrawDetails(float z, float ax, float ay, float bx, float by) { }

    // ---- native state --------------------------------------------------------------------

    private uint _handle;                 // the native joint, 0 while there is none
    private int _index = -1;              // this joint's slot in the simulation's registry
    private PhysicsWorld2D? _world;       // the world the native joint lives in
    private PhysicsWorld2D? _queuedOn;    // the world whose simulation is holding this joint in its pending list
    private bool _broken;

    private Rigidbody2D? _attached;       // the bodies the native joint was built on
    private Rigidbody2D? _connected;

    // What the joint was built from, so OnValidate can tell an edit that needs a rebuild from one that only needs re-applying.
    private float _builtAnchorX, _builtAnchorY, _builtConnectedX, _builtConnectedY, _builtFrameAngle;
    private bool _builtAuto;
    private Rigidbody2D? _builtConnectedBody;

    /// <summary>Whether this joint currently exists in the physics world.</summary>
    public bool IsCreated => _handle != 0;

    /// <summary>True after the joint broke (see <see cref="BreakForce"/>), until it is rebuilt.</summary>
    public bool IsBroken => _broken;

    /// <summary>The rigidbody that owns this joint: the nearest enabled one on this GameObject or above, or null.</summary>
    public Rigidbody2D? AttachedRigidbody => FindAttachedBody();

    /// <summary>The force the joint is exerting right now, in newtons. Zero while it does not exist.</summary>
    public Float2 ReactionForce => new Float2(StateValue(0), StateValue(1));

    /// <summary>The torque the joint is exerting right now, in newton-metres. Zero while it does not exist.</summary>
    public float ReactionTorque => StateValue(2);

    // Joint state readouts for the subclasses. Index meanings are those of pb2_joint_get_state.
    private protected float JointPositionValue => StateValue(3);
    private protected float JointSpeedValue => StateValue(4);
    private protected float MotorLoadValue => StateValue(5);

    private protected Rigidbody2D? AttachedBody => _attached;
    private protected Rigidbody2D? ConnectedBodyInUse => _connected;

    private static readonly float[] s_state = new float[8];    // scratch for JointGetState: joints are touched from the main thread only

    private float StateValue(int slot)
    {
        if (_handle == 0) return 0f;
        PB2.JointGetState(_handle, s_state);
        return s_state[slot];
    }

    // ---- creation ------------------------------------------------------------------------

    private Rigidbody2D? FindAttachedBody()
    {
        foreach (Rigidbody2D rb in GetComponentsInParent<Rigidbody2D>())
            if (rb.IsValid() && rb.EnabledInHierarchy) return rb;
        return null;
    }

    private static float SafeScale(float s) => MathF.Abs(s) < 1e-4f ? (s < 0f ? -1e-4f : 1e-4f) : s;

    private static float Threshold(float value) => float.IsFinite(value) && value >= 0f ? value : float.MaxValue;

    /// <summary>Writes the common tunables (collision, break thresholds), then the type's own.</summary>
    private void BuildTunables(ref PB2JointDef def)
    {
        def.flags = collideConnected ? PB2.JfCollideConnected : 0u;
        def.forceThreshold = Threshold(breakForce);
        def.torqueThreshold = Threshold(breakTorque);
        FillTunables(ref def);
    }

    /// <summary>Creates the native joint if both bodies are ready. Returns whether the joint exists afterwards.</summary>
    private bool TryCreate()
    {
        if (_handle != 0) return true;
        if (_broken || !this.IsValid() || !EnabledInHierarchy) return false;

        Rigidbody2D? attached = FindAttachedBody();
        if (attached == null || !attached.IsSimulated) return false;

        Rigidbody2D? connected = connectedBody;
        if (connected is not null)
        {
            // Destroyed, or not simulating (disabled, or its scene does not hold the world): wait, rather than quietly
            // pinning this body to the world.
            if (!connected.IsValid() || !connected.IsSimulated) return false;
            if (ReferenceEquals(connected, attached))
            {
                Debug.LogErrorOnce("Joint2D.SelfConnected", "[Physics2D] A Joint2D cannot connect a rigidbody to itself, so it stays inert.");
                return false;
            }
            if (!ReferenceEquals(connected.World, attached.World))
            {
                Debug.LogErrorOnce("Joint2D.OtherWorld", "[Physics2D] A Joint2D cannot connect rigidbodies of different scenes, so it stays inert.");
                return false;
            }
        }

        return CreateNative(attached, connected);
    }

    private bool CreateNative(Rigidbody2D attached, Rigidbody2D? connected)
    {
        PhysicsWorld2D world = attached.World!;
        PhysicsSimulation2D sim = world.Simulation;

        // The simulated poses, not the Transforms: those are interpolated behind the simulation, and the native joint has to be
        // built from where the bodies really are.
        Float2 pB = attached.Position;
        float angleB = attached.Rotation * Maths.Deg2Rad;
        Float3 scaleB = attached.Transform.LossyScale;
        float anchorBx = anchor.X * SafeScale(scaleB.X), anchorBy = anchor.Y * SafeScale(scaleB.Y); // in body B's space
        float cB = MathF.Cos(angleB), sB = MathF.Sin(angleB);
        float worldBx = pB.X + cB * anchorBx - sB * anchorBy;                                       // in the world
        float worldBy = pB.Y + sB * anchorBx + cB * anchorBy;

        uint bodyA = 0;
        int indexA = -1;
        float angleA = 0f;
        float anchorAx, anchorAy;     // in body A's space, or the world's when there is no body A
        float worldAx, worldAy;
        if (connected is not null)
        {
            Float2 pA = connected.Position;
            angleA = connected.Rotation * Maths.Deg2Rad;
            Float3 scaleA = connected.Transform.LossyScale;
            float sxA = SafeScale(scaleA.X), syA = SafeScale(scaleA.Y);
            float cA = MathF.Cos(angleA), sA = MathF.Sin(angleA);
            if (autoConfigureConnectedAnchor)
            {
                float dx = worldBx - pA.X, dy = worldBy - pA.Y;
                anchorAx = cA * dx + sA * dy;                  // rotate by -angleA
                anchorAy = -sA * dx + cA * dy;
                connectedAnchor = new Float2(anchorAx / sxA, anchorAy / syA);
            }
            else
            {
                anchorAx = connectedAnchor.X * sxA;
                anchorAy = connectedAnchor.Y * syA;
            }
            worldAx = pA.X + cA * anchorAx - sA * anchorAy;
            worldAy = pA.Y + sA * anchorAx + cA * anchorAy;
            bodyA = connected.Handle;
            indexA = connected.SimIndex;
        }
        else
        {
            if (autoConfigureConnectedAnchor)
            {
                anchorAx = worldBx;
                anchorAy = worldBy;
                connectedAnchor = new Float2(worldBx, worldBy);
            }
            else
            {
                anchorAx = connectedAnchor.X;
                anchorAy = connectedAnchor.Y;
            }
            worldAx = anchorAx;
            worldAy = anchorAy;
        }

        BeforeCreate(worldAx, worldAy, worldBx, worldBy);

        // Both frames share the axis angle, and frame B is turned by however far the bodies are already rotated apart, so
        // the two frames coincide in the world and the joint's angle starts at zero.
        float frameAngle = FrameAngleRadians;
        var def = new PB2JointDef
        {
            bodyA = bodyA,
            bodyB = attached.Handle,
            type = JointType,
            ax = anchorAx, ay = anchorAy, aAngle = frameAngle,
            bx = anchorBx, by = anchorBy, bAngle = Angle2D.WrapPi(frameAngle + angleA - angleB),
        };
        BuildTunables(ref def);

        int index = sim.RegisterJoint(this, indexA, attached.SimIndex);
        if (index < 0)
        {
            PhysicsWorld2D.ReportUnavailable();
            return false;
        }
        def.jointIndex = index;

        uint handle = PB2.JointCreate(ref def);
        if (handle == 0)
        {
            sim.UnregisterJoint(index);
            Debug.LogErrorOnce("Joint2D.CreateFailed", "[Physics2D] The physics world refused to create a Joint2D (are both rigidbodies alive and distinct?).");
            return false;
        }

        _handle = handle;
        _index = index;
        _world = world;
        _attached = attached;
        _connected = connected;
        _builtAnchorX = anchor.X; _builtAnchorY = anchor.Y;
        _builtConnectedX = connectedAnchor.X; _builtConnectedY = connectedAnchor.Y;
        _builtAuto = autoConfigureConnectedAnchor;
        _builtFrameAngle = frameAngle;
        _builtConnectedBody = connectedBody;

        Unqueue();
        return true;
    }

    private void DestroyNative()
    {
        if (_handle != 0 && _world != null && _world.Simulation.IsAcquired)
        {
            PB2.JointDestroy(_handle); // harmless if a body already took it along
            _world.Simulation.UnregisterJoint(_index);
        }
        ClearNative();
    }

    private void ClearNative()
    {
        _handle = 0;
        _index = -1;
        _world = null;
        _attached = null;
        _connected = null;
    }

    private void Queue()
    {
        var scene = GameObject.IsValid() ? GameObject.Scene : null;
        if (!scene.IsValid()) return;
        PhysicsWorld2D world = scene.Physics2D;
        world.Simulation.QueueJoint(this);
        _queuedOn = world;
    }

    private void Unqueue()
    {
        _queuedOn?.Simulation.DequeueJoint(this);
        _queuedOn = null;
    }

    /// <summary>
    /// Destroys the joint and builds it again from the bodies' current poses, which also clears <see cref="IsBroken"/>. Needed after
    /// rescaling a body at runtime; the setters of everything that cannot change in place call it for you.
    /// </summary>
    [Button("Rebuild Joint")]
    public void Rebuild()
    {
        AssertOwner();
        DestroyNative();
        _broken = false;
        if (!EnabledInHierarchy) return;
        Unqueue();
        if (!TryCreate()) Queue();
    }

    /// <summary>Pushes the current tunable values into the live joint, and wakes the bodies so the change is felt.</summary>
    private protected void Apply()
    {
        AssertOwner();
        if (_handle == 0) return;
        var def = new PB2JointDef { type = JointType };
        BuildTunables(ref def);
        PB2.JointApply(_handle, ref def);
    }

    // ---- simulation callbacks ------------------------------------------------------------

    void IJoint2DHost.OnNativeJointLost()
    {
        ClearNative(); // the joint died with a body, and the simulation has already unregistered it
        if (!_broken && this.IsValid() && EnabledInHierarchy) Queue();
    }

    bool IJoint2DHost.TryCreateJoint()
    {
        if (!this.IsValid()) return true; // gone: stop being offered
        return TryCreate();
    }

    void IJoint2DHost.OnBreak()
    {
        if (_handle == 0) return;
        DestroyNative();
        _broken = true;
        SceneDispatcher.JointBreak2D(GameObject, this);
    }

    // ---- lifecycle -----------------------------------------------------------------------

    public override void OnEnable()
    {
        _broken = false;
        if (!TryCreate()) Queue(); // a body may not exist yet: the simulation offers the joint a chance every step
    }

    public override void OnDisable()
    {
        Unqueue();
        DestroyNative();
    }

    public override void OnValidate()
    {
        breakForce = MathF.Max(breakForce, 0f);
        breakTorque = MathF.Max(breakTorque, 0f);
        SanitizeValues();

        if (_handle == 0) return;

        bool framesChanged = !ReferenceEquals(_builtConnectedBody, connectedBody)
                             || anchor.X != _builtAnchorX || anchor.Y != _builtAnchorY
                             || autoConfigureConnectedAnchor != _builtAuto
                             || (!autoConfigureConnectedAnchor && (connectedAnchor.X != _builtConnectedX || connectedAnchor.Y != _builtConnectedY))
                             || FrameAngleRadians != _builtFrameAngle;
        if (framesChanged) Rebuild();
        else Apply();
    }

    // ---- editor gizmos -------------------------------------------------------------------
    //
    // Gizmos run in the editor whether or not anything is playing, so none of this touches the native world: the anchors come
    // from the bodies' Transforms.

    private static void DrawCross(float x, float y, float z, float size, Color color)
    {
        Debug.DrawLine(new Float3(x - size, y, z), new Float3(x + size, y, z), color);
        Debug.DrawLine(new Float3(x, y - size, z), new Float3(x, y + size, z), color);
    }

    /// <summary>The world-space anchors, as the gizmo sees them. Returns false when there is no rigidbody to attach to.</summary>
    private bool GizmoAnchors(out float z, out float ax, out float ay, out float bx, out float by)
    {
        z = ax = ay = bx = by = 0f;
        Rigidbody2D? rb = FindAttachedBody();
        if (!rb.IsValid()) return false;

        Float3 a = rb.Transform.TransformPoint(new Float3(anchor.X, anchor.Y, 0f));
        z = a.Z; ax = a.X; ay = a.Y;

        // Until the joint has measured it, an automatic connected anchor is simply this anchor.
        if (autoConfigureConnectedAnchor && _handle == 0) { bx = ax; by = ay; return true; }

        Rigidbody2D? other = connectedBody;
        if (other is not null)
        {
            if (!other.IsValid()) { bx = ax; by = ay; return true; }
            Float3 b = other.Transform.TransformPoint(new Float3(connectedAnchor.X, connectedAnchor.Y, 0f));
            bx = b.X; by = b.Y;
        }
        else
        {
            bx = connectedAnchor.X; by = connectedAnchor.Y;
        }
        return true;
    }

    /// <summary>Cyan anchor on this body, white on the connected side, joined by a dashed line; red once the joint has broken.</summary>
    public override void DrawGizmos()
    {
        if (!GizmoAnchors(out float z, out float ax, out float ay, out float bx, out float by)) return;

        Color own = _broken ? Color.Red : Color.Cyan;
        DrawCross(ax, ay, z, 0.08f, own);
        DrawCross(bx, by, z, 0.06f, _broken ? Color.Red : Color.White);
        if (ax != bx || ay != by)
            Debug.DrawDashedLine(new Float3(ax, ay, z), new Float3(bx, by, z), own);
    }

    public override void DrawGizmosSelected()
    {
        if (!GizmoAnchors(out float z, out float ax, out float ay, out float bx, out float by)) return;
        DrawDetails(z, ax, ay, bx, by);
    }

    /// <summary>World rotation in radians of the connected body, or 0 for the world.</summary>
    private protected float ConnectedWorldAngle()
    {
        Rigidbody2D? other = connectedBody;
        if (other is null || !other.IsValid()) return 0f;
        Quaternion q = other.Transform.Rotation;
        return Angle2D.FromQuaternionZ(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>World rotation in radians of the rigidbody that owns this joint, or 0 if there is none.</summary>
    private protected float AttachedWorldAngle()
    {
        Rigidbody2D? rb = FindAttachedBody();
        if (!rb.IsValid()) return 0f;
        Quaternion q = rb.Transform.Rotation;
        return Angle2D.FromQuaternionZ(q.X, q.Y, q.Z, q.W);
    }

    private protected static void DrawLine(float z, float x1, float y1, float x2, float y2, Color color)
        => Debug.DrawLine(new Float3(x1, y1, z), new Float3(x2, y2, z), color);
}
