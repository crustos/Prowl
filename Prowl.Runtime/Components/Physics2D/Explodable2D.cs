// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Runtime.Destruction2D;
using Prowl.Runtime.Physics2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>How an <see cref="Explodable2D"/> is cut up.</summary>
public enum ShatterMode
{
    /// <summary>Delaunay triangles between the outline's corners (and the extra points). Sharp, shard-like pieces.</summary>
    Triangle = 0,
    /// <summary>The Voronoi cells of the corners (and the extra points). Rounder, cracked-stone pieces.</summary>
    Voronoi = 1,
}

/// <summary>
/// Breaks the GameObject into physical fragments: it takes the outline of the <see cref="BoxCollider2D"/> or
/// <see cref="PolygonCollider2D"/> on the same object, cuts it into convex pieces, and replaces the object with one dynamic
/// <see cref="Rigidbody2D"/> and <see cref="PolygonCollider2D"/> per piece. The pieces inherit the object's velocity, spin, mass (shared out by
/// area), layer and material, and can be flung by a blast and dig a crater in every <see cref="PixelTerrain2D"/>.
/// It is the engine side of <c>Prowl.Runtime/Destruction2D/Shatter</c>, the code that the 2D player's <c>Prowl.Core2D.Shatter2D</c> shares,
/// and a port of Unity-2D-Destruction's <c>Explodable</c>.
/// <para/>
/// Fragments have no picture of their own: this component does not draw anything. <see cref="Exploded"/> hands them over, with
/// their outlines (<see cref="PolygonCollider2D.Points"/>) so that whatever draws them can cut the source's texture to match.
/// </summary>
[AddComponentMenu("Physics 2D/Explodable 2D")]
[ComponentIcon("")]
public sealed class Explodable2D : MonoBehaviour
{
    [Header("Shatter"), Tooltip("Triangle: sharp shards. Voronoi: rounded cells.")]
    public ShatterMode Mode = ShatterMode.Triangle;

    [Range(0f, 64f), Tooltip("Random points inside the outline, on top of its corners. More points make more, smaller pieces.")]
    public int ExtraPoints = 4;

    [Range(0f, 3f), Tooltip("How many more times every piece is cut again. 1 or 2 is plenty: each step multiplies the pieces.")]
    public int SubshatterSteps;

    [Tooltip("The same seed always gives the same pieces. 0 gives different ones each time.")]
    public uint Seed;

    [Header("Fragments"), Tooltip("Physics layer of the fragments. -1: the layer of this object.")]
    public int FragmentLayer = -1;

    [Tooltip("Seconds until the fragments are removed. 0: they stay.")]
    public float FragmentLifetime = 0f;

    [Tooltip("Remove this object when it explodes. Turn off to keep it, e.g. to fade it out yourself.")]
    public bool DestroySource = true;

    [Header("Blast"), Tooltip("Bodies within this distance of the blast are pushed away. 0: no blast.")]
    public float BlastRadius = 3f;

    [Tooltip("Speed, in units per second, given to a body at the centre of the blast. It falls off to nothing at the radius.")]
    public float BlastSpeed = 6f;

    [Tooltip("Extra upward speed at the centre of the blast, falling off the same way.")]
    public float BlastUplift = 2f;

    [Tooltip("Radius of the crater dug in every PixelTerrain2D the blast reaches. 0: none.")]
    public float CraterRadius = 0.8f;

    [Header("Trigger"), Tooltip("Explode by itself when it hits something hard enough.")]
    public bool ExplodeOnImpact;

    [Tooltip("The impact (the collision's impulse, in newton-seconds) that sets it off.")]
    public float ImpactImpulse = 6f;

    /// <summary>Raised after an explosion, with the fragments (each has a Rigidbody2D and a PolygonCollider2D) and the blast centre.</summary>
    public event Action<IReadOnlyList<GameObject>, Float2>? Exploded;

    [NonSerialized] private bool _exploded;
    [NonSerialized] private bool _pending;
    [NonSerialized] private Float2 _pendingCentre;
    [NonSerialized] private Fracturer? _fracturer;
    [NonSerialized] private readonly List<float> _poly = new();
    [NonSerialized] private readonly List<Collider2D> _hits = new();

    /// <summary>Has this already exploded?</summary>
    public bool HasExploded => _exploded;

    /// <summary>Explodes now, with the blast at this object's position. Playing only.</summary>
    [Button("Explode")]
    public void Explode() => Explode(null);

    /// <summary>Explodes now. <paramref name="blastCentre"/> is where the blast comes from (null: this object's position).</summary>
    /// <returns>How many fragments were made; 0 if there is nothing to shatter (no usable collider, or already exploded).</returns>
    public int Explode(Float2? blastCentre)
    {
        if (_exploded) return 0;
        if (!Application.IsPlaying) { Debug.LogWarning($"[{Name}] Explodable2D only explodes while the game is playing."); return 0; }
        var scene = GameObject.Scene;
        if (!scene.IsValid()) return 0;

        if (!OutlineInWorld(out Collider2D? source) || _poly.Count < 6)
        {
            Debug.LogWarning($"[{Name}] Explodable2D needs a BoxCollider2D or a convex PolygonCollider2D on the same GameObject.");
            return 0;
        }

        _fracturer ??= new Fracturer();
        _fracturer.Seed(Seed != 0 ? Seed : (uint)Environment.TickCount);
        int pieces = _fracturer.Shatter(_poly, (int)Mode, Math.Max(0, ExtraPoints), Math.Max(0, SubshatterSteps));
        if (pieces == 0)
        {
            Debug.LogWarning($"[{Name}] Explodable2D could not shatter this outline (it is degenerate or concave).");
            return 0;
        }
        _exploded = true;

        Float3 here = Transform.Position;
        Float2 centre = blastCentre ?? new Float2(here.X, here.Y);

        // What the fragments inherit.
        var body = GetComponent<Rigidbody2D>();
        Float2 velocity = body.IsValid() ? body.LinearVelocity : Float2.Zero;
        float spin = body.IsValid() ? body.AngularVelocity : 0f;
        float sourceMass = body.IsValid() ? body.Mass : 0f;
        int layer = FragmentLayer >= 0 ? FragmentLayer : GameObject.LayerIndex;

        // Out of the way first, so the fragments do not spawn inside the shapes they came from.
        if (source.IsValid()) source.Enabled = false;
        if (body.IsValid()) body.Enabled = false;

        float totalArea = 0f;
        for (int p = 0; p < pieces; p++)
            totalArea += MathF.Abs(Fracturer.SignedArea(_fracturer.Pieces.Points, _fracturer.Pieces.Starts[p], _fracturer.Pieces.Counts[p]));

        var fragments = new List<GameObject>(pieces);
        var fragmentBodies = new HashSet<Rigidbody2D>();
        for (int p = 0; p < pieces; p++)
        {
            PolySet ps = _fracturer.Pieces;
            int start = ps.Starts[p], count = ps.Counts[p];
            float mx = 0f, my = 0f;                      // the pivot: the mean of the points, as the original does
            for (int k = 0; k < count; k++) { mx += ps.X(p, k); my += ps.Y(p, k); }
            mx /= count; my /= count;

            var points = new Float2[count];
            for (int k = 0; k < count; k++) points[k] = new Float2(ps.X(p, k) - mx, ps.Y(p, k) - my);

            var go = new GameObject($"{Name} Fragment {p}");
            scene.Add(go);
            go.LayerIndex = layer;
            go.Transform.Position = new Float3(mx, my, here.Z);

            var rb = go.AddComponent<Rigidbody2D>();
            rb.BodyType = BodyType2D.Dynamic;
            if (body.IsValid())
            {
                rb.GravityScale = body.GravityScale;
                rb.LinearDamping = body.LinearDamping;
                rb.AngularDamping = body.AngularDamping;
                if (totalArea > 0f)
                {
                    float area = MathF.Abs(Fracturer.SignedArea(ps.Points, start, count));
                    rb.Mass = sourceMass * area / totalArea;
                }
            }

            var col = go.AddComponent<PolygonCollider2D>();
            col.Points = points;
            if (source.IsValid())
            {
                col.Density = source.Density;
                col.Friction = source.Friction;
                col.Bounciness = source.Bounciness;
            }

            // The source's motion, plus the spin's share at this fragment's distance from the source's centre.
            float rx = mx - here.X, ry = my - here.Y;
            float w = spin * Maths.Deg2Rad;
            rb.LinearVelocity = new Float2(velocity.X - w * ry, velocity.Y + w * rx);
            rb.AngularVelocity = spin;

            if (FragmentLifetime > 0f) go.AddComponent<TimedDestroy2D>().Seconds = FragmentLifetime;
            fragments.Add(go);
            fragmentBodies.Add(rb);
        }

        Blast(centre, fragments, fragmentBodies);
        DigCraters(centre);

        if (DestroySource) GameObject.Destroy();
        else Enabled = false;

        Exploded?.Invoke(fragments, centre);
        return fragments.Count;
    }

    // The source's outline in world space (x, y pairs), and the collider it came from. False if it has no usable collider.
    private bool OutlineInWorld(out Collider2D? source)
    {
        source = null;
        _poly.Clear();

        Float2[]? local = null;
        Collider2D? col = null;
        var poly = GetComponent<PolygonCollider2D>();
        if (poly.IsValid())
        {
            local = poly.Points;
            col = poly;
        }
        else
        {
            var box = GetComponent<BoxCollider2D>();
            if (!box.IsValid()) return false;
            float hw = box.Size.X * 0.5f, hh = box.Size.Y * 0.5f;
            local = new[] { new Float2(-hw, -hh), new Float2(hw, -hh), new Float2(hw, hh), new Float2(-hw, hh) };
            col = box;
        }
        if (local == null || local.Length < 3) return false;

        // The same frame the collider builds its shape in: the offset point, scale, then rotation.
        Float3 centre = Transform.TransformPoint(new Float3(col.Offset.X, col.Offset.Y, 0f));
        Float3 lossy = Transform.LossyScale;
        Quaternion q = Transform.Rotation;
        float angle = Angle2D.FromQuaternionZ(q.X, q.Y, q.Z, q.W) + col.Rotation * Maths.Deg2Rad;

        source = col;
        for (int i = 0; i < local.Length; i++)
        {
            Collider2DGeometry.TransformPoint(local[i].X, local[i].Y, lossy.X, lossy.Y, angle, centre.X, centre.Y, out float wx, out float wy);
            _poly.Add(wx); _poly.Add(wy);
        }
        return true;
    }

    // Pushes the fragments and every other body in reach away from the blast, hardest at the centre, nothing at the radius.
    private void Blast(Float2 centre, List<GameObject> fragments, HashSet<Rigidbody2D> fragmentBodies)
    {
        if (BlastRadius <= 0f || (BlastSpeed == 0f && BlastUplift == 0f)) return;

        foreach (GameObject f in fragments)
        {
            Float3 p = f.Transform.Position;
            Push(f.GetComponent<Rigidbody2D>(), centre, new Float2(p.X, p.Y));
        }

        var scene = GameObject.Scene;
        if (!scene.IsValid()) return;
        scene.Physics2D.OverlapCircle(centre, BlastRadius, _hits);
        foreach (Collider2D c in _hits)
        {
            Rigidbody2D? rb = c.AttachedRigidbody;
            if (!rb.IsValid() || fragmentBodies.Contains(rb) || ReferenceEquals(rb.GameObject, GameObject)) continue;
            Float3 p = rb.Transform.Position;
            Push(rb, centre, new Float2(p.X, p.Y));
        }
        _hits.Clear();
    }

    private void Push(Rigidbody2D? rb, Float2 centre, Float2 at)
    {
        if (!rb.IsValid()) return;
        float dx = at.X - centre.X, dy = at.Y - centre.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist >= BlastRadius) return;
        float falloff = 1f - dist / BlastRadius;
        float nx = 0f, ny = 1f;
        if (dist > 1e-4f) { nx = dx / dist; ny = dy / dist; }
        rb.AddForce(new Float2(nx * BlastSpeed * falloff, ny * BlastSpeed * falloff + BlastUplift * falloff), ForceMode.VelocityChange);
    }

    private void DigCraters(Float2 centre)
    {
        if (CraterRadius <= 0f) return;
        var terrains = PixelTerrain2D.Active;
        for (int i = 0; i < terrains.Count; i++)
            terrains[i].DigCircle(centre, CraterRadius);
    }

    // ---- triggering ----------------------------------------------------------------------

    public override void OnCollisionBegin2D(Collision2D collision)
    {
        if (!ExplodeOnImpact || _exploded || _pending) return;
        if (collision.ImpulseMagnitude < ImpactImpulse) return;
        // Not now: this runs inside the physics step, and the explosion destroys shapes and makes bodies. Update does it.
        _pending = true;
        _pendingCentre = collision.Point;
    }

    public override void Update()
    {
        if (!_pending) return;
        _pending = false;
        Explode(_pendingCentre);
    }
}

/// <summary>Removes its GameObject after a time. Put on fragments by <see cref="Explodable2D"/>.</summary>
[AddComponentMenu("Physics 2D/Timed Destroy 2D")]
public sealed class TimedDestroy2D : MonoBehaviour
{
    [Tooltip("Seconds until this GameObject is destroyed.")]
    public float Seconds = 5f;

    [NonSerialized] private float _age;

    public override void Update()
    {
        _age += Time.DeltaTime;
        if (_age >= Seconds) GameObject.Destroy();
    }
}
