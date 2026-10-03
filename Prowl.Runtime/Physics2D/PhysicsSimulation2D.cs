// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace Prowl.Runtime.Physics2D;

/// <summary>Identity marker for a collider. The simulation only ever hands these back; it never looks inside.</summary>
internal interface ICollider2DHost { }

/// <summary>
/// Implemented by whatever owns a native joint (the Joint2D component). A joint dies natively with either of its
/// bodies, so the simulation tells its host when that happens and keeps asking it to come back until it can.
/// </summary>
internal interface IJoint2DHost
{
    /// <summary>
    /// The native joint no longer exists because a body it connected was destroyed. The host must forget its native handle.
    /// The simulation has already unregistered the joint; the host is expected to queue itself to be recreated.
    /// </summary>
    void OnNativeJointLost();

    /// <summary>
    /// Offered once per step while the host has no native joint, so it can create one as soon as both bodies exist.
    /// Returns true when it now has one (and stops being offered).
    /// </summary>
    bool TryCreateJoint();

    /// <summary>The joint's force or torque went over its threshold. The host is expected to destroy the joint.</summary>
    void OnBreak();
}

internal readonly struct Contact2D
{
    public readonly float X, Y;      // world contact point
    public readonly float NX, NY;    // normal pointing from "self" toward "other"
    public readonly float Impulse;

    public Contact2D(float x, float y, float nx, float ny, float impulse)
    {
        X = x; Y = y; NX = nx; NY = ny; Impulse = impulse;
    }
}

/// <summary>
/// Receives the simulation's events. Every pair event is delivered twice, once from each side (Unity semantics):
/// <c>self</c> is the collider whose GameObject should hear about it, <c>other</c> is what it touched.
/// Implementations must be exception-safe: an exception aborts the rest of the step's events.
/// </summary>
internal interface IPhysics2DEvents
{
    void CollisionBegin(ICollider2DHost self, ICollider2DHost other, Contact2D contact);
    void CollisionEnd(ICollider2DHost self, ICollider2DHost other);
    void TriggerEnter(ICollider2DHost self, ICollider2DHost other);
    void TriggerStay(ICollider2DHost self, ICollider2DHost other);
    void TriggerExit(ICollider2DHost self, ICollider2DHost other);
}

internal readonly struct RayHit2D
{
    public readonly ICollider2DHost Collider;
    public readonly float X, Y, NX, NY;
    public readonly float Fraction;
    public readonly float Distance;

    public RayHit2D(ICollider2DHost collider, float x, float y, float nx, float ny, float fraction, float distance)
    {
        Collider = collider; X = x; Y = y; NX = nx; NY = ny; Fraction = fraction; Distance = distance;
    }
}

/// <summary>
/// The engine-layer face of the 2D simulation: what cannot be translated to C. The simulation itself (the native world, the step, the registry
/// of bodies and joints and the arena records, the active trigger pairs, the queries, and the step's events) is <see cref="SimCore2D"/>, which has
/// no objects in it and runs as C. This class adds the four things that need objects:
/// <list type="bullet">
/// <item>the <b>host tables</b>: the managed collider and joint objects to call, by the same indices the core uses;</item>
/// <item>the <b>dispatch loop</b>: a bare <c>switch</c> over the core's event stream, calling the handlers between two reads of it;</item>
/// <item>the queue of joints waiting for their bodies (the components that will create them);</item>
/// <item>the <b>ownership</b> of the one native world among scenes.</item>
/// </list>
/// <para/>
/// <b>Ownership.</b> The native Box2D-Packed world is a process-wide singleton. The first simulation that registers
/// something acquires it and keeps it while it has live objects. A simulation that has gone idle (no bodies, no
/// colliders) is silently evicted when another one needs the world, which is exactly what a scene swap looks like:
/// the outgoing scene is torn down, then the incoming one enables. A second simulation that registers while the
/// first is still busy (a preview scene next to the running game) is refused; its registrations return -1 and
/// its components stay inert instead of corrupting the live world.
/// </summary>
internal sealed class PhysicsSimulation2D
{
    private static PhysicsSimulation2D? s_owner;

    private readonly IPhysics2DEvents _events;
    private readonly SimCore2D _core = new();
    private readonly HostTable<ICollider2DHost> _colliderHosts = new();
    private readonly HostTable<IJoint2DHost> _jointHosts = new();
    private readonly List<int> _lostJoints = new();
    private readonly List<IJoint2DHost> _pendingJoints = new();
    private readonly uint[] _layerMatrix = new uint[Prowl.Native.Box2D.PB2.LayerCount];

    public PhysicsSimulation2D(IPhysics2DEvents events)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        Array.Fill(_layerMatrix, uint.MaxValue);
    }

    // ---- state ---------------------------------------------------------------------------

    public bool IsAcquired => _core.HasWorld;
    public bool InStep => _core.InStep;

    /// <summary>Index of the most recently started step. Poses are stamped with it (see <see cref="BodyPose2D"/>).</summary>
    public int StepIndex => _core.StepIndex;

    public int BodyCount => _core.Registry.BodyCount;
    public int ColliderCount => _core.Registry.ColliderCount;
    public int JointCount => _core.Registry.JointCount;

    /// <summary>No live bodies or colliders, so the native world could be handed to someone else.</summary>
    public bool IsIdle => _core.IsIdle;

    public float GravityX => _core.GravityX;
    public float GravityY => _core.GravityY;

    public void SetGravity(float x, float y) => _core.SetGravity(x, y);

    /// <summary>Takes effect the next time the native world is created.</summary>
    public int WorkerCount
    {
        get => _core.WorkerCount;
        set => _core.SetWorkerCount(value);
    }

    public void SetLayerMatrix(ReadOnlySpan<uint> rows)
    {
        if (rows.Length != Prowl.Native.Box2D.PB2.LayerCount) throw new ArgumentException($"Expected {Prowl.Native.Box2D.PB2.LayerCount} rows.", nameof(rows));
        rows.CopyTo(_layerMatrix);
        _core.SetLayerMatrix(_layerMatrix);
    }

    /// <summary>Queues a teleport (<paramref name="kinematic"/> false) or a kinematic move that reaches the pose over the next step. Flushed in one native call at the start of the step.</summary>
    public void QueueTransform(uint body, float x, float y, float angle, bool kinematic) => _core.QueueTransform(body, x, y, angle, kinematic);

    // ---- ownership -----------------------------------------------------------------------

    private bool EnsureWorld()
    {
        if (_core.HasWorld) return true;

        if (s_owner != null && s_owner != this)
        {
            if (!s_owner.IsIdle) return false; // a live simulation owns it: refuse rather than corrupt it
            s_owner.ReleaseNative();
        }

        Prowl.Native.Box2D.PB2.VerifyAbi();    // a stale or mismatched native library fails here, not by corrupting memory
        _core.Acquire();
        s_owner = this;
        return true;
    }

    private void ReleaseNative()
    {
        if (!_core.HasWorld) return;
        _colliderHosts.Clear();
        _jointHosts.Clear();
        _core.Release();
        if (ReferenceEquals(s_owner, this)) s_owner = null;
    }

    /// <summary>
    /// Releases the native world if nothing is registered. Never releases a busy world: a stale handle held by a live
    /// object could otherwise alias a new world's objects, because ids restart from the same indices.
    /// </summary>
    public bool ReleaseIfIdle()
    {
        if (!IsIdle) return false;
        ReleaseNative();
        return true;
    }

    /// <summary>Releases the native world. Only valid between steps.</summary>
    public void Dispose()
    {
        if (InStep) throw new InvalidOperationException("Cannot dispose the simulation from inside a step.");
        ReleaseNative();
    }

    // ---- registration --------------------------------------------------------------------

    /// <summary>Returns the managed body index to hand to the native layer, or -1 if the native world is owned elsewhere.</summary>
    /// <remarks>
    /// There is no host to register: the step writes each body's pose straight into its <see cref="BodyRecord"/> (see <see cref="BodyOf"/>).
    /// Throws when <see cref="PhysicsLimits.Bodies"/> bodies are already in use: that capacity is the size of the body arena, a limit and not a hint.
    /// </remarks>
    public int RegisterBody()
    {
        if (!EnsureWorld()) return -1;
        int index = _core.Registry.AddBody();
        if (index < 0)
            throw new InvalidOperationException($"Too many 2D bodies (the limit is {PhysicsLimits.Bodies}: PhysicsLimits.Bodies, the capacity of the body arena).");
        return index;
    }

    /// <summary>The record of a live body: its pose history and native handle. Null once the body is unregistered.</summary>
    public BodyRecord? BodyOf(int index) => _core.Registry.Body(index);

    /// <summary>Returns the collider index to hand to the native layer, or -1 if the native world is owned elsewhere.</summary>
    public int RegisterCollider(ICollider2DHost host)
    {
        if (!EnsureWorld()) return -1;
        int index = _core.Registry.AddCollider();
        if (index >= 0xFFFFFF) // the native layer keeps the index in 24 bits (the top 8 hold the layer)
        {
            _core.Registry.RemoveCollider(index);
            throw new InvalidOperationException("Too many 2D colliders (limit is 16,777,214).");
        }
        _colliderHosts.Set(index, host);
        return index;
    }

    /// <summary>
    /// Returns the joint index to hand to the native layer (it comes back in the joint's userData and in break events),
    /// or -1 if the native world is owned elsewhere. The body indices are the ones <see cref="RegisterBody"/> returned;
    /// pass -1 for the second when the joint is anchored to the world. They are how the simulation knows which joints
    /// die when a body is unregistered.
    /// </summary>
    public int RegisterJoint(IJoint2DHost host, int bodyIndexA, int bodyIndexB)
    {
        if (!EnsureWorld()) return -1;
        int index = _core.Registry.AddJoint(bodyIndexA, bodyIndexB);
        if (index < 0)
            throw new InvalidOperationException($"Too many 2D joints (the limit is {PhysicsLimits.Joints}: PhysicsLimits.Joints, the capacity of the joint arena).");
        _jointHosts.Set(index, host);
        return index;
    }

    /// <summary>Forgets a joint. The caller destroys the native joint itself (or it died with a body).</summary>
    public void UnregisterJoint(int index)
    {
        if (_core.Registry.RemoveJoint(index)) _jointHosts.Remove(index);
    }

    /// <summary>Asks to be offered a chance to create the native joint at the start of every step until that succeeds.</summary>
    public void QueueJoint(IJoint2DHost host)
    {
        if (!_pendingJoints.Contains(host)) _pendingJoints.Add(host);
    }

    public void DequeueJoint(IJoint2DHost host) => _pendingJoints.Remove(host);

    /// <summary>
    /// Unregisters a body. Every joint that connected it dies natively along with it (Box2D destroys a body's joints), so
    /// each one's host is told, after the joint has been unregistered.
    /// </summary>
    public void UnregisterBody(int index)
    {
        if (_core.Registry.JointCount > 0) LoseJointsOf(index);    // asked before the body goes, though the answer would be the same after
        _core.Registry.RemoveBody(index);
    }

    private void LoseJointsOf(int bodyIndex)
    {
        // Which joints connect this body is a comparison of the references in the arena records (Registry2D, which translates to C); telling
        // each one's host is not.
        _lostJoints.Clear();
        if (_core.Registry.JointsOfBody(bodyIndex, _lostJoints) == 0) return;

        for (int i = 0; i < _lostJoints.Count; i++)
        {
            int j = _lostJoints[i];
            IJoint2DHost? host = _jointHosts.Get(j);
            _core.Registry.RemoveJoint(j);
            _jointHosts.Remove(j);
            host?.OnNativeJointLost();
        }
    }

    public void UnregisterCollider(int index)
    {
        if (!_core.Registry.RemoveCollider(index)) return;
        _colliderHosts.Remove(index);
        _core.ForgetCollider(index);
    }

    // The hosts of a live index. A removed one is already cleared, and the liveness check is what keeps a quarantined slot from answering.
    private ICollider2DHost? ColliderHost(int index) => _core.Registry.ColliderLive(index) ? _colliderHosts.Get(index) : null;
    private IJoint2DHost? JointHost(int index) => _core.Registry.JointLive(index) ? _jointHosts.Get(index) : null;

    // ---- stepping ------------------------------------------------------------------------

    public void Step(float dt) { Step(dt, 4); }

    public void Step(float dt, int subSteps)
    {
        if (!_core.HasWorld) return;

        CreatePendingJoints();

        try
        {
            _core.Step(dt, subSteps);

            // The whole of the dispatch: the core decides what happened and in what order, from the world as it is at each call; this says who to
            // tell. Anything a handler does (destroy a collider, create a joint) is seen by the next call, and a slot it frees is held until EndStep.
            while (_core.NextEvent())
            {
                switch (_core.EventKind)
                {
                    case SimEvent.CollisionEnd:
                        if (ColliderHost(_core.EventSelf) is { } endSelf && ColliderHost(_core.EventOther) is { } endOther)
                            _events.CollisionEnd(endSelf, endOther);
                        break;

                    case SimEvent.CollisionBegin:
                        if (ColliderHost(_core.EventSelf) is { } beginSelf && ColliderHost(_core.EventOther) is { } beginOther)
                            _events.CollisionBegin(beginSelf, beginOther,
                                new Contact2D(_core.EventX, _core.EventY, _core.EventNX, _core.EventNY, _core.EventImpulse));
                        break;

                    case SimEvent.TriggerExit:
                        if (ColliderHost(_core.EventSelf) is { } exitSelf && ColliderHost(_core.EventOther) is { } exitOther)
                            _events.TriggerExit(exitSelf, exitOther);
                        break;

                    case SimEvent.TriggerStay:
                        if (ColliderHost(_core.EventSelf) is { } staySelf && ColliderHost(_core.EventOther) is { } stayOther)
                            _events.TriggerStay(staySelf, stayOther);
                        break;

                    case SimEvent.TriggerEnter:
                        if (ColliderHost(_core.EventSelf) is { } enterSelf && ColliderHost(_core.EventOther) is { } enterOther)
                            _events.TriggerEnter(enterSelf, enterOther);
                        break;

                    case SimEvent.JointBreak:
                        JointHost(_core.EventJoint)?.OnBreak();
                        break;
                }
            }
        }
        finally
        {
            _core.EndStep();
        }
    }

    /// <summary>Gives every joint that lost a body, or was added before its bodies existed, the chance to be (re)created.</summary>
    private void CreatePendingJoints()
    {
        for (int i = _pendingJoints.Count - 1; i >= 0; i--)
        {
            if (i >= _pendingJoints.Count) continue; // a host's creation removed others from the list
            IJoint2DHost host = _pendingJoints[i];
            if (host.TryCreateJoint() && i < _pendingJoints.Count && ReferenceEquals(_pendingJoints[i], host))
                _pendingJoints.RemoveAt(i);
        }
    }

    // ---- queries -------------------------------------------------------------------------

    /// <summary>Box2D's traversal overflows on huge translations, and Prowl's API defaults to float.MaxValue.</summary>
    public const float MaxQueryDistance = SimCore2D.MaxQueryDistance;

    public bool Raycast(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, bool hitSensors, out RayHit2D hit)
    {
        hit = default;
        int index = _core.Raycast(ox, oy, dx, dy, maxDistance, layerMask, hitSensors);
        if (index < 0) return false;
        ICollider2DHost? c = ColliderHost(index);
        if (c == null) return false;
        hit = new RayHit2D(c, _core.HitX, _core.HitY, _core.HitNX, _core.HitNY, _core.HitFraction, _core.HitDistance);
        return true;
    }

    /// <summary>Appends hits nearest-first. Returns the number appended.</summary>
    public int RaycastAll(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, bool hitSensors, List<RayHit2D> results)
    {
        int n = _core.RaycastAll(ox, oy, dx, dy, maxDistance, layerMask, hitSensors);
        int added = 0;
        for (int i = 0; i < n; i++)
        {
            ICollider2DHost? c = ColliderHost(_core.RayHitCollider(i));
            if (c == null) continue;
            results.Add(new RayHit2D(c, _core.RayHitX(i), _core.RayHitY(i), _core.RayHitNX(i), _core.RayHitNY(i), _core.RayHitFraction(i), _core.RayHitDistance(i)));
            added++;
        }
        return added;
    }

    public int OverlapPoint(float x, float y, uint layerMask, bool hitSensors, List<ICollider2DHost> results)
        => Collect(_core.OverlapPoint(x, y, layerMask, hitSensors), results);

    public int OverlapCircle(float cx, float cy, float radius, uint layerMask, bool hitSensors, List<ICollider2DHost> results)
        => Collect(_core.OverlapCircle(cx, cy, radius, layerMask, hitSensors), results);

    public int OverlapBox(float cx, float cy, float halfW, float halfH, float angle, uint layerMask, bool hitSensors, List<ICollider2DHost> results)
        => Collect(_core.OverlapBox(cx, cy, halfW, halfH, angle, layerMask, hitSensors), results);

    /// <summary>Appends each collider the core found, once. Returns how many.</summary>
    private int Collect(int n, List<ICollider2DHost> results)
    {
        int start = results.Count;
        for (int i = 0; i < n; i++)
        {
            ICollider2DHost? c = ColliderHost(_core.OverlapResult(i));
            if (c != null) results.Add(c);
        }
        return results.Count - start;
    }
}
