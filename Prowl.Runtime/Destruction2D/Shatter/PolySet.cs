// Prowl.Runtime.Destruction2D. The shattering idea is from Unity-2D-Destruction (MIT, (c) 2016 Matthew Holtzem); see LICENSE.md.
using System.Collections.Generic;

namespace Prowl.Runtime.Destruction2D
{
    /// <summary>
    /// A set of polygons, flat so it passes to physics and rendering without objects: polygon k has Counts[k] points, starting at point
    /// Starts[k] of Points (x, y pairs).
    /// </summary>
    internal sealed class PolySet
    {
        public List<float> Points = new List<float>();
        public List<int> Starts = new List<int>();
        public List<int> Counts = new List<int>();

        public int Count { get { return Starts.Count; } }

        public void Clear()
        {
            Points.Clear();
            Starts.Clear();
            Counts.Clear();
        }

        /// <summary>Starts a new polygon; <see cref="AddPoint"/> adds its points.</summary>
        public void Begin()
        {
            Starts.Add(Points.Count / 2);
            Counts.Add(0);
        }

        public void AddPoint(float x, float y)
        {
            Points.Add(x);
            Points.Add(y);
            Counts[Counts.Count - 1] = Counts[Counts.Count - 1] + 1;
        }

        /// <summary>Removes the polygon last begun, with its points.</summary>
        public void DropLast()
        {
            int k = Starts.Count - 1;
            int drop = Counts[k] * 2;
            for (int i = 0; i < drop; i++) Points.RemoveAt(Points.Count - 1);
            Starts.RemoveAt(k);
            Counts.RemoveAt(k);
        }

        public float X(int polygon, int point) { return Points[2 * (Starts[polygon] + point)]; }
        public float Y(int polygon, int point) { return Points[2 * (Starts[polygon] + point) + 1]; }

        public void CopyFrom(PolySet other)
        {
            Clear();
            for (int i = 0; i < other.Points.Count; i++) Points.Add(other.Points[i]);
            for (int i = 0; i < other.Starts.Count; i++)
            {
                Starts.Add(other.Starts[i]);
                Counts.Add(other.Counts[i]);
            }
        }
    }
}
