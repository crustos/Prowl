// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Physics2D;

/// <summary>
/// A joint, as the simulation sees it: which two bodies it connects, as references to their records. "Which joints die with this body" is
/// then a comparison of references, where it was a scan of two parallel integer lists. A joint anchored to the world has a null
/// <see cref="A"/>.
/// <para/>
/// An arena class, so its references to <see cref="BodyRecord"/>s are plain pointers between two arenas.
/// </summary>
[MaxInstances(PhysicsLimits.Joints)]
internal sealed class JointRecord
{
    public int Index;
    public BodyRecord? A;        // the world when null
    public BodyRecord? B;

    public void Reset(int index, BodyRecord? a, BodyRecord? b)
    {
        Index = index;
        A = a;
        B = b;
    }

    /// <summary>Whether this joint connects <paramref name="body"/>.</summary>
    public bool Touches(BodyRecord body)
    {
        return A == body || B == body;
    }
}
