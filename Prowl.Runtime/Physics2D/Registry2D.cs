// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace Prowl.Runtime.Physics2D;

/// <summary>
/// Which bodies, colliders and joints exist, and the small arena records that hold the simulation's data for them. Not generic and holding no
/// interface or delegate, so it translates to C (see tools/ccsharp): the engine layer keeps its own table of host objects, indexed by what
/// this hands out.
/// <para/>
/// <b>Why records for bodies and joints, and ids only for colliders.</b> An id by itself is cheaper as an integer: a slot of
/// <see cref="HandleTable"/> costs 4 bytes, where an arena record costs its own fields plus the 8-byte pointer that finds it by index. A
/// record pays for itself when it carries data that is hot and is used through references. A body's pose is written for every moving body on
/// every step, and a joint points at two bodies; a collider here is just an id, so it stays one.
/// <para/>
/// <b>Capacity.</b> Bodies and joints are limited to <see cref="PhysicsLimits"/>; the one past the limit gets -1, never a larger table.
/// Records are made the first time a slot is handed out and recycled afterwards, so the arena is touched only as far as the world has
/// grown. <b>Quarantine</b>, <b>generations</b> and index reuse are <see cref="HandleTable"/>'s, unchanged.
/// <para/>
/// An arena class of capacity 1, like the <see cref="SimCore2D"/> that owns it: one registry per process, which is what lets the core hand out a
/// reference to it. (Under .NET each simulation has its own.)
/// </summary>
[MaxInstances(1)]
internal sealed class Registry2D
{
    private HandleTable _bodyIds;
    private HandleTable _colliderIds;
    private HandleTable _jointIds;
    private BodyRecord[] _bodies;
    private JointRecord[] _joints;

    public Registry2D()
    {
        _bodyIds = new HandleTable();
        _colliderIds = new HandleTable();
        _jointIds = new HandleTable();
        _bodies = new BodyRecord[PhysicsLimits.Bodies];
        _joints = new JointRecord[PhysicsLimits.Joints];
    }

    // ---- all three ----------------------------------------------------------------------

    /// <summary>While on, a freed slot of any kind waits for <see cref="Flush"/> instead of being reused (see <see cref="HandleTable"/>).</summary>
    public void SetQuarantining(bool on)
    {
        _bodyIds.Quarantining = on;
        _colliderIds.Quarantining = on;
        _jointIds.Quarantining = on;
    }

    public void Flush()
    {
        _bodyIds.Flush();
        _colliderIds.Flush();
        _jointIds.Flush();
    }

    /// <summary>Frees everything. The records stay allocated, to be recycled; generations keep counting, so old handles stay dead.</summary>
    public void Clear()
    {
        _bodyIds.Clear();
        _colliderIds.Clear();
        _jointIds.Clear();
    }

    // ---- bodies -------------------------------------------------------------------------

    public int BodyCount => _bodyIds.Count;
    public int BodyHighWater => _bodyIds.HighWater;

    /// <summary>A new body's slot, or -1 when <see cref="PhysicsLimits.Bodies"/> are in use.</summary>
    public int AddBody()
    {
        int index = _bodyIds.AddWithin(PhysicsLimits.Bodies);
        if (index < 0) return -1;
        BodyRecord? record = _bodies[index];
        if (record == null)
        {
            record = new BodyRecord();
            _bodies[index] = record;
        }
        record.Reset(index);
        return index;
    }

    /// <summary>The record of a live body, or null.</summary>
    public BodyRecord? Body(int index)
    {
        return _bodyIds.IsLive(index) ? _bodies[index] : null;
    }

    public bool BodyLive(int index) { return _bodyIds.IsLive(index); }

    /// <summary>The generation of a live body (a handle to it carries this), or 0.</summary>
    public int BodyGeneration(int index) { return _bodyIds.GenerationOf(index); }

    public bool RemoveBody(int index) { return _bodyIds.Remove(index); }

    // ---- colliders: ids only ------------------------------------------------------------

    public int ColliderCount => _colliderIds.Count;
    public int ColliderHighWater => _colliderIds.HighWater;
    public int AddCollider() { return _colliderIds.Add(); }
    public bool ColliderLive(int index) { return _colliderIds.IsLive(index); }
    public int ColliderGeneration(int index) { return _colliderIds.GenerationOf(index); }
    public bool RemoveCollider(int index) { return _colliderIds.Remove(index); }

    // ---- joints -------------------------------------------------------------------------

    public int JointCount => _jointIds.Count;
    public int JointHighWater => _jointIds.HighWater;

    /// <summary>
    /// A new joint between two bodies, or -1 when <see cref="PhysicsLimits.Joints"/> are in use. Either body index may be -1, or no longer
    /// live: that end is then the world.
    /// </summary>
    public int AddJoint(int bodyA, int bodyB)
    {
        int index = _jointIds.AddWithin(PhysicsLimits.Joints);
        if (index < 0) return -1;
        JointRecord? record = _joints[index];
        if (record == null)
        {
            record = new JointRecord();
            _joints[index] = record;
        }
        record.Reset(index, Body(bodyA), Body(bodyB));
        return index;
    }

    public JointRecord? Joint(int index)
    {
        return _jointIds.IsLive(index) ? _joints[index] : null;
    }

    public bool JointLive(int index) { return _jointIds.IsLive(index); }

    public bool RemoveJoint(int index)
    {
        if (!_jointIds.Remove(index)) return false;
        JointRecord? record = _joints[index];
        if (record != null) record.Reset(index, null, null);   // do not keep a body alive through a dead joint
        return true;
    }

    /// <summary>The body index at each end of a live joint, -1 for the world (or for a joint that is not live).</summary>
    public int JointBodyA(int index)
    {
        JointRecord? j = Joint(index);
        return j != null && j.A != null ? j.A.Index : -1;
    }

    public int JointBodyB(int index)
    {
        JointRecord? j = Joint(index);
        return j != null && j.B != null ? j.B.Index : -1;
    }

    /// <summary>
    /// Appends to <paramref name="into"/> the index of every live joint that connects body <paramref name="bodyIndex"/>, and returns how many.
    /// The body is named by its slot, whether or not it is still live: this is asked at the moment a body is being removed, to find what
    /// dies with it. Which joints those are is a comparison of references.
    /// </summary>
    public int JointsOfBody(int bodyIndex, List<int> into)
    {
        if (bodyIndex < 0 || bodyIndex >= PhysicsLimits.Bodies) return 0;
        BodyRecord? body = _bodies[bodyIndex];
        if (body == null) return 0;
        int found = 0;
        for (int i = 0; i < _jointIds.HighWater; i++)
        {
            JointRecord? j = Joint(i);
            if (j != null && j.Touches(body))
            {
                into.Add(i);
                found++;
            }
        }
        return found;
    }
}
