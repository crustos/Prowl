// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Runtime.Physics2D.Native;

namespace Prowl.Runtime.Physics2D;

/// <summary>Implemented by whatever owns a simulated body (the Rigidbody2D component). Receives its pose each step it moved.</summary>
internal interface IBody2DHost
{
    void OnMoved(float x, float y, float cos, float sin, bool fellAsleep);
}

/// <summary>Identity marker for a collider. The simulation only ever hands these back; it never looks inside.</summary>
internal interface ICollider2DHost { }

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
/// Everything about 2D physics that does not need the engine: native-world ownership, the body / collider
/// registries, stepping, event dispatch and queries. The engine-facing PhysicsWorld2D / Rigidbody2D / Collider2D
/// are thin layers over this, which is what lets it be tested against the real native library on its own.
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
    private readonly SlotRegistry<IBody2DHost> _bodies = new();
    private readonly SlotRegistry<ICollider2DHost> _colliders = new();
    private readonly HashSet<long> _activeTriggers = new();
    private readonly List<long> _triggerSnapshot = new();

    private Box2DWorld? _world;
    private float _gravityX, _gravityY = -9.81f;
    private int _workerCount = 1;
    private readonly uint[] _layerMatrix = new uint[PB2.LayerCount];

    private int[] _overlapBuffer = new int[256];
    private PB2RayHit[] _rayBuffer = new PB2RayHit[64];

    public PhysicsSimulation2D(IPhysics2DEvents events)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        Array.Fill(_layerMatrix, uint.MaxValue);
    }

    // ---- state ---------------------------------------------------------------------------

    public bool IsAcquired => _world != null;
    public bool InStep { get; private set; }

    /// <summary>Index of the most recently started step. Poses are stamped with it (see <see cref="BodyPose2D"/>).</summary>
    public int StepIndex { get; private set; }

    public int BodyCount => _bodies.Count;
    public int ColliderCount => _colliders.Count;

    /// <summary>No live bodies or colliders, so the native world could be handed to someone else.</summary>
    public bool IsIdle => !InStep && _bodies.Count == 0 && _colliders.Count == 0;

    /// <summary>The native world. Throws if this simulation has not acquired it.</summary>
    public Box2DWorld World => _world ?? throw new InvalidOperationException("The 2D physics simulation does not hold the native world.");

    public float GravityX => _gravityX;
    public float GravityY => _gravityY;

    public void SetGravity(float x, float y)
    {
        _gravityX = x;
        _gravityY = y;
        _world?.SetGravity(x, y);
    }

    /// <summary>Takes effect the next time the native world is created.</summary>
    public int WorkerCount
    {
        get => _workerCount;
        set => _workerCount = Math.Max(1, value);
    }

    public void SetLayerMatrix(ReadOnlySpan<uint> rows)
    {
        if (rows.Length != PB2.LayerCount) throw new ArgumentException($"Expected {PB2.LayerCount} rows.", nameof(rows));
        rows.CopyTo(_layerMatrix);
        _world?.SetLayerMatrix(_layerMatrix);
    }

    // ---- ownership -----------------------------------------------------------------------

    private bool EnsureWorld()
    {
        if (_world != null) return true;

        if (s_owner != null && s_owner != this)
        {
            if (!s_owner.IsIdle) return false; // a live simulation owns it: refuse rather than corrupt it
            s_owner.ReleaseNative();
        }

        _world = Box2DWorld.Create(_gravityX, _gravityY, _workerCount);
        _world.SetLayerMatrix(_layerMatrix);
        s_owner = this;
        return true;
    }

    private void ReleaseNative()
    {
        if (_world == null) return;
        _bodies.Clear();
        _colliders.Clear();
        _activeTriggers.Clear();
        _world.Dispose();
        _world = null;
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
    public int RegisterBody(IBody2DHost host) => EnsureWorld() ? _bodies.Add(host) : -1;

    /// <summary>Returns the collider index to hand to the native layer, or -1 if the native world is owned elsewhere.</summary>
    public int RegisterCollider(ICollider2DHost host)
    {
        if (!EnsureWorld()) return -1;
        int index = _colliders.Add(host);
        if (index >= 0xFFFFFF) // the native layer keeps the index in 24 bits (the top 8 hold the layer)
        {
            _colliders.Remove(index);
            throw new InvalidOperationException("Too many 2D colliders (limit is 16,777,214).");
        }
        return index;
    }

    public void UnregisterBody(int index) => _bodies.Remove(index);

    public void UnregisterCollider(int index)
    {
        if (!_colliders.Remove(index)) return;
        if (_activeTriggers.Count > 0)
            _activeTriggers.RemoveWhere(k => (int)(k >> 32) == index || (int)k == index);
    }

    // ---- stepping ------------------------------------------------------------------------

    private static long PairKey(int sensor, int visitor) => ((long)sensor << 32) | (uint)visitor;

    /// <summary>A handler may destroy either collider between the two directional callbacks of one event.</summary>
    private bool Alive(int ia, ICollider2DHost a, int ib, ICollider2DHost b)
        => ReferenceEquals(_colliders.Get(ia), a) && ReferenceEquals(_colliders.Get(ib), b);

    public void Step(float dt) { Step(dt, 4); }

    public void Step(float dt, int subSteps)
    {
        if (_world == null) return;

        StepIndex++;
        InStep = true;
        _bodies.Quarantining = _colliders.Quarantining = true;
        try
        {
            StepEvents ev = _world.Step(dt, subSteps);

            // 1. poses
            foreach (ref readonly PB2BodyMove m in ev.Moves)
                _bodies.Get(m.BodyIndex)?.OnMoved(m.X, m.Y, m.C, m.S, m.FellAsleep != 0);

            // 2. contacts: ends first, so a pair that ends and restarts in one step reads End then Begin
            foreach (ref readonly PB2ContactEvent c in ev.ContactEnds)
            {
                ICollider2DHost? a = _colliders.Get(c.ColliderA), b = _colliders.Get(c.ColliderB);
                if (a == null || b == null) continue;
                _events.CollisionEnd(a, b);
                if (Alive(c.ColliderA, a, c.ColliderB, b)) _events.CollisionEnd(b, a);
            }
            foreach (ref readonly PB2ContactEvent c in ev.ContactBegins)
            {
                ICollider2DHost? a = _colliders.Get(c.ColliderA), b = _colliders.Get(c.ColliderB);
                if (a == null || b == null) continue;
                _events.CollisionBegin(a, b, new Contact2D(c.PX, c.PY, c.NX, c.NY, c.Impulse));
                if (Alive(c.ColliderA, a, c.ColliderB, b))
                    _events.CollisionBegin(b, a, new Contact2D(c.PX, c.PY, -c.NX, -c.NY, c.Impulse));
            }

            // 3. triggers: Exit, then Stay for pairs that were already overlapping last step, then Enter
            foreach (ref readonly PB2SensorEvent s in ev.Sensors)
            {
                if (s.Flags != PB2EventFlags.End) continue;
                if (!_activeTriggers.Remove(PairKey(s.SensorCollider, s.VisitorCollider))) continue;
                ICollider2DHost? sensor = _colliders.Get(s.SensorCollider), visitor = _colliders.Get(s.VisitorCollider);
                if (sensor == null || visitor == null) continue;
                _events.TriggerExit(sensor, visitor);
                if (Alive(s.SensorCollider, sensor, s.VisitorCollider, visitor)) _events.TriggerExit(visitor, sensor);
            }

            if (_activeTriggers.Count > 0)
            {
                _triggerSnapshot.Clear();
                _triggerSnapshot.AddRange(_activeTriggers); // handlers may unregister colliders, mutating the set
                foreach (long key in _triggerSnapshot)
                {
                    ICollider2DHost? sensor = _colliders.Get((int)(key >> 32)), visitor = _colliders.Get((int)key);
                    if (sensor == null || visitor == null) { _activeTriggers.Remove(key); continue; }
                    if (!_activeTriggers.Contains(key)) continue; // removed by an earlier handler this step
                    _events.TriggerStay(sensor, visitor);
                    if (Alive((int)(key >> 32), sensor, (int)key, visitor)) _events.TriggerStay(visitor, sensor);
                }
            }

            foreach (ref readonly PB2SensorEvent s in ev.Sensors)
            {
                if (s.Flags != PB2EventFlags.Begin) continue;
                if (!_activeTriggers.Add(PairKey(s.SensorCollider, s.VisitorCollider))) continue;
                ICollider2DHost? sensor = _colliders.Get(s.SensorCollider), visitor = _colliders.Get(s.VisitorCollider);
                if (sensor == null || visitor == null) { _activeTriggers.Remove(PairKey(s.SensorCollider, s.VisitorCollider)); continue; }
                _events.TriggerEnter(sensor, visitor);
                if (Alive(s.SensorCollider, sensor, s.VisitorCollider, visitor)) _events.TriggerEnter(visitor, sensor);
            }
        }
        finally
        {
            InStep = false;
            _bodies.Quarantining = _colliders.Quarantining = false;
            _bodies.Flush();
            _colliders.Flush();
        }
    }

    // ---- queries -------------------------------------------------------------------------

    /// <summary>Box2D's traversal overflows on huge translations, and Prowl's API defaults to float.MaxValue.</summary>
    public const float MaxQueryDistance = 100000f;

    public bool Raycast(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, bool hitSensors, out RayHit2D hit)
    {
        hit = default;
        if (_world == null) return false;
        maxDistance = Math.Min(maxDistance, MaxQueryDistance);
        PB2RayHit h;
        if (!_world.Raycast(ox, oy, dx, dy, maxDistance, layerMask, hitSensors, out h)) return false;
        ICollider2DHost? c = _colliders.Get(h.Collider);
        if (c == null) return false;
        hit = MakeHit(c, h, maxDistance);
        return true;
    }

    /// <summary>Appends hits nearest-first. Returns the number appended.</summary>
    public int RaycastAll(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, bool hitSensors, List<RayHit2D> results)
    {
        if (_world == null) return 0;
        maxDistance = Math.Min(maxDistance, MaxQueryDistance);

        int n;
        while ((n = _world.RaycastAll(ox, oy, dx, dy, maxDistance, layerMask, hitSensors, _rayBuffer)) == _rayBuffer.Length
               && _rayBuffer.Length < 65536)
            _rayBuffer = new PB2RayHit[_rayBuffer.Length * 2]; // the buffer filled up, so there may be more: grow and redo

        int added = 0;
        for (int i = 0; i < n; i++)
        {
            ICollider2DHost? c = _colliders.Get(_rayBuffer[i].Collider);
            if (c == null) continue;
            results.Add(MakeHit(c, _rayBuffer[i], maxDistance));
            added++;
        }
        return added;
    }

    private static RayHit2D MakeHit(ICollider2DHost c, PB2RayHit h, float maxDistance)
        => new(c, h.PX, h.PY, h.NX, h.NY, h.Fraction, h.Fraction * maxDistance);

    private interface IOverlapQuery
    {
        int Run(Box2DWorld world, Span<int> buffer);
    }

    // Struct queries + a generic method: no closure or delegate allocation on a call that games make every frame.
    private readonly struct PointQuery(float x, float y, uint mask, bool sensors) : IOverlapQuery
    {
        public int Run(Box2DWorld w, Span<int> buf) => w.OverlapPoint(x, y, mask, sensors, buf);
    }

    private readonly struct CircleQuery(float cx, float cy, float r, uint mask, bool sensors) : IOverlapQuery
    {
        public int Run(Box2DWorld w, Span<int> buf) => w.OverlapCircle(cx, cy, r, mask, sensors, buf);
    }

    private readonly struct BoxQuery(float cx, float cy, float hw, float hh, float angle, uint mask, bool sensors) : IOverlapQuery
    {
        public int Run(Box2DWorld w, Span<int> buf) => w.OverlapBox(cx, cy, hw, hh, angle, mask, sensors, buf);
    }

    public int OverlapPoint(float x, float y, uint layerMask, bool hitSensors, List<ICollider2DHost> results)
        => Overlap(results, new PointQuery(x, y, layerMask, hitSensors));

    public int OverlapCircle(float cx, float cy, float radius, uint layerMask, bool hitSensors, List<ICollider2DHost> results)
        => Overlap(results, new CircleQuery(cx, cy, radius, layerMask, hitSensors));

    public int OverlapBox(float cx, float cy, float halfW, float halfH, float angle, uint layerMask, bool hitSensors, List<ICollider2DHost> results)
        => Overlap(results, new BoxQuery(cx, cy, halfW, halfH, angle, layerMask, hitSensors));

    /// <summary>Runs an overlap query, growing the buffer if it fills, and appends each collider once.</summary>
    private int Overlap<TQuery>(List<ICollider2DHost> results, TQuery query) where TQuery : struct, IOverlapQuery
    {
        if (_world == null) return 0;

        int n;
        while ((n = query.Run(_world, _overlapBuffer)) == _overlapBuffer.Length && _overlapBuffer.Length < 1 << 20)
            _overlapBuffer = new int[_overlapBuffer.Length * 2];

        int start = results.Count;
        // A collider built from several shapes is reported once per shape; list each only once. A linear scan is
        // fastest for the usual handful of results; a big sweep switches to a set so it stays linear.
        HashSet<ICollider2DHost>? seenSet = n > 64 ? new HashSet<ICollider2DHost>(ReferenceEqualityComparer.Instance) : null;
        for (int i = 0; i < n; i++)
        {
            ICollider2DHost? c = _colliders.Get(_overlapBuffer[i]);
            if (c == null) continue;
            if (seenSet != null)
            {
                if (seenSet.Add(c)) results.Add(c);
                continue;
            }
            bool seen = false;
            for (int j = start; j < results.Count; j++)
                if (ReferenceEquals(results[j], c)) { seen = true; break; }
            if (!seen) results.Add(c);
        }
        return results.Count - start;
    }
}
