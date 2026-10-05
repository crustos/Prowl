namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// How many terrain objects there can be: arena classes are allocated once, at these sizes (see [MaxInstances]). Raise a number to
    /// fit a game, and the memory follows: Chunks x the size of a chunk. A layer of ChunksX x ChunksY chunks uses that many.
    /// </summary>
    internal static class PixelTerrainLimits
    {
        public const int Layers = 4;
        public const int Chunks = 64;
        public const int ChainPoints = 4096;       // the most points one native chain is made of; a longer outline becomes several chains
    }
}
