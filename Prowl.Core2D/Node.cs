// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.Runtime.Physics2D;

namespace Prowl.Core2D;

/// <summary>
/// A place in the scene: a position, an angle and a scale, in a tree. This is the engine's GameObject and Transform for the 2D runtime, with every
/// reference an arena reference, so the whole hierarchy translates to C and a node is a pointer into a small static array.
/// <para/>
/// <b>Transform.</b> Local position, angle (radians, counter-clockwise) and scale; the world transform is a 2x3 matrix
/// (<c>x' = a x + c y + tx</c>, <c>y' = b x + d y + ty</c>) built from the parent's and cached. The cache has one invariant, which is what keeps
/// invalidation cheap: <b>a node whose world is invalid has only invalid descendants</b>. A change therefore marks a subtree and stops at the first
/// node that is already stale, and a read recomputes only the stale path above it.
/// <para/>
/// <b>Versions.</b> <see cref="Version"/> is a unique, ever-increasing id given to a node whenever its local transform changes or it is reparented.
/// <see cref="WorldVersion"/> is the greatest id along the path to the root, so it rises when the node, or anything above it, is edited. A body that
/// writes its pose into a node remembers the world version it left behind and treats any other value as somebody else's edit.
/// <para/>
/// Non-uniform scale under a rotated parent shears the child, as in any 2D engine that stores matrices; <see cref="LossyScaleX"/> and
/// <see cref="WorldAngle"/> are the lengths and direction of the matrix's columns, and <see cref="SetParent"/> with keepWorld preserves position
/// and angle, not scale.
/// </summary>
[MaxInstances(CoreLimits.Nodes)]
internal sealed class Node
{
    private static int s_epoch;

    public int Index;                    // the node's slot in the scene
    public bool Alive;                   // false while its slot is free
    public int Tag;                      // for the game: what kind of thing this is
    public int Layer;                    // the physics layer of the node's colliders (0..31)
    public bool ActiveSelf;              // set by the game (Scene2D.SetActive)
    public bool ActiveInHierarchy;       // ActiveSelf, and every ancestor's too; kept by the scene
    public bool Destroyed;               // marked for removal at the end of the frame

    public Node Parent;
    public Node FirstChild;
    public Node LastChild;
    public Node NextSibling;
    public Node PrevSibling;
    public int ChildCount;

    public float X, Y;                   // local position
    public float Angle;                  // local angle
    public float ScaleX, ScaleY;
    public int Version;                  // unique id of the last change to the local transform or the parent

    private float _a, _b, _c, _d, _tx, _ty;
    private int _worldVersion;
    private bool _worldValid;

    /// <summary>Back to a new node's state, for a recycled slot.</summary>
    public void Reset(int index)
    {
        Index = index;
        Alive = true;
        Tag = 0;
        Layer = 0;
        ActiveSelf = true;
        ActiveInHierarchy = true;
        Destroyed = false;
        Parent = null;
        FirstChild = null;
        LastChild = null;
        NextSibling = null;
        PrevSibling = null;
        ChildCount = 0;
        X = 0f;
        Y = 0f;
        Angle = 0f;
        ScaleX = 1f;
        ScaleY = 1f;
        Version = 0;
        _worldValid = false;
    }

    // ---- local transform -----------------------------------------------------------------

    private void Touch()
    {
        s_epoch = s_epoch + 1;
        Version = s_epoch;
        InvalidateWorld();
    }

    // Stops at a node that is already stale: by the invariant, everything below it is too.
    private void InvalidateWorld()
    {
        if (!_worldValid) return;
        _worldValid = false;
        for (Node c = FirstChild; c != null; c = c.NextSibling) c.InvalidateWorld();
    }

    public void SetPosition(float x, float y)
    {
        X = x;
        Y = y;
        Touch();
    }

    public void SetAngle(float angle)
    {
        Angle = angle;
        Touch();
    }

    public void SetScale(float sx, float sy)
    {
        ScaleX = sx;
        ScaleY = sy;
        Touch();
    }

    public void SetLocal(float x, float y, float angle, float sx, float sy)
    {
        X = x;
        Y = y;
        Angle = angle;
        ScaleX = sx;
        ScaleY = sy;
        Touch();
    }

    // ---- world transform -----------------------------------------------------------------

    private void EnsureWorld()
    {
        if (_worldValid) return;
        float c = MathF.Cos(Angle), s = MathF.Sin(Angle);
        float la = c * ScaleX, lb = s * ScaleX, lc = -s * ScaleY, ld = c * ScaleY;
        if (Parent == null)
        {
            _a = la; _b = lb; _c = lc; _d = ld;
            _tx = X; _ty = Y;
            _worldVersion = Version;
        }
        else
        {
            Parent.EnsureWorld();
            Node p = Parent;
            _a = p._a * la + p._c * lb;
            _b = p._b * la + p._d * lb;
            _c = p._a * lc + p._c * ld;
            _d = p._b * lc + p._d * ld;
            _tx = p._a * X + p._c * Y + p._tx;
            _ty = p._b * X + p._d * Y + p._ty;
            _worldVersion = Version > p._worldVersion ? Version : p._worldVersion;
        }
        _worldValid = true;
    }

    public float WorldX() { EnsureWorld(); return _tx; }
    public float WorldY() { EnsureWorld(); return _ty; }
    public float WorldAngle() { EnsureWorld(); return MathF.Atan2(_b, _a); }
    public float LossyScaleX() { EnsureWorld(); return MathF.Sqrt(_a * _a + _b * _b); }

    /// <summary>The length of the matrix's second column, negative if the node is mirrored.</summary>
    public float LossyScaleY()
    {
        EnsureWorld();
        float sy = MathF.Sqrt(_c * _c + _d * _d);
        return _a * _d - _b * _c < 0f ? -sy : sy;
    }

    /// <summary>Changes whenever this node or any ancestor is edited or reparented, and never goes back.</summary>
    public int WorldVersion() { EnsureWorld(); return _worldVersion; }

    public void ToWorld(float lx, float ly, out float wx, out float wy)
    {
        EnsureWorld();
        wx = _a * lx + _c * ly + _tx;
        wy = _b * lx + _d * ly + _ty;
    }

    /// <summary>A world point in this node's space. A collapsed node (scale zero) has no inverse: it is treated as a translation.</summary>
    public void ToLocal(float wx, float wy, out float lx, out float ly)
    {
        EnsureWorld();
        float dx = wx - _tx, dy = wy - _ty;
        float det = _a * _d - _b * _c;
        if (det > -1e-12f && det < 1e-12f)
        {
            lx = dx;
            ly = dy;
            return;
        }
        float inv = 1f / det;
        lx = (_d * dx - _c * dy) * inv;
        ly = (-_b * dx + _a * dy) * inv;
    }

    /// <summary>Places the node at a world position and angle, by changing its local transform (scale is left alone).</summary>
    public void SetWorldPose(float wx, float wy, float wangle)
    {
        if (Parent == null)
        {
            X = wx;
            Y = wy;
            Angle = wangle;
        }
        else
        {
            float lx, ly;
            Parent.ToLocal(wx, wy, out lx, out ly);
            X = lx;
            Y = ly;
            Angle = Angle2D.WrapPi(wangle - Parent.WorldAngle());
        }
        Touch();
    }

    // ---- hierarchy -----------------------------------------------------------------------

    public bool IsDescendantOf(Node other)
    {
        for (Node n = Parent; n != null; n = n.Parent)
            if (n == other) return true;
        return false;
    }

    /// <summary>
    /// Moves the node under <paramref name="parent"/> (null for the root), as that parent's last child. Returns false, changing nothing, if that
    /// would make the node its own ancestor. With <paramref name="keepWorld"/> the node keeps its world position and angle; without, it keeps its
    /// local transform and so moves.
    /// </summary>
    public bool SetParent(Node parent, bool keepWorld)
    {
        if (parent == this || (parent != null && parent.IsDescendantOf(this))) return false;
        float wx = 0f, wy = 0f, wa = 0f;
        if (keepWorld)
        {
            wx = WorldX();
            wy = WorldY();
            wa = WorldAngle();
        }
        Unlink();
        LinkTo(parent);
        Touch();                                  // its world changed even though its local did not
        if (keepWorld) SetWorldPose(wx, wy, wa);
        return true;
    }

    /// <summary>Takes the node out of its parent's children (it keeps its local transform).</summary>
    public void Detach()
    {
        if (Parent == null) return;
        Unlink();
        Touch();
    }

    private void LinkTo(Node parent)
    {
        Parent = parent;
        NextSibling = null;
        PrevSibling = null;
        if (parent == null) return;
        if (parent.LastChild == null)
        {
            parent.FirstChild = this;
            parent.LastChild = this;
        }
        else
        {
            PrevSibling = parent.LastChild;
            parent.LastChild.NextSibling = this;
            parent.LastChild = this;
        }
        parent.ChildCount++;
    }

    private void Unlink()
    {
        if (Parent != null)
        {
            if (PrevSibling != null) PrevSibling.NextSibling = NextSibling;
            else Parent.FirstChild = NextSibling;
            if (NextSibling != null) NextSibling.PrevSibling = PrevSibling;
            else Parent.LastChild = PrevSibling;
            Parent.ChildCount--;
        }
        Parent = null;
        NextSibling = null;
        PrevSibling = null;
    }
}
