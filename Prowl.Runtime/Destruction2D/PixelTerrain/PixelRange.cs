// Prowl.Runtime.Destruction2D. Ported from DTerrain (MIT, (c) 2020 Dominik Zimny); see LICENSE.md.

namespace Prowl.Runtime.Destruction2D
{
    ///<summary>
    ///PixelRange represents a single range: [min;max]
    ///
    ///A struct: two ints, no heap allocation per range. Equality is by value (Min and Max),
    ///which is what PixelColumn.RemoveEqual relies on.
    ///</summary>
    internal struct PixelRange
    {
        public int Min;
        public int Max;

        public int Length { 
            get
            {
                int len = PixelMath.Abs(Max - Min);
                if (len <= 0) return 0;
                else return len;
            } 
        }

        public bool isWithin(int point)
        {
            return point <= Max && point >= Min;
        }

        public PixelRange(int a, int b)
        {
            Min = a;
            Max = b;
        }

        public bool Equals(PixelRange r)
        {
            return (Min == r.Min) && (Max == r.Max);
        }

        /// <summary>This range moved by a rows (negative: down).</summary>
        public PixelRange Shifted(int a)
        {
            return new PixelRange(Min + a, Max + a);
        }
    }
}
