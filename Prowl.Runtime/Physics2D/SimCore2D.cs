// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using Prowl.Native.Box2D;

namespace Prowl.Runtime.Physics2D;

/// <summary>What <see cref="SimCore2D.NextEvent"/> says happened. Plain integers, so the same constants work in C.</summary>
internal static class SimEvent
{
    public const int None = 0;
    /// <summary>Two colliders stopped touching. <c>EventSelf</c> is the one that should hear about it.</summary>
    public const int CollisionEnd = 1;
    /// <summary>Two colliders began touching. The contact point, normal (pointing self to other) and impulse are in <c>EventX</c> ..</summary>
    public const int CollisionBegin = 2;
    public const int TriggerExit = 3;
    public const int TriggerStay = 4;
    public const int TriggerEnter = 5;
    /// <summary>A joint went past its force or torque threshold. <c>EventJoint</c> is its slot; the owner is to destroy it.</summary>
    public const int JointBreak = 6;
}

/// <summary>
/// The 2D simulation with every object taken out: the native world's life, the step, the registry of what exists, the active trigger pairs,
/// the queries, and the step's events as a <b>stream</b>. It talks to Box2D only through the generated bindings, holds no interface, delegate
/// or host object, and throws nothing, so it translates to C (tools/ccsharp) and runs there against the real library. What cannot translate
/// (the managed objects to call, the handing of the native world between scenes) is <see cref="PhysicsSimulation2D"/>, a thin layer over this.
/// <para/>
/// <b>How events reach the engine: a cursor, not a list and not a callback.</b>
/// <code>
///   core.Step(dt, substeps);
///   while (core.NextEvent())
///       switch (core.EventKind) { ... call the handler for core.EventSelf / EventOther ... }
///   core.EndStep();
/// </code>
/// Each <see cref="NextEvent"/> computes the next event from the state as it is NOW. That is the point. The old step interleaved the logic and the
/// calls, and the logic depended on what the calls did: a handler that destroys a collider removes its trigger pairs and must stop any later
/// event about it; the reverse direction of a pair ("the ball hit the ground", then "the ground was hit by the ball") is delivered only if both are
/// still alive after the first handler ran. A list built up front cannot know any of that. A callback registered with the core would have to re-enter
/// it in the middle of its own iteration, and the C# subset has no delegate to register. A cursor can do both: the order and the rules live here, the
/// handlers run between two calls, and every point at which a handler can change the world is the visible boundary of a loop body.
/// <para/>
/// The order is fixed: contact ends, contact begins, trigger exits, trigger stays (for the pairs still overlapping after the exits), trigger enters,
/// joint breaks. Ends come before begins so a pair that ends and restarts in one step reads End then Begin. Poses are written before any of it.
/// <para/>
/// An arena class of capacity 1: one simulation per process, as there is one native world. (Under .NET each scene has its own; the capacity is
/// only read by the translator.)
/// </summary>
[MaxInstances(1)]
internal sealed class SimCore2D
{
    /// <summary>The largest query distance: Box2D's traversal overflows on huge translations, and callers default to float.MaxValue.</summary>
    public const float MaxQueryDistance = 100000f;

    public Registry2D Registry;                  // what exists, and the arena records of bodies and joints

    private TriggerSet _active;                  // sensor/visitor pairs overlapping right now
    private ContactSet _contacts;                // collider pairs touching right now, and how many pairs of their shapes do
    private ContactSet _vanished;               // pairs whose shapes were destroyed with the collider kept (ShapesVanishing), until the next step settles them
    private List<long> _synth;                   // pairs that stopped touching because their shapes were destroyed: Ends with no native contact behind them
    private int _synthCursor;
    private List<int> _due;                      // for each contact of the step: 1 if it is the one to tell the game about (see CountContacts)
    private PB2StepInfo _info;                   // the last step's events, in native memory until the next step
    private List<PB2TransformSet> _queued;       // teleports and kinematic moves, flushed in one native call at the start of the step
    private int _queuedCount;
    private uint[] _matrix;                      // 32 rows: bit b of row a set = layers a and b collide
    private bool _hasWorld;
    private float _gravityX, _gravityY;
    private int _workers;
    private bool _inStep;
    private int _stepIndex;

    // ---- the event stream
    public int EventKind;                        // a SimEvent, or SimEvent.None
    public int EventSelf;                        // collider slots; the one that hears about it, and the other
    public int EventOther;
    public int EventJoint;                       // a joint slot
    public float EventX, EventY, EventNX, EventNY, EventImpulse;

    private int _phase;
    private int _cursor;
    private bool _phaseStarted;
    private int _secondKind;                     // the reverse direction waiting to be delivered, or SimEvent.None
    private float _secondNX, _secondNY;
    private List<long> _stay;                    // the pairs to say Stay for, taken when that phase starts
    private int _stayCount;

    // ---- queries
    public float HitX, HitY, HitNX, HitNY, HitFraction, HitDistance;
    private float _rayMax;
    private List<PB2RayHit> _rayHits;
    private int _rayCapacity;
    private int[] _overlap;
    private List<int> _distinct;
    private Dictionary<int, int> _seen;

    public SimCore2D()
    {
        Registry = new Registry2D();
        _active = new TriggerSet();
        _contacts = new ContactSet();
        _due = new List<int>();
        _vanished = new ContactSet();
        _synth = new List<long>();
        _queued = new List<PB2TransformSet>();
        _matrix = new uint[32];
        for (int i = 0; i < 32; i++) _matrix[i] = 0xFFFFFFFFu;
        _gravityX = 0f;
        _gravityY = -9.81f;
        _workers = 1;
        _stay = new List<long>();
        _rayHits = new List<PB2RayHit>();
        _overlap = new int[256];
        _distinct = new List<int>();
        _seen = new Dictionary<int, int>();
        _phase = 6;
    }

    // ---- state ---------------------------------------------------------------------------

    public bool HasWorld => _hasWorld;
    public bool InStep => _inStep;

    /// <summary>Index of the most recently started step. Poses are stamped with it.</summary>
    public int StepIndex => _stepIndex;

    public float GravityX => _gravityX;
    public float GravityY => _gravityY;
    public int WorkerCount => _workers;

    /// <summary>Holders of the native world that are not registered bodies or colliders (terrain layers): the world stays while any holds it.</summary>
    public int Pins;

    /// <summary>No live bodies or colliders, and nobody pinning the world, so the native world could be handed to someone else.</summary>
    public bool IsIdle => !_inStep && Registry.BodyCount == 0 && Registry.ColliderCount == 0 && Pins == 0;

    public void SetGravity(float x, float y)
    {
        _gravityX = x;
        _gravityY = y;
        if (_hasWorld) PB2.WorldSetGravity(x, y);
    }

    /// <summary>Takes effect the next time the native world is created.</summary>
    public void SetWorkerCount(int count)
    {
        _workers = count < 1 ? 1 : count;
    }

    /// <summary>Row <c>a</c>, bit <c>b</c> set means layers a and b collide. Takes effect for pairs created after the call.</summary>
    public void SetLayerMatrix(uint[] rows)
    {
        for (int i = 0; i < 32; i++) _matrix[i] = rows[i];
        if (_hasWorld) PB2.WorldSetLayerMatrix(_matrix);
    }

    // ---- the native world ----------------------------------------------------------------

    /// <summary>Creates the native world. The caller has made sure nobody else holds it.</summary>
    public void Acquire()
    {
        PB2.WorldCreate(_gravityX, _gravityY, _workers);
        PB2.WorldSetLayerMatrix(_matrix);
        _hasWorld = true;
    }

    /// <summary>
    /// Destroys the native world and forgets everything that lived in it. Generations keep counting, so a handle from before can never name
    /// something made after.
    /// </summary>
    public void Release()
    {
        if (!_hasWorld) return;
        Registry.Clear();
        _active.Clear();
        _contacts.Clear();
        _vanished.Clear();
        _synth.Clear();
        _queuedCount = 0;
        _phase = 6;
        _secondKind = SimEvent.None;
        PB2.WorldDestroy();
        _hasWorld = false;
    }

    /// <summary>Queues a teleport (<paramref name="kinematic"/> false) or a kinematic move that reaches the pose over the next step.</summary>
    public void QueueTransform(uint body, float x, float y, float angle, bool kinematic)
    {
        PB2TransformSet t = new PB2TransformSet();
        t.body = body;
        t.x = x;
        t.y = y;
        t.angle = angle;
        t.mode = kinematic ? 1 : 0;
        if (_queuedCount < _queued.Count) _queued[_queuedCount] = t;
        else _queued.Add(t);
        _queuedCount++;
    }

    // ---- stepping ------------------------------------------------------------------------

    /// <summary>
    /// Steps the world and writes the poses of the bodies that moved into their records. Then the events can be read with <see cref="NextEvent"/>;
    /// <see cref="EndStep"/> finishes. Between the two, slots freed are held back (a quarantine), so an index in a pending event cannot have been
    /// handed to a newcomer.
    /// </summary>
    public void Step(float dt, int subSteps)
    {
        if (!_hasWorld) return;
        _stepIndex++;
        _inStep = true;
        Registry.SetQuarantining(true);

        if (_queuedCount > 0)
        {
            PB2.BodiesSetTransforms(_queued, _queuedCount, dt);
            _queuedCount = 0;
        }
        PB2.Step(dt, subSteps < 1 ? 1 : subSteps, ref _info);

        for (int i = 0; i < _info.moveCount; i++)
        {
            PB2BodyMove m = PB2.StepInfoMoves(ref _info, i);
            BodyRecord? record = Registry.Body(m.bodyIndex);
            if (record != null) record.Moved(m.x, m.y, m.c, m.s, m.fellAsleep != 0, _stepIndex);   // straight into the body's record
        }

        _synth.Clear();
        _synthCursor = 0;
        CountContacts();

        _phase = 0;
        _cursor = 0;
        _phaseStarted = false;
        _secondKind = SimEvent.None;
        EventKind = SimEvent.None;
    }

    /// <summary>
    /// The native layer reports a contact for every pair of SHAPES; the game is told about every pair of COLLIDERS, once. A collider can have many
    /// shapes (an edge collider, a chunk of terrain), so this counts how many pairs of shapes touch each pair of colliders, and marks as due the
    /// contact that makes the count 1 (the colliders began touching) and the one that makes it 0 (they stopped). The others are not told.
    /// <para/>
    /// The step's begins are counted BEFORE its ends, whatever order they are delivered in. A body that rolls from one shape of a collider onto the next
    /// has the first shape's end and the second shape's begin in the same step; counting the end first would drop the count to 0 and tell the game it
    /// stopped touching and touched again. The same goes for a collider whose shapes are rebuilt while something rests on it.
    /// </summary>
    private void CountContacts()
    {
        int total = _info.contactCount;
        int begins = _info.contactBeginCount;
        _due.Clear();
        for (int i = 0; i < total; i++) _due.Add(0);
        for (int i = 0; i < begins; i++)
        {
            PB2ContactEvent c = PB2.StepInfoContacts(ref _info, i);
            if (!Registry.ColliderLive(c.colliderA) || !Registry.ColliderLive(c.colliderB)) continue;
            long key = ContactSet.Pair(c.colliderA, c.colliderB);
            bool continued = _vanished.Touching(key) > 0;               // its shapes were rebuilt: this is the same contact, not a new one
            if (_contacts.Increment(key) == 1 && !continued) _due[i] = 1;
        }
        for (int i = begins; i < total; i++)
        {
            PB2ContactEvent c = PB2.StepInfoContacts(ref _info, i);
            if (!Registry.ColliderLive(c.colliderA) || !Registry.ColliderLive(c.colliderB)) continue;
            if (_contacts.Decrement(ContactSet.Pair(c.colliderA, c.colliderB)) == 0) _due[i] = 1;
        }
        // the pairs whose shapes were rebuilt: the new shapes' begins are in; a pair that touches none of them has ended, and no native event says so
        for (int i = 0; i < _vanished.Count; i++)
        {
            long key = _vanished.KeyAt(i);
            if (_contacts.Touching(key) == 0) _synth.Add(key);
        }
        _vanished.Clear();
    }

    /// <summary>
    /// Call BEFORE destroying all the native shapes of a collider that stays registered and making them again (a terrain chunk after a dig). The
    /// native layer reports no end for a destroyed shape, so the pairs this collider is touching would otherwise stay counted for ever and never end.
    /// Here they are remembered instead: the new shapes' begins are taken for the same contact (no event), and after the step a pair that touches none
    /// of them gets its End. Not for a collider that is going away (<see cref="ForgetCollider"/>), and not for rebuilding only some of its shapes.
    /// </summary>
    public void ShapesVanishing(int collider)
    {
        for (int i = _contacts.Count - 1; i >= 0; i--)
        {
            long key = _contacts.KeyAt(i);
            if (ContactSet.Low(key) != collider && ContactSet.High(key) != collider) continue;
            _contacts.Remove(key);
            _vanished.Increment(key);
        }
    }

    // the Ends of ShapesVanishing: one per pair, delivered after the native ones, in both directions
    private bool NextSynthEnd()
    {
        while (_synthCursor < _synth.Count)
        {
            long key = _synth[_synthCursor];
            _synthCursor++;
            int a = ContactSet.Low(key);
            int b = ContactSet.High(key);
            if (!Registry.ColliderLive(a) || !Registry.ColliderLive(b)) continue;
            Emit(SimEvent.CollisionEnd, a, b, 0f, 0f, 0f, 0f, 0f);
            _secondKind = SimEvent.CollisionEnd;
            _secondNX = 0f;
            _secondNY = 0f;
            return true;
        }
        return false;
    }

    /// <summary>Ends the step: slots freed during it become reusable. Safe to call whether or not the stream was read to its end.</summary>
    public void EndStep()
    {
        _inStep = false;
        _phase = 6;
        _secondKind = SimEvent.None;
        EventKind = SimEvent.None;
        Registry.SetQuarantining(false);
        Registry.Flush();
    }

    // ---- the event stream ----------------------------------------------------------------

    private void Emit(int kind, int self, int other, float x, float y, float nx, float ny, float impulse)
    {
        EventKind = kind;
        EventSelf = self;
        EventOther = other;
        EventX = x;
        EventY = y;
        EventNX = nx;
        EventNY = ny;
        EventImpulse = impulse;
    }

    /// <summary>
    /// Advances to the next event and returns true, with <see cref="EventKind"/> and the fields beside it describing it, or returns false when
    /// the step has no more. Everything is decided from the world as it is when this is called, which is what lets a handler destroy things.
    /// </summary>
    public bool NextEvent()
    {
        EventKind = SimEvent.None;

        // the reverse direction of the last pair, if both colliders survived the first handler
        if (_secondKind != SimEvent.None)
        {
            int kind = _secondKind;
            _secondKind = SimEvent.None;
            if (Registry.ColliderLive(EventSelf) && Registry.ColliderLive(EventOther))
            {
                int self = EventOther;
                int other = EventSelf;
                Emit(kind, self, other, EventX, EventY, _secondNX, _secondNY, EventImpulse);
                return true;
            }
        }

        while (_phase < 6)
        {
            bool got = false;
            if (_phase == 0) got = NextContact(false);
            else if (_phase == 1) got = NextContact(true);
            else if (_phase == 2) got = NextTriggerExit();
            else if (_phase == 3) got = NextTriggerStay();
            else if (_phase == 4) got = NextTriggerEnter();
            else got = NextJointBreak();
            if (got) return true;
            _phase++;
            _cursor = 0;
            _phaseStarted = false;
        }
        return false;
    }

    // Contacts: the native array holds the begins first (contactBeginCount of them), then the ends.
    private bool NextContact(bool begins)
    {
        int first = begins ? 0 : _info.contactBeginCount;
        int end = begins ? _info.contactBeginCount : _info.contactCount;
        while (first + _cursor < end)
        {
            int at = first + _cursor;
            PB2ContactEvent c = PB2.StepInfoContacts(ref _info, at);
            _cursor++;
            if (_due[at] == 0) continue;                       // another pair of shapes of the same two colliders is (still) touching
            if (!Registry.ColliderLive(c.colliderA) || !Registry.ColliderLive(c.colliderB)) continue;
            if (begins)
            {
                Emit(SimEvent.CollisionBegin, c.colliderA, c.colliderB, c.px, c.py, c.nx, c.ny, c.impulse);
                _secondKind = SimEvent.CollisionBegin;
                _secondNX = -c.nx;
                _secondNY = -c.ny;
            }
            else
            {
                Emit(SimEvent.CollisionEnd, c.colliderA, c.colliderB, 0f, 0f, 0f, 0f, 0f);
                _secondKind = SimEvent.CollisionEnd;
                _secondNX = 0f;
                _secondNY = 0f;
            }
            return true;
        }
        return begins ? false : NextSynthEnd();
    }

    // A pair leaves: forgotten at once, and reported only if both colliders are still there to be told.
    private bool NextTriggerExit()
    {
        while (_cursor < _info.sensorCount)
        {
            PB2SensorEvent s = PB2.StepInfoSensors(ref _info, _cursor);
            _cursor++;
            if (s.flags != PB2.EventEnd) continue;
            if (!_active.Remove(TriggerSet.Pair(s.sensorCollider, s.visitorCollider))) continue;
            if (!Registry.ColliderLive(s.sensorCollider) || !Registry.ColliderLive(s.visitorCollider)) continue;
            Emit(SimEvent.TriggerExit, s.sensorCollider, s.visitorCollider, 0f, 0f, 0f, 0f, 0f);
            _secondKind = SimEvent.TriggerExit;
            return true;
        }
        return false;
    }

    // The pairs that were overlapping before this step and still are: taken when the phase starts, so the exits (and whatever their handlers
    // did) are already in it, and the pairs entering this step are not. Each is checked again as its turn comes.
    private bool NextTriggerStay()
    {
        if (!_phaseStarted)
        {
            _phaseStarted = true;
            _stayCount = 0;
            for (int i = 0; i < _active.Count; i++)
            {
                long key = _active.KeyAt(i);
                if (_stayCount < _stay.Count) _stay[_stayCount] = key;
                else _stay.Add(key);
                _stayCount++;
            }
        }
        while (_cursor < _stayCount)
        {
            long key = _stay[_cursor];
            _cursor++;
            int sensor = TriggerSet.Sensor(key);
            int visitor = TriggerSet.Visitor(key);
            if (!Registry.ColliderLive(sensor) || !Registry.ColliderLive(visitor))
            {
                _active.Remove(key);              // one of them is gone: the pair is over
                continue;
            }
            if (!_active.Contains(key)) continue; // removed by an earlier handler this step
            Emit(SimEvent.TriggerStay, sensor, visitor, 0f, 0f, 0f, 0f, 0f);
            _secondKind = SimEvent.TriggerStay;
            return true;
        }
        return false;
    }

    private bool NextTriggerEnter()
    {
        while (_cursor < _info.sensorCount)
        {
            PB2SensorEvent s = PB2.StepInfoSensors(ref _info, _cursor);
            _cursor++;
            if (s.flags != PB2.EventBegin) continue;
            long key = TriggerSet.Pair(s.sensorCollider, s.visitorCollider);
            if (!_active.Add(key)) continue;
            if (!Registry.ColliderLive(s.sensorCollider) || !Registry.ColliderLive(s.visitorCollider))
            {
                _active.Remove(key);
                continue;
            }
            Emit(SimEvent.TriggerEnter, s.sensorCollider, s.visitorCollider, 0f, 0f, 0f, 0f, 0f);
            _secondKind = SimEvent.TriggerEnter;
            return true;
        }
        return false;
    }

    // A joint past its threshold. Its slot is held by the quarantine, so a handler that creates another joint cannot be given the index of one
    // still waiting in this list.
    private bool NextJointBreak()
    {
        while (_cursor < _info.jointEventCount)
        {
            int joint = PB2.StepInfoJoints(ref _info, _cursor);
            _cursor++;
            if (!Registry.JointLive(joint)) continue;
            Emit(SimEvent.JointBreak, -1, -1, 0f, 0f, 0f, 0f, 0f);
            EventJoint = joint;
            return true;
        }
        return false;
    }

    /// <summary>A collider's pair is no longer tracked: call when a collider is removed, so no pair names a slot that may be reused.</summary>
    public void ForgetCollider(int collider)
    {
        if (_active.Count > 0) _active.RemoveInvolving(collider);
        if (_contacts.Count > 0) _contacts.RemoveInvolving(collider);
        if (_vanished.Count > 0) _vanished.RemoveInvolving(collider);
    }

    // ---- queries -------------------------------------------------------------------------

    /// <summary>The nearest hit: its collider's slot (-1 for none), with the point, normal and distance in the Hit fields.</summary>
    public int Raycast(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, bool hitSensors)
    {
        if (!_hasWorld) return -1;
        _rayMax = maxDistance > MaxQueryDistance ? MaxQueryDistance : maxDistance;
        PB2RayHit hit = new PB2RayHit();
        int n = PB2.Raycast(ox, oy, dx, dy, _rayMax, layerMask, hitSensors ? 1 : 0, ref hit);
        if (n == 0) return -1;
        HitX = hit.px;
        HitY = hit.py;
        HitNX = hit.nx;
        HitNY = hit.ny;
        HitFraction = hit.fraction;
        HitDistance = hit.fraction * _rayMax;
        return hit.collider;
    }

    /// <summary>Every hit along the ray, nearest first. Read them with the RayHit methods.</summary>
    public int RaycastAll(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, bool hitSensors)
    {
        if (!_hasWorld) return 0;
        _rayMax = maxDistance > MaxQueryDistance ? MaxQueryDistance : maxDistance;
        if (_rayCapacity == 0) _rayCapacity = 64;
        int n;
        while (true)
        {
            while (_rayHits.Count < _rayCapacity)
            {
                PB2RayHit blank = new PB2RayHit();
                _rayHits.Add(blank);
            }
            n = PB2.RaycastAll(ox, oy, dx, dy, _rayMax, layerMask, hitSensors ? 1 : 0, _rayHits, _rayCapacity);
            // a full buffer may have lost hits: grow it and ask again
            if (n < _rayCapacity || _rayCapacity >= 65536) break;
            _rayCapacity = _rayCapacity * 2;
        }
        return n;
    }

    public int RayHitCollider(int i) { PB2RayHit h = _rayHits[i]; return h.collider; }
    public float RayHitX(int i) { PB2RayHit h = _rayHits[i]; return h.px; }
    public float RayHitY(int i) { PB2RayHit h = _rayHits[i]; return h.py; }
    public float RayHitNX(int i) { PB2RayHit h = _rayHits[i]; return h.nx; }
    public float RayHitNY(int i) { PB2RayHit h = _rayHits[i]; return h.ny; }
    public float RayHitFraction(int i) { PB2RayHit h = _rayHits[i]; return h.fraction; }
    public float RayHitDistance(int i) { PB2RayHit h = _rayHits[i]; return h.fraction * _rayMax; }

    public int OverlapPoint(float x, float y, uint layerMask, bool hitSensors)
    {
        if (!_hasWorld) return 0;
        int n;
        while (true)
        {
            n = PB2.OverlapPoint(x, y, layerMask, hitSensors ? 1 : 0, _overlap, _overlap.Length);
            if (n < _overlap.Length || _overlap.Length >= 1048576) break;
            _overlap = new int[_overlap.Length * 2];
        }
        return Distinct(n);
    }

    public int OverlapCircle(float cx, float cy, float radius, uint layerMask, bool hitSensors)
    {
        if (!_hasWorld) return 0;
        int n;
        while (true)
        {
            n = PB2.OverlapCircle(cx, cy, radius, layerMask, hitSensors ? 1 : 0, _overlap, _overlap.Length);
            if (n < _overlap.Length || _overlap.Length >= 1048576) break;
            _overlap = new int[_overlap.Length * 2];
        }
        return Distinct(n);
    }

    public int OverlapBox(float cx, float cy, float halfW, float halfH, float angle, uint layerMask, bool hitSensors)
    {
        if (!_hasWorld) return 0;
        int n;
        while (true)
        {
            n = PB2.OverlapBox(cx, cy, halfW, halfH, angle, layerMask, hitSensors ? 1 : 0, _overlap, _overlap.Length);
            if (n < _overlap.Length || _overlap.Length >= 1048576) break;
            _overlap = new int[_overlap.Length * 2];
        }
        return Distinct(n);
    }

    /// <summary>The collider at position i of the last overlap query: each live collider once, in the order first found.</summary>
    public int OverlapResult(int i) { return _distinct[i]; }

    // A collider made of several shapes is reported once per shape; list each only once, and not one that is gone. A linear scan is fastest for
    // the usual handful of results; a big sweep switches to a map so it stays linear.
    private int Distinct(int n)
    {
        _distinct.Clear();
        bool big = n > 64;
        if (big) _seen.Clear();
        for (int i = 0; i < n; i++)
        {
            int collider = _overlap[i];
            if (!Registry.ColliderLive(collider)) continue;
            bool seen = false;
            if (big) seen = _seen.ContainsKey(collider);
            else
                for (int j = 0; j < _distinct.Count; j++)
                    if (_distinct[j] == collider) { seen = true; break; }
            if (seen) continue;
            if (big) _seen[collider] = 1;
            _distinct.Add(collider);
        }
        return _distinct.Count;
    }
}
