// Prowl.Runtime.Destruction2D. The structure (per-cell element types, chunks that sleep until something changes near them, a per-step "already moved"
// mark, a scan order that is not always the same) follows VladGGDev/Falling-sand-in-unity (CC0). The rules are rewritten for the C# subset that translates
// to C: no objects per cell, no delegates, no UnityEngine.Random, integer math only, so the same source runs on .NET and as C and gives the same cells.
// NivMiz0/GPU-Sand-Sim-Unity (no license) was read for the idea of the diagonal that alternates; none of its code is used.

#pragma warning disable CS8618   // the arrays are made by Reset, which the constructor calls (the engine compiles this file with nullable checks on)

namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// Falling sand on the pixels of a terrain: every pixel is one cell of one element (air, stone, sand, water), and a step moves grains by
    /// the rules below. Gravity is toward row 0 (the bottom, as in the terrain's bitmap, so the sim's rows are the bitmap's rows).
    /// <para/>
    /// A cell's contents are its element (<see cref="Types"/>) and its four bytes of the terrain's RGBA bitmap, which move together: a grain keeps its colour.
    /// <list type="bullet">
    /// <item>Sand falls straight down into air or water (it sinks through water, which rises into the cell it left), else slides down a diagonal.</item>
    /// <item>Water falls, else slides down a diagonal, else flows sideways over air, up to <see cref="WaterSpread"/> cells at a time.</item>
    /// <item>Stone and air never move. The outer edge of the grid is a wall.</item>
    /// </list>
    /// A cell moves at most once a step (a stamp per cell), rows are scanned from the bottom, and each row is scanned from a random end. Every random
    /// choice is a hash of (seed, step, cell), not the next number of a sequence, so a chunk that sleeps does not change what the others draw:
    /// the result is the same with every chunk awake as with the sleeping ones skipped, which a conformance test checks.
    /// <para/>
    /// Chunks: only chunks marked awake are stepped. A chunk stays awake while something in it moves, and a move wakes the chunks beside it that it could
    /// matter to. Settled sand costs nothing.
    /// <para/>
    /// Ground. Stone and sand are ground for physics, water is not. The terrain layer keeps run-length columns of the ground and rebuilds a chunk's
    /// colliders from them, which is too much work to do for every grain that falls. So a chunk whose ground changed becomes <i>stale</i> and is
    /// <i>ready</i> to publish once it has been still for <see cref="SettleSteps"/> steps, or has waited <see cref="MaxWait"/> steps (so a steady pour
    /// still updates its colliders now and then). The terrain layer reads <see cref="Ready"/>, rebuilds the columns of those chunks and calls <see cref="Published"/>.
    /// </summary>
    [MaxInstances(PixelTerrainLimits.Layers)]
    internal sealed class SandSim
    {
        public const int Air = 0;
        public const int Stone = 1;
        public const int Sand = 2;
        public const int Water = 3;

        public int Width, Height;               // cells (pixels)
        public int ChunksX, ChunksY;
        public int ChunkWidth, ChunkHeight;     // cells
        public int WaterSpread = 6;             // the most cells water jumps sideways in one step
        public int SettleSteps = 4;             // steps a chunk's ground must be still before it is ready to publish
        public int MaxWait = 30;                // steps after which a chunk whose ground keeps changing is ready anyway

        public byte[] Types;                    // y * Width + x: Air, Stone, Sand, Water
        public byte[] Ready;                    // per chunk (cx * ChunksY + cy): 1 if the terrain layer should publish its ground now
        public int Moves;                       // moves made by the last Step
        public int Steps;                       // steps made

        // the rectangle of cells (inclusive) that changed since ClearStepDirty, for the layer to merge into its own dirty rectangle
        public bool StepDirty;
        public int DirtyX0, DirtyY0, DirtyX1, DirtyY1;

        private byte[] _stamp;                  // per cell: the tick of the step in which it last moved
        private byte[] _awake;                  // per chunk: stepped this Step
        private byte[] _wake;                   // per chunk: to be stepped the next Step
        private byte[] _stale;                  // per chunk: the ground differs from what was last published
        private byte[] _changed;                // per chunk: ground cells moved in the Step being made
        private int[] _idle;                    // per chunk, while stale: steps since the ground last changed
        private int[] _wait;                    // per chunk, while stale: steps since it went stale
        private int _tick;                      // 1..255
        private uint _seed = 2463534242u;

        public SandSim(int width, int height, int chunksX, int chunksY)
        {
            Reset(width, height, chunksX, chunksY);
        }

        /// <summary>
        /// Makes the simulation an empty grid of a size (which must divide into whole chunks), as if it were new: an arena class has a fixed number of
        /// slots, so a test that wants many simulations reuses one.
        /// </summary>
        public void Reset(int width, int height, int chunksX, int chunksY)
        {
            Width = width;
            Height = height;
            ChunksX = chunksX;
            ChunksY = chunksY;
            ChunkWidth = width / chunksX;
            ChunkHeight = height / chunksY;
            Types = new byte[width * height];
            _stamp = new byte[width * height];
            int n = chunksX * chunksY;
            Ready = new byte[n];
            _awake = new byte[n];
            _wake = new byte[n];
            _stale = new byte[n];
            _changed = new byte[n];
            _idle = new int[n];
            _wait = new int[n];
            _tick = 0;
            Steps = 0;
            Moves = 0;
            StepDirty = false;
            _seed = 2463534242u;
        }

        /// <summary>Sets the seed of the random choices (the same seed and the same calls give the same cells).</summary>
        public void Seed(uint seed)
        {
            _seed = seed;
        }

        // a hash of the seed, the step, a cell (or a row) and which choice it is: the same in every run and on every platform
        private uint Mix(int cell, int salt)
        {
            uint h = _seed ^ ((uint)Steps * 2654435761u) ^ ((uint)cell * 2246822519u) ^ ((uint)salt * 3266489917u);
            h ^= h >> 15;
            h *= 746135789u;
            h ^= h >> 12;
            h *= 695743881u;
            h ^= h >> 15;
            return h;
        }

        private int Bit(int cell, int salt) { return (int)((Mix(cell, salt) >> 16) & 1u); }

        private int Below(int cell, int salt, int n) { return (int)((Mix(cell, salt) >> 8) % (uint)n); }

        public static bool IsGround(int element) { return element == Stone || element == Sand; }

        public bool InBounds(int x, int y) { return x >= 0 && x < Width && y >= 0 && y < Height; }

        public int ElementAt(int x, int y)
        {
            if (!InBounds(x, y)) return Stone;          // beyond the grid is wall
            return Types[y * Width + x];
        }

        public bool IsGroundAt(int x, int y)
        {
            if (!InBounds(x, y)) return false;
            return IsGround(Types[y * Width + x]);
        }

        public int Count(int element)
        {
            int n = 0;
            for (int i = 0; i < Types.Length; i++)
                if (Types[i] == element) n++;
            return n;
        }

        public int AwakeChunks()
        {
            int n = 0;
            for (int i = 0; i < _awake.Length; i++)
                if (_awake[i] != 0) n++;
            return n;
        }

        public int StaleChunks()
        {
            int n = 0;
            for (int i = 0; i < _stale.Length; i++)
                if (_stale[i] != 0) n++;
            return n;
        }

        /// <summary>A hash of every cell's element, for comparing two runs.</summary>
        public uint Hash()
        {
            uint h = 2166136261u;
            for (int i = 0; i < Types.Length; i++)
            {
                h ^= (uint)Types[i];
                h *= 16777619u;
            }
            return h;
        }

        public void ClearStepDirty() { StepDirty = false; }

        // ---- editing the cells from outside ------------------------------------------------------

        /// <summary>Sets a cell's element with no side effects: for filling the grid before the first step.</summary>
        public void SetRaw(int index, int element)
        {
            Types[index] = (byte)element;
        }

        /// <summary>
        /// Sets a cell's element and wakes what it could matter to. Sand coming or going makes the chunk's ground stale. (A change between air and stone
        /// is made by the terrain layer's own digging and building, which keep its columns right; they are not stale.)
        /// </summary>
        public void SetType(int x, int y, int element)
        {
            if (!InBounds(x, y)) return;
            int i = y * Width + x;
            int old = Types[i];
            if (old == element) return;
            Types[i] = (byte)element;
            if (old == Sand || element == Sand) MarkStale(x, y);
            WakeAround(x, y);
        }

        /// <summary>Puts one grain of an element in an air cell, with its colour in the bitmap. False if the cell is not air, or the element is.</summary>
        public bool Spawn(byte[] pixels, int x, int y, int element)
        {
            if (!InBounds(x, y) || element == Air) return false;
            int i = y * Width + x;
            if (Types[i] != Air) return false;
            WriteColor(pixels, x, y, element);
            SetType(x, y, element);
            Dirty(x, y);
            return true;
        }

        /// <summary>The colour of an element at a cell, a little different from cell to cell (a hash of the position, so it is the same in every run).</summary>
        public void WriteColor(byte[] pixels, int x, int y, int element)
        {
            int o = (y * Width + x) * 4;
            uint h = (uint)x * 73856093u ^ (uint)y * 19349663u;
            h ^= h >> 13;
            h *= 1274126177u;
            h ^= h >> 16;
            int v = (int)(h & 15u) - 8;                 // -8 .. 7
            if (element == Sand)
            {
                pixels[o] = (byte)(194 + v);
                pixels[o + 1] = (byte)(176 + v);
                pixels[o + 2] = (byte)(120 + v);
                pixels[o + 3] = 255;
            }
            else if (element == Water)
            {
                pixels[o] = (byte)(40 + v / 2);
                pixels[o + 1] = (byte)(100 + v);
                pixels[o + 2] = (byte)(200 + v);
                pixels[o + 3] = 200;
            }
            else if (element == Stone)
            {
                pixels[o] = (byte)(110 + v / 2);
                pixels[o + 1] = (byte)(110 + v / 2);
                pixels[o + 2] = (byte)(116 + v / 2);
                pixels[o + 3] = 255;
            }
            else
            {
                pixels[o] = 0;
                pixels[o + 1] = 0;
                pixels[o + 2] = 0;
                pixels[o + 3] = 0;
            }
        }

        /// <summary>Wakes the chunk that holds a cell, and the chunks beside it if the cell is near their edge (water looks that far).</summary>
        public void WakeAround(int x, int y)
        {
            int cx = x / ChunkWidth;
            int cy = y / ChunkHeight;
            int lx = x - cx * ChunkWidth;
            int ly = y - cy * ChunkHeight;
            int mx = WaterSpread < 1 ? 1 : WaterSpread;
            int dx0 = lx < mx ? -1 : 0;
            int dx1 = lx >= ChunkWidth - mx ? 1 : 0;
            int dy0 = ly < 1 ? -1 : 0;
            int dy1 = ly >= ChunkHeight - 1 ? 1 : 0;
            for (int dy = dy0; dy <= dy1; dy++)
            {
                for (int dx = dx0; dx <= dx1; dx++)
                {
                    int ax = cx + dx;
                    int ay = cy + dy;
                    if (ax < 0 || ax >= ChunksX || ay < 0 || ay >= ChunksY) continue;
                    int ci = ax * ChunksY + ay;
                    _wake[ci] = 1;
                    _awake[ci] = 1;
                }
            }
        }

        /// <summary>Wakes every chunk. Not needed in use; the conformance test uses it to compare a run with sleeping against one without.</summary>
        public void WakeAll()
        {
            for (int i = 0; i < _awake.Length; i++) _awake[i] = 1;
        }

        private void MarkStale(int x, int y)
        {
            int ci = (x / ChunkWidth) * ChunksY + (y / ChunkHeight);
            _stale[ci] = 1;
            _idle[ci] = 0;
        }

        private void Dirty(int x, int y)
        {
            if (!StepDirty)
            {
                StepDirty = true;
                DirtyX0 = x; DirtyY0 = y; DirtyX1 = x; DirtyY1 = y;
                return;
            }
            if (x < DirtyX0) DirtyX0 = x;
            if (y < DirtyY0) DirtyY0 = y;
            if (x > DirtyX1) DirtyX1 = x;
            if (y > DirtyY1) DirtyY1 = y;
        }

        /// <summary>The layer has rebuilt the columns of a chunk from the cells: its ground is no longer stale.</summary>
        public void Published(int chunk)
        {
            _stale[chunk] = 0;
            _idle[chunk] = 0;
            _wait[chunk] = 0;
            Ready[chunk] = 0;
        }

        // ---- the step ----------------------------------------------------------------------------

        /// <summary>
        /// One step of every awake chunk. <paramref name="pixels"/> is the terrain's RGBA bitmap, whose pixels move with their cells.
        /// </summary>
        /// <returns>How many moves were made.</returns>
        public int Step(byte[] pixels)
        {
            _tick++;
            if (_tick > 255) _tick = 1;
            Steps++;
            if (Steps == 2147483647) Steps = 0;
            Moves = 0;
            for (int i = 0; i < _wake.Length; i++)
            {
                _wake[i] = 0;
                _changed[i] = 0;
            }
            bool reverse = (_tick & 1) == 0;                // the chunks of a row are walked from alternate ends
            for (int cy = 0; cy < ChunksY; cy++)
            {
                for (int k = 0; k < ChunksX; k++)
                {
                    int cx = reverse ? ChunksX - 1 - k : k;
                    if (_awake[cx * ChunksY + cy] == 0) continue;
                    StepChunk(pixels, cx, cy);
                }
            }
            for (int i = 0; i < _awake.Length; i++)
            {
                _awake[i] = _wake[i];                       // a chunk where something moved is stepped again; one where nothing did sleeps
                Ready[i] = 0;
                if (_stale[i] == 0) continue;
                if (_changed[i] != 0) _idle[i] = 0;
                else _idle[i]++;
                _wait[i]++;
                if (_idle[i] >= SettleSteps || _wait[i] >= MaxWait) Ready[i] = 1;
            }
            return Moves;
        }

        private void StepChunk(byte[] pixels, int cx, int cy)
        {
            int x0 = cx * ChunkWidth;
            int y0 = cy * ChunkHeight;
            byte tick = (byte)_tick;
            for (int ly = 0; ly < ChunkHeight; ly++)
            {
                int y = y0 + ly;
                bool fromLeft = Bit(y, 0) == 0;
                for (int k = 0; k < ChunkWidth; k++)
                {
                    int x = fromLeft ? x0 + k : x0 + ChunkWidth - 1 - k;
                    int i = y * Width + x;
                    int e = Types[i];
                    if (e < Sand) continue;                 // air and stone do not move
                    if (_stamp[i] == tick) continue;        // it has moved this step
                    if (e == Sand) StepSand(pixels, x, y, i);
                    else StepWater(pixels, x, y, i);
                }
            }
        }

        // a cell a falling grain can go into: air, or water that has not moved yet in this step
        private bool Yields(int j)
        {
            int e = Types[j];
            if (e == Air) return true;
            return e == Water && _stamp[j] != (byte)_tick;
        }

        private void StepSand(byte[] pixels, int x, int y, int i)
        {
            if (y == 0) return;
            int below = i - Width;
            if (Yields(below))
            {
                Swap(pixels, x, y, i, x, y - 1, below);
                return;
            }
            int d = Bit(i, 1) == 0 ? -1 : 1;
            for (int k = 0; k < 2; k++)
            {
                int nx = x + d;
                if (nx >= 0 && nx < Width && Yields(below + d))
                {
                    Swap(pixels, x, y, i, nx, y - 1, below + d);
                    return;
                }
                d = -d;
            }
        }

        private void StepWater(byte[] pixels, int x, int y, int i)
        {
            if (y > 0)
            {
                int below = i - Width;
                if (Types[below] == Air)
                {
                    Swap(pixels, x, y, i, x, y - 1, below);
                    return;
                }
                int d = Bit(i, 2) == 0 ? -1 : 1;
                for (int k = 0; k < 2; k++)
                {
                    int nx = x + d;
                    if (nx >= 0 && nx < Width && Types[below + d] == Air)
                    {
                        Swap(pixels, x, y, i, nx, y - 1, below + d);
                        return;
                    }
                    d = -d;
                }
            }
            int dir = Bit(i, 3) == 0 ? -1 : 1;
            for (int k = 0; k < 2; k++)
            {
                int room = 0;                               // how many air cells there are in a row to this side, up to WaterSpread
                while (room < WaterSpread)
                {
                    int nx = x + dir * (room + 1);
                    if (nx < 0 || nx >= Width) break;
                    if (Types[i + dir * (room + 1)] != Air) break;
                    room++;
                }
                if (room > 0)
                {
                    int s = 1 + Below(i, 4, room);
                    Swap(pixels, x, y, i, x + dir * s, y, i + dir * s);
                    return;
                }
                dir = -dir;
            }
        }

        // exchanges two cells: their elements and their pixels. Both have moved this step.
        private void Swap(byte[] pixels, int xi, int yi, int i, int xj, int yj, int j)
        {
            byte ti = Types[i];
            byte tj = Types[j];
            Types[i] = tj;
            Types[j] = ti;
            int pi = i * 4;
            int pj = j * 4;
            for (int k = 0; k < 4; k++)
            {
                byte t = pixels[pi + k];
                pixels[pi + k] = pixels[pj + k];
                pixels[pj + k] = t;
            }
            _stamp[i] = (byte)_tick;
            _stamp[j] = (byte)_tick;
            Moves++;
            WakeAround(xi, yi);
            WakeAround(xj, yj);
            Dirty(xi, yi);
            Dirty(xj, yj);
            if (IsGround(ti) != IsGround(tj))               // the ground changed in both cells' chunks
            {
                int ca = (xi / ChunkWidth) * ChunksY + (yi / ChunkHeight);
                int cb = (xj / ChunkWidth) * ChunksY + (yj / ChunkHeight);
                _stale[ca] = 1;
                _changed[ca] = 1;
                _stale[cb] = 1;
                _changed[cb] = 1;
            }
        }
    }
}
