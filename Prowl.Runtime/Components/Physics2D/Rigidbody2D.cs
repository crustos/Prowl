// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Prowl.Echo;
using Prowl.Runtime.Physics2D;
using Prowl.Runtime.Physics2D.Native;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// A 2D physics body (Box2D-Packed). Position is X/Y of the Transform and rotation is its Z axis; Z position is left alone.
/// Angles are in <b>degrees</b> (as <c>Transform.EulerAngles</c> is) and so is <see cref="AngularVelocity"/>.
/// <para/>
/// While the body is awake its Transform follows the simulation, interpolated between fixed steps when
/// <see cref="Interpolation"/> is on. Editing the Transform yourself teleports the body.
/// </summary>
[AddComponentMenu("Physics 2D/Rigidbody 2D")]
[ComponentIcon("\uf1b2")] // Cube
public sealed class Rigidbody2D : MonoBehaviour, IBody2DHost
{
    private const float MinMass = 0.001f;

    [SerializeField, Header("Body"), Tooltip("Static never moves. Kinematic moves only when told to (velocity or MovePosition) and is not pushed by others. Dynamic is fully simulated.")]
    private BodyType2D bodyType = BodyType2D.Dynamic;

    [SerializeField, ShowIf(nameof(IsDynamic)), Tooltip("Mass in kilograms. Overrides what the colliders' density would give.")]
    private float mass = 1f;

    [SerializeField, ShowIf(nameof(IsDynamic)), Tooltip("Multiplier on the world's gravity. 0 floats; negative rises.")]
    private float gravityScale = 1f;

    [SerializeField, ShowIf(nameof(IsDynamic)), Tooltip("Drag on movement. 0 never slows down on its own.")]
    private float linearDamping = 0f;

    [SerializeField, ShowIf(nameof(IsDynamic)), Tooltip("Drag on rotation. 0 never stops spinning on its own.")]
    private float angularDamping = 0.05f;

    [SerializeField, Header("Constraints"), ShowIf(nameof(IsDynamic)), Tooltip("Motion this body is locked out of. Frozen axes still move if you set the Transform or call MovePosition.")]
    private RigidbodyConstraints2D constraints = RigidbodyConstraints2D.None;

    [SerializeField, Header("Simulation"), ShowIf(nameof(IsDynamic)), Tooltip("Continuous collision detection against other bodies, for small fast objects that would otherwise tunnel through thin geometry. Costs more.")]
    private bool isBullet;

    [SerializeField, ShowIf(nameof(IsNotStatic)), Tooltip("Whether the body may fall asleep once it comes to rest. Sleeping bodies cost almost nothing; turn this off only if something must never stop being simulated.")]
    private bool canSleep = true;

    [SerializeField, ShowIf(nameof(IsNotStatic)), Tooltip("How the Transform is drawn between fixed steps. Interpolate is smooth; None snaps to the last step. Extrapolate is not implemented for 2D yet and behaves as None.")]
    private RigidbodyInterpolation interpolation = RigidbodyInterpolation.Interpolate;

    // Conditions for [ShowIf]: the inspector looks these up by name.
    private bool IsDynamic => bodyType == BodyType2D.Dynamic;
    private bool IsNotStatic => bodyType != BodyType2D.Static;

    // ---- native state --------------------------------------------------------------------

    private uint _handle;                 // the native body, 0 while there is none
    private int _index = -1;              // this body's slot in the simulation's registry
    private PhysicsWorld2D? _world;
    private BodyPose2D _pose;

    // The pose last written into the Transform. Lets us tell "the user moved the Transform" from "only its scale or
    // a parent changed", and skip dirtying the Transform every frame for a body that is at rest.
    private float _writtenX, _writtenY, _writtenAngle;
    private bool _hasWritten;
    private uint _lastSyncedVersion;

    // A pending kinematic MovePosition / MoveRotation target, valid for the step it was set in.
    private float _targetX, _targetY, _targetAngle;
    private int _targetStep = -1;

    internal int SyncIndex = -1;          // slot in PhysicsWorld2D's sync list

    internal uint Handle => _handle;
    internal PhysicsWorld2D? World => _world;

    /// <summary>Whether this rigidbody currently has a body in the physics world.</summary>
    [MemberNotNullWhen(true, nameof(_world))]
    public bool IsSimulated => _handle != 0 && _world != null;

    // ---- settings ------------------------------------------------------------------------

    public BodyType2D BodyType
    {
        get => bodyType;
        set
        {
            AssertOwner();
            bodyType = value;
            if (!IsSimulated) return;
            PB2.BodySetType(_handle, (int)value);
            ApplyMass();
        }
    }

    /// <summary>Mass in kilograms. Overrides what the colliders' density would give. Dynamic bodies only.</summary>
    public float Mass
    {
        get => mass;
        set
        {
            AssertOwner();
            mass = MathF.Max(value, MinMass);
            ApplyMass();
        }
    }

    public float GravityScale
    {
        get => gravityScale;
        set
        {
            AssertOwner();
            gravityScale = value;
            if (IsSimulated) PB2.BodySetGravityScale(_handle, value);
        }
    }

    public float LinearDamping
    {
        get => linearDamping;
        set
        {
            AssertOwner();
            linearDamping = MathF.Max(value, 0f);
            if (IsSimulated) PB2.BodySetDamping(_handle, linearDamping, angularDamping);
        }
    }

    public float AngularDamping
    {
        get => angularDamping;
        set
        {
            AssertOwner();
            angularDamping = MathF.Max(value, 0f);
            if (IsSimulated) PB2.BodySetDamping(_handle, linearDamping, angularDamping);
        }
    }

    public RigidbodyConstraints2D Constraints
    {
        get => constraints;
        set
        {
            AssertOwner();
            constraints = value;
            if (IsSimulated) PB2.BodySetFlags(_handle, (uint)BuildFlags());
        }
    }

    /// <summary>Continuous collision detection against other bodies, for fast small objects that would tunnel.</summary>
    public bool IsBullet
    {
        get => isBullet;
        set
        {
            AssertOwner();
            isBullet = value;
            if (IsSimulated) PB2.BodySetFlags(_handle, (uint)BuildFlags());
        }
    }

    /// <summary>Whether the body may fall asleep when it comes to rest. Sleeping bodies cost almost nothing.</summary>
    public bool CanSleep
    {
        get => canSleep;
        set
        {
            AssertOwner();
            canSleep = value;
            if (IsSimulated) PB2.BodySetFlags(_handle, (uint)BuildFlags());
        }
    }

    /// <summary>
    /// How the Transform is drawn between fixed steps. <see cref="RigidbodyInterpolation.Extrapolate"/> is not
    /// implemented for 2D yet and behaves as <see cref="RigidbodyInterpolation.None"/>.
    /// </summary>
    public RigidbodyInterpolation Interpolation
    {
        get => interpolation;
        set
        {
            AssertOwner();
            interpolation = value;
            ResetPose();
        }
    }

    private PB2BodyFlags BuildFlags()
    {
        PB2BodyFlags f = PB2BodyFlags.None;
        if (isBullet) f |= PB2BodyFlags.Bullet;
        if (!canSleep) f |= PB2BodyFlags.NoSleep;
        if ((constraints & RigidbodyConstraints2D.FreezePositionX) != 0) f |= PB2BodyFlags.LockX;
        if ((constraints & RigidbodyConstraints2D.FreezePositionY) != 0) f |= PB2BodyFlags.LockY;
        if ((constraints & RigidbodyConstraints2D.FreezeRotation) != 0) f |= PB2BodyFlags.LockRotation;
        return f;
    }

    // ---- state ---------------------------------------------------------------------------

    /// <summary>
    /// The body's simulated position. This is the true simulation state, which can be a fixed step ahead of what the
    /// (interpolated) Transform shows. Setting it teleports the body, keeping its velocity.
    /// </summary>
    public Float2 Position
    {
        get => IsSimulated ? new Float2(_pose.CurX, _pose.CurY) : TransformXY();
        set
        {
            AssertOwner();
            TeleportRadians(value, IsSimulated ? _pose.CurAngle : TransformAngle());
        }
    }

    /// <summary>The body's simulated rotation in degrees, continuous (it can exceed 360 for a spinning body). Setting it teleports.</summary>
    public float Rotation
    {
        get => (IsSimulated ? _pose.CurAngle : TransformAngle()) * Maths.Rad2Deg;
        set
        {
            AssertOwner();
            TeleportRadians(IsSimulated ? new Float2(_pose.CurX, _pose.CurY) : TransformXY(), value * Maths.Deg2Rad);
        }
    }

    public Float2 LinearVelocity
    {
        get
        {
            if (!IsSimulated) return Float2.Zero;
            RefreshState();
            return new Float2(_stVx, _stVy);
        }
        set
        {
            AssertOwner();
            if (!IsSimulated) return;
            RefreshState();
            PB2.BodySetVelocity(_handle, value.X, value.Y, _stW);
        }
    }

    /// <summary>Spin in degrees per second, counter-clockwise positive.</summary>
    public float AngularVelocity
    {
        get
        {
            if (!IsSimulated) return 0f;
            RefreshState();
            return _stW * Maths.Rad2Deg;
        }
        set
        {
            AssertOwner();
            if (!IsSimulated) return;
            RefreshState();
            PB2.BodySetVelocity(_handle, _stVx, _stVy, value * Maths.Deg2Rad);
        }
    }

    public bool IsAwake
    {
        get
        {
            if (!IsSimulated) return false;
            RefreshState();
            return _stAwake;
        }
    }

    // The last state read from the native body. Filled by RefreshState; fields rather than out parameters (and not a struct return)
    // because the Crust subset has neither inline `out` declarations nor discards.
    private float _stVx, _stVy, _stW, _stInertia;
    private bool _stAwake;

    private unsafe void RefreshState()
    {
        float* s = stackalloc float[10];
        PB2.BodyGetState(_handle, s);
        _stVx = s[4]; _stVy = s[5]; _stW = s[6]; _stInertia = s[8]; _stAwake = s[9] > 0.5f;
    }

    // ---- Transform <-> simulation --------------------------------------------------------

    private Float2 TransformXY() { Float3 p = Transform.Position; return new Float2(p.X, p.Y); }

    private float TransformAngle()
    {
        Quaternion q = Transform.Rotation;
        return Angle2D.FromQuaternionZ(q.X, q.Y, q.Z, q.W);
    }

    private void WriteTransform(float x, float y, float angle)
    {
        Float3 p = Transform.Position;
        Transform.Position = new Float3(x, y, p.Z); // Z is the drawing depth, not physics
        float z, w;
        Angle2D.ToQuaternionZ(angle, out z, out w);
        Transform.Rotation = new Quaternion(0f, 0f, z, w);
        _writtenX = x; _writtenY = y; _writtenAngle = angle;
        _hasWritten = true;
        _lastSyncedVersion = Transform.Version; // our own write is not a user edit
    }

    void IBody2DHost.OnMoved(float x, float y, float cos, float sin, bool fellAsleep)
        => _pose.Push(x, y, cos, sin, _world!.Simulation.StepIndex);

    /// <summary>
    /// Renders the Transform from the simulation. A body that did not move this step is left exactly where it is, so
    /// a field of resting bodies does not dirty a single Transform.
    /// </summary>
    public override void Update()
    {
        if (!IsSimulated || bodyType == BodyType2D.Static) return;

        float alpha = interpolation == RigidbodyInterpolation.Interpolate ? Time.FixedAlpha : 1f;
        float x, y, angle;
        _pose.Sample(alpha, _world.Simulation.StepIndex, out x, out y, out angle);

        if (_hasWritten && x == _writtenX && y == _writtenY && angle == _writtenAngle) return;
        WriteTransform(x, y, angle);
    }

    /// <summary>
    /// Pushes a Transform edit made by game code into the body. Called by the world once per fixed step, before it
    /// steps; the actual native move is batched with every other body's.
    /// </summary>
    internal void SyncTransformToBody()
    {
        if (!IsSimulated) return;
        uint version = Transform.Version;
        if (version == _lastSyncedVersion) return;
        _lastSyncedVersion = version;

        float x = Transform.Position.X, y = Transform.Position.Y, angle = TransformAngle();

        // Compare with what we last wrote (or the simulated pose if we never wrote one): the Transform changing
        // because of its scale or a parent is not a request to move, and must not rewind an interpolated body.
        float refX = _hasWritten ? _writtenX : _pose.CurX;
        float refY = _hasWritten ? _writtenY : _pose.CurY;
        float refA = _hasWritten ? _writtenAngle : _pose.CurAngle;
        if (x == refX && y == refY && MathF.Abs(Angle2D.WrapPi(angle - refA)) < 1e-6f) return;

        _world.Simulation.World.QueueTransform(_handle, x, y, angle, false);
        _pose.Reset(x, y, angle, _world.Simulation.StepIndex);
        _writtenX = x; _writtenY = y; _writtenAngle = angle;
        _hasWritten = true;
    }

    /// <summary>Moves the body to a pose immediately without disturbing its velocity, and skips interpolation across the jump.</summary>
    public void Teleport(Float2 position, float rotationDegrees) => TeleportRadians(position, rotationDegrees * Maths.Deg2Rad);

    private void TeleportRadians(Float2 position, float angleRadians)
    {
        if (!IsSimulated) { Transform.Position = new Float3(position.X, position.Y, Transform.Position.Z); return; }

        PB2.BodySetTransform(_handle, position.X, position.Y, angleRadians);
        PB2.BodySetAwake(_handle, 1);
        _pose.Reset(position.X, position.Y, angleRadians, _world.Simulation.StepIndex);
        WriteTransform(position.X, position.Y, angleRadians);
    }

    private void ResetPose()
    {
        if (!IsSimulated) return;
        _pose.Reset(_pose.CurX, _pose.CurY, _pose.CurAngle, _world.Simulation.StepIndex);
    }

    /// <summary>
    /// Moves toward a position. A <see cref="BodyType2D.Kinematic"/> body glides there over the next step with the
    /// velocity needed, so it carries or pushes bodies resting on it. Any other body type teleports.
    /// </summary>
    public void MovePosition(Float2 position)
    {
        AssertOwner();
        if (!IsSimulated) return;
        if (bodyType != BodyType2D.Kinematic) { TeleportRadians(position, _pose.CurAngle); return; }

        BeginTarget();
        _targetX = position.X; _targetY = position.Y;
        _world.Simulation.World.QueueTransform(_handle, _targetX, _targetY, _targetAngle, true);
    }

    /// <summary>Turns toward an angle in degrees; see <see cref="MovePosition"/> for how each body type treats it.</summary>
    public void MoveRotation(float degrees)
    {
        AssertOwner();
        if (!IsSimulated) return;
        float radians = degrees * Maths.Deg2Rad;
        if (bodyType != BodyType2D.Kinematic) { TeleportRadians(new Float2(_pose.CurX, _pose.CurY), radians); return; }

        BeginTarget();
        _targetAngle = radians;
        _world.Simulation.World.QueueTransform(_handle, _targetX, _targetY, _targetAngle, true);
    }

    // A MovePosition followed by a MoveRotation in the same step must produce one combined target, not two that
    // each carry a stale half of the other.
    private void BeginTarget()
    {
        int step = _world!.Simulation.StepIndex;
        if (_targetStep == step) return;
        _targetStep = step;
        _targetX = _pose.CurX; _targetY = _pose.CurY; _targetAngle = _pose.CurAngle;
    }

    // ---- forces --------------------------------------------------------------------------

    /// <summary>Applies a force through the centre of mass. See <see cref="ForceMode"/>. Dynamic bodies only.</summary>
    public void AddForce(Float2 force) { AddForce(force, ForceMode.Force); }

    public void AddForce(Float2 force, ForceMode mode)
    {
        AssertOwner();
        if (!IsSimulated || bodyType != BodyType2D.Dynamic) return;
        switch (mode)
        {
            case ForceMode.Force: PB2.BodyApplyForce(_handle, force.X, force.Y, 0, 0, 0); break;
            case ForceMode.Acceleration: PB2.BodyApplyForce(_handle, force.X * mass, force.Y * mass, 0, 0, 0); break;
            case ForceMode.Impulse: PB2.BodyApplyImpulse(_handle, force.X, force.Y, 0, 0, 0); break;
            case ForceMode.VelocityChange: PB2.BodyApplyImpulse(_handle, force.X * mass, force.Y * mass, 0, 0, 0); break;
        }
    }

    /// <summary>Applies a force at a world-space point, which also spins the body.</summary>
    public void AddForceAtPosition(Float2 force, Float2 worldPosition) { AddForceAtPosition(force, worldPosition, ForceMode.Force); }

    public void AddForceAtPosition(Float2 force, Float2 worldPosition, ForceMode mode)
    {
        AssertOwner();
        if (!IsSimulated || bodyType != BodyType2D.Dynamic) return;
        switch (mode)
        {
            case ForceMode.Force: PB2.BodyApplyForce(_handle, force.X, force.Y, 1, worldPosition.X, worldPosition.Y); break;
            case ForceMode.Acceleration: PB2.BodyApplyForce(_handle, force.X * mass, force.Y * mass, 1, worldPosition.X, worldPosition.Y); break;
            case ForceMode.Impulse: PB2.BodyApplyImpulse(_handle, force.X, force.Y, 1, worldPosition.X, worldPosition.Y); break;
            case ForceMode.VelocityChange: PB2.BodyApplyImpulse(_handle, force.X * mass, force.Y * mass, 1, worldPosition.X, worldPosition.Y); break;
        }
    }

    /// <summary>
    /// Applies a torque about Z. <see cref="ForceMode.Force"/> and <see cref="ForceMode.Impulse"/> take torque and angular
    /// impulse in physical units; <see cref="ForceMode.Acceleration"/> and <see cref="ForceMode.VelocityChange"/> take degrees per
    /// second squared and per second, and ignore the body's inertia.
    /// </summary>
    public void AddTorque(float torque) { AddTorque(torque, ForceMode.Force); }

    public void AddTorque(float torque, ForceMode mode)
    {
        AssertOwner();
        if (!IsSimulated || bodyType != BodyType2D.Dynamic) return;
        RefreshState();
        float inertia = _stInertia;
        switch (mode)
        {
            case ForceMode.Force: PB2.BodyApplyTorque(_handle, torque, 0); break;
            case ForceMode.Acceleration: PB2.BodyApplyTorque(_handle, torque * Maths.Deg2Rad * inertia, 0); break;
            case ForceMode.Impulse: PB2.BodyApplyTorque(_handle, torque, 1); break;
            case ForceMode.VelocityChange: PB2.BodyApplyTorque(_handle, torque * Maths.Deg2Rad * inertia, 1); break;
        }
    }

    public void WakeUp() { AssertOwner(); if (IsSimulated) PB2.BodySetAwake(_handle, 1); }

    public void Sleep() { AssertOwner(); if (IsSimulated) PB2.BodySetAwake(_handle, 0); }

    // ---- lifecycle -----------------------------------------------------------------------

    internal void ApplyMass()
    {
        if (IsSimulated && bodyType == BodyType2D.Dynamic) PB2.BodySetMass(_handle, mass);
    }

    /// <summary>Creates the native body if it does not exist yet. Game code may touch a body in the frame it is added, before OnEnable.</summary>
    private bool EnsureBody()
    {
        if (IsSimulated) return true;
        var scene = GameObject.IsValid() ? GameObject.Scene : null;
        return scene.IsValid() && CreateBody(scene.Physics2D);
    }

    private bool CreateBody(PhysicsWorld2D world)
    {
        int index = world.Simulation.RegisterBody(this);
        if (index < 0)
        {
            PhysicsWorld2D.ReportUnavailable();
            return false;
        }

        Float2 p = TransformXY();
        float angle = TransformAngle();
        _handle = PB2.BodyCreate((int)bodyType, p.X, p.Y, angle, index, gravityScale, linearDamping, angularDamping, (uint)BuildFlags());
        _index = index;
        _world = world;
        _pose.Reset(p.X, p.Y, angle, world.Simulation.StepIndex);
        _hasWritten = false;
        _targetStep = -1;
        _lastSyncedVersion = Transform.Version; // the initial pose is already in the body
        world.RegisterRigidbody(this);
        return true;
    }

    private void DestroyBody()
    {
        if (_world == null) return;
        _world.UnregisterRigidbody(this);
        _world.Simulation.UnregisterBody(_index);
        PB2.BodyDestroy(_handle); // takes any shapes still on it along
        _handle = 0;
        _index = -1;
        _world = null;
    }

    public override void OnEnable()
    {
        if (!EnsureBody()) return;
        ClaimChildColliders();
    }

    /// <summary>Claims every collider here and below that is not already claimed by a nested rigidbody.</summary>
    private void ClaimChildColliders()
    {
        if (!IsSimulated) return;
        foreach (Collider2D collider in GetComponentsInChildren<Collider2D>())
            if (collider.IsValid() && ReferenceEquals(collider.FindOwningRigidbody(), this))
                collider.TryAttachTo(this);
    }

    public override void OnDisable()
    {
        if (!IsSimulated) return;

        // Take the colliders off while the body is alive so their shapes are removed cleanly...
        var colliders = new List<Collider2D>(GetComponentsInChildren<Collider2D>());
        foreach (Collider2D collider in colliders)
            if (collider.IsValid()) collider.Detach();

        DestroyBody();

        // ...then let them find an outer rigidbody, or fall back to a static body of their own.
        foreach (Collider2D collider in colliders)
            if (collider.IsValid() && collider.EnabledInHierarchy) collider.Reattach();
    }

    /// <summary>Body origin and, while simulating, where the current velocity would take it in a quarter of a second.</summary>
    public override void DrawGizmosSelected()
    {
        Float3 p = Transform.Position;
        const float size = 0.15f;
        Debug.DrawLine(new Float3(p.X - size, p.Y, p.Z), new Float3(p.X + size, p.Y, p.Z), Color.Cyan);
        Debug.DrawLine(new Float3(p.X, p.Y - size, p.Z), new Float3(p.X, p.Y + size, p.Z), Color.Cyan);

        if (IsSimulated && bodyType != BodyType2D.Static)
        {
            Float2 v = LinearVelocity;
            Debug.DrawLine(p, new Float3(p.X + v.X * 0.25f, p.Y + v.Y * 0.25f, p.Z), Color.Cyan);
        }
    }

    public override void OnValidate()
    {
        mass = MathF.Max(mass, MinMass);
        linearDamping = MathF.Max(linearDamping, 0f);
        angularDamping = MathF.Max(angularDamping, 0f);

        if (!IsSimulated) return;
        PB2.BodySetType(_handle, (int)bodyType);
        PB2.BodySetGravityScale(_handle, gravityScale);
        PB2.BodySetDamping(_handle, linearDamping, angularDamping);
        PB2.BodySetFlags(_handle, (uint)BuildFlags());
        ApplyMass();
    }
}
