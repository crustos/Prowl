// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Runtime.Physics2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// A scene's 2D physics world (<c>scene.Physics2D</c>), backed by Box2D-Packed. It steps once per fixed update,
/// keeps bodies and the Transform hierarchy in sync, turns native events into <see cref="Collision2D"/> and
/// trigger callbacks, and answers ray casts and overlap tests.
/// <para/>
/// The native world is a process-wide singleton. Only one scene at a time can have live 2D bodies (the running
/// game); a preview scene alongside it gets inert 2D components and one logged error rather than corrupting the
/// live simulation. Replacing the current scene hands the world over cleanly.
/// </summary>
public sealed class PhysicsWorld2D : IPhysics2DEvents
{
    private readonly PhysicsSimulation2D _sim;
    private readonly List<Rigidbody2D> _syncBodies = new();
    private int _appliedMatrixVersion = -1;
    private Float2 _gravity = new(0f, -9.81f);

    // Scratch for queries. The world is main-thread only (see MainThreadContext), so these are never shared.
    private readonly List<RayHit2D> _rayScratch = new();
    private readonly List<ICollider2DHost> _overlapScratch = new();

    public PhysicsWorld2D()
    {
        _sim = new PhysicsSimulation2D(this);
        _sim.SetGravity(_gravity.X, _gravity.Y);
    }

    internal PhysicsSimulation2D Simulation => _sim;

    /// <summary>World gravity in units per second squared. Default (0, -9.81).</summary>
    public Float2 Gravity
    {
        get => _gravity;
        set
        {
            _gravity = value;
            _sim.SetGravity(value.X, value.Y);
        }
    }

    /// <summary>Solver sub-steps per fixed update. More is more stable for stacks and joints, and costs more.</summary>
    public int Substeps { get; set; } = 4;

    /// <summary>
    /// Worker threads Box2D may use. Takes effect the next time the native world is created (the first body after the
    /// scene is loaded). 1 keeps the whole step on the calling thread.
    /// </summary>
    public int WorkerCount
    {
        get => _sim.WorkerCount;
        set => _sim.WorkerCount = value;
    }

    /// <summary>True while this scene holds the native world, i.e. has live 2D bodies or colliders.</summary>
    public bool IsActive => _sim.IsAcquired;

    // ---- stepping ------------------------------------------------------------------------

    /// <summary>One fixed step. Driven by <c>Scene.FixedUpdate</c>.</summary>
    public void Update()
    {
        if (!_sim.IsAcquired) return;

        int matrixVersion = CollisionMatrix.Version;
        if (matrixVersion != _appliedMatrixVersion)
        {
            _appliedMatrixVersion = matrixVersion;
            _sim.SetLayerMatrix(CollisionMatrix.GetRows());
        }

        // Push Transform edits made by game code into their bodies (one batched native call for all of them).
        for (int i = 0; i < _syncBodies.Count; i++)
            _syncBodies[i].SyncTransformToBody();

        _sim.Step(Time.FixedDeltaTime, Math.Max(1, Substeps));
    }

    /// <summary>Gives the native world back when this scene has nothing left in it. Called as a scene is disposed.</summary>
    internal void ReleaseIfIdle() => _sim.ReleaseIfIdle();

    internal void RegisterRigidbody(Rigidbody2D body)
    {
        if (body.SyncIndex >= 0) return;
        body.SyncIndex = _syncBodies.Count;
        _syncBodies.Add(body);
    }

    internal void UnregisterRigidbody(Rigidbody2D body)
    {
        int i = body.SyncIndex;
        if (i < 0 || i >= _syncBodies.Count || !ReferenceEquals(_syncBodies[i], body)) return;
        int last = _syncBodies.Count - 1;
        if (i != last)
        {
            _syncBodies[i] = _syncBodies[last];
            _syncBodies[i].SyncIndex = i;
        }
        _syncBodies.RemoveAt(last);
        body.SyncIndex = -1;
    }

    internal static void ReportUnavailable()
        => Debug.LogErrorOnce("Physics2D.NativeWorldOwned",
            "[Physics2D] Another scene already owns the 2D physics world, so 2D bodies in this scene stay inert. "
            + "Box2D-Packed is single-world: only one scene at a time can run 2D physics.");

    // ---- events from the simulation ------------------------------------------------------

    void IPhysics2DEvents.CollisionBegin(ICollider2DHost self, ICollider2DHost other, Contact2D contact)
    {
        if (self is not Collider2D s || other is not Collider2D o || !s.IsValid() || !o.IsValid()) return;
        // Contact2D's normal points self -> other; Collision2D's points other -> self (Unity's convention).
        var collision = new Collision2D(o.AttachedRigidbody, o, new Float2(contact.X, contact.Y), new Float2(-contact.NX, -contact.NY), contact.Impulse);
        SceneDispatcher.CollisionBegin2D(s.GameObject, collision);
    }

    void IPhysics2DEvents.CollisionEnd(ICollider2DHost self, ICollider2DHost other)
    {
        if (self is not Collider2D s || other is not Collider2D o || !s.IsValid() || !o.IsValid()) return;
        var collision = new Collision2D(o.AttachedRigidbody, o, Float2.Zero, Float2.Zero, 0f);
        SceneDispatcher.CollisionEnd2D(s.GameObject, collision);
    }

    void IPhysics2DEvents.TriggerEnter(ICollider2DHost self, ICollider2DHost other)
    {
        if (self is Collider2D s && other is Collider2D o && s.IsValid() && o.IsValid())
            SceneDispatcher.TriggerEnter2D(s.GameObject, o);
    }

    void IPhysics2DEvents.TriggerStay(ICollider2DHost self, ICollider2DHost other)
    {
        if (self is Collider2D s && other is Collider2D o && s.IsValid() && o.IsValid())
            SceneDispatcher.TriggerStay2D(s.GameObject, o);
    }

    void IPhysics2DEvents.TriggerExit(ICollider2DHost self, ICollider2DHost other)
    {
        if (self is Collider2D s && other is Collider2D o && s.IsValid() && o.IsValid())
            SceneDispatcher.TriggerExit2D(s.GameObject, o);
    }

    // ---- queries -------------------------------------------------------------------------

    private static bool Passes(Collider2D c, QueryFilter2D f)
    {
        if (!c.IsValid()) return false;
        if (f.ExcludeTriggers && c.IsTrigger) return false;
        if (f.IgnoreCollider.IsValid() && ReferenceEquals(c, f.IgnoreCollider)) return false;
        if (f.IgnoreRigidbody.IsValid() && ReferenceEquals(c.AttachedRigidbody, f.IgnoreRigidbody)) return false;
        return true;
    }

    private static RaycastHit2D ToHit(Collider2D c, RayHit2D h)
        => new(c, c.AttachedRigidbody, new Float2(h.X, h.Y), new Float2(h.NX, h.NY), h.Distance, h.Fraction);

    /// <summary>Casts a ray and returns the nearest hit. <paramref name="direction"/> need not be normalised.</summary>
    public bool Raycast(Float2 origin, Float2 direction, out RaycastHit2D hit)
    {
        return Raycast(origin, direction, out hit, float.MaxValue, QueryFilter2D.Default);
    }

    public bool Raycast(Float2 origin, Float2 direction, out RaycastHit2D hit, float maxDistance)
    {
        return Raycast(origin, direction, out hit, maxDistance, QueryFilter2D.Default);
    }

    public bool Raycast(Float2 origin, Float2 direction, out RaycastHit2D hit, float maxDistance, QueryFilter2D filter)
    {
        hit = default;
        if (!_sim.IsAcquired) return false;
        uint mask = filter.LayerMask.Mask;

        if (!filter.HasExclusions)
        {
            // The native ray already honours the layer mask and the trigger toggle, and returns just the closest hit.
            RayHit2D h;
            if (!_sim.Raycast(origin.X, origin.Y, direction.X, direction.Y, maxDistance, mask, !filter.ExcludeTriggers, out h)) return false;
            if (h.Collider is not Collider2D c || !c.IsValid()) return false;
            hit = ToHit(c, h);
            return true;
        }

        // The closest hit may be the caster's own collider, so take them all, nearest first, and keep the first that passes.
        _rayScratch.Clear();
        _sim.RaycastAll(origin.X, origin.Y, direction.X, direction.Y, maxDistance, mask, !filter.ExcludeTriggers, _rayScratch);
        foreach (RayHit2D h in _rayScratch)
        {
            if (h.Collider is Collider2D c && Passes(c, filter))
            {
                hit = ToHit(c, h);
                _rayScratch.Clear();
                return true;
            }
        }
        _rayScratch.Clear();
        return false;
    }

    /// <summary>Casts a ray and collects every hit along it, nearest first. Clears <paramref name="hits"/> first.</summary>
    public int RaycastAll(Float2 origin, Float2 direction, float maxDistance, List<RaycastHit2D> hits)
        => RaycastAll(origin, direction, maxDistance, hits, QueryFilter2D.Default);

    public int RaycastAll(Float2 origin, Float2 direction, float maxDistance, List<RaycastHit2D> hits, QueryFilter2D filter)
    {
        hits.Clear();
        if (!_sim.IsAcquired) return 0;

        _rayScratch.Clear();
        _sim.RaycastAll(origin.X, origin.Y, direction.X, direction.Y, maxDistance, filter.LayerMask.Mask, !filter.ExcludeTriggers, _rayScratch);
        foreach (RayHit2D h in _rayScratch)
            if (h.Collider is Collider2D c && Passes(c, filter))
                hits.Add(ToHit(c, h));
        _rayScratch.Clear();
        return hits.Count;
    }

    /// <summary>Colliders containing <paramref name="point"/>. Clears <paramref name="results"/> first.</summary>
    public int OverlapPoint(Float2 point, List<Collider2D> results) => OverlapPoint(point, results, QueryFilter2D.Default);

    public int OverlapPoint(Float2 point, List<Collider2D> results, QueryFilter2D filter)
    {
        results.Clear();
        if (!_sim.IsAcquired) return 0;
        _overlapScratch.Clear();
        _sim.OverlapPoint(point.X, point.Y, filter.LayerMask.Mask, !filter.ExcludeTriggers, _overlapScratch);
        return Collect(results, filter);
    }

    /// <summary>Colliders touching the circle. Clears <paramref name="results"/> first.</summary>
    public int OverlapCircle(Float2 center, float radius, List<Collider2D> results) => OverlapCircle(center, radius, results, QueryFilter2D.Default);

    public int OverlapCircle(Float2 center, float radius, List<Collider2D> results, QueryFilter2D filter)
    {
        results.Clear();
        if (!_sim.IsAcquired) return 0;
        _overlapScratch.Clear();
        _sim.OverlapCircle(center.X, center.Y, radius, filter.LayerMask.Mask, !filter.ExcludeTriggers, _overlapScratch);
        return Collect(results, filter);
    }

    /// <summary>Colliders touching the box. <paramref name="size"/> is the full width and height; the angle is in degrees.</summary>
    public int OverlapBox(Float2 center, Float2 size, float angleDegrees, List<Collider2D> results)
        => OverlapBox(center, size, angleDegrees, results, QueryFilter2D.Default);

    public int OverlapBox(Float2 center, Float2 size, float angleDegrees, List<Collider2D> results, QueryFilter2D filter)
    {
        results.Clear();
        if (!_sim.IsAcquired) return 0;
        _overlapScratch.Clear();
        _sim.OverlapBox(center.X, center.Y, size.X * 0.5f, size.Y * 0.5f, angleDegrees * Maths.Deg2Rad, filter.LayerMask.Mask, !filter.ExcludeTriggers, _overlapScratch);
        return Collect(results, filter);
    }

    private int Collect(List<Collider2D> results, QueryFilter2D filter)
    {
        foreach (ICollider2DHost host in _overlapScratch)
            if (host is Collider2D c && Passes(c, filter))
                results.Add(c);
        _overlapScratch.Clear();
        return results.Count;
    }
}
