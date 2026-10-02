// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime;

/// <summary>
/// How a rigidbody's Transform is filled in between physics steps. Physics runs at a fixed rate, so
/// without smoothing the visuals move in fixed-rate jumps whenever the frame rate differs from it.
/// </summary>
public enum RigidbodyInterpolation
{
    /// <summary>Write the simulated pose as-is. Cheapest, and visibly steps at high frame rates.</summary>
    None,
    /// <summary>Render between the last two steps. Smooth, at the cost of trailing one fixed step behind.</summary>
    Interpolate,
    /// <summary>Predict ahead of the last step from the body's velocity. No lag, but can overshoot a collision.</summary>
    Extrapolate
}
