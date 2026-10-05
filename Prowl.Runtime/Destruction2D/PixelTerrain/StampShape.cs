// Prowl.Runtime.Destruction2D. Ported from DTerrain (MIT, (c) 2020 Dominik Zimny); see LICENSE.md.
using System.Collections.Generic;

namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// StampShape is a simple class that holds a list of Ranges (not ranges) and then is used to destroy terrain with.
    /// To make complicated shape destructions (not squares, circles etc.) don't use it as it supports only list of ranges.
    /// </summary>
    internal class StampShape
    {

        public List<PixelRange> Ranges;

        /// <summary>
        /// Columns of the shape's box that come before its first range (a circle's first column holds no pixel). Ranges[k] belongs to
        /// the column OffsetX + k, so a stamp at x puts it at x + OffsetX + k. (DTerrain placed it at x + k, which shifts a circle by one pixel.)
        /// </summary>
        public int OffsetX;
        public int Width { get; private set; }
        public int Height { get; private set; }

        public StampShape(int w, int h)
        {
            Width = w;
            Height = h;
            Ranges = new List<PixelRange>();
        }

        public static StampShape GenerateShapeRange(int length)
        {
            StampShape s = new StampShape(1, length);
            s.Ranges.Add(new PixelRange(0, length));
            return s;
        }

        /// <summary>
        /// Generates a StampShape: circle.
        /// </summary>
        /// <param name="r">Radius</param>
        /// <returns>StampShape: circle.</returns>
        public static StampShape GenerateShapeCircle(int r)
        {
            int centerX = r;
            int centerY = r;
            StampShape s = new StampShape(2 * r, 2 * r);
            for (int i = 0; i <= 2 * r; i++)
            {
                bool down = false;
                int min = 0;
                int max = 0;
                for (int j = 0; j <= 2 * r; j++)
                {
                    if ((centerX - i) * (centerX - i) + (centerY - j) * (centerY - j) < r * r)
                    {
                        if (down == false)
                        {
                            down = true;
                            min = j;
                        }

                    }
                    else
                    {
                        if (down)
                        {
                            max = j;
                            break;

                        }

                    }

                }
                if (down)
                {
                    PixelRange range = new PixelRange(min, max);
                    if (s.Ranges.Count == 0) s.OffsetX = i;
                    s.Ranges.Add(range);
                }

            }

            return s;
        }

        public static StampShape GenerateShapeRect(int w, int h)
        {
            StampShape s = new StampShape(w, h);

            for(int i = 0; i<w;i++)
            {
                s.Ranges.Add(new PixelRange(0, h-1)); //0,1,2...h-1
            }

            return s;
        }
    }
}
