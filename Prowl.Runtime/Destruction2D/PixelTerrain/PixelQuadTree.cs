// Prowl.Runtime.Destruction2D. Ported from DTerrain (MIT, (c) 2020 Dominik Zimny); see LICENSE.md.
using System.Collections.Generic;

namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// Splits chunk data (a list of Columns) into rectangles of solid ground with a Quadtree.
    ///
    /// Columns are run-length data, so a node asks each of its columns one question
    /// (does one run of ranges cover rows y..y+h-1, does any range touch them) instead of
    /// testing every pixel against every range.
    /// </summary>
    internal static class PixelQuadTree
    {
        /// <summary>
        /// Generates rectangles of ground, in pixels.
        /// </summary>
        /// <param name="chunk">Chunk data</param>
        /// <param name="x">Offset x</param>
        /// <param name="y">Offset y</param>
        /// <param name="sizeX">Width of this step</param>
        /// <param name="sizeY">Height of this step</param>
        /// <param name="rects">Found rectangles are added here</param>
        public static void Build(List<PixelColumn> chunk, int x, int y, int sizeX, int sizeY, List<PixelRect> rects)
        {
            if (sizeX <= 0 || sizeY <= 0) return;

            int lastY = y + sizeY - 1;
            bool hasAnyAir = false;
            bool hasAnyGround = false;

            for (int i = x; i < x + sizeX; i++)
            {

                if (hasAnyGround == false && chunk[i].Touches(y, lastY))
                    hasAnyGround = true;
                if (hasAnyAir == false && chunk[i].Covers(y, lastY) == false)
                    hasAnyAir = true;

                if (hasAnyAir && hasAnyGround)
                {
                    Build(chunk, x, y, sizeX / 2, sizeY / 2, rects);
                    Build(chunk, x + sizeX / 2, y, sizeX / 2, sizeY / 2, rects);
                    Build(chunk, x, y + sizeY / 2, sizeX / 2, sizeY / 2, rects);
                    Build(chunk, x + sizeX / 2, y + sizeY / 2, sizeX / 2, sizeY / 2, rects);
                    return;
                }
            }

            if (hasAnyGround && !hasAnyAir)
                rects.Add(new PixelRect(x, y, sizeX, sizeY));
        }
    }
}
