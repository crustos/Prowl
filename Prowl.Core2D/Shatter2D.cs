// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Runtime.Destruction2D;
using Prowl.Native.Box2D;

namespace Prowl.Core2D;

/// <summary>
/// Shatters nodes into fragments, as Unity-2D-Destruction's Explodable.explode() does, and pushes things away from a point (its demo's
/// ExplosionForce). A fragment is a node of its own: a dynamic rigidbody, a polygon collider and a <see cref="MeshRenderer2D"/> showing its part of
/// the source's texture, moving as the source was moving. The source node is destroyed.
/// <para/>
/// What can be shattered: a node with a box or polygon collider, convex (the pieces are convex, and a native polygon shape is limited to
/// <see cref="Fracturer.MaxVertices"/> points, so a piece with more is several fragments). All counts are bounded by the scene's pools; when one runs
/// out, <see cref="Explode"/> stops and keeps the fragments made so far, and tells how many.
/// <para/>
/// An arena class of capacity 1, made once per scene.
/// </summary>
[MaxInstances(1)]
internal sealed class Shatter2D
{
    public List<Node> Fragments;         // the fragments of the last Explode
    public List<float> Areas;            // their areas, in the source's units (before its scale)
    public int Requested;                // how many fragments the last Explode asked for; fewer are made when the scene's pools run out

    private Scene2D _scene;
    private Fracturer _fr;
    private List<float> _poly;
    private float[] _xy;
    private float[] _uv;

    public Shatter2D(Scene2D scene, uint seed)
    {
        _scene = scene;
        _fr = new Fracturer();
        _fr.Seed(seed);
        _poly = new List<float>();
        _xy = new float[CoreLimits.MeshVertices * 2];
        _uv = new float[CoreLimits.MeshVertices * 2];
        Fragments = new List<Node>();
        Areas = new List<float>();
    }

    public void Seed(uint seed) { _fr.Seed(seed); }

    /// <summary>
    /// Shatters the node. Needs a box or polygon collider on it; takes its rigidbody's velocity, and the size of its sprite for the texture
    /// coordinates (the shape's bounding box if it has none). The node is destroyed.
    /// </summary>
    /// <returns>The number of fragments made (also <c>Fragments.Count</c>); 0 if the node cannot be shattered, and then it is left as it is</returns>
    public int Explode(Node source, ExplodeOptions o)
    {
        Fragments.Clear();
        Areas.Clear();
        Requested = 0;
        if (!_scene.IsLive(source)) return 0;

        Component cc = _scene.Find(source, ComponentKind.PolygonCollider2D);
        if (cc == null) cc = _scene.Find(source, ComponentKind.BoxCollider2D);
        if (cc == null) return 0;
        Collider2D col = cc.Collider;
        _poly.Clear();
        if (col.ShapeKind == Collider2D.Polygon)
        {
            for (int i = 0; i < col.PolyCount * 2; i++) _poly.Add(col.Poly[i]);
        }
        else
        {
            float hw = col.SizeX * 0.5f, hh = col.SizeY * 0.5f;      // the box's corners, as the original lists them
            _poly.Add(col.OffsetX - hw); _poly.Add(col.OffsetY - hh);
            _poly.Add(col.OffsetX - hw); _poly.Add(col.OffsetY + hh);
            _poly.Add(col.OffsetX + hw); _poly.Add(col.OffsetY + hh);
            _poly.Add(col.OffsetX + hw); _poly.Add(col.OffsetY - hh);
        }

        // the sprite's rectangle in the node's space: where the texture coordinates 0..1 lie
        float rx0 = _poly[0], rx1 = _poly[0], ry0 = _poly[1], ry1 = _poly[1];
        Component sc = _scene.Find(source, ComponentKind.SpriteRenderer2D);
        if (sc != null)
        {
            rx0 = -sc.Sprite.Width * 0.5f; rx1 = sc.Sprite.Width * 0.5f;
            ry0 = -sc.Sprite.Height * 0.5f; ry1 = sc.Sprite.Height * 0.5f;
        }
        else
        {
            for (int i = 1; i < _poly.Count / 2; i++)
            {
                if (_poly[2 * i] < rx0) rx0 = _poly[2 * i];
                if (_poly[2 * i] > rx1) rx1 = _poly[2 * i];
                if (_poly[2 * i + 1] < ry0) ry0 = _poly[2 * i + 1];
                if (_poly[2 * i + 1] > ry1) ry1 = _poly[2 * i + 1];
            }
        }
        float rw = rx1 - rx0, rh = ry1 - ry0;
        if (rw <= 0f || rh <= 0f) return 0;

        int pieces = _fr.Shatter(_poly, o.Mode, o.ExtraPoints, o.SubshatterSteps);
        if (pieces == 0) return 0;
        Requested = pieces;

        float angle = source.WorldAngle();
        float sx = MathF.Abs(source.LossyScaleX()), sy = MathF.Abs(source.LossyScaleY());
        float vx = 0f, vy = 0f;
        Component bc = _scene.Find(source, ComponentKind.Rigidbody2D);
        if (bc != null)
        {
            vx = bc.Body.VelocityX();
            vy = bc.Body.VelocityY();
        }
        float density = col.Density, friction = col.Friction, bounciness = col.Bounciness;

        for (int p = 0; p < pieces; p++)
        {
            int ps = _fr.Pieces.Starts[p];
            int c = _fr.Pieces.Counts[p];
            float mx = 0f, my = 0f;                                      // the fragment's pivot: the mean of its points, as the original does
            for (int k = 0; k < c; k++)
            {
                mx += _fr.Pieces.Points[2 * (ps + k)];
                my += _fr.Pieces.Points[2 * (ps + k) + 1];
            }
            mx /= c;
            my /= c;
            for (int k = 0; k < c; k++)
            {
                float px = _fr.Pieces.Points[2 * (ps + k)];
                float py = _fr.Pieces.Points[2 * (ps + k) + 1];
                _xy[2 * k] = px - mx;
                _xy[2 * k + 1] = py - my;
                _uv[2 * k] = o.UvMinX + (px - rx0) / rw * (o.UvMaxX - o.UvMinX);
                _uv[2 * k + 1] = o.UvMinY + (py - ry0) / rh * (o.UvMaxY - o.UvMinY);
            }

            Node f = _scene.NewNode(null);
            if (f == null) break;
            float wx, wy;
            source.ToWorld(mx, my, out wx, out wy);
            f.SetLocal(wx, wy, angle, sx, sy);
            f.Layer = o.FragmentLayer;

            Rigidbody2D rb = _scene.AddRigidbody(f, PB2.BodyDynamic);
            Collider2D pc = _scene.NewPolygonCollider(f, _xy, c);
            MeshRenderer2D mesh = _scene.NewMesh(f, _xy, _uv, c);
            if (rb == null || pc == null || mesh == null)               // a pool ran out: this one cannot be finished
            {
                _scene.Destroy(f);
                break;
            }
            pc.Density = density;
            pc.Friction = friction;
            pc.Bounciness = bounciness;
            _scene.Finish(pc.Self);
            mesh.Texture = o.Texture;
            mesh.SetColor(o.R, o.G, o.B, o.A);
            mesh.Layer = o.RenderLayer;
            _scene.Finish(mesh.Self);
            rb.SetVelocity(vx, vy);

            Fragments.Add(f);
            Areas.Add(Fracturer.SignedArea(_fr.Pieces.Points, ps, c));
        }
        if (Fragments.Count == 0) return 0;
        _scene.Destroy(source);                                          // its collider and body go at once, so they cannot push the fragments
        return Fragments.Count;
    }

    /// <summary>
    /// Pushes every rigidbody with a collider within the radius away from the point, as the original's demo script does: a force along the line from the
    /// point, strongest at the point and fading to nothing at the radius, plus an upward force of <c>force * (1 - uplift / radius)</c> (the original's
    /// own formula: it does not depend on the distance). Forces last for the next step, as a force does.
    /// </summary>
    /// <returns>The number of colliders pushed</returns>
    public int AddExplosionForce(float x, float y, float radius, float force, float uplift)
    {
        World2D w = _scene.Physics;
        int n = w.Sim.OverlapCircle(x, y, radius, 0xFFFFFFFFu, false);
        int pushed = 0;
        for (int i = 0; i < n; i++)
        {
            Collider2D col = w.ColliderByIndex(w.Sim.OverlapResult(i));
            if (col == null) continue;
            Rigidbody2D rb = col.Attached;
            if (rb == null) continue;
            Node bn = rb.Self.Node;
            float dx = bn.WorldX() - x;
            float dy = bn.WorldY() - y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float wearoff = 1f - dist / radius;
            if (dist > 1e-6f) rb.AddForce(dx / dist * force * wearoff, dy / dist * force * wearoff);
            if (uplift != 0f) rb.AddForce(0f, force * (1f - uplift / radius));
            pushed++;
        }
        return pushed;
    }
}
