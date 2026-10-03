// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Physics2D;

/// <summary>
/// How many of each thing a 2D world can hold. These are the capacities of the arena classes, so they are limits of the engine, not tuning
/// hints: the registry refuses the one past them (a thrown exception here, an abort in the C build). `python3 build.py arenas` prints what
/// they cost, measured: a body record is 44 bytes and a joint record 24, plus 12 per slot for the tables that find them. At 256 bodies and 64
/// joints that is about 16 KiB, half of a 32 KiB L1 data cache, which leaves the rest for what else a step touches. 512 and 128 is already
/// 32.5 KiB (the whole L1), and 32768 bodies would be 1.75 MiB, which does not even fit L2. Only a Rigidbody2D takes a body slot: a collider
/// with no rigidbody is static level geometry and costs none, so this is a limit on moving things, not on the level.
/// </summary>
internal static class PhysicsLimits
{
    public const int Bodies = 256;
    public const int Joints = 64;
}
