// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using Prowl.Runtime.Destruction2D;
using Prowl.Native.Box2D;

namespace Prowl.Core2D;

/// <summary>
/// A destructible terrain: a bitmap of Width x Height pixels (RGBA, row 0 at the bottom, so it uploads to GL as it is), cut into
/// ChunksX x ChunksY chunks. Digging and building change the pixels and the chunks' columns; <see cref="Update"/> then rebuilds the
/// native shapes of the chunks that changed.
/// <para/>
/// The ground of each chunk is a node with a terrain collider (a static body, and the collider index contact events name), so a ball that touches the
/// ground is told, like any collision, which chunk it touched: one Begin when it first touches the chunk's shapes and one End when it leaves them,
/// however many of the shapes that is. In <see cref="Boxes"/> mode the shapes are the rectangles PixelRectMerge makes of the columns; in
/// <see cref="Chains"/> mode they are the ground's outline, one-sided chains with the ground on their left, which are smooth for things that roll
/// over them. A dig rebuilds the shapes of the chunks it changed in place: the contacts the old shapes had are handed to the simulation first
/// (<c>SimCore2D.ShapesVanishing</c>), so what still touches the chunk hears nothing and what lost its ground hears an End.
/// <para/>
/// A chunk costs a node and a collider of the scene's pools.
/// <para/>
/// World units: pixel (x, y) is the square from (x, y) to (x + 1, y + 1) pixels, and a pixel is 1 / PixelsPerUnit units.
/// </summary>
[MaxInstances(PixelTerrainLimits.Layers)]
internal sealed class PixelTerrain2D
{
    public const int Boxes = 0;
    public const int Chains = 1;

    public int Width, Height;               // pixels
    public int ChunksX, ChunksY;
    public int ChunkWidth, ChunkHeight;     // pixels
    public float PixelsPerUnit;
    public float OriginX, OriginY;          // the world position of the lower-left corner
    public int ColliderKind;                // Boxes or Chains
    public int PhysicsLayer;                // the native collision layer of the shapes
    public float Friction = 0.6f;
    public float Bounciness = 0f;
    public int MaxChainPoints = PixelTerrainLimits.ChainPoints;   // at most PixelTerrainLimits.ChainPoints; lower it to make longer outlines split sooner (tests)
    public int AlphaThreshold = 2;          // a pixel is ground when its alpha is above this

    public byte[] Pixels;                   // (y * Width + x) * 4 : r, g, b, a
    public List<PixelChunk> Chunks;

    // the pixels changed since ClearDirty: a renderer uploads this rectangle (inclusive), not the whole bitmap
    public bool PixelsDirty;
    public int DirtyX0, DirtyY0, DirtyX1, DirtyY1;

    public bool Ok;                         // the layer was built
    public int ShapeCount;                  // live native shapes / chains over all chunks

    private bool _hasPixels;
    private Scene2D _scene;
    private World2D _world;
    private List<Node> _nodes;                   // one node and terrain collider per chunk, in the order of Chunks: what contact events name
    private List<Collider2D> _cols;
    private List<PixelRect> _rects;
    private PixelChainSet _chains;
    private float[] _xy;

    /// <summary>Makes the layer; <see cref="Build"/> comes after the pixels are filled in. The size must divide into whole chunks.</summary>
    public PixelTerrain2D(Scene2D scene, int width, int height, int chunksX, int chunksY, float pixelsPerUnit, float originX, float originY, int colliderKind)
    {
        Width = width;
        Height = height;
        ChunksX = chunksX;
        ChunksY = chunksY;
        PixelsPerUnit = pixelsPerUnit;
        OriginX = originX;
        OriginY = originY;
        ColliderKind = colliderKind;
        _scene = scene;
        _world = scene.Physics;
        _nodes = new List<Node>();
        _cols = new List<Collider2D>();
        Chunks = new List<PixelChunk>();
        _rects = new List<PixelRect>();
        _chains = new PixelChainSet();
        _xy = new float[PixelTerrainLimits.ChainPoints * 2];      // allocated once: nothing is ever grown or replaced
        if (width > 0 && height > 0 && chunksX > 0 && chunksY > 0 && width % chunksX == 0 && height % chunksY == 0 && pixelsPerUnit > 0f)
        {
            ChunkWidth = width / chunksX;
            ChunkHeight = height / chunksY;
            Pixels = new byte[width * height * 4];
            _hasPixels = true;
        }
        else Pixels = new byte[4];
    }

    /// <summary>Does the bitmap exist (the size divided into whole chunks)?</summary>
    public bool HasPixels { get { return _hasPixels; } }

    // ---- pixels ----------------------------------------------------------------------------

    public void SetPixel(int x, int y, int r, int g, int b, int a)
    {
        if (!_hasPixels || x < 0 || x >= Width || y < 0 || y >= Height) return;
        int i = (y * Width + x) * 4;
        Pixels[i] = (byte)r;
        Pixels[i + 1] = (byte)g;
        Pixels[i + 2] = (byte)b;
        Pixels[i + 3] = (byte)a;
    }

    public int AlphaAt(int x, int y)
    {
        if (!_hasPixels || x < 0 || x >= Width || y < 0 || y >= Height) return 0;
        return Pixels[(y * Width + x) * 4 + 3];
    }

    public void ClearDirty() { PixelsDirty = false; }

    private void MarkDirty(int x0, int y0, int x1, int y1)
    {
        if (!PixelsDirty)
        {
            PixelsDirty = true;
            DirtyX0 = x0; DirtyY0 = y0; DirtyX1 = x1; DirtyY1 = y1;
            return;
        }
        if (x0 < DirtyX0) DirtyX0 = x0;
        if (y0 < DirtyY0) DirtyY0 = y0;
        if (x1 > DirtyX1) DirtyX1 = x1;
        if (y1 > DirtyY1) DirtyY1 = y1;
    }

    // ---- building --------------------------------------------------------------------------

    /// <summary>
    /// Cuts the pixels into chunks, makes their columns, a node with a terrain collider for each, and builds the first shapes. False if the layer has no bitmap, or
    /// the scene has no room for a node and a collider per chunk.
    /// </summary>
    public bool Build()
    {
        if (!_hasPixels || Ok) return false;
        _world.PinWorld();
        for (int cx = 0; cx < ChunksX; cx++)
        {
            for (int cy = 0; cy < ChunksY; cy++)
            {
                PixelChunk c = new PixelChunk();
                c.Index = cx * ChunksY + cy;
                c.CX = cx;
                c.CY = cy;
                c.X0 = cx * ChunkWidth;
                c.Y0 = cy * ChunkHeight;
                c.Width = ChunkWidth;
                c.Height = ChunkHeight;
                PrepareColumns(c);
                Node node = _scene.NewNode(null);
                if (node == null) return FailBuild();
                node.SetPosition(OriginX + c.X0 / PixelsPerUnit, OriginY + c.Y0 / PixelsPerUnit);
                node.Layer = PhysicsLayer;
                Collider2D col = _scene.AddTerrainCollider(node);
                if (col == null || col.OwnBody == 0)
                {
                    _scene.Destroy(node);
                    return FailBuild();
                }
                c.Body = col.OwnBody;
                _nodes.Add(node);
                _cols.Add(col);
                c.Dirty = true;
                Chunks.Add(c);
            }
        }
        for (int i = 0; i < Chunks.Count; i++)
        {
            PixelChunk c = Chunks[i];
            c.Left = ChunkAt(c.CX - 1, c.CY);
            c.Right = ChunkAt(c.CX + 1, c.CY);
            c.Down = ChunkAt(c.CX, c.CY - 1);
            c.Up = ChunkAt(c.CX, c.CY + 1);
        }
        Ok = true;
        Update();
        return true;
    }

    private bool FailBuild()
    {
        for (int i = 0; i < _nodes.Count; i++) _scene.Destroy(_nodes[i]);
        _nodes.Clear();
        _cols.Clear();
        Chunks.Clear();
        _world.UnpinWorld();
        return false;
    }

    /// <summary>One column per pixel column: a range for each run of pixels whose alpha is above the threshold.</summary>
    private void PrepareColumns(PixelChunk c)
    {
        c.Columns.Clear();
        for (int x = 0; x < c.Width; x++)
        {
            c.Columns.Add(new PixelColumn(x));
            int y = 0;
            while (y < c.Height)
            {
                if (AlphaAt(c.X0 + x, c.Y0 + y) > AlphaThreshold)
                {
                    int min = y;
                    while (y < c.Height && AlphaAt(c.X0 + x, c.Y0 + y) > AlphaThreshold) y++;
                    c.Columns[x].AddRange(min, y - 1);
                }
                else y++;
            }
        }
    }

    // ---- painting --------------------------------------------------------------------------

    /// <summary>The chunk at a chunk grid position, null outside the grid.</summary>
    public PixelChunk ChunkAt(int cx, int cy)
    {
        if (cx < 0 || cx >= ChunksX || cy < 0 || cy >= ChunksY) return null;
        return Chunks[cx * ChunksY + cy];
    }

    public bool IsSolid(int x, int y)
    {
        if (!Ok || x < 0 || x >= Width || y < 0 || y >= Height) return false;
        PixelChunk c = Chunks[(x / ChunkWidth) * ChunksY + (y / ChunkHeight)];
        return c.Columns[x - c.X0].isWithin(y - c.Y0);
    }

    /// <summary>
    /// Stamps a shape with the lower-left of its box at pixel (px, py), the way DTerrain's layer does: each of the shape's ranges is a column of
    /// Length + 1 rows (at column px + OffsetX + k). Digging clears them (pixels become transparent); building fills them with the colour.
    /// </summary>
    /// <returns>True if the ground changed.</returns>
    public bool Paint(StampShape shape, int px, int py, bool dig, int r, int g, int b)
    {
        if (!Ok) return false;
        bool changed = false;
        for (int k = 0; k < shape.Ranges.Count; k++)
        {
            int x = px + shape.OffsetX + k;
            if (x < 0 || x >= Width) continue;
            PixelRange range = shape.Ranges[k];
            int y0 = py + range.Min;
            int y1 = y0 + range.Length;             // Length + 1 rows
            if (y0 < 0) y0 = 0;
            if (y1 > Height - 1) y1 = Height - 1;
            if (y1 < y0) continue;
            if (PaintColumn(x, y0, y1, dig, r, g, b)) changed = true;
        }
        return changed;
    }

    private bool PaintColumn(int x, int y0, int y1, bool dig, int r, int g, int b)
    {
        int cx = x / ChunkWidth;
        bool changed = false;
        int cy0 = y0 / ChunkHeight;
        int cy1 = y1 / ChunkHeight;
        for (int cy = cy0; cy <= cy1; cy++)
        {
            PixelChunk c = Chunks[cx * ChunksY + cy];
            int lo = y0 > c.Y0 ? y0 : c.Y0;
            int hi = y1 < c.Y0 + c.Height - 1 ? y1 : c.Y0 + c.Height - 1;
            int lx = x - c.X0;
            bool did;
            if (dig) did = c.Columns[lx].ClearRows(lo - c.Y0, hi - c.Y0);
            else did = c.Columns[lx].SumRange(new PixelRange(lo - c.Y0, hi - c.Y0));
            if (!did) continue;
            changed = true;
            c.Dirty = true;
            MarkNeighbors(c, x - c.X0, lo - c.Y0, hi - c.Y0);
        }
        for (int y = y0; y <= y1; y++)
        {
            if (dig) SetPixel(x, y, 0, 0, 0, 0);
            else SetPixel(x, y, r, g, b, 255);
        }
        if (changed) MarkDirty(x, y0, x, y1);
        return changed;
    }

    /// <summary>
    /// A change at a chunk's border changes the boundary the next chunk traces there, but only if that chunk has ground right beside
    /// the changed pixels: then it rebuilds too. (Boxes do not depend on neighbors.)
    /// </summary>
    private void MarkNeighbors(PixelChunk c, int localX, int localY0, int localY1)
    {
        if (ColliderKind != Chains) return;
        if (localX == 0 && c.Left != null && c.Left.Columns[c.Left.Width - 1].Touches(localY0, localY1)) c.Left.Dirty = true;
        if (localX == c.Width - 1 && c.Right != null && c.Right.Columns[0].Touches(localY0, localY1)) c.Right.Dirty = true;
        if (localY0 == 0 && c.Down != null && c.Down.Columns[localX].isWithin(c.Down.Height - 1)) c.Down.Dirty = true;
        if (localY1 == c.Height - 1 && c.Up != null && c.Up.Columns[localX].isWithin(0)) c.Up.Dirty = true;
    }

    // ---- shapes ----------------------------------------------------------------------------

    /// <summary>Rebuilds the native shapes of every chunk that changed. Call it once a frame, before the step.</summary>
    /// <returns>How many chunks were rebuilt.</returns>
    public int Update()
    {
        if (!Ok) return 0;
        int rebuilt = 0;
        for (int i = 0; i < Chunks.Count; i++)
        {
            PixelChunk c = Chunks[i];
            if (!c.Dirty) continue;
            c.Dirty = false;
            RebuildShapes(c);
            c.Rebuilds++;
            rebuilt++;
        }
        return rebuilt;
    }

    private void DestroyShapes(PixelChunk c)
    {
        for (int i = 0; i < c.Shapes.Count; i++)
        {
            if (ColliderKind == Chains) PB2.ChainDestroy(c.Shapes[i]);
            else PB2.ShapeDestroy(c.Shapes[i]);
            ShapeCount--;
        }
        c.Shapes.Clear();
    }

    private void RebuildShapes(PixelChunk c)
    {
        _world.Sim.ShapesVanishing(_cols[c.Index].ColliderIndex);       // the native layer reports no end for a destroyed shape (see SimCore2D.ShapesVanishing)
        DestroyShapes(c);
        float inv = 1f / PixelsPerUnit;
        if (ColliderKind == Chains)
        {
            PixelChainTrace trace = new PixelChainTrace(c);
            _chains.Clear();
            trace.Trace(_chains);
            for (int k = 0; k < _chains.ChainCount; k++)
                AddChain(c, _chains.Starts[k], _chains.Counts[k], _chains.Loop[k], inv);
        }
        else
        {
            _rects.Clear();
            PixelRectMerge.FromColumns(c.Columns, _rects);
            for (int k = 0; k < _rects.Count; k++)
            {
                PixelRect r = _rects[k];
                float hw = r.W * 0.5f * inv;
                float hh = r.H * 0.5f * inv;
                uint id = PB2.ShapeCreateBox(c.Body, _cols[c.Index].ColliderIndex, PhysicsLayer, hw, hh, (r.X + r.W * 0.5f) * inv, (r.Y + r.H * 0.5f) * inv, 0f, 0f, 1f, Friction, Bounciness, 0u);
                if (id != 0)
                {
                    c.Shapes.Add(id);
                    ShapeCount++;
                }
            }
        }
    }

    /// <summary>
    /// Makes the native chain(s) of one traced outline. One chain if it fits the buffer; if not, open chains of at most MaxChainPoints
    /// points, each starting where the one before ended (a loop is walked once round and back to its first point), so no ground is dropped.
    /// </summary>
    private void AddChain(PixelChunk c, int start, int count, int loop, float inv)
    {
        int cap = MaxChainPoints;
        if (cap > PixelTerrainLimits.ChainPoints) cap = PixelTerrainLimits.ChainPoints;
        if (cap < 2) cap = 2;
        if (count <= cap)
        {
            for (int p = 0; p < count; p++)
            {
                _xy[2 * p] = _chains.Points[2 * (start + p)] * inv;
                _xy[2 * p + 1] = _chains.Points[2 * (start + p) + 1] * inv;
            }
            MakeChain(c, count, loop);
            return;
        }
        int total = count;
        if (loop == 1) total = count + 1;
        int from = 0;
        while (from < total - 1)
        {
            int n = total - from;
            if (n > cap) n = cap;
            for (int p = 0; p < n; p++)
            {
                int at = (from + p) % count;
                _xy[2 * p] = _chains.Points[2 * (start + at)] * inv;
                _xy[2 * p + 1] = _chains.Points[2 * (start + at) + 1] * inv;
            }
            MakeChain(c, n, 0);
            from += n - 1;
        }
    }

    private void MakeChain(PixelChunk c, int points, int loop)
    {
        uint id = PB2.ChainCreate(c.Body, _cols[c.Index].ColliderIndex, PhysicsLayer, _xy, points, loop, Friction, Bounciness);
        if (id != 0)
        {
            c.Shapes.Add(id);
            ShapeCount++;
        }
    }

    /// <summary>Destroys the layer's bodies and shapes and lets go of the world.</summary>
    public void Destroy()
    {
        if (!Ok) return;
        for (int i = 0; i < Chunks.Count; i++)
        {
            ShapeCount -= Chunks[i].Shapes.Count;                          // the shapes go with the chunk's body, when its collider does
            Chunks[i].Shapes.Clear();
            _scene.Destroy(_nodes[i]);
        }
        _nodes.Clear();
        _cols.Clear();
        Chunks.Clear();
        Ok = false;
        _world.UnpinWorld();
    }
}
