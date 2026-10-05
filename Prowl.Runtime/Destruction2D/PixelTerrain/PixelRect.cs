// Prowl.Runtime.Destruction2D. Ported from DTerrain (MIT, (c) 2020 Dominik Zimny); see LICENSE.md.
namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// A rectangle of whole pixels: X, Y is the lower-left pixel, W x H its size.
    /// Plain ints, so the same rectangles can feed Unity colliders or Box2D shapes.
    /// </summary>
    internal struct PixelRect
    {
        public int X;
        public int Y;
        public int W;
        public int H;

        public PixelRect(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }
    }
}
