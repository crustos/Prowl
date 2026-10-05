// Prowl.Runtime.Destruction2D. Ported from DTerrain (MIT, (c) 2020 Dominik Zimny); see LICENSE.md.
using System.Collections.Generic;

namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// Builds rectangles of ground straight from run-length data, merging along rows:
    /// a range that repeats unchanged in the next column extends the rectangle instead of
    /// starting a new one. A chunk becomes far fewer rectangles than quadtree leaves,
    /// so far fewer physics shapes.
    /// </summary>
    internal static class PixelRectMerge
    {
        /// <summary>
        /// The rectangles cover exactly the rows the columns' ranges contain (X is the column index,
        /// Y the range's Min, H its length in rows). Ranges with Min > Max hold no row and are skipped.
        /// Rectangles are added in the order they start: by column, then by range.
        /// </summary>
        /// <param name="columns">Chunk data</param>
        /// <param name="rects">Found rectangles are added here</param>
        public static void FromColumns(List<PixelColumn> columns, List<PixelRect> rects)
        {
            int first = rects.Count;
            List<int> open = new List<int>();   //rects (indices) that end at the previous column
            List<int> next = new List<int>();

            for (int x = 0; x < columns.Count; x++)
            {
                next.Clear();

                for (int i = 0; i < columns[x].Ranges.Count; i++)
                {
                    PixelRange r = columns[x].Ranges[i];
                    if (r.Min > r.Max) continue;

                    int height = r.Max - r.Min + 1;
                    int found = -1;
                    for (int k = 0; k < open.Count; k++)
                    {
                        int oi = open[k];                       // (an index inside an index: written in two steps)
                        PixelRect o = rects[oi];
                        if (o.Y == r.Min && o.H == height)
                        {
                            found = k;
                            break;
                        }
                    }

                    if (found >= 0)
                    {
                        int idx = open[found];
                        open.RemoveAt(found); //a rectangle is extended once per column
                        PixelRect o = rects[idx];
                        rects[idx] = new PixelRect(o.X, o.Y, o.W + 1, o.H);
                        next.Add(idx);
                    }
                    else
                    {
                        rects.Add(new PixelRect(x, r.Min, 1, height));
                        next.Add(rects.Count - 1);
                    }
                }

                open.Clear();                       // next becomes open: copied, a list is owned by one name
                for (int k = 0; k < next.Count; k++) open.Add(next[k]);
            }
        }
    }
}
