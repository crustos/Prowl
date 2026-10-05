// Prowl.Runtime.Destruction2D. Ported from DTerrain (MIT, (c) 2020 Dominik Zimny); see LICENSE.md.
using System.Collections.Generic;

namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// PixelColumn represents a list of ranges: [ [min1;max1], [min2;max2] ... ].
    /// </summary>
    internal class PixelColumn
    {
        /// <summary>
        /// X pos of an column. Unused, but can be useful in future.
        /// </summary>
        public int X;
        public List<PixelRange> Ranges;
        public PixelColumn(int x) { this.X = x; Ranges = new List<PixelRange>(); }
        public PixelRange AddRange(int mini, int maxi)
        {
            PixelRange r = new PixelRange(mini, maxi);
            Ranges.Add(r);
            return r;
        }

        public PixelRange AddRange(PixelRange r)
        {
            Ranges.Add(r);
            return r;
        }

        public bool isWithin(int point)
        {
            foreach (PixelRange r in Ranges)
            {
                if (r.isWithin(point) == true) return true;
            }
            return false;
        }


        /// <param name="point">Point of intrest</param>
        /// <returns>Index of the first range that contains a point or -1 if none has it</returns>
        public int IndexWithin(int point)
        {
            for (int i = 0; i < Ranges.Count; i++)
            {
                if (Ranges[i].isWithin(point) == true) return i;
            }
            return -1;
        }

        /// <summary>
        /// True if every row in [y0;y1] belongs to some range (ranges that touch each other count together).
        /// </summary>
        public bool Covers(int y0, int y1)
        {
            int cur = y0;
            while (cur <= y1)
            {
                //Furthest reach of any range that contains cur
                int next = cur;
                for (int k = 0; k < Ranges.Count; k++)
                {
                    PixelRange r = Ranges[k];
                    if (r.Min <= cur && r.Max >= cur && r.Max + 1 > next) next = r.Max + 1;
                }
                if (next == cur) return false; //cur is air
                cur = next;
            }
            return true;
        }

        /// <summary>
        /// True if at least one row in [y0;y1] belongs to some range.
        /// </summary>
        public bool Touches(int y0, int y1)
        {
            for (int k = 0; k < Ranges.Count; k++)
            {
                PixelRange r = Ranges[k];
                if (PixelMath.Max(r.Min, y0) <= PixelMath.Min(r.Max, y1)) return true;
            }
            return false;
        }

        /// <summary>
        /// Deletes the rows y0..y1 (inclusive) from the column: a range that contains some of them is cut,
        /// and one that contains all of them is removed. Every other row is untouched.
        /// </summary>
        /// <returns>True if any row was deleted</returns>
        public bool ClearRows(int y0, int y1)
        {
            if (y0 > y1) return false;
            bool changed = false;
            int n = Ranges.Count;                       // pieces cut off a range are added after the original ones
            int i = 0;
            while (i < n)
            {
                PixelRange r = Ranges[i];
                if (r.Max < y0 || r.Min > y1)
                {
                    i++;
                    continue;
                }
                changed = true;
                bool left = r.Min < y0;
                bool right = r.Max > y1;
                if (left && right)
                {
                    Ranges[i] = new PixelRange(r.Min, y0 - 1);
                    Ranges.Add(new PixelRange(y1 + 1, r.Max));
                    i++;
                }
                else if (left)
                {
                    Ranges[i] = new PixelRange(r.Min, y0 - 1);
                    i++;
                }
                else if (right)
                {
                    Ranges[i] = new PixelRange(y1 + 1, r.Max);
                    i++;
                }
                else
                {
                    Ranges.RemoveAt(i);
                    n--;
                }
            }
            return changed;
        }

        /// <summary>Deletes a single row.</summary>
        public void SingleDelRange(int pos)
        {
            ClearRows(pos, pos);
        }

        /// <summary>
        /// Deletes the rows strictly between delr.Min and delr.Max (both bounds are rows that stay).
        /// This is how CollidableChunk asks: it passes the rectangle it paints, one row wider on each side.
        /// </summary>
        /// <returns>True if any row was deleted</returns>
        public bool DelRange(PixelRange delr)
        {
            return ClearRows(delr.Min + 1, delr.Max - 1);
        }

        /// <summary>
        /// Adds the rows addr.Min..addr.Max (inclusive). The new rows and every range they overlap or touch become one range,
        /// so ranges stay apart from each other.
        /// </summary>
        /// <returns>True if any row was newly added</returns>
        public bool SumRange(PixelRange addr)
        {
            if (addr.Min > addr.Max) return false;
            if (Covers(addr.Min, addr.Max)) return false;
            int min = addr.Min;
            int max = addr.Max;
            bool merged = true;
            while (merged)                              // a merge widens the range, which can reach the next one
            {
                merged = false;
                int i = 0;
                while (i < Ranges.Count)
                {
                    PixelRange r = Ranges[i];
                    if (r.Min <= max + 1 && r.Max >= min - 1)
                    {
                        min = PixelMath.Min(min, r.Min);
                        max = PixelMath.Max(max, r.Max);
                        Ranges.RemoveAt(i);
                        merged = true;
                    }
                    else i++;
                }
            }
            Ranges.Add(new PixelRange(min, max));
            return true;
        }
    }
}
