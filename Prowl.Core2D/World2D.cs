// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Native.Box2D;
using Prowl.Runtime.Physics2D;

namespace Prowl.Core2D;

/// <summary>
/// What connects the scene's physics components to the simulation core (<see cref="SimCore2D"/>): it makes a body when a Rigidbody2D is enabled and a
/// shape when a collider is, undoes them when they are disabled, pushes the game's edits of a node into its body before a step, writes the bodies'
/// poses (interpolated) back into their nodes after it, and turns the simulation's events into the messages the scene delivers to scripts.
/// <para/>
/// <b>Pose sync.</b> After the scene writes a body's pose into its node it remembers the node's <see cref="Node.WorldVersion"/>. A different version
/// later means the game moved the node (or an ancestor): the next fixed step pushes that pose into the body as a teleport (a kinematic body is
/// moved to it instead) and the interpolated write-back leaves the node alone until then. A teleport wakes a sleeping body: the shim does it.
/// <para/>
/// <b>What is not here yet:</b> joints; changing a setting of a live component (disable and enable it); a body-less collider following its node
/// when the node moves; the layer matrix (all layers collide); a body's mass override.
/// <para/>
/// An arena class of capacity 1.
/// </summary>
[MaxInstances(1)]
internal sealed class World2D
{
    public SimCore2D Sim;

    // ---- the message being delivered (see Scene2D.NextCall)
    public int EventKind;                // a Callbacks bit
    public Collider2D EventSelf;
    public Collider2D EventOther;
    public float EventX, EventY, EventNX, EventNY, EventImpulse;

    private Rigidbody2D[] _rbPool;
    private List<int> _rbFree;
    private int _rbHigh;
    private Collider2D[] _colPool;
    private List<int> _colFree;
    private int _colHigh;

    private List<Rigidbody2D> _bodies;       // enabled rigidbodies that have a native body
    private List<Collider2D> _colliders;     // enabled colliders that have a native shape
    private List<Collider2D> _byIndex;       // the collider of each simulation collider slot, null where none
    private List<Collider2D> _scratch;
    private float[] _poly;                   // a polygon's points on their way to the native shape
    private bool _streaming;

    public World2D()
    {
        Sim = new SimCore2D();
        _rbPool = new Rigidbody2D[CoreLimits.Rigidbodies];
        _rbFree = new List<int>();
        _colPool = new Collider2D[CoreLimits.Colliders];
        _colFree = new List<int>();
        _bodies = new List<Rigidbody2D>();
        _colliders = new List<Collider2D>();
        _byIndex = new List<Collider2D>();
        _scratch = new List<Collider2D>();
        _poly = new float[CoreLimits.MeshVertices * 2];
    }

    // ---- making components ---------------------------------------------------------------

    /// <summary>A Rigidbody2D on a node, not yet enabled: set its fields, then <see cref="Scene2D.Finish"/> its component. Null if all are in use.</summary>
    public Rigidbody2D NewRigidbody(Scene2D scene, Node node, int bodyType)
    {
        int slot;
        if (_rbFree.Count > 0)
        {
            slot = _rbFree[_rbFree.Count - 1];
            _rbFree.RemoveAt(_rbFree.Count - 1);
        }
        else
        {
            if (_rbHigh >= CoreLimits.Rigidbodies) return null;
            slot = _rbHigh;
            _rbHigh++;
        }
        Rigidbody2D rb = _rbPool[slot];
        if (rb == null)
        {
            rb = new Rigidbody2D();
            _rbPool[slot] = rb;
        }
        rb.Reset(bodyType);
        Component c = scene.Create(node, ComponentKind.Rigidbody2D, slot, 0, 0);
        if (c == null)
        {
            _rbFree.Add(slot);
            return null;
        }
        c.Body = rb;
        rb.Self = c;
        return rb;
    }

    /// <summary>A collider of a <see cref="Collider2D"/> shape kind (Box, Circle or Polygon) on a node, not yet enabled.</summary>
    public Collider2D NewCollider(Scene2D scene, Node node, int shapeKind)
    {
        int slot;
        if (_colFree.Count > 0)
        {
            slot = _colFree[_colFree.Count - 1];
            _colFree.RemoveAt(_colFree.Count - 1);
        }
        else
        {
            if (_colHigh >= CoreLimits.Colliders) return null;
            slot = _colHigh;
            _colHigh++;
        }
        Collider2D col = _colPool[slot];
        if (col == null)
        {
            col = new Collider2D();
            _colPool[slot] = col;
        }
        col.Reset(shapeKind);
        int kind = ComponentKind.BoxCollider2D;
        if (shapeKind == Collider2D.Circle) kind = ComponentKind.CircleCollider2D;
        else if (shapeKind == Collider2D.Polygon) kind = ComponentKind.PolygonCollider2D;
        else if (shapeKind == Collider2D.Terrain) kind = ComponentKind.TerrainCollider2D;
        Component c = scene.Create(node, kind, slot, 0, 0);
        if (c == null)
        {
            _colFree.Add(slot);
            return null;
        }
        c.Collider = col;
        col.Self = c;
        return col;
    }

    private static bool IsColliderKind(int kind)
    {
        return kind == ComponentKind.BoxCollider2D || kind == ComponentKind.CircleCollider2D || kind == ComponentKind.PolygonCollider2D || kind == ComponentKind.TerrainCollider2D;
    }

    // ---- the scene's hooks ---------------------------------------------------------------

    public void EnableComponent(Scene2D scene, Component c)
    {
        if (c.Kind == ComponentKind.Rigidbody2D) EnableBody(scene, c.Body);
        else if (IsColliderKind(c.Kind)) EnableCollider(scene, c.Collider);
    }

    public void DisableComponent(Scene2D scene, Component c)
    {
        if (c.Kind == ComponentKind.Rigidbody2D) DisableBody(scene, c.Body);
        else if (IsColliderKind(c.Kind)) DestroyShape(c.Collider);
    }

    /// <summary>The component's record is being recycled: its payload goes back to its pool, and the native world is given back when nothing is left.</summary>
    public void FreeComponent(Component c)
    {
        if (c.Kind == ComponentKind.Rigidbody2D) _rbFree.Add(c.Slot);
        else if (IsColliderKind(c.Kind)) _colFree.Add(c.Slot);
        else return;
        if (Sim.HasWorld && Sim.IsIdle) Sim.Release();
    }

    private void EnsureWorld()
    {
        if (!Sim.HasWorld) Sim.Acquire();
    }

    /// <summary>Makes sure the native world exists and keeps it alive until <see cref="UnpinWorld"/>: for things that put bodies in it without registering them (terrain).</summary>
    public void PinWorld()
    {
        EnsureWorld();
        Sim.Pins++;
    }

    public void UnpinWorld()
    {
        if (Sim.Pins > 0) Sim.Pins--;
        if (Sim.HasWorld && Sim.IsIdle) Sim.Release();
    }

    // ---- bodies --------------------------------------------------------------------------

    private void EnableBody(Scene2D scene, Rigidbody2D rb)
    {
        EnsureWorld();
        int index = Sim.Registry.AddBody();
        if (index < 0) return;                         // no room: the component stays inert
        Node n = rb.Self.Node;
        float x = n.WorldX(), y = n.WorldY(), a = n.WorldAngle();
        uint flags = 0u;
        if (rb.IsBullet) flags |= PB2.BfBullet;
        if (!rb.CanSleep) flags |= PB2.BfNoSleep;
        if (rb.FreezeRotation) flags |= PB2.BfLockRot;
        uint handle = PB2.BodyCreate(rb.BodyType, x, y, a, index, rb.GravityScale, rb.LinearDamping, rb.AngularDamping, flags);
        if (handle == 0)
        {
            Sim.Registry.RemoveBody(index);
            return;
        }
        rb.Handle = handle;
        rb.BodyIndex = index;
        BodyRecord record = Sim.Registry.Body(index);
        rb.Record = record;
        record.Native = handle;
        record.Pose.Reset(x, y, a, Sim.StepIndex);
        rb.SyncedVersion = n.WorldVersion();
        rb.HasWritten = false;
        _bodies.Add(rb);

        // colliders enabled before this body, on its node or below it, move onto it
        _scratch.Clear();
        for (int i = 0; i < _colliders.Count; i++)
        {
            Collider2D col = _colliders[i];
            Node cn = col.Self.Node;
            if (col.Attached != rb && (cn == n || cn.IsDescendantOf(n))) _scratch.Add(col);
        }
        for (int i = 0; i < _scratch.Count; i++)
        {
            Collider2D col = _scratch[i];
            DestroyShape(col);
            BuildCollider(scene, col);
        }
    }

    private void DisableBody(Scene2D scene, Rigidbody2D rb)
    {
        if (rb.Handle == 0) return;
        // the colliders on this body lose it: their shapes go, and any still wanted are rebuilt, on a static body of their own
        _scratch.Clear();
        for (int i = 0; i < _colliders.Count; i++)
            if (_colliders[i].Attached == rb) _scratch.Add(_colliders[i]);
        for (int i = 0; i < _scratch.Count; i++) DestroyShape(_scratch[i]);

        PB2.BodyDestroy(rb.Handle);
        Sim.Registry.RemoveBody(rb.BodyIndex);
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_bodies[i] == rb)
            {
                _bodies.RemoveAt(i);
                break;
            }
        }
        rb.Handle = 0;
        rb.BodyIndex = -1;
        rb.Record = null;

        for (int i = 0; i < _scratch.Count; i++)
        {
            Collider2D col = _scratch[i];
            Node cn = col.Self.Node;
            if (col.Self.Enabled && !col.Self.Destroyed && cn.ActiveInHierarchy && !cn.Destroyed) BuildCollider(scene, col);
        }
    }

    // ---- colliders -----------------------------------------------------------------------

    private void EnableCollider(Scene2D scene, Collider2D col)
    {
        EnsureWorld();
        BuildCollider(scene, col);
    }

    // The nearest enabled rigidbody with a body, on the node or above it.
    private Rigidbody2D FindBody(Scene2D scene, Node n)
    {
        for (Node k = n; k != null; k = k.Parent)
        {
            Component c = scene.Find(k, ComponentKind.Rigidbody2D);
            if (c != null && c.EnabledInHierarchy && c.Body.Handle != 0) return c.Body;
        }
        return null;
    }

    private static float Magnitude(float v)
    {
        float a = MathF.Abs(v);
        return a < 1e-4f ? 1e-4f : a;
    }

    private void BuildCollider(Scene2D scene, Collider2D col)
    {
        if (col.ColliderIndex >= 0) return;
        if (col.ShapeKind == Collider2D.Terrain)
        {
            BuildTerrainCollider(col);
            return;
        }
        Node n = col.Self.Node;
        Rigidbody2D rb = FindBody(scene, n);
        float myAngle = n.WorldAngle();
        float bodyX, bodyY, bodyAngle;
        uint body;
        if (rb != null)
        {
            Node bn = rb.Self.Node;
            bodyX = bn.WorldX();
            bodyY = bn.WorldY();
            bodyAngle = bn.WorldAngle();
            body = rb.Handle;
        }
        else
        {
            bodyX = n.WorldX();
            bodyY = n.WorldY();
            bodyAngle = myAngle;
            col.OwnBody = PB2.BodyCreate(PB2.BodyStatic, bodyX, bodyY, bodyAngle, -1, 1f, 0f, 0f, 0u);
            body = col.OwnBody;
        }

        // the shape's centre in the world, then in the body's space: a native shape lives in its body's local space, with no scale
        float cx, cy;
        n.ToWorld(col.OffsetX, col.OffsetY, out cx, out cy);
        float lx, ly, la;
        Collider2DGeometry.ToBodySpace(bodyX, bodyY, bodyAngle, cx, cy, myAngle, out lx, out ly, out la);
        float sx = Magnitude(n.LossyScaleX()), sy = Magnitude(n.LossyScaleY());

        int index = Sim.Registry.AddCollider();
        uint flags = col.IsTrigger ? PB2.SfSensor : 0u;
        uint shape;
        if (col.ShapeKind == Collider2D.Box)
        {
            float hw = MathF.Max(col.SizeX, 0.001f) * 0.5f * sx;
            float hh = MathF.Max(col.SizeY, 0.001f) * 0.5f * sy;
            shape = PB2.ShapeCreateBox(body, index, n.Layer, hw, hh, lx, ly, la, 0f, col.Density, col.Friction, col.Bounciness, flags);
        }
        else if (col.ShapeKind == Collider2D.Polygon)
        {
            // each point: node space -> world (the node's scale and turn) -> the body's space
            float cs = MathF.Cos(-bodyAngle), sn = MathF.Sin(-bodyAngle);
            for (int i = 0; i < col.PolyCount; i++)
            {
                float wx, wy;
                n.ToWorld(col.Poly[2 * i], col.Poly[2 * i + 1], out wx, out wy);
                float dx = wx - bodyX, dy = wy - bodyY;
                _poly[2 * i] = dx * cs - dy * sn;
                _poly[2 * i + 1] = dx * sn + dy * cs;
            }
            shape = PB2.ShapeCreatePolygon(body, index, n.Layer, _poly, col.PolyCount, 0f, col.Density, col.Friction, col.Bounciness, flags);
        }
        else
        {
            float r = MathF.Max(col.Radius, 0.001f) * MathF.Max(sx, sy);
            shape = PB2.ShapeCreateCircle(body, index, n.Layer, lx, ly, r, col.Density, col.Friction, col.Bounciness, flags);
        }
        if (shape == 0)
        {
            Sim.Registry.RemoveCollider(index);
            if (col.OwnBody != 0)
            {
                PB2.BodyDestroy(col.OwnBody);
                col.OwnBody = 0;
            }
            return;
        }
        col.Shape = shape;
        col.ColliderIndex = index;
        col.Attached = rb;
        SetByIndex(index, col);
        _colliders.Add(col);
    }

    /// <summary>
    /// A collider with no shape of its own: a collider index (what contact events name) and a static body at its node. Whoever owns it builds the
    /// shapes on <c>OwnBody</c>, with this index, and tells <c>Sim.ShapesVanishing</c> before it destroys and rebuilds them.
    /// </summary>
    private void BuildTerrainCollider(Collider2D col)
    {
        Node n = col.Self.Node;
        col.OwnBody = PB2.BodyCreate(PB2.BodyStatic, n.WorldX(), n.WorldY(), n.WorldAngle(), -1, 1f, 0f, 0f, 0u);
        if (col.OwnBody == 0) return;
        int index = Sim.Registry.AddCollider();
        col.Shape = 0;
        col.ColliderIndex = index;
        col.Attached = null;
        SetByIndex(index, col);
        _colliders.Add(col);
    }

    private void DestroyShape(Collider2D col)
    {
        if (col.ColliderIndex < 0) return;
        if (col.Shape != 0) PB2.ShapeDestroy(col.Shape);
        if (col.OwnBody != 0)
        {
            PB2.BodyDestroy(col.OwnBody);
            col.OwnBody = 0;
        }
        Sim.Registry.RemoveCollider(col.ColliderIndex);
        Sim.ForgetCollider(col.ColliderIndex);             // a trigger pair that names it is over
        Collider2D none = null;
        SetByIndex(col.ColliderIndex, none);
        col.ColliderIndex = -1;
        col.Shape = 0;
        col.Attached = null;
        for (int i = 0; i < _colliders.Count; i++)
        {
            if (_colliders[i] == col)
            {
                _colliders.RemoveAt(i);
                break;
            }
        }
    }

    private void SetByIndex(int index, Collider2D col)
    {
        Collider2D none = null;
        while (_byIndex.Count <= index) _byIndex.Add(none);
        _byIndex[index] = col;
    }

    /// <summary>The collider in a simulation collider slot (what a query returns), null if none.</summary>
    public Collider2D ColliderByIndex(int index) { return GetByIndex(index); }

    private Collider2D GetByIndex(int index)
    {
        if (index < 0 || index >= _byIndex.Count) return null;
        return _byIndex[index];
    }

    // ---- the step ------------------------------------------------------------------------

    /// <summary>Pushes the game's edits of nodes into their bodies, ahead of a step.</summary>
    public void PreStep(Scene2D scene)
    {
        if (!Sim.HasWorld) return;
        for (int i = 0; i < _bodies.Count; i++)
        {
            Rigidbody2D rb = _bodies[i];
            Node n = rb.Self.Node;
            int version = n.WorldVersion();
            if (version == rb.SyncedVersion) continue;
            float x = n.WorldX(), y = n.WorldY(), a = n.WorldAngle();
            Sim.QueueTransform(rb.Handle, x, y, a, rb.BodyType == PB2.BodyKinematic);
            rb.Record.Pose.Reset(x, y, a, Sim.StepIndex);
            rb.SyncedVersion = version;
            rb.HasWritten = false;
        }
    }

    /// <summary>Steps the world. Its events are then read with <see cref="NextEvent"/>, which also ends the step.</summary>
    public void Step(float dt)
    {
        if (!Sim.HasWorld) return;
        Sim.Step(dt, 4);
        _streaming = true;
    }

    /// <summary>
    /// The next collision or trigger message, in the Event fields; false when there are no more (and then the step is ended, so what the handlers
    /// destroyed is freed). A message is for a pair of colliders that are both still alive when it comes up.
    /// </summary>
    public bool NextEvent()
    {
        if (!_streaming) return false;
        while (Sim.NextEvent())
        {
            int k = Sim.EventKind;
            int bit;
            if (k == SimEvent.CollisionBegin) bit = Callbacks.CollisionBegin2D;
            else if (k == SimEvent.CollisionEnd) bit = Callbacks.CollisionEnd2D;
            else if (k == SimEvent.TriggerEnter) bit = Callbacks.TriggerEnter2D;
            else if (k == SimEvent.TriggerStay) bit = Callbacks.TriggerStay2D;
            else if (k == SimEvent.TriggerExit) bit = Callbacks.TriggerExit2D;
            else continue;
            Collider2D self = GetByIndex(Sim.EventSelf);
            Collider2D other = GetByIndex(Sim.EventOther);
            if (self == null || other == null) continue;
            EventKind = bit;
            EventSelf = self;
            EventOther = other;
            EventX = Sim.EventX;
            EventY = Sim.EventY;
            EventNX = Sim.EventNX;
            EventNY = Sim.EventNY;
            EventImpulse = Sim.EventImpulse;
            return true;
        }
        _streaming = false;
        Sim.EndStep();
        return false;
    }

    /// <summary>Writes each body's pose, interpolated to <paramref name="alpha"/> of the way into the next step, into its node.</summary>
    public void Sync(Scene2D scene, float alpha)
    {
        if (!Sim.HasWorld) return;
        for (int i = 0; i < _bodies.Count; i++)
        {
            Rigidbody2D rb = _bodies[i];
            if (rb.BodyType == PB2.BodyStatic) continue;
            Node n = rb.Self.Node;
            if (n.WorldVersion() != rb.SyncedVersion) continue;          // the game moved it: the next step takes that pose
            float x, y, a;
            rb.Record.Pose.Sample(alpha, Sim.StepIndex, out x, out y, out a);
            if (rb.HasWritten && x == rb.WrittenX && y == rb.WrittenY && a == rb.WrittenAngle) continue;     // at rest: nothing to write
            n.SetWorldPose(x, y, a);
            rb.SyncedVersion = n.WorldVersion();
            rb.WrittenX = x;
            rb.WrittenY = y;
            rb.WrittenAngle = a;
            rb.HasWritten = true;
        }
    }
}
