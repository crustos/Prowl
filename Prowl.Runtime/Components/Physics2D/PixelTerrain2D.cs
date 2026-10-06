// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using Prowl.Native.Box2D;
using Prowl.Runtime.Destruction2D;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>How a <see cref="PixelTerrain2D"/> with no source texture starts out.</summary>
public enum PixelTerrainFill
{
    /// <summary>No ground. Build it up with <see cref="PixelTerrain2D.AddCircle"/>.</summary>
    Empty = 0,
    /// <summary>Ground everywhere.</summary>
    Solid = 1,
    /// <summary>Ground below a rolling surface at about half the height.</summary>
    Hills = 2,
}

/// <summary>The kind of native shape a <see cref="PixelTerrain2D"/> makes its ground from.</summary>
public enum PixelColliderKind
{
    /// <summary>Merged rectangles. Robust, and the cheapest to rebuild.</summary>
    Boxes = 0,
    /// <summary>One-sided chains along the surface. Smooth for things that roll or slide, but thin: fast bodies can tunnel.</summary>
    Chains = 1,
}

/// <summary>
/// Ground you can dig and build: a bitmap (a texture, or generated) cut into chunks, each with Box2D shapes that follow the pixels.
/// <see cref="DigCircle"/> and <see cref="AddCircle"/> change the ground; the shapes of the chunks that changed are rebuilt at the next
/// <see cref="Update"/>. It is the engine side of <c>Prowl.Runtime/Destruction2D/PixelTerrain</c>, the code that the 2D player's
/// <c>Prowl.Core2D.PixelTerrain2D</c> shares.
/// <para/>
/// The terrain's position is the lower-left corner of the bitmap, in world units; keep its scale at 1 and its rotation at 0. Each
/// chunk is a hidden child object with its own static body (a body can only have so many shapes before a rebuild gets slow).
/// Do not put a <see cref="Rigidbody2D"/> on the terrain or above it.
/// <para/>
/// This component makes the ground collide and lets it be edited; it does not draw it. <see cref="Pixels"/> and <see cref="Version"/>
/// are there for whatever does (upload the bytes to a texture when the version changes).
/// </summary>
[AddComponentMenu("Physics 2D/Pixel Terrain 2D")]
[ComponentIcon("")]
public sealed class PixelTerrain2D : MonoBehaviour
{
    // ---- settings ------------------------------------------------------------------------

    [Header("Bitmap"), Tooltip("The ground's picture: pixels with alpha above the threshold are ground. Needs a readable RGBA 8-bit texture. Empty: the ground is generated from the settings below.")]
    public Texture2D? Source;

    [Tooltip("Width in pixels when there is no source texture. Must divide by Chunks X.")]
    public int Width = 256;

    [Tooltip("Height in pixels when there is no source texture. Must divide by Chunks Y.")]
    public int Height = 128;

    [Tooltip("What a terrain with no source texture starts as.")]
    public PixelTerrainFill Fill = PixelTerrainFill.Hills;

    [Tooltip("Colour (0 to 255) of generated ground, and of ground built with the default colour.")]
    public byte GroundR = 110, GroundG = 84, GroundB = 52;

    [Header("Layout"), Tooltip("Pixels per world unit. The terrain is Width / this units wide.")]
    public float PixelsPerUnit = 16f;

    [Tooltip("The bitmap is cut into this many chunks across. The size in pixels must divide by it.")]
    public int ChunksX = 4;

    [Tooltip("The bitmap is cut into this many chunks up.")]
    public int ChunksY = 2;

    [Header("Collision"), Tooltip("Boxes (merged rectangles) or Chains (the surface outline).")]
    public PixelColliderKind ColliderKind = PixelColliderKind.Boxes;

    [Tooltip("A pixel is ground when its alpha is above this (0 to 254).")]
    public int AlphaThreshold = 2;

    [Tooltip("How much the ground resists sliding. 0 is ice.")]
    public float Friction = 0.6f;

    [Range(0f, 1f), Tooltip("Restitution of the ground.")]
    public float Bounciness;

    [Tooltip("Dig a crater of this many pixels' radius when a button below is pressed.")]
    public int TestCraterRadius = 12;

    // ---- state ---------------------------------------------------------------------------

    private static readonly List<PixelTerrain2D> s_active = new();

    /// <summary>The terrains that are built right now, so a blast can find the ground it hits.</summary>
    public static IReadOnlyList<PixelTerrain2D> Active => s_active;

    [NonSerialized] private byte[]? _pixels;
    [NonSerialized] private int _width, _height, _chunkW, _chunkH, _chunksX, _chunksY;
    [NonSerialized] private bool _built;
    [NonSerialized] private List<PixelChunk> _chunks = new();
    [NonSerialized] private List<GameObject> _chunkObjects = new();
    [NonSerialized] private List<PixelChunkCollider2D> _colliders = new();
    [NonSerialized] private List<PixelRect> _rects = new();
    [NonSerialized] private PixelChainSet _chainSet = new();
    [NonSerialized] private float[] _xy = new float[PixelTerrainLimits.ChainPoints * 2];
    [NonSerialized] private readonly Dictionary<int, StampShape> _circles = new();
    [NonSerialized] private int _version;

    /// <summary>Whether the ground exists in the physics world right now (it does while playing and enabled).</summary>
    public bool IsBuilt => _built;

    /// <summary>Bitmap size in pixels once built.</summary>
    public int PixelWidth => _width;
    public int PixelHeight => _height;

    /// <summary>The bitmap, (y * width + x) * 4 bytes of r, g, b, a with row 0 at the bottom. Null until built. Do not write to it.</summary>
    public byte[]? Pixels => _pixels;

    /// <summary>Increases every time the ground changes.</summary>
    public int Version => _version;

    /// <summary>World position of the lower-left corner of the bitmap.</summary>
    public Float2 Origin
    {
        get { Float3 p = Transform.Position; return new Float2(p.X, p.Y); }
    }

    /// <summary>The bitmap's size in world units.</summary>
    public Float2 SizeInUnits
    {
        get
        {
            GetLayout(out int w, out int h, out _, out _);
            return new Float2(w / PixelsPerUnit, h / PixelsPerUnit);
        }
    }

    // ---- lifecycle -----------------------------------------------------------------------

    public override void OnEnable()
    {
        if (Application.IsPlaying) BuildTerrain();
    }

    public override void OnDisable() => TearDown();

    /// <summary>Throws the ground away and makes it again from its settings. Playing only.</summary>
    [Button("Rebuild")]
    public void Rebuild()
    {
        if (!Application.IsPlaying) { Debug.LogWarning($"[{Name}] PixelTerrain2D only builds while the game is playing."); return; }
        TearDown();
        BuildTerrain();
    }

    /// <summary>Digs a crater at the terrain's centre, to try it out. Playing only.</summary>
    [Button("Dig Test Crater")]
    public void DigTestCrater()
    {
        if (!_built) return;
        Float2 o = Origin;
        DigCircle(new Float2(o.X + _width / PixelsPerUnit * 0.5f, o.Y + _height / PixelsPerUnit * 0.5f), TestCraterRadius / PixelsPerUnit);
    }

    // ---- editing the ground ----------------------------------------------------------------

    /// <summary>Removes ground in a circle (world position and radius in world units). Returns true if any ground was removed.</summary>
    public bool DigCircle(Float2 world, float radius) => PaintCircle(world, radius, true, GroundR, GroundG, GroundB);

    /// <summary>Adds ground in a circle, in the terrain's colour. Returns true if any ground was added.</summary>
    public bool AddCircle(Float2 world, float radius) => PaintCircle(world, radius, false, GroundR, GroundG, GroundB);

    /// <summary>Adds ground in a circle, in the given colour (0 to 255).</summary>
    public bool AddCircle(Float2 world, float radius, byte r, byte g, byte b) => PaintCircle(world, radius, false, r, g, b);

    /// <summary>Removes ground in a box (centre and full size in world units, aligned to the axes).</summary>
    public bool DigBox(Float2 centre, Float2 size) => PaintBox(centre, size, true, GroundR, GroundG, GroundB);

    /// <summary>Adds ground in a box, in the terrain's colour.</summary>
    public bool AddBox(Float2 centre, Float2 size) => PaintBox(centre, size, false, GroundR, GroundG, GroundB);

    /// <summary>Is there ground at this world position?</summary>
    public bool IsSolidAt(Float2 world)
    {
        if (!_built) return false;
        Float2 o = Origin;
        int x = (int)MathF.Floor((world.X - o.X) * PixelsPerUnit);
        int y = (int)MathF.Floor((world.Y - o.Y) * PixelsPerUnit);
        if (x < 0 || x >= _width || y < 0 || y >= _height) return false;
        PixelChunk c = _chunks[(x / _chunkW) * _chunksY + (y / _chunkH)];
        return c.Columns[x - c.X0].isWithin(y - c.Y0);
    }

    private bool PaintCircle(Float2 world, float radius, bool dig, byte r, byte g, byte b)
    {
        if (!_built || radius <= 0f) return false;
        int pr = Math.Max(1, (int)MathF.Round(radius * PixelsPerUnit));
        if (!_circles.TryGetValue(pr, out StampShape? shape))
        {
            shape = StampShape.GenerateShapeCircle(pr);
            _circles[pr] = shape;
        }
        Float2 o = Origin;
        int cx = (int)MathF.Floor((world.X - o.X) * PixelsPerUnit);
        int cy = (int)MathF.Floor((world.Y - o.Y) * PixelsPerUnit);
        return Paint(shape, cx - shape.Width / 2, cy - shape.Height / 2, dig, r, g, b);
    }

    private bool PaintBox(Float2 centre, Float2 size, bool dig, byte r, byte g, byte b)
    {
        if (!_built) return false;
        int w = Math.Max(1, (int)MathF.Round(size.X * PixelsPerUnit));
        int h = Math.Max(1, (int)MathF.Round(size.Y * PixelsPerUnit));
        StampShape shape = StampShape.GenerateShapeRect(w, h);
        Float2 o = Origin;
        int px = (int)MathF.Floor((centre.X - o.X) * PixelsPerUnit) - w / 2;
        int py = (int)MathF.Floor((centre.Y - o.Y) * PixelsPerUnit) - h / 2;
        return Paint(shape, px, py, dig, r, g, b);
    }

    // Stamps a shape with the lower-left of its box at pixel (px, py): the same as Core2D's PixelTerrain2D.Paint.
    private bool Paint(StampShape shape, int px, int py, bool dig, int r, int g, int b)
    {
        bool changed = false;
        for (int k = 0; k < shape.Ranges.Count; k++)
        {
            int x = px + shape.OffsetX + k;
            if (x < 0 || x >= _width) continue;
            PixelRange range = shape.Ranges[k];
            int y0 = py + range.Min;
            int y1 = y0 + range.Length;     // Length + 1 rows
            if (y0 < 0) y0 = 0;
            if (y1 > _height - 1) y1 = _height - 1;
            if (y1 < y0) continue;
            if (PaintColumn(x, y0, y1, dig, r, g, b)) changed = true;
        }
        if (changed) _version++;
        return changed;
    }

    private bool PaintColumn(int x, int y0, int y1, bool dig, int r, int g, int b)
    {
        int cx = x / _chunkW;
        bool changed = false;
        int cy0 = y0 / _chunkH;
        int cy1 = y1 / _chunkH;
        for (int cy = cy0; cy <= cy1; cy++)
        {
            PixelChunk c = _chunks[cx * _chunksY + cy];
            int lo = Math.Max(y0, c.Y0);
            int hi = Math.Min(y1, c.Y0 + c.Height - 1);
            int lx = x - c.X0;
            bool did = dig ? c.Columns[lx].ClearRows(lo - c.Y0, hi - c.Y0)
                           : c.Columns[lx].SumRange(new PixelRange(lo - c.Y0, hi - c.Y0));
            if (!did) continue;
            changed = true;
            c.Dirty = true;
            MarkNeighbours(c, lx, lo - c.Y0, hi - c.Y0);
        }
        for (int y = y0; y <= y1; y++)
        {
            int i = (y * _width + x) * 4;
            _pixels![i] = (byte)(dig ? 0 : r);
            _pixels[i + 1] = (byte)(dig ? 0 : g);
            _pixels[i + 2] = (byte)(dig ? 0 : b);
            _pixels[i + 3] = (byte)(dig ? 0 : 255);
        }
        return changed;
    }

    // A change on a chunk's border changes the outline the chunk next to it traces (chains only; boxes do not depend on neighbours).
    private void MarkNeighbours(PixelChunk c, int localX, int localY0, int localY1)
    {
        if (ColliderKind != PixelColliderKind.Chains) return;
        if (localX == 0 && c.Left != null && c.Left.Columns[c.Left.Width - 1].Touches(localY0, localY1)) c.Left.Dirty = true;
        if (localX == c.Width - 1 && c.Right != null && c.Right.Columns[0].Touches(localY0, localY1)) c.Right.Dirty = true;
        if (localY0 == 0 && c.Down != null && c.Down.Columns[localX].isWithin(c.Down.Height - 1)) c.Down.Dirty = true;
        if (localY1 == c.Height - 1 && c.Up != null && c.Up.Columns[localX].isWithin(0)) c.Up.Dirty = true;
    }

    // ---- building ------------------------------------------------------------------------

    private void GetLayout(out int w, out int h, out int cx, out int cy)
    {
        cx = Math.Max(1, ChunksX);
        cy = Math.Max(1, ChunksY);
        if (Source != null && Source.Width > 0 && Source.Height > 0) { w = (int)Source.Width; h = (int)Source.Height; }
        else { w = Math.Max(1, Width); h = Math.Max(1, Height); }
    }

    private bool MakePixels(int w, int h)
    {
        byte[] px = new byte[w * h * 4];
        if (Source != null && Source.Width > 0)
        {
            if (Source.GetSize() != px.Length)
            {
                Debug.LogError($"[{Name}] PixelTerrain2D needs an RGBA 8-bit source texture (expected {px.Length} bytes, got {Source.GetSize()}).");
                return false;
            }
            Source.GetData<byte>(px);   // rows start at the bottom, as the terrain's do
        }
        else if (Fill != PixelTerrainFill.Empty)
        {
            for (int x = 0; x < w; x++)
            {
                int top = h;
                if (Fill == PixelTerrainFill.Hills)
                    top = (int)(h * 0.5f + h * 0.18f * MathF.Sin(x * 6.2831853f * 2f / w));
                for (int y = 0; y < top && y < h; y++)
                {
                    int i = (y * w + x) * 4;
                    px[i] = GroundR; px[i + 1] = GroundG; px[i + 2] = GroundB; px[i + 3] = 255;
                }
            }
        }
        _pixels = px;
        return true;
    }

    private void BuildTerrain()
    {
        if (_built) return;
        var scene = GameObject.Scene;
        if (!scene.IsValid()) return;
        if (PixelsPerUnit <= 0f) { Debug.LogError($"[{Name}] PixelTerrain2D needs Pixels Per Unit above 0."); return; }

        GetLayout(out int w, out int h, out int cx, out int cy);
        if (w % cx != 0 || h % cy != 0)
        {
            Debug.LogError($"[{Name}] PixelTerrain2D: the bitmap ({w} x {h}) must divide into whole chunks ({cx} x {cy}).");
            return;
        }
        if (!MakePixels(w, h)) return;

        _width = w; _height = h; _chunksX = cx; _chunksY = cy;
        _chunkW = w / cx; _chunkH = h / cy;
        _chunks = new List<PixelChunk>();
        _chunkObjects = new List<GameObject>();
        _colliders = new List<PixelChunkCollider2D>();
        float inv = 1f / PixelsPerUnit;

        for (int ix = 0; ix < cx; ix++)
        {
            for (int iy = 0; iy < cy; iy++)
            {
                var c = new PixelChunk
                {
                    Index = ix * cy + iy, CX = ix, CY = iy,
                    X0 = ix * _chunkW, Y0 = iy * _chunkH, Width = _chunkW, Height = _chunkH,
                    Dirty = true,
                };
                PrepareColumns(c);

                var go = new GameObject($"{Name} Chunk {ix},{iy}");
                go.HideFlags = HideFlags.HideAndDontSave | HideFlags.NoGizmos;
                go.SetParent(GameObject, false);
                go.LayerIndex = GameObject.LayerIndex;
                go.Transform.LocalPosition = new Float3(c.X0 * inv, c.Y0 * inv, 0f);
                var col = go.AddComponent<PixelChunkCollider2D>();

                c.Body = col.OwnBodyHandle;
                _chunks.Add(c);
                _chunkObjects.Add(go);
                _colliders.Add(col);
            }
        }
        for (int i = 0; i < _chunks.Count; i++)
        {
            PixelChunk c = _chunks[i];
            c.Left = ChunkAt(c.CX - 1, c.CY);
            c.Right = ChunkAt(c.CX + 1, c.CY);
            c.Down = ChunkAt(c.CX, c.CY - 1);
            c.Up = ChunkAt(c.CX, c.CY + 1);
        }

        _built = true;
        _version++;
        s_active.Add(this);
        UpdateShapes();
    }

    private PixelChunk? ChunkAt(int cx, int cy)
    {
        if (cx < 0 || cx >= _chunksX || cy < 0 || cy >= _chunksY) return null;
        return _chunks[cx * _chunksY + cy];
    }

    private int AlphaAt(int x, int y) => _pixels![(y * _width + x) * 4 + 3];

    // One column per pixel column: a range for each run of pixels whose alpha is above the threshold.
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

    private void TearDown()
    {
        s_active.Remove(this);
        // The chunk colliders drop their bodies, and the shapes with them, as they are disabled.
        for (int i = 0; i < _chunkObjects.Count; i++)
            if (_chunkObjects[i].IsValid()) _chunkObjects[i].Destroy();
        _chunkObjects = new List<GameObject>();
        _colliders = new List<PixelChunkCollider2D>();
        _chunks = new List<PixelChunk>();
        _pixels = null;
        _built = false;
    }

    // ---- shapes --------------------------------------------------------------------------

    public override void Update() => UpdateShapes();

    /// <summary>Rebuilds the shapes of every chunk that changed. Runs every frame; call it to see a change sooner.</summary>
    public int UpdateShapes()
    {
        if (!_built) return 0;
        int rebuilt = 0;
        for (int i = 0; i < _chunks.Count; i++)
        {
            PixelChunk c = _chunks[i];
            PixelChunkCollider2D col = _colliders[i];
            if (!col.IsValid()) continue;

            // The collider got a new body (its layer changed, say): the old shapes went with the old body.
            uint body = col.OwnBodyHandle;
            if (body != c.Body)
            {
                c.Body = body;
                c.Shapes.Clear();
                c.Dirty = true;
            }
            if (body == 0 || !c.Dirty) continue;

            c.Dirty = false;
            RebuildShapes(c, col);
            c.Rebuilds++;
            rebuilt++;
        }
        return rebuilt;
    }

    private void RebuildShapes(PixelChunk c, PixelChunkCollider2D col)
    {
        int index = col.RegistryIndex;
        GameObject.Scene?.Physics2D.Simulation.ShapesVanishing(index);
        for (int i = 0; i < c.Shapes.Count; i++)
        {
            if (ColliderKind == PixelColliderKind.Chains) PB2.ChainDestroy(c.Shapes[i]);
            else PB2.ShapeDestroy(c.Shapes[i]);
        }
        c.Shapes.Clear();

        int layer = GameObject.LayerIndex;
        float inv = 1f / PixelsPerUnit;
        if (ColliderKind == PixelColliderKind.Chains)
        {
            var trace = new PixelChainTrace(c);
            _chainSet.Clear();
            trace.Trace(_chainSet);
            for (int k = 0; k < _chainSet.ChainCount; k++)
                AddChain(c, index, layer, _chainSet.Starts[k], _chainSet.Counts[k], _chainSet.Loop[k], inv);
        }
        else
        {
            _rects.Clear();
            PixelRectMerge.FromColumns(c.Columns, _rects);
            for (int k = 0; k < _rects.Count; k++)
            {
                PixelRect r = _rects[k];
                uint id = PB2.ShapeCreateBox(c.Body, index, layer, r.W * 0.5f * inv, r.H * 0.5f * inv,
                                             (r.X + r.W * 0.5f) * inv, (r.Y + r.H * 0.5f) * inv, 0f, 0f, 1f, Friction, Bounciness, 0u);
                if (id != 0) c.Shapes.Add(id);
            }
        }
    }

    // One chain if the outline fits the buffer; otherwise open chains of at most ChainPoints points, each starting where the one before
    // ended (a loop is walked once round and back to its first point), so no ground is dropped. Same as Core2D's.
    private void AddChain(PixelChunk c, int index, int layer, int start, int count, int loop, float inv)
    {
        int cap = PixelTerrainLimits.ChainPoints;
        if (count <= cap)
        {
            for (int p = 0; p < count; p++)
            {
                _xy[2 * p] = _chainSet.Points[2 * (start + p)] * inv;
                _xy[2 * p + 1] = _chainSet.Points[2 * (start + p) + 1] * inv;
            }
            MakeChain(c, index, layer, count, loop);
            return;
        }
        int total = loop == 1 ? count + 1 : count;
        int from = 0;
        while (from < total - 1)
        {
            int n = Math.Min(total - from, cap);
            for (int p = 0; p < n; p++)
            {
                int at = (from + p) % count;
                _xy[2 * p] = _chainSet.Points[2 * (start + at)] * inv;
                _xy[2 * p + 1] = _chainSet.Points[2 * (start + at) + 1] * inv;
            }
            MakeChain(c, index, layer, n, 0);
            from += n - 1;
        }
    }

    private void MakeChain(PixelChunk c, int index, int layer, int points, int loop)
    {
        uint id = PB2.ChainCreate(c.Body, index, layer, _xy, points, loop, Friction, Bounciness);
        if (id != 0) c.Shapes.Add(id);
    }

    // ---- editor gizmos -------------------------------------------------------------------

    public override void DrawGizmos()
    {
        GetLayout(out int w, out int h, out _, out _);
        DrawRect(0f, 0f, w / PixelsPerUnit, h / PixelsPerUnit, Color.Green);
    }

    public override void DrawGizmosSelected()
    {
        GetLayout(out int w, out int h, out int cx, out int cy);
        float uw = w / PixelsPerUnit, uh = h / PixelsPerUnit;
        for (int i = 1; i < cx; i++) DrawLine(uw * i / cx, 0f, uw * i / cx, uh, Color.Yellow);
        for (int i = 1; i < cy; i++) DrawLine(0f, uh * i / cy, uw, uh * i / cy, Color.Yellow);
    }

    private void DrawRect(float x0, float y0, float x1, float y1, Color color)
    {
        DrawLine(x0, y0, x1, y0, color);
        DrawLine(x1, y0, x1, y1, color);
        DrawLine(x1, y1, x0, y1, color);
        DrawLine(x0, y1, x0, y0, color);
    }

    private void DrawLine(float x0, float y0, float x1, float y1, Color color)
    {
        Float3 p = Transform.Position;
        Debug.DrawLine(new Float3(p.X + x0, p.Y + y0, p.Z), new Float3(p.X + x1, p.Y + y1, p.Z), color);
    }
}
