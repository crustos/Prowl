// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Native.Box2D;
using Prowl.Runtime.Physics2D;

namespace Prowl.Core2D;

/// <summary>
/// The data of a Rigidbody2D component: how its body is made, and the body itself once it exists. A body is made when the component is
/// enabled, with the values below at that moment (change one by disabling and enabling the component), and destroyed when it is disabled.
/// While it lives, the simulation writes its pose into <see cref="Record"/> and the scene copies that, interpolated, into the node.
/// <para/>
/// An arena class: a pool of <see cref="CoreLimits.Rigidbodies"/>, recycled when the component is freed.
/// </summary>
[MaxInstances(CoreLimits.Rigidbodies)]
internal sealed class Rigidbody2D
{
    private static float[] s_state;
    private static bool s_ready;            // (an array is a value in the subset: it has no null to test)

    public Component Self;
    public int BodyType;                 // PB2.BodyStatic, BodyKinematic or BodyDynamic
    public float GravityScale;
    public float LinearDamping;
    public float AngularDamping;
    public bool IsBullet;
    public bool CanSleep;
    public bool FreezeRotation;

    public int BodyIndex;                // the slot in the simulation's registry, -1 while there is no body
    public uint Handle;                  // the native body, 0 while there is none
    public BodyRecord Record;            // its pose history, written by the step
    public int SyncedVersion;            // the node's world version just after the scene last wrote the pose: any other value is an edit by the game
    public float WrittenX, WrittenY, WrittenAngle;
    public bool HasWritten;

    public void Reset(int bodyType)
    {
        Self = null;
        BodyType = bodyType;
        GravityScale = 1f;
        LinearDamping = 0f;
        AngularDamping = 0.05f;
        IsBullet = false;
        CanSleep = true;
        FreezeRotation = false;
        BodyIndex = -1;
        Handle = 0;
        Record = null;
        SyncedVersion = 0;
        WrittenX = 0f;
        WrittenY = 0f;
        WrittenAngle = 0f;
        HasWritten = false;
    }

    /// <summary>True while the body exists (the component is enabled and there was room for it).</summary>
    public bool IsSimulated => Handle != 0;

    public void SetVelocity(float vx, float vy)
    {
        if (Handle != 0) PB2.BodySetVelocity(Handle, vx, vy, AngularVelocity());
    }

    public void AddImpulse(float ix, float iy)
    {
        if (Handle != 0) PB2.BodyApplyImpulse(Handle, ix, iy, 0, 0f, 0f);
    }

    public void AddForce(float fx, float fy)
    {
        if (Handle != 0) PB2.BodyApplyForce(Handle, fx, fy, 0, 0f, 0f);
    }

    // The state is 10 floats: position, cos/sin, linear velocity, angular velocity, mass, inertia, awake.
    private void Refresh()
    {
        if (!s_ready)
        {
            s_state = new float[10];
            s_ready = true;
        }
        PB2.BodyGetState(Handle, s_state);
    }

    public float VelocityX() { if (Handle == 0) return 0f; Refresh(); return s_state[4]; }
    public float VelocityY() { if (Handle == 0) return 0f; Refresh(); return s_state[5]; }
    public float AngularVelocity() { if (Handle == 0) return 0f; Refresh(); return s_state[6]; }
    public bool IsAwake() { if (Handle == 0) return false; Refresh(); return s_state[9] > 0.5f; }
}
