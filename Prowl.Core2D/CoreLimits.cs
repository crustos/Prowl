// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Core2D;

/// <summary>
/// How many of each thing the 2D runtime holds. Each is the capacity of an arena class (see Prowl.Runtime/Physics2D/Arena.cs): the most that
/// can exist at once, and so the most memory the class can ever occupy. `python3 build.py arenas` prints what they cost.
/// </summary>
internal static class CoreLimits
{
    public const int Nodes = 256;
    public const int Components = 512;
    public const int Rigidbodies = 128;     // <= Prowl.Runtime.Physics2D.PhysicsLimits.Bodies
    public const int Colliders = 256;
    public const int Sprites = 256;

    /// <summary>Floats per sprite in a draw batch. Must equal GFX_SPRITE_FLOATS in Native/Gfx2D/gfx2d.h (player_build.py checks).</summary>
    public const int SpriteFloats = 12;
}
