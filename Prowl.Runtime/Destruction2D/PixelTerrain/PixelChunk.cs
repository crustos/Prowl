// Prowl.Runtime.Destruction2D. The chunk idea is DTerrain's (MIT, (c) 2020 Dominik Zimny); see LICENSE.md.
using System.Collections.Generic;

#pragma warning disable CS0649   // the fields are filled in by whatever owns the chunk: the terrain layer of each runtime (Core2D's PixelTerrain2D, the engine's)

namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// A rectangle of the terrain: the run-length columns of its pixels, and the one static native body whose shapes are its ground.
    /// Plain data; the terrain layer that owns the chunk does the work. Chunks exist to keep a rebuild small: a change rebuilds the shapes of the
    /// chunks it touches, not the layer's.
    /// </summary>
    [MaxInstances(PixelTerrainLimits.Chunks)]
    internal sealed class PixelChunk
    {
        public int Index;               // cx * ChunksY + cy
        public int CX, CY;              // position in the chunk grid
        public int X0, Y0;              // lower-left pixel in the layer
        public int Width, Height;       // pixels
        public List<PixelColumn> Columns;    // Width columns of Height rows (row 0 is the bottom)
        public uint Body;               // the static native body, 0 until the layer is built
        public List<uint> Shapes;       // box shape ids (boxes mode) or chain ids (chains mode)
        public PixelChunk Left, Right, Down, Up;   // the chunks beside this one, null at the layer's edge (set by the layer)
        public bool Dirty;              // the shapes no longer match the columns
        public int Rebuilds;            // how many times the shapes were rebuilt (tests and profiling)

        public PixelChunk()
        {
            Columns = new List<PixelColumn>();
            Shapes = new List<uint>();
        }
    }
}
