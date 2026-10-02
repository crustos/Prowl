// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace Prowl.Runtime.Physics2D.Native;

/// <summary>The events of one step. Spans point into native memory and are valid only until the next <see cref="Box2DWorld.Step"/>.</summary>
internal readonly ref struct StepEvents
{
    public readonly ReadOnlySpan<PB2BodyMove> Moves;
    /// <summary>Begin events first (<see cref="BeginCount"/> of them), then end events.</summary>
    public readonly ReadOnlySpan<PB2ContactEvent> Contacts;
    public readonly ReadOnlySpan<PB2SensorEvent> Sensors;
    public readonly int BeginCount;
    public readonly int AwakeBodyCount;

    public ReadOnlySpan<PB2ContactEvent> ContactBegins => Contacts[..BeginCount];
    public ReadOnlySpan<PB2ContactEvent> ContactEnds => Contacts[BeginCount..];

    internal unsafe StepEvents(PB2StepInfo i)
    {
        Moves = new ReadOnlySpan<PB2BodyMove>((void*)i.Moves, i.MoveCount);
        Contacts = new ReadOnlySpan<PB2ContactEvent>((void*)i.Contacts, i.ContactCount);
        Sensors = new ReadOnlySpan<PB2SensorEvent>((void*)i.Sensors, i.SensorCount);
        BeginCount = i.ContactBeginCount;
        AwakeBodyCount = i.AwakeBodyCount;
    }
}

/// <summary>
/// Owns the (single, process-wide) native Box2D world. The fork this builds against is single-world by
/// design, so only one instance can exist at a time.
/// <para/>
/// Crossing budget per frame: <b>one</b> <see cref="Step"/> call, plus one <c>BodiesSetTransforms</c> call if any
/// transforms were queued. Everything else (creating bodies, impulses, queries) is user-driven and rare.
/// </summary>
internal sealed unsafe class Box2DWorld : IDisposable
{
    private static Box2DWorld? s_instance;

    private PB2TransformSet[] _queued = new PB2TransformSet[64];
    private int _queuedCount;
    private bool _disposed;

    private Box2DWorld() { }

    public static Box2DWorld Instance => s_instance ?? throw new InvalidOperationException("No Box2D world exists. Call Box2DWorld.Create first.");
    public static bool Exists => s_instance != null;

    public static Box2DWorld Create(float gravityX, float gravityY, int workerCount = 1)
    {
        if (s_instance != null)
            throw new InvalidOperationException("The Box2D-Packed build is single-world; dispose the existing world first.");

        VerifyAbi();
        PB2.WorldCreate(gravityX, gravityY, workerCount);
        return s_instance = new Box2DWorld();
    }

    /// <summary>Fails fast if the loaded native library does not match these struct layouts.</summary>
    private static void VerifyAbi()
    {
        int* a = stackalloc int[8];
        PB2.Abi(a);

        (string name, int native, int managed)[] checks =
        [
            ("ABI version", a[0], PB2.AbiVersion),
            ("PB2BodyMove", a[1], Unsafe.SizeOf<PB2BodyMove>()),
            ("PB2ContactEvent", a[2], Unsafe.SizeOf<PB2ContactEvent>()),
            ("PB2SensorEvent", a[3], Unsafe.SizeOf<PB2SensorEvent>()),
            ("PB2StepInfo", a[4], Unsafe.SizeOf<PB2StepInfo>()),
            ("PB2TransformSet", a[5], Unsafe.SizeOf<PB2TransformSet>()),
            ("PB2RayHit", a[6], Unsafe.SizeOf<PB2RayHit>()),
            ("sizeof(void*)", a[7], IntPtr.Size),
        ];

        foreach (var (name, native, managed) in checks)
            if (native != managed)
                throw new InvalidOperationException($"prowl_box2d ABI mismatch: {name} is {native} natively but {managed} in managed code. Rebuild Native/Box2D.");
    }

    public void SetGravity(float x, float y) => PB2.WorldSetGravity(x, y);

    /// <summary>rows[a] bit b set means layers a and b collide. Takes effect for pairs created after the call.</summary>
    public void SetLayerMatrix(ReadOnlySpan<uint> rows)
    {
        if (rows.Length != PB2.LayerCount) throw new ArgumentException($"Expected {PB2.LayerCount} rows.", nameof(rows));
        fixed (uint* p = rows) PB2.WorldSetLayerMatrix(p);
    }

    /// <summary>
    /// Queues a teleport (<paramref name="kinematicTarget"/> false) or a kinematic move that reaches the pose over the
    /// next step. Flushed in one native call at the start of <see cref="Step"/>.
    /// </summary>
    public void QueueTransform(uint body, float x, float y, float angle, bool kinematicTarget = false)
    {
        if (_queuedCount == _queued.Length) Array.Resize(ref _queued, _queued.Length * 2);
        _queued[_queuedCount++] = new PB2TransformSet { Body = body, X = x, Y = y, Angle = angle, Mode = kinematicTarget ? 1 : 0 };
    }

    public StepEvents Step(float dt, int subSteps = 4)
    {
        if (_queuedCount > 0)
        {
            fixed (PB2TransformSet* p = _queued) PB2.BodiesSetTransforms(p, _queuedCount, dt);
            _queuedCount = 0;
        }

        PB2StepInfo info;
        PB2.Step(dt, subSteps, &info);
        return new StepEvents(info);
    }

    // ---- queries -------------------------------------------------------------------------

    public bool Raycast(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, bool hitSensors, out PB2RayHit hit)
    {
        PB2RayHit h;
        int n = PB2.Raycast(ox, oy, dx, dy, maxDistance, layerMask, hitSensors ? 1 : 0, &h);
        hit = h;
        return n != 0;
    }

    public int RaycastAll(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, bool hitSensors, Span<PB2RayHit> results)
    {
        fixed (PB2RayHit* p = results)
            return PB2.RaycastAll(ox, oy, dx, dy, maxDistance, layerMask, hitSensors ? 1 : 0, p, results.Length);
    }

    public int OverlapPoint(float x, float y, uint layerMask, bool hitSensors, Span<int> colliders)
    {
        fixed (int* p = colliders) return PB2.OverlapPoint(x, y, layerMask, hitSensors ? 1 : 0, p, colliders.Length);
    }

    public int OverlapCircle(float cx, float cy, float radius, uint layerMask, bool hitSensors, Span<int> colliders)
    {
        fixed (int* p = colliders) return PB2.OverlapCircle(cx, cy, radius, layerMask, hitSensors ? 1 : 0, p, colliders.Length);
    }

    public int OverlapBox(float cx, float cy, float halfW, float halfH, float angle, uint layerMask, bool hitSensors, Span<int> colliders)
    {
        fixed (int* p = colliders) return PB2.OverlapBox(cx, cy, halfW, halfH, angle, layerMask, hitSensors ? 1 : 0, p, colliders.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PB2.WorldDestroy();
        s_instance = null;
    }
}
