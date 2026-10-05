// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace Prowl.Core2D;

/// <summary>
/// Turns the scene's sprites into a draw batch: the enabled <see cref="SpriteRenderer2D"/>s, in layer order, as <see cref="CoreLimits.SpriteFloats"/>
/// floats each in <see cref="DrawData"/>, which a game hands to the renderer (<c>GFX.Draw(scene.Render.DrawData, scene.CollectSprites())</c>). The batch
/// is what Native/Gfx2D/gfx2d.h describes: x, y, half width, half height, angle, r, g, b, a, shape, layer.
/// <para/>
/// Nothing here draws. It reads node poses (the physics write-back has already interpolated them) and fills an array, so it translates to C and is
/// checked like the rest: the batch it makes is the same on .NET and in C.
/// <para/>
/// An arena class of capacity 1.
/// </summary>
[MaxInstances(1)]
internal sealed class Renderer2D
{
    public float[] DrawData;
    public float[] MeshData;                   // the triangle list of CollectMeshes: x, y, u, v, r, g, b, a per vertex
    private MeshRenderer2D[] _meshPool;
    private List<int> _meshFree;
    private int _meshHigh;
    private List<MeshRenderer2D> _meshes;       // enabled meshes, kept sorted by layer like the sprites

    private SpriteRenderer2D[] _pool;
    private List<int> _free;
    private int _high;
    private List<SpriteRenderer2D> _enabled;       // enabled sprites, kept sorted by layer, equal layers in the order they were enabled

    public Renderer2D()
    {
        DrawData = new float[CoreLimits.Sprites * CoreLimits.SpriteFloats];
        _pool = new SpriteRenderer2D[CoreLimits.Sprites];
        _free = new List<int>();
        _enabled = new List<SpriteRenderer2D>();
        MeshData = new float[CoreLimits.MeshBatchFloats];
        _meshPool = new MeshRenderer2D[CoreLimits.Meshes];
        _meshFree = new List<int>();
        _meshes = new List<MeshRenderer2D>();
    }

    /// <summary>A SpriteRenderer2D on a node, not yet enabled: set its colour and layer, then <see cref="Scene2D.Finish"/> its <c>Self</c>. Null if all are in use.</summary>
    public SpriteRenderer2D NewSprite(Scene2D scene, Node node, int shape, float width, float height)
    {
        int slot;
        if (_free.Count > 0)
        {
            slot = _free[_free.Count - 1];
            _free.RemoveAt(_free.Count - 1);
        }
        else
        {
            if (_high >= CoreLimits.Sprites) return null;
            slot = _high;
            _high++;
        }
        SpriteRenderer2D sprite = _pool[slot];
        if (sprite == null)
        {
            sprite = new SpriteRenderer2D();
            _pool[slot] = sprite;
        }
        sprite.Reset(shape, width, height);
        Component c = scene.Create(node, ComponentKind.SpriteRenderer2D, slot, 0, 0);
        if (c == null)
        {
            _free.Add(slot);
            return null;
        }
        c.Sprite = sprite;
        sprite.Self = c;
        return sprite;
    }

    /// <summary>A MeshRenderer2D on a node, not yet enabled. Null if there are too many or too few points, or all are in use.</summary>
    public MeshRenderer2D NewMesh(Scene2D scene, Node node, float[] xy, float[] uv, int count)
    {
        if (count < 3 || count > CoreLimits.MeshVertices) return null;
        int slot;
        if (_meshFree.Count > 0)
        {
            slot = _meshFree[_meshFree.Count - 1];
            _meshFree.RemoveAt(_meshFree.Count - 1);
        }
        else
        {
            if (_meshHigh >= CoreLimits.Meshes) return null;
            slot = _meshHigh;
            _meshHigh++;
        }
        MeshRenderer2D mesh = _meshPool[slot];
        if (mesh == null)
        {
            mesh = new MeshRenderer2D();
            _meshPool[slot] = mesh;
        }
        mesh.Reset(count);
        for (int i = 0; i < count * 2; i++)
        {
            mesh.Points[i] = xy[i];
            mesh.Uvs[i] = uv[i];
        }
        Component c = scene.Create(node, ComponentKind.MeshRenderer2D, slot, 0, 0);
        if (c == null)
        {
            _meshFree.Add(slot);
            return null;
        }
        c.Mesh = mesh;
        mesh.Self = c;
        return mesh;
    }

    public void EnableComponent(Component c)
    {
        if (c.Kind == ComponentKind.SpriteRenderer2D) _enabled.Add(c.Sprite);
        else if (c.Kind == ComponentKind.MeshRenderer2D) _meshes.Add(c.Mesh);
    }

    public void DisableComponent(Component c)
    {
        if (c.Kind == ComponentKind.MeshRenderer2D)
        {
            for (int i = 0; i < _meshes.Count; i++)
            {
                if (_meshes[i] == c.Mesh)
                {
                    _meshes.RemoveAt(i);
                    return;
                }
            }
            return;
        }
        if (c.Kind != ComponentKind.SpriteRenderer2D) return;
        for (int i = 0; i < _enabled.Count; i++)
        {
            if (_enabled[i] == c.Sprite)
            {
                _enabled.RemoveAt(i);                  // order is kept: the others slide down
                return;
            }
        }
    }

    public void FreeComponent(Component c)
    {
        if (c.Kind == ComponentKind.SpriteRenderer2D) _free.Add(c.Slot);
        else if (c.Kind == ComponentKind.MeshRenderer2D) _meshFree.Add(c.Slot);
    }

    /// <summary>Fills <see cref="DrawData"/> with this frame's sprites and returns how many there are.</summary>
    public int Collect()
    {
        // a stable insertion sort by layer: the list is nearly in order from one frame to the next, so this is close to one pass
        for (int i = 1; i < _enabled.Count; i++)
        {
            SpriteRenderer2D s = _enabled[i];
            int k = i;
            while (k > 0)
            {
                SpriteRenderer2D before = _enabled[k - 1];
                if (before.Layer <= s.Layer) break;
                _enabled[k] = before;
                k--;
            }
            _enabled[k] = s;
        }
        int n = 0;
        for (int i = 0; i < _enabled.Count; i++)
        {
            SpriteRenderer2D s = _enabled[i];
            if (!s.Visible) continue;
            Node node = s.Self.Node;
            int b = n * CoreLimits.SpriteFloats;
            DrawData[b] = node.WorldX();
            DrawData[b + 1] = node.WorldY();
            DrawData[b + 2] = s.Width * 0.5f * MathF.Abs(node.LossyScaleX());
            DrawData[b + 3] = s.Height * 0.5f * MathF.Abs(node.LossyScaleY());
            DrawData[b + 4] = node.WorldAngle();
            DrawData[b + 5] = s.R;
            DrawData[b + 6] = s.G;
            DrawData[b + 7] = s.B;
            DrawData[b + 8] = s.A;
            DrawData[b + 9] = (float)s.Shape;
            DrawData[b + 10] = (float)s.Layer;
            DrawData[b + 11] = 0f;
            n++;
        }
        return n;
    }

    /// <summary>
    /// Fills <see cref="MeshData"/> with this frame's meshes, in layer order, as a triangle list in world space (each mesh a fan from its first point):
    /// x, y, u, v, r, g, b, a per vertex. Returns the number of vertices.
    /// </summary>
    public int CollectMeshes()
    {
        for (int i = 1; i < _meshes.Count; i++)
        {
            MeshRenderer2D m = _meshes[i];
            int k = i;
            while (k > 0)
            {
                MeshRenderer2D before = _meshes[k - 1];
                if (before.Layer <= m.Layer) break;
                _meshes[k] = before;
                k--;
            }
            _meshes[k] = m;
        }
        int n = 0;
        for (int i = 0; i < _meshes.Count; i++)
        {
            MeshRenderer2D m = _meshes[i];
            if (!m.Visible) continue;
            Node node = m.Self.Node;
            for (int t = 1; t < m.Count - 1; t++)
            {
                for (int v = 0; v < 3; v++)
                {
                    int p = 0;
                    if (v == 1) p = t;
                    else if (v == 2) p = t + 1;
                    float wx, wy;
                    node.ToWorld(m.Points[2 * p], m.Points[2 * p + 1], out wx, out wy);
                    int b = n * CoreLimits.MeshVertexFloats;
                    MeshData[b] = wx;
                    MeshData[b + 1] = wy;
                    MeshData[b + 2] = m.Uvs[2 * p];
                    MeshData[b + 3] = m.Uvs[2 * p + 1];
                    MeshData[b + 4] = m.R;
                    MeshData[b + 5] = m.G;
                    MeshData[b + 6] = m.B;
                    MeshData[b + 7] = m.A;
                    n++;
                }
            }
        }
        return n;
    }
}
