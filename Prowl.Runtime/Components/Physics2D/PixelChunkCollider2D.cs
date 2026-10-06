// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace Prowl.Runtime;

/// <summary>
/// The collider of one <see cref="PixelTerrain2D"/> chunk. It makes no shapes of its own: it gives the chunk a native static
/// body and a registry index (what contact events name), and the terrain builds and rebuilds the chunk's shapes on that body as
/// the ground changes. Created by the terrain on hidden child objects; not meant to be added by hand.
/// </summary>
internal sealed class PixelChunkCollider2D : Collider2D
{
    private protected override void CreateShapes(uint body, ShapeSpec2D spec, List<uint> shapes)
    {
        // Nothing: the terrain owns the shapes. If this collider is rebuilt (its layer changed) the body is new and empty, and the
        // terrain notices (OwnBodyHandle changed) and fills it again.
    }

    private protected override bool BuildOutline(ShapeSpec2D spec, List<float> segments) => true;
}
