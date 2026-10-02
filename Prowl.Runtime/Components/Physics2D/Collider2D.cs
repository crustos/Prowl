// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Echo;
using Prowl.Runtime.Physics2D;
using Prowl.Runtime.Physics2D.Native;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>Everything a shape needs to be built in its body's space. Scale is already folded in by the collider.</summary>
internal readonly struct ShapeSpec2D
{
    public readonly float X, Y, Angle;            // centre and rotation relative to the body
    public readonly float ScaleX, ScaleY;         // signed lossy scale (a mirrored axis stays mirrored), never ~0
    public readonly int ColliderIndex;
    public readonly int Layer;
    public readonly bool IsTrigger;
    public readonly float Density, Friction, Bounciness;

    public ShapeSpec2D(float x, float y, float angle, float scaleX, float scaleY, int colliderIndex, int layer,
                       bool isTrigger, float density, float friction, float bounciness)
    {
        X = x; Y = y; Angle = angle; ScaleX = scaleX; ScaleY = scaleY;
        ColliderIndex = colliderIndex; Layer = layer; IsTrigger = isTrigger;
        Density = density; Friction = friction; Bounciness = bounciness;
    }

    public float AbsScaleX => Collider2DGeometry.AbsScale(ScaleX);
    public float AbsScaleY => Collider2DGeometry.AbsScale(ScaleY);
    public uint Flags => IsTrigger ? (uint)PB2ShapeFlags.Sensor : 0u;
}

/// <summary>
/// Base class of 2D colliders. A collider belongs to the nearest enabled <see cref="Rigidbody2D"/> above it (or on its own
/// GameObject); with none it gets a static body of its own, which makes it level geometry.
/// <para/>
/// Shapes live in their body's space, so a collider only rebuilds when it moves <i>relative to its body</i>, or is
/// rescaled or moved to another layer. A body-less collider that merely moves is carried by moving its static body.
/// </summary>
[ComponentIcon("\uf1b2")]
public abstract class Collider2D : MonoBehaviour, ICollider2DHost
{
    [SerializeField, Tooltip("Centre offset in the GameObject's local space. Scaled along with the GameObject.")]
    private Float2 offset;

    [SerializeField, Tooltip("Rotation in degrees relative to the GameObject.")]
    private float rotation;

    [SerializeField, Tooltip("A trigger reports overlaps (OnTriggerEnter2D) but does not collide.")]
    private bool isTrigger;

    [SerializeField, Header("Material"), Tooltip("Relative weight of this shape within its body. Total mass comes from the Rigidbody2D.")]
    private float density = 1f;

    [SerializeField, Tooltip("How much the surface resists sliding. 0 is ice.")]
    private float friction = 0.4f;

    [SerializeField, Range(0f, 1f), Tooltip("Restitution. 0 absorbs impacts, 1 bounces back with all its speed.")]
    private float bounciness;

    // ---- settings ------------------------------------------------------------------------

    /// <summary>Centre offset in the GameObject's local space (scaled with it).</summary>
    public Float2 Offset
    {
        get => offset;
        set { offset = value; Rebuild(); }
    }

    /// <summary>Rotation in degrees relative to the GameObject.</summary>
    public float Rotation
    {
        get => rotation;
        set { rotation = value; Rebuild(); }
    }

    /// <summary>A trigger detects overlaps (<see cref="MonoBehaviour.OnTriggerEnter2D"/>) but does not collide.</summary>
    public bool IsTrigger
    {
        get => isTrigger;
        set { isTrigger = value; Rebuild(); }
    }

    /// <summary>Relative weight of this shape within its body (mass itself comes from <see cref="Rigidbody2D.Mass"/>).</summary>
    public float Density
    {
        get => density;
        set
        {
            density = MathF.Max(value, 0f);
            if (_shapes == null) return;
            foreach (uint s in _shapes) PB2.ShapeSetDensity(s, density);
            _attachedRigidbody?.ApplyMass();
        }
    }

    public float Friction
    {
        get => friction;
        set
        {
            friction = MathF.Max(value, 0f);
            ApplyMaterial();
        }
    }

    /// <summary>Restitution: 0 is dead, 1 bounces back with all its speed.</summary>
    public float Bounciness
    {
        get => bounciness;
        set
        {
            bounciness = Math.Clamp(value, 0f, 1f);
            ApplyMaterial();
        }
    }

    private void ApplyMaterial()
    {
        if (_shapes == null) return;
        foreach (uint s in _shapes) PB2.ShapeSetMaterial(s, friction, bounciness);
    }

    // ---- attachment state ----------------------------------------------------------------

    private Rigidbody2D? _attachedRigidbody;
    private uint _ownBody;                  // a static body owned by this collider, when there is no rigidbody
    private uint[]? _shapes;
    private int _index = -1;
    private PhysicsWorld2D? _world;

    private uint _lastTransformVersion;
    private int _lastLayer;
    private Float3 _lastLossy = Float3.One;
    private Float3 _lastBodyScale = Float3.One;

    /// <summary>The rigidbody this collider is part of, or null for level geometry.</summary>
    public Rigidbody2D? AttachedRigidbody => _attachedRigidbody;

    /// <summary>Whether this collider has shapes in the physics world right now.</summary>
    public bool IsInWorld => _world != null;

    internal bool IsClaimed => _attachedRigidbody != null;

    /// <summary>Builds this collider's native shapes. <paramref name="body"/> is the body they belong to.</summary>
    private protected abstract void CreateShapes(uint body, ShapeSpec2D spec, List<uint> shapes);

    /// <summary>
    /// Appends this collider's outline as (x1, y1, x2, y2) quads, in the frame of <paramref name="spec"/>. Returns false if the
    /// configuration is one the physics would reject; it still appends whatever helps the user see why (shown in red).
    /// Shares its dimension resolution with <see cref="CreateShapes"/>, so the gizmo is the shape, not a lookalike.
    /// </summary>
    private protected abstract bool BuildOutline(ShapeSpec2D spec, List<float> segments);

    /// <summary>World-space points worth a handle while selected (polygon and edge vertices). Appended as x, y pairs.</summary>
    private protected virtual void CollectHandles(ShapeSpec2D spec, List<float> points) { }

    // ---- building ------------------------------------------------------------------------

    private float ZAngle(Transform t)
    {
        Quaternion q = t.Rotation;
        return Angle2D.FromQuaternionZ(q.X, q.Y, q.Z, q.W);
    }

    private static float SignedClamp(float v)
    {
        const float min = 1e-4f;
        if (v <= -min || v >= min) return v;
        return v < 0f ? -min : min;
    }

    private void BuildShapes(PhysicsWorld2D world, Rigidbody2D? rb)
    {
        int index = world.Simulation.RegisterCollider(this);
        if (index < 0)
        {
            PhysicsWorld2D.ReportUnavailable();
            return;
        }
        _index = index;
        _world = world;

        float bodyX, bodyY, bodyAngle;
        uint body;
        float myAngle = ZAngle(Transform);
        if (rb != null)
        {
            // Both poses read from Transforms, so the offset between them is consistent even while the body's own
            // Transform is interpolated behind the simulation.
            Float3 bp = rb.Transform.Position;
            bodyX = bp.X; bodyY = bp.Y; bodyAngle = ZAngle(rb.Transform);
            body = rb.Handle;
        }
        else
        {
            Float3 p = Transform.Position;
            bodyX = p.X; bodyY = p.Y; bodyAngle = myAngle;
            _ownBody = PB2.BodyCreate(PB2BodyType.Static, bodyX, bodyY, bodyAngle, -1, 1f, 0f, 0f, 0u);
            body = _ownBody;
        }

        float lx;
        float ly;
        float la;
        Float3 centre = Transform.TransformPoint(new Float3(offset.X, offset.Y, 0f)); // applies rotation and scale
        Collider2DGeometry.ToBodySpace(bodyX, bodyY, bodyAngle, centre.X, centre.Y, myAngle + rotation * Maths.Deg2Rad,
                                       out lx, out ly, out la);

        Float3 lossy = Transform.LossyScale;
        var spec = new ShapeSpec2D(lx, ly, la, SignedClamp(lossy.X), SignedClamp(lossy.Y), index, GameObject.LayerIndex,
                                   isTrigger, density, friction, bounciness);

        var shapes = new List<uint>();
        CreateShapes(body, spec, shapes);
        _shapes = shapes.ToArray();

        _lastLossy = lossy;
        _lastLayer = GameObject.LayerIndex;
        _lastTransformVersion = CurrentTransformVersion();
        _lastBodyScale = rb != null ? rb.Transform.LocalScale : Float3.One;

        rb?.ApplyMass();
    }

    private uint ComputeTransformVersion(Transform? stopAt)
    {
        uint v = 17;
        for (Transform? t = Transform; t != null && t != stopAt; t = t.Parent)
            v = v * 31 + t.Version;
        return v;
    }

    // Stops at the body: shapes are in body space, so only movement relative to it matters.
    private uint CurrentTransformVersion()
        => ComputeTransformVersion(_attachedRigidbody.IsValid() ? _attachedRigidbody.Transform : null);

    internal bool TryAttachTo(Rigidbody2D rigidbody)
    {
        if (!rigidbody.IsSimulated) return false;
        if (IsClaimed && !ReferenceEquals(_attachedRigidbody, rigidbody)) return false; // claimed by a different rigidbody

        Detach();
        _attachedRigidbody = rigidbody;
        BuildShapes(rigidbody.World!, rigidbody);
        if (_world == null) _attachedRigidbody = null; // the world is owned elsewhere, so there is nothing to attach to
        return _world != null;
    }

    private void AttachStatic()
    {
        var scene = GameObject.IsValid() ? GameObject.Scene : null;
        if (!scene.IsValid()) return;
        _attachedRigidbody = null;
        BuildShapes(scene.Physics2D, null);
    }

    internal void Detach()
    {
        if (_world != null)
        {
            if (_ownBody != 0) PB2.BodyDestroy(_ownBody); // takes its shapes with it
            else if (_shapes != null)
                foreach (uint s in _shapes) PB2.ShapeDestroy(s);
            _world.Simulation.UnregisterCollider(_index);
        }

        Rigidbody2D? rb = _attachedRigidbody;
        _ownBody = 0;
        _shapes = null;
        _index = -1;
        _world = null;
        _attachedRigidbody = null;

        if (rb.IsValid()) rb.ApplyMass(); // mass follows the shapes that remain
    }

    internal Rigidbody2D? FindOwningRigidbody()
    {
        foreach (Rigidbody2D rb in GetComponentsInParent<Rigidbody2D>())
            if (rb.IsValid() && rb.EnabledInHierarchy) return rb;
        return null;
    }

    internal void Reattach()
    {
        Detach();
        Rigidbody2D? rb = FindOwningRigidbody();
        if (rb.IsValid() && rb.IsSimulated && TryAttachTo(rb)) return;
        AttachStatic();
    }

    /// <summary>Rebuilds the shapes. Called when a property that can't be changed in place is edited.</summary>
    public void Rebuild()
    {
        AssertOwner();
        if (_world == null) return; // not in the world yet; OnEnable builds it
        Reattach();
    }

    public override void OnEnable() => Reattach();

    public override void OnDisable() => Detach();

    public override void OnValidate()
    {
        density = MathF.Max(density, 0f);
        friction = MathF.Max(friction, 0f);
        bounciness = Math.Clamp(bounciness, 0f, 1f);
        if (_world != null) Reattach();
    }

    public override void Update()
    {
        if (_world == null) return;

        bool layerChanged = GameObject.LayerIndex != _lastLayer;
        uint version = CurrentTransformVersion();
        bool transformChanged = version != _lastTransformVersion;
        // The version walk stops at the body, so the body's own rescale is the one thing it cannot see.
        bool bodyScaleChanged = _attachedRigidbody.IsValid() && !_attachedRigidbody.Transform.LocalScale.Equals(_lastBodyScale);
        if (!layerChanged && !transformChanged && !bodyScaleChanged) return;

        // A body-less collider that only moved: shapes are in its own body's space, so move the body instead of rebuilding.
        if (_ownBody != 0 && !layerChanged && Transform.LossyScale.Equals(_lastLossy))
        {
            Float3 p = Transform.Position;
            PB2.BodySetTransform(_ownBody, p.X, p.Y, ZAngle(Transform));
            _lastTransformVersion = version;
            return;
        }

        Reattach();
    }

    // ---- editor gizmos -------------------------------------------------------------------
    //
    // Gizmos run in the editor whether or not anything is playing, so none of this touches the native world: the outline
    // comes from the Transform alone, through the same dimension resolution the physics uses.

    private static readonly List<float> s_gizmoSegments = new();
    private static readonly List<float> s_gizmoPoints = new();

    /// <summary>This collider's frame in world space (not body space), for drawing.</summary>
    private ShapeSpec2D WorldSpec()
    {
        Float3 centre = Transform.TransformPoint(new Float3(offset.X, offset.Y, 0f));
        Float3 lossy = Transform.LossyScale;
        return new ShapeSpec2D(centre.X, centre.Y, ZAngle(Transform) + rotation * Maths.Deg2Rad,
                               SignedClamp(lossy.X), SignedClamp(lossy.Y), -1, GameObject.LayerIndex,
                               isTrigger, density, friction, bounciness);
    }

    private static void DrawSegments(List<float> segments, float z, Color color)
    {
        for (int i = 0; i + 3 < segments.Count; i += 4)
            Debug.DrawLine(new Float3(segments[i], segments[i + 1], z), new Float3(segments[i + 2], segments[i + 3], z), color);
    }

    private static void DrawCross(float x, float y, float z, float size, Color color)
    {
        Debug.DrawLine(new Float3(x - size, y, z), new Float3(x + size, y, z), color);
        Debug.DrawLine(new Float3(x, y - size, z), new Float3(x, y + size, z), color);
    }

    /// <summary>Green outline, yellow for a trigger, red when the configuration is one the physics would reject.</summary>
    public override void DrawGizmos()
    {
        s_gizmoSegments.Clear();
        ShapeSpec2D spec = WorldSpec();
        bool valid = BuildOutline(spec, s_gizmoSegments);
        DrawSegments(s_gizmoSegments, Transform.Position.Z, valid ? (isTrigger ? Color.Yellow : Color.Green) : Color.Red);
    }

    /// <summary>Adds the centre, vertex handles, and a line to the owning rigidbody.</summary>
    public override void DrawGizmosSelected()
    {
        ShapeSpec2D spec = WorldSpec();
        float z = Transform.Position.Z;
        DrawCross(spec.X, spec.Y, z, 0.1f, Color.White);

        s_gizmoPoints.Clear();
        CollectHandles(spec, s_gizmoPoints);
        for (int i = 0; i + 1 < s_gizmoPoints.Count; i += 2)
            DrawCross(s_gizmoPoints[i], s_gizmoPoints[i + 1], z, 0.06f, Color.White);

        Rigidbody2D? rb = FindOwningRigidbody();
        if (rb.IsValid() && !ReferenceEquals(rb.GameObject, GameObject))
            Debug.DrawDashedLine(new Float3(spec.X, spec.Y, z), rb.Transform.Position, Color.Cyan);
    }
}
