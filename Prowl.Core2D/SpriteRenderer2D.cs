// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Core2D;

/// <summary>
/// The data of a SpriteRenderer2D component: a coloured box or disc drawn where its node is, as large as <see cref="Width"/> by <see cref="Height"/>
/// times the node's scale, turned with the node. Drawn by the renderer in <c>Native/Gfx2D</c>: every enabled sprite of a frame is one 24-byte instance
/// of one instanced draw call, ordered by <see cref="Layer"/> (a higher layer over a lower, equal layers in the order they were enabled).
/// <para/>
/// Its values are read every frame, so a script may change a colour or a layer at any time (unlike a physics component's settings).
/// An arena class: a pool of <see cref="CoreLimits.Sprites"/>, recycled when the component is freed.
/// </summary>
[MaxInstances(CoreLimits.Sprites)]
internal sealed class SpriteRenderer2D
{
    public const int Box = 0;            // = GFX_SHAPE_BOX
    public const int Disc = 1;           // = GFX_SHAPE_DISC

    public Component Self;
    public int Shape;
    public float Width, Height;          // the full size, before the node's scale
    public float R, G, B, A;             // the tint, 0..1
    public int Layer;                    // 0..255
    public bool Visible;

    public void Reset(int shape, float width, float height)
    {
        Self = null;
        Shape = shape;
        Width = width;
        Height = height;
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
