// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// Raw bindings for Native/Box2D/prowl_box2d.h. This file (and Box2DWorld.cs) deliberately depend on
// nothing else in Prowl so they can be built and tested standalone (Native/Box2D/Tests).
//
// Every struct here mirrors a fixed-layout record from the header. Box2DWorld.Create() asserts the
// sizes against the loaded native library, so a stale or mismatched binary fails fast instead of
// corrupting memory.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Prowl.Runtime.Physics2D.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PB2BodyMove
{
    public int BodyIndex;
    public float X, Y;
    public float C, S; // rotation cos / sin
    public int FellAsleep;

    public readonly float Angle => MathF.Atan2(S, C);
}

internal static class PB2EventFlags
{
    public const int Begin = 1;
    public const int End = 2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PB2ContactEvent
{
    public int ColliderA, ColliderB;
    public float PX, PY;   // world contact point (Begin only)
    public float NX, NY;   // normal A -> B (Begin only)
    public float Impulse;  // total normal impulse (Begin only)
    public int Flags;
    public int BodyA, BodyB;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PB2SensorEvent
{
    public int SensorCollider, VisitorCollider;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PB2StepInfo
{
    public int MoveCount;
    public int ContactCount;
    public int ContactBeginCount;
    public int SensorCount;
    public int AwakeBodyCount;
    public int Reserved;
    public nint Moves;
    public nint Contacts;
    public nint Sensors;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PB2TransformSet
{
    public uint Body;
    public float X, Y, Angle;
    public int Mode; // 0 teleport, 1 kinematic target
}

[StructLayout(LayoutKind.Sequential)]
internal struct PB2RayHit
{
    public int Collider;
    public int Body;
    public float PX, PY;
    public float NX, NY;
    public float Fraction;
}

internal static class PB2BodyType
{
    public const int Static = 0, Kinematic = 1, Dynamic = 2;
}

[Flags]
internal enum PB2BodyFlags : uint
{
    None = 0,
    Bullet = 1,
    NoSleep = 2,
    LockX = 4,
    LockY = 8,
    LockRotation = 16,
    Disabled = 32,
}

[Flags]
internal enum PB2ShapeFlags : uint
{
    None = 0,
    Sensor = 1,
}

/// <summary>Raw native entry points. (SuppressGCTransition was measured at ~0 ns benefit and rejected: it blocks GC suspension for no gain.)</summary>
internal static unsafe partial class PB2
{
    public const string Lib = "prowl_box2d";
    public const int AbiVersion = 1;
    public const int LayerCount = 32;

    // ---- lifecycle -----------------------------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "pb2_abi")] public static partial void Abi(int* out8);
    [LibraryImport(Lib, EntryPoint = "pb2_world_create")] public static partial void WorldCreate(float gx, float gy, int workers);
    [LibraryImport(Lib, EntryPoint = "pb2_world_destroy")] public static partial void WorldDestroy();
    [LibraryImport(Lib, EntryPoint = "pb2_world_set_gravity")] public static partial void WorldSetGravity(float x, float y);
    [LibraryImport(Lib, EntryPoint = "pb2_world_set_layer_matrix")] public static partial void WorldSetLayerMatrix(uint* rows32);
    [LibraryImport(Lib, EntryPoint = "pb2_step")] public static partial void Step(float dt, int subSteps, PB2StepInfo* info);
    [LibraryImport(Lib, EntryPoint = "pb2_bodies_set_transforms")] public static partial void BodiesSetTransforms(PB2TransformSet* sets, int count, float dt);

    // ---- bodies --------------------------------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "pb2_body_create")] public static partial uint BodyCreate(int type, float x, float y, float angle, int bodyIndex, float gravityScale, float linearDamping, float angularDamping, uint flags);
    [LibraryImport(Lib, EntryPoint = "pb2_body_destroy")] public static partial void BodyDestroy(uint body);
    [LibraryImport(Lib, EntryPoint = "pb2_body_set_transform")] public static partial void BodySetTransform(uint body, float x, float y, float angle);
    [LibraryImport(Lib, EntryPoint = "pb2_body_set_type")] public static partial void BodySetType(uint body, int type);
    [LibraryImport(Lib, EntryPoint = "pb2_body_set_flags")] public static partial void BodySetFlags(uint body, uint flags);
    [LibraryImport(Lib, EntryPoint = "pb2_body_set_damping")] public static partial void BodySetDamping(uint body, float linear, float angular);
    [LibraryImport(Lib, EntryPoint = "pb2_body_set_gravity_scale")] public static partial void BodySetGravityScale(uint body, float scale);
    [LibraryImport(Lib, EntryPoint = "pb2_body_set_velocity")] public static partial void BodySetVelocity(uint body, float vx, float vy, float w);
    [LibraryImport(Lib, EntryPoint = "pb2_body_set_mass")] public static partial void BodySetMass(uint body, float mass);
    [LibraryImport(Lib, EntryPoint = "pb2_body_set_awake")] public static partial void BodySetAwake(uint body, int awake);
    [LibraryImport(Lib, EntryPoint = "pb2_body_apply_force")] public static partial void BodyApplyForce(uint body, float fx, float fy, int hasPoint, float px, float py);
    [LibraryImport(Lib, EntryPoint = "pb2_body_apply_impulse")] public static partial void BodyApplyImpulse(uint body, float ix, float iy, int hasPoint, float px, float py);
    [LibraryImport(Lib, EntryPoint = "pb2_body_apply_torque")] public static partial void BodyApplyTorque(uint body, float torque, int asImpulse);
    [LibraryImport(Lib, EntryPoint = "pb2_body_get_state")] public static partial void BodyGetState(uint body, float* out10);

    // ---- shapes --------------------------------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "pb2_shape_create_circle")] public static partial uint ShapeCreateCircle(uint body, int colliderIndex, int layer, float cx, float cy, float radius, float density, float friction, float restitution, uint flags);
    [LibraryImport(Lib, EntryPoint = "pb2_shape_create_box")] public static partial uint ShapeCreateBox(uint body, int colliderIndex, int layer, float halfW, float halfH, float ox, float oy, float angle, float cornerRadius, float density, float friction, float restitution, uint flags);
    [LibraryImport(Lib, EntryPoint = "pb2_shape_create_capsule")] public static partial uint ShapeCreateCapsule(uint body, int colliderIndex, int layer, float x1, float y1, float x2, float y2, float radius, float density, float friction, float restitution, uint flags);
    [LibraryImport(Lib, EntryPoint = "pb2_shape_create_polygon")] public static partial uint ShapeCreatePolygon(uint body, int colliderIndex, int layer, float* xy, int pointCount, float radius, float density, float friction, float restitution, uint flags);
    [LibraryImport(Lib, EntryPoint = "pb2_segments_create")] public static partial int SegmentsCreate(uint body, int colliderIndex, int layer, float* xy, int pointCount, int isLoop, float friction, float restitution, uint* outShapes);
    [LibraryImport(Lib, EntryPoint = "pb2_chain_create")] public static partial uint ChainCreate(uint body, int colliderIndex, int layer, float* xy, int pointCount, int isLoop, float friction, float restitution);
    [LibraryImport(Lib, EntryPoint = "pb2_shape_destroy")] public static partial void ShapeDestroy(uint shape);
    [LibraryImport(Lib, EntryPoint = "pb2_chain_destroy")] public static partial void ChainDestroy(uint chain);
    [LibraryImport(Lib, EntryPoint = "pb2_shape_set_material")] public static partial void ShapeSetMaterial(uint shape, float friction, float restitution);
    [LibraryImport(Lib, EntryPoint = "pb2_shape_set_density")] public static partial void ShapeSetDensity(uint shape, float density);

    // ---- queries -------------------------------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "pb2_raycast")] public static partial int Raycast(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, int hitSensors, PB2RayHit* hit);
    [LibraryImport(Lib, EntryPoint = "pb2_raycast_all")] public static partial int RaycastAll(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, int hitSensors, PB2RayHit* hits, int capacity);
    [LibraryImport(Lib, EntryPoint = "pb2_overlap_point")] public static partial int OverlapPoint(float x, float y, uint layerMask, int hitSensors, int* colliders, int capacity);
    [LibraryImport(Lib, EntryPoint = "pb2_overlap_circle")] public static partial int OverlapCircle(float cx, float cy, float radius, uint layerMask, int hitSensors, int* colliders, int capacity);
    [LibraryImport(Lib, EntryPoint = "pb2_overlap_box")] public static partial int OverlapBox(float cx, float cy, float halfW, float halfH, float angle, uint layerMask, int hitSensors, int* colliders, int capacity);
}
