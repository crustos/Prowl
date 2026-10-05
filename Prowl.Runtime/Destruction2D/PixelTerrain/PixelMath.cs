namespace Prowl.Runtime.Destruction2D
{
    /// <summary>Integer helpers the model needs (Unity's Mathf, without Unity).</summary>
    internal static class PixelMath
    {
        public static int Max(int a, int b) { return a > b ? a : b; }
        public static int Min(int a, int b) { return a < b ? a : b; }
        public static int Abs(int a) { return a < 0 ? -a : a; }
    }
}
