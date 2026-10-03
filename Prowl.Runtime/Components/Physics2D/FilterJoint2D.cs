// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Native.Box2D;

namespace Prowl.Runtime;

/// <summary>
/// Stops two specific rigidbodies from colliding with each other and does nothing else: no constraint, no force. Use it for the limbs of a
/// ragdoll, or a held object that should pass through its holder, without giving up collisions with everything else.
/// <para/>
/// Put it on one body and connect the other with <see cref="Joint2D.ConnectedBody"/>. It has no anchors to speak of, and
/// <see cref="Joint2D.CollideConnected"/> has no use here: a filter joint with collisions switched back on does nothing at all.
/// </summary>
[AddComponentMenu("Physics 2D/Joints/Filter Joint 2D")]
[ComponentIcon("\uf05e")] // Ban
public sealed class FilterJoint2D : Joint2D
{
    private protected override int JointType => PB2.JointFilter;

    private protected override void FillTunables(ref PB2JointDef def) { }
}
