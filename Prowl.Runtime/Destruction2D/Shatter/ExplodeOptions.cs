// Prowl.Runtime.Destruction2D. The shattering idea is from Unity-2D-Destruction (MIT, (c) 2016 Matthew Holtzem); see LICENSE.md.
namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// What Unity-2D-Destruction's Explodable component holds: how a node is shattered and what its fragments look like. (Its "allow runtime
    /// fragmentation" flag has no counterpart: fragments are always made when the scene-side <c>Explode</c> (Shatter2D in Core2D, Explodable2D in the engine) is called.)
    /// </summary>
    internal sealed class ExplodeOptions
    {
        public int Mode = Fracturer.Triangle;      // Fracturer.Triangle or Fracturer.Voronoi (Triangle is the original's default too)
        public int ExtraPoints = 0;                // random sites inside the shape, on top of its vertices
        public int SubshatterSteps = 0;            // how many more times every piece is broken again (the original advises at most 2)
        public int FragmentLayer = 0;              // the physics layer of the fragments
        public int RenderLayer = 0;                // the draw layer of the fragments (the original's order in layer)
        public int Texture = 0;                    // the texture the coordinates below are in
        public float UvMinX = 0f, UvMinY = 0f;     // where the source sprite is in that texture
        public float UvMaxX = 1f, UvMaxY = 1f;
        public float R = 1f, G = 1f, B = 1f, A = 1f;   // the fragments' tint
    }
}
