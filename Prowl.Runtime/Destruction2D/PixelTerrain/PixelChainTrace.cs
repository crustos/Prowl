// Prowl.Runtime.Destruction2D. Ported from DTerrain (MIT, (c) 2020 Dominik Zimny); see LICENSE.md.
using System.Collections.Generic;

namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// Chains found by PixelChainTrace, flat so they pass to physics without objects:
    /// chain k has Counts[k] points, starting at point Starts[k] of Points (x, y pairs, in pixels).
    /// Loop[k] is 1 for a closed loop (its last point connects to its first), 0 for an open chain.
    /// Walking a chain the ground is on the left hand and the air on the right.
    /// </summary>
    internal class PixelChainSet
    {
        public List<int> Points = new List<int>();
        public List<int> Starts = new List<int>();
        public List<int> Counts = new List<int>();
        public List<int> Loop = new List<int>();

        public int ChainCount { get { return Starts.Count; } }

        public void Clear()
        {
            Points.Clear();
            Starts.Clear();
            Counts.Clear();
            Loop.Clear();
        }
    }

    /// <summary>
    /// Traces the boundary of a chunk's ground as chains, instead of filling it with boxes.
    /// A solid region becomes one chain however large it is, and the chain is smooth for things
    /// rolling over it: no seams between boxes.
    ///
    /// A boundary edge is a pixel side between ground and air. It belongs to the chunk of its ground
    /// pixel, so two chunks never trace the same edge: where the boundary crosses a chunk border the
    /// chain ends in one chunk and the next begins in the other. Pixels beyond the neighbors given
    /// (Left, Right, Down, Up: the chunks next to this one, same size) are air.
    /// </summary>
    internal class PixelChainTrace
    {
        public PixelChunk Chunk;  // the chunk whose boundary is traced; its Left / Right / Down / Up (null at the layer's edge) answer next to it
        public int Width;
        public int Height;

        // directions: 0 +x, 1 +y, 2 -x, 3 -y
        private static int Dx(int d) { return d == 0 ? 1 : (d == 2 ? -1 : 0); }
        private static int Dy(int d) { return d == 1 ? 1 : (d == 3 ? -1 : 0); }

        private List<int> edgeX = new List<int>();   // start vertex of each unit edge
        private List<int> edgeY = new List<int>();
        private List<int> edgeDir = new List<int>();
        private List<int> edgeUsed = new List<int>();
        private List<int> outFirst = new List<int>();   // per vertex: first / second edge leaving it, -1 for none
        private List<int> outSecond = new List<int>();
        private List<int> inCount = new List<int>();

        public PixelChainTrace(PixelChunk chunk)
        {
            Chunk = chunk;
            Width = chunk.Width;
            Height = chunk.Height;
        }

        /// <summary>Is the pixel ground? Next to this chunk the neighbors answer, beyond them it is air.</summary>
        public bool Solid(int x, int y)
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height)
                return Chunk.Columns[x].isWithin(y);
            if (y >= 0 && y < Height)
            {
                if (x == -1 && Chunk.Left != null && Chunk.Left.Columns.Count == Width) return Chunk.Left.Columns[Width - 1].isWithin(y);
                if (x == Width && Chunk.Right != null && Chunk.Right.Columns.Count == Width) return Chunk.Right.Columns[0].isWithin(y);
            }
            else if (x >= 0 && x < Width)
            {
                if (y == -1 && Chunk.Down != null && Chunk.Down.Columns.Count == Width) return Chunk.Down.Columns[x].isWithin(Height - 1);
                if (y == Height && Chunk.Up != null && Chunk.Up.Columns.Count == Width) return Chunk.Up.Columns[x].isWithin(0);
            }
            return false;
        }

        private void AddEdge(int x, int y, int dir)
        {
            edgeX.Add(x);
            edgeY.Add(y);
            edgeDir.Add(dir);
            edgeUsed.Add(0);
        }

        private int VertexKey(int x, int y)
        {
            return x * (Height + 1) + y;
        }

        /// <summary>Traces the chunk's boundary; chains are added to result.</summary>
        public void Trace(PixelChainSet result)
        {
            edgeX.Clear();
            edgeY.Clear();
            edgeDir.Clear();
            edgeUsed.Clear();

            //Unit edges around each ground pixel that touches air, ground on the left of the way
            for (int x = 0; x < Width; x++)
            {
                for (int r = 0; r < Chunk.Columns[x].Ranges.Count; r++)
                {
                    int lo = Ranges_Max(Chunk.Columns[x].Ranges[r].Min, 0);
                    int hi = Ranges_Min(Chunk.Columns[x].Ranges[r].Max, Height - 1);
                    for (int y = lo; y <= hi; y++)
                    {
                        //A pixel in several ranges is one pixel
                        bool seen = false;
                        for (int q = 0; q < r && !seen; q++)
                            if (Chunk.Columns[x].Ranges[q].isWithin(y)) seen = true;
                        if (seen) continue;

                        if (!Solid(x, y - 1)) AddEdge(x, y, 0);          //air below: along +x
                        if (!Solid(x + 1, y)) AddEdge(x + 1, y, 1);      //air right: up +y
                        if (!Solid(x, y + 1)) AddEdge(x + 1, y + 1, 2);  //air above: along -x
                        if (!Solid(x - 1, y)) AddEdge(x, y + 1, 3);      //air left: down -y
                    }
                }
            }

            int vertices = (Width + 1) * (Height + 1);
            outFirst.Clear();
            outSecond.Clear();
            inCount.Clear();
            for (int v = 0; v < vertices; v++)
            {
                outFirst.Add(-1);
                outSecond.Add(-1);
                inCount.Add(0);
            }
            for (int e = 0; e < edgeX.Count; e++)
            {
                int k = VertexKey(edgeX[e], edgeY[e]);
                if (outFirst[k] < 0) outFirst[k] = e;
                else outSecond[k] = e;
                int d = edgeDir[e];
                inCount[VertexKey(edgeX[e] + Dx(d), edgeY[e] + Dy(d))] += 1;
            }

            //Open chains first: they start where more edges leave a vertex than arrive
            for (int e = 0; e < edgeX.Count; e++)
            {
                if (edgeUsed[e] != 0) continue;
                int k = VertexKey(edgeX[e], edgeY[e]);
                int outs = (outFirst[k] >= 0 ? 1 : 0) + (outSecond[k] >= 0 ? 1 : 0);
                if (outs > inCount[k])
                    Walk(e, false, result);
            }
            //What is left is closed
            for (int e = 0; e < edgeX.Count; e++)
            {
                if (edgeUsed[e] == 0)
                    Walk(e, true, result);
            }
        }

        /// <summary>The unused edge leaving vertex k that turns right most, then straight, then left; -1 if none.</summary>
        private int NextEdge(int k, int comingDir)
        {
            int best = -1;
            int bestRank = 99;
            for (int c = 0; c < 2; c++)
            {
                int e = c == 0 ? outFirst[k] : outSecond[k];
                if (e < 0 || edgeUsed[e] != 0) continue;
                int turn = (edgeDir[e] - comingDir + 4) % 4; // 0 straight, 1 left, 3 right
                int rank = turn == 3 ? 0 : (turn == 0 ? 1 : 2);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = e;
                }
            }
            return best;
        }

        private void Walk(int first, bool closed, PixelChainSet result)
        {
            List<int> xs = new List<int>();
            List<int> ys = new List<int>();
            List<int> dirs = new List<int>();

            int e = first;
            int startKey = VertexKey(edgeX[first], edgeY[first]);
            while (e >= 0)
            {
                edgeUsed[e] = 1;
                xs.Add(edgeX[e]);
                ys.Add(edgeY[e]);
                dirs.Add(edgeDir[e]);
                int d = edgeDir[e];
                int ex = edgeX[e] + Dx(d);
                int ey = edgeY[e] + Dy(d);
                int k = VertexKey(ex, ey);
                if (closed && k == startKey) break;
                e = NextEdge(k, d);
                if (e < 0)
                {
                    //an open chain ends here, on a vertex no edge leaves
                    xs.Add(ex);
                    ys.Add(ey);
                    dirs.Add(-1);
                }
            }

            //Keep corners only: a point between two edges of one direction is dropped
            int start = result.Points.Count / 2;
            int n = xs.Count;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                bool corner;
                if (closed)
                    corner = dirs[i] != dirs[(i + n - 1) % n];
                else if (i == 0 || dirs[i] == -1)
                    corner = true;
                else
                    corner = dirs[i] != dirs[i - 1];
                if (!corner) continue;
                result.Points.Add(xs[i]);
                result.Points.Add(ys[i]);
                count++;
            }
            if (count < (closed ? 3 : 2))
            {
                for (int q = 0; q < 2 * count; q++) result.Points.RemoveAt(result.Points.Count - 1);   // the points just added: the tail
                return;
            }
            result.Starts.Add(start);
            result.Counts.Add(count);
            result.Loop.Add(closed ? 1 : 0);
        }

        private static int Ranges_Max(int a, int b) { return a > b ? a : b; }
        private static int Ranges_Min(int a, int b) { return a < b ? a : b; }
    }
}
