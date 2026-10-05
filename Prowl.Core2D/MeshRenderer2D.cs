// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Core2D;

/// <summary>
/// The data of a MeshRenderer2D component: a convex polygon with a texture coordinate on each point and a tint, drawn where its node is, turned and
/// scaled with it, as a fan of triangles from its first point. This is how a fragment of a shattered sprite is drawn (its points are the piece, its
/// coordinates the piece's place in the sprite's texture), and it draws any convex polygon sprite.
/// <para/>
/// The points are in the node's space; <see cref="Texture"/> says which texture the coordinates are in (0 is the renderer's plain white). Like a sprite,
/// its colour and layer are read every frame, and equal layers are drawn in the order they were enabled. An arena class: a pool of
/// <see cref="CoreLimits.Meshes"/>, recycled when the component is freed.
/// </summary>
[MaxInstances(CoreLimits.Meshes)]
internal sealed class MeshRenderer2D
{
    public Component Self;
    public float[] Points;               // x, y pairs, in the node's space
    public float[] Uvs;                  // u, v pairs
    public int Count;
    public int Texture;
    public float R, G, B, A;             // the tint, 0..1
    public int Layer;                    // 0..255
    public bool Visible;

    public MeshRenderer2D()
    {
        Points = new float[CoreLimits.MeshVertices * 2];
        Uvs = new float[CoreLimits.MeshVertices * 2];
    }

    public void Reset(int count)
    {
        Self = null;
        Count = count;
        Texture = 0;
        R = 1f;
        G = 1f;
        B = 1f;
        A = 1f;
        Layer = 0;
        Visible = true;
    }

    public void SetColor(float r, float g, float b, float a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }
}
