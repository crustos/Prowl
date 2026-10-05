// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using Prowl.Runtime.Physics2D;

namespace Prowl.Core2D;

/// <summary>
/// The scene of the 2D runtime: the nodes and components, which of them are active, and the order in which the game's callbacks run. This is the
/// engine's SceneDispatcher and GameObject lifecycle with every object an arena object and nothing virtual, so it translates to C.
/// <para/>
/// <b>Who calls the game.</b> The scene cannot call a script (the subset has no virtual dispatch, interface or delegate), so it produces a
/// <b>stream of calls</b>, as the simulation core produces a stream of events:
/// <code>
///   scene.BeginFixedStep(dt);              // or BeginFrame(dt, alpha)
///   while (scene.NextCall())
///       Scripts.Invoke(scene);             // generated: a switch on scene.CallComponent.Kind and scene.CallKind
/// </code>
/// Each <see cref="NextCall"/> decides what runs next from the scene as it is now, so a callback may enable, disable, destroy and create things and
/// the next call already reflects it. The rules are the engine's: <b>Start</b> runs once, before a component's first per-frame callback; the
/// per-frame callbacks go through channels sorted by (execution order, registration sequence) that are rebuilt only at the start of a phase, so a
/// change in the middle of one cannot disturb the walk; a component that is disabled or destroyed is skipped; <b>destroyed things are freed at the
/// end of the frame</b>, not before.
/// <para/>
/// <b>One deliberate difference from the .NET engine.</b> OnEnable and OnDisable are <i>queued</i> and delivered as the very next calls, after the
/// callback that caused them returns, where the .NET engine runs them inside the <c>SetActive</c> call. The scene cannot re-enter the game in the
/// middle of the game's own callback. Code after <c>SetActive(true)</c> in the same callback runs before the OnEnable; nothing else does.
/// <para/>
/// The frame is the engine's: a fixed step is Start, FixedUpdate, the physics step and its messages, then the end of the frame; a frame is Start,
/// Update, the write-back of physics poses into nodes, LateUpdate, then the end of the frame.
/// <para/>
/// An arena class of capacity 1.
/// </summary>
[MaxInstances(1)]
internal sealed class Scene2D
{
    public static Scene2D Current;

    public World2D Physics;
    public Renderer2D Render;                // the sprites, and the batch a renderer draws
    public Collision2D Collision;            // the payload of the collision message being delivered: valid for that callback only

    public float DeltaTime;
    public float FixedDeltaTime;
    public float FixedAlpha;                 // how far into the next fixed step this frame is, 0..1
    public int FrameIndex;
    public int FixedIndex;
    public bool IsActive;                    // OnEnable / OnDisable are only delivered while the scene is active
    public int NodeCount;
    public int ComponentCount;

    // ---- the call being made
    public int CallKind;                     // a Callbacks bit, or 0
    public Component CallComponent;

    private Node[] _nodes;
    private List<int> _freeNodes;
    private int _nodeHigh;
    private Component[] _firstComp;          // by node slot: the node's components, first and last
    private Component[] _lastComp;

    private Component[] _comps;
    private List<int> _freeComps;
    private int _compHigh;

    private Component[] _registered;         // the components with a per-frame callback that are enabled: dense, removal swaps the tail in
    private int _regCount;
    private int _sequence;
    private List<Component> _chStart;
    private List<Component> _chFixed;
    private List<Component> _chUpdate;
    private List<Component> _chLate;
    private bool _dStart, _dFixed, _dUpdate, _dLate;

    private List<int> _pendKind;             // lifecycle messages waiting to be delivered, in order
    private List<Component> _pendComp;
    private int _pendHead;

    private List<Node> _doomedNodes;
    private List<Component> _doomedComps;

    private int _mode;                       // 0 idle, 1 fixed step, 2 frame
    private int _phase;
    private int _cursor;
    private bool _phaseStarted;
    private int _eventBit;                   // physics: the message being walked over the node's components
    private Component _eventComp;

    public Scene2D()
    {
        Current = this;
        IsActive = true;
        Physics = new World2D();
        Render = new Renderer2D();
        Collision = new Collision2D();
        _nodes = new Node[CoreLimits.Nodes];
        _freeNodes = new List<int>();
        _firstComp = new Component[CoreLimits.Nodes];
        _lastComp = new Component[CoreLimits.Nodes];
        _comps = new Component[CoreLimits.Components];
        _freeComps = new List<int>();
        _registered = new Component[CoreLimits.Components];
        _chStart = new List<Component>();
        _chFixed = new List<Component>();
        _chUpdate = new List<Component>();
        _chLate = new List<Component>();
        _pendKind = new List<int>();
        _pendComp = new List<Component>();
        _doomedNodes = new List<Node>();
        _doomedComps = new List<Component>();
        FixedDeltaTime = 1f / 60f;
    }

    // ---- nodes ---------------------------------------------------------------------------

    /// <summary>A new, active node, as the last child of <paramref name="parent"/> (null for a root). Null if all the nodes are in use.</summary>
    public Node NewNode(Node parent)
    {
        int index;
        if (_freeNodes.Count > 0)
        {
            index = _freeNodes[_freeNodes.Count - 1];
            _freeNodes.RemoveAt(_freeNodes.Count - 1);
        }
        else
        {
            if (_nodeHigh >= CoreLimits.Nodes) return null;
            index = _nodeHigh;
            _nodeHigh++;
        }
        Node n = _nodes[index];
        if (n == null)
        {
            n = new Node();
            _nodes[index] = n;
        }
        n.Reset(index);
        _firstComp[index] = null;
        _lastComp[index] = null;
        NodeCount++;
        if (parent != null)
        {
            n.SetParent(parent, false);
            n.ActiveInHierarchy = parent.ActiveInHierarchy;
        }
        return n;
    }

    /// <summary>The node in a slot, or null if there is none alive there.</summary>
    public Node NodeAt(int index)
    {
        if (index < 0 || index >= _nodeHigh) return null;
        Node n = _nodes[index];
        return n != null && n.Alive ? n : null;
    }

    public int NodeHighWater => _nodeHigh;
    public int ComponentHighWater => _compHigh;

    /// <summary>The live component in a slot, or null.</summary>
    public Component ComponentAt(int index)
    {
        if (index < 0 || index >= _compHigh) return null;
        Component c = _comps[index];
        return c != null && c.Node != null ? c : null;
    }

    /// <summary>Alive and not marked for destruction.</summary>
    public bool IsLive(Node n) { return n != null && n.Alive && !n.Destroyed; }

    public Component FirstComponent(Node n) { return _firstComp[n.Index]; }

    /// <summary>The first component of a kind on the node that is not destroyed, or null.</summary>
    public Component Find(Node n, int kind)
    {
        for (Component c = _firstComp[n.Index]; c != null; c = c.NextOnNode)
            if (c.Kind == kind && !c.Destroyed) return c;
        return null;
    }

    /// <summary>Moves a node under a new parent (see <see cref="Node.SetParent"/>), and settles whether it is now active.</summary>
    public bool SetParent(Node n, Node parent, bool keepWorld)
    {
        if (!IsLive(n) || (parent != null && !IsLive(parent))) return false;
        if (!n.SetParent(parent, keepWorld)) return false;
        RefreshActive(n);
        return true;
    }

    // ---- activity ------------------------------------------------------------------------

    public void SetActive(Node n, bool active)
    {
        if (!IsLive(n) || n.ActiveSelf == active) return;
        n.ActiveSelf = active;
        RefreshActive(n);
    }

    // The node and then its children, in order: whichever of them changed state, their components follow.
    private void RefreshActive(Node n)
    {
        bool parentActive = n.Parent == null || n.Parent.ActiveInHierarchy;
        bool now = n.ActiveSelf && parentActive && !n.Destroyed;
        if (now == n.ActiveInHierarchy) return;
        n.ActiveInHierarchy = now;
        for (Component c = _firstComp[n.Index]; c != null; c = c.NextOnNode) Reevaluate(c);
        for (Node child = n.FirstChild; child != null; child = child.NextSibling) RefreshActive(child);
    }

    /// <summary>Whether OnEnable / OnDisable are delivered. Going active enables everything that is enabled in hierarchy; going inactive disables it.</summary>
    public void SetSceneActive(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;
        for (int i = 0; i < _compHigh; i++)
        {
            Component c = _comps[i];
            if (c == null || c.Node == null || c.Destroyed || !c.EnabledInHierarchy) continue;
            if (active) EnableInternal(c);
            else DisableInternal(c);
        }
    }

    // ---- components ----------------------------------------------------------------------

    /// <summary>
    /// Makes a component record on a node, not yet enabled: the caller sets up its data first (see <see cref="Finish"/>). Null if all are in use.
    /// </summary>
    public Component Create(Node node, int kind, int slot, int callbacks, int order)
    {
        if (!IsLive(node)) return null;
        int index;
        if (_freeComps.Count > 0)
        {
            index = _freeComps[_freeComps.Count - 1];
            _freeComps.RemoveAt(_freeComps.Count - 1);
        }
        else
        {
            if (_compHigh >= CoreLimits.Components) return null;
            index = _compHigh;
            _compHigh++;
        }
        Component c = _comps[index];
        if (c == null)
        {
            c = new Component();
            _comps[index] = c;
        }
        c.Reset(index);
        c.Kind = kind;
        c.Slot = slot;
        c.Node = node;
        c.Callbacks = callbacks;
        c.Order = order;
        if (_lastComp[node.Index] == null) _firstComp[node.Index] = c;
        else _lastComp[node.Index].NextOnNode = c;
        _lastComp[node.Index] = c;
        ComponentCount++;
        return c;
    }

    /// <summary>Enables a component made with <see cref="Create"/>, if its node is active.</summary>
    public void Finish(Component c) { Reevaluate(c); }

    /// <summary>Makes a component and enables it. This is what the generated script factories call.</summary>
    public Component AddComponent(Node node, int kind, int slot, int callbacks, int order)
    {
        Component c = Create(node, kind, slot, callbacks, order);
        if (c != null) Reevaluate(c);
        return c;
    }

    public void SetEnabled(Component c, bool enabled)
    {
        if (c == null || c.Destroyed || c.Enabled == enabled) return;
        c.Enabled = enabled;
        Reevaluate(c);
    }

    private void Reevaluate(Component c)
    {
        bool now = c.Enabled && !c.Destroyed && c.Node.ActiveInHierarchy;
        if (now == c.EnabledInHierarchy) return;
        c.EnabledInHierarchy = now;
        if (!IsActive) return;
        if (now) EnableInternal(c);
        else DisableInternal(c);
    }

    private void EnableInternal(Component c)
    {
        Physics.EnableComponent(this, c);                  // the built-in kinds do their own work, at once
        Render.EnableComponent(c);
        Register(c);
        if ((c.Callbacks & Callbacks.OnEnable) != 0) Queue(Callbacks.OnEnable, c);
    }

    private void DisableInternal(Component c)
    {
        if ((c.Callbacks & Callbacks.OnDisable) != 0) Queue(Callbacks.OnDisable, c);
        Unregister(c);
        Physics.DisableComponent(this, c);
        Render.DisableComponent(c);
    }

    private void Queue(int kind, Component c)
    {
        _pendKind.Add(kind);
        _pendComp.Add(c);
    }

    // ---- registration: the components with a per-frame callback --------------------------

    private void Register(Component c)
    {
        if (c.DispatchSlot != 0) return;
        if ((c.Callbacks & Callbacks.AnyFrame) == 0) return;       // nothing per frame: never in the arrays, so it costs nothing to have
        _sequence++;
        c.Sequence = _sequence;
        _registered[_regCount] = c;
        _regCount++;
        c.DispatchSlot = _regCount;
        MarkDirty(c.Callbacks);
    }

    private void Unregister(Component c)
    {
        int slot = c.DispatchSlot;
        if (slot == 0) return;
        int index = slot - 1;
        _regCount--;
        Component moved = _registered[_regCount];
        _registered[index] = moved;                                // the tail fills the hole: order is carried by Sequence, not by position
        moved.DispatchSlot = index + 1;
        _registered[_regCount] = null;
        c.DispatchSlot = 0;
        MarkDirty(c.Callbacks);
    }

    private void MarkDirty(int callbacks)
    {
        if ((callbacks & Callbacks.Start) != 0) _dStart = true;
        if ((callbacks & Callbacks.FixedUpdate) != 0) _dFixed = true;
        if ((callbacks & Callbacks.Update) != 0) _dUpdate = true;
        if ((callbacks & Callbacks.LateUpdate) != 0) _dLate = true;
    }

    private static bool Before(Component a, Component b)
    {
        return a.Order < b.Order || (a.Order == b.Order && a.Sequence < b.Sequence);
    }

    // A channel is the registered components that have its callback, in execution order. Insertion sort: the registration array is nearly in
    // order already, and this runs once per phase and only if something joined or left.
    private void Rebuild(List<Component> channel, int bit)
    {
        channel.Clear();
        for (int i = 0; i < _regCount; i++)
        {
            Component c = _registered[i];
            if ((c.Callbacks & bit) == 0) continue;
            if (bit == Callbacks.Start && c.HasStarted) continue;      // a component only starts once, so it leaves this channel for good
            channel.Add(c);
            int k = channel.Count - 1;
            while (k > 0)
            {
                Component prev = channel[k - 1];
                if (!Before(c, prev)) break;
                channel[k] = prev;
                k--;
            }
            channel[k] = c;
        }
    }

    // ---- destruction ---------------------------------------------------------------------

    /// <summary>
    /// Destroys a node and everything under it. Their callbacks stop at once and each enabled component gets its OnDisable; the slots are freed at
    /// the end of the frame, so a callback still walking a list never meets a recycled one.
    /// </summary>
    public void Destroy(Node n)
    {
        if (!IsLive(n)) return;
        MarkDestroyed(n);
    }

    private void MarkDestroyed(Node n)
    {
        n.Destroyed = true;
        n.ActiveInHierarchy = false;
        _doomedNodes.Add(n);
        for (Component c = _firstComp[n.Index]; c != null; c = c.NextOnNode)
        {
            if (c.Destroyed) continue;
            c.Destroyed = true;
            Reevaluate(c);
            _doomedComps.Add(c);
        }
        for (Node child = n.FirstChild; child != null; child = child.NextSibling)
            if (!child.Destroyed) MarkDestroyed(child);
    }

    public void DestroyComponent(Component c)
    {
        if (c == null || c.Destroyed) return;
        c.Destroyed = true;
        Reevaluate(c);
        _doomedComps.Add(c);
    }

    // The end of the frame: scripts are told once that their component is going (so their pool can recycle the slot), then everything is freed.
    private void FreeDoomed()
    {
        for (int i = 0; i < _doomedComps.Count; i++)
        {
            Component c = _doomedComps[i];
            Physics.FreeComponent(c);
            Render.FreeComponent(c);
            Node n = c.Node;
            if (n != null && !n.Destroyed)                         // a component removed from a node that lives on: unlink it
            {
                Component prev = null;
                Component k = _firstComp[n.Index];
                while (k != null && k != c)
                {
                    prev = k;
                    k = k.NextOnNode;
                }
                if (k == c)
                {
                    if (prev == null) _firstComp[n.Index] = c.NextOnNode;
                    else prev.NextOnNode = c.NextOnNode;
                    if (_lastComp[n.Index] == c) _lastComp[n.Index] = prev;
                }
            }
            c.Node = null;
            c.Kind = ComponentKind.None;
            _freeComps.Add(c.Index);
            ComponentCount--;
        }
        _doomedComps.Clear();
        for (int i = 0; i < _doomedNodes.Count; i++)
        {
            Node n = _doomedNodes[i];
            if (n.Parent != null && !n.Parent.Destroyed) n.Detach();
            n.Alive = false;
            _firstComp[n.Index] = null;
            _lastComp[n.Index] = null;
            _freeNodes.Add(n.Index);
            NodeCount--;
        }
        _doomedNodes.Clear();
    }

    // ---- physics components --------------------------------------------------------------

    /// <summary>A Rigidbody2D on a node, not yet enabled: set its fields, then <see cref="Finish"/> its <c>Self</c>. Null if all are in use.</summary>
    public Rigidbody2D NewRigidbody(Node n, int bodyType) { return IsLive(n) ? Physics.NewRigidbody(this, n, bodyType) : null; }

    /// <summary>A box collider of this full width and height (scaled by the node), not yet enabled.</summary>
    public Collider2D NewBoxCollider(Node n, float width, float height)
    {
        if (!IsLive(n)) return null;
        Collider2D col = Physics.NewCollider(this, n, Collider2D.Box);
        if (col != null)
        {
            col.SizeX = width;
            col.SizeY = height;
        }
        return col;
    }

    /// <summary>A circle collider of this radius (scaled by the node), not yet enabled.</summary>
    public Collider2D NewCircleCollider(Node n, float radius)
    {
        if (!IsLive(n)) return null;
        Collider2D col = Physics.NewCollider(this, n, Collider2D.Circle);
        if (col != null) col.Radius = radius;
        return col;
    }

    /// <summary>
    /// A convex polygon collider on a node, not yet enabled: <paramref name="count"/> points of <paramref name="xy"/> (x, y pairs, in the node's space,
    /// before its scale), at most <see cref="CoreLimits.MeshVertices"/>. Null if there are too many or too few, or all colliders are in use.
    /// </summary>
    public Collider2D NewPolygonCollider(Node n, float[] xy, int count)
    {
        if (!IsLive(n) || count < 3 || count > CoreLimits.MeshVertices) return null;
        Collider2D col = Physics.NewCollider(this, n, Collider2D.Polygon);
        if (col != null)
        {
            col.PolyCount = count;
            for (int i = 0; i < count * 2; i++) col.Poly[i] = xy[i];
        }
        return col;
    }

    /// <summary>A Rigidbody2D with the default settings, enabled at once.</summary>
    public Rigidbody2D AddRigidbody(Node n, int bodyType)
    {
        Rigidbody2D rb = NewRigidbody(n, bodyType);
        if (rb != null) Finish(rb.Self);
        return rb;
    }

    public Collider2D AddBoxCollider(Node n, float width, float height)
    {
        Collider2D col = NewBoxCollider(n, width, height);
        if (col != null) Finish(col.Self);
        return col;
    }

    public Collider2D AddPolygonCollider(Node n, float[] xy, int count)
    {
        Collider2D col = NewPolygonCollider(n, xy, count);
        if (col != null) Finish(col.Self);
        return col;
    }

    /// <summary>
    /// A terrain collider on a node, enabled at once: no shape of its own, a collider index and a static body at the node, for the terrain that owns it to
    /// build its shapes on. Null if the node is not live or all colliders are in use.
    /// </summary>
    public Collider2D AddTerrainCollider(Node n)
    {
        if (!IsLive(n)) return null;
        Collider2D col = Physics.NewCollider(this, n, Collider2D.Terrain);
        if (col != null) Finish(col.Self);
        return col;
    }

    public Collider2D AddCircleCollider(Node n, float radius)
    {
        Collider2D col = NewCircleCollider(n, radius);
        if (col != null) Finish(col.Self);
        return col;
    }

    public void SetGravity(float x, float y) { Physics.Sim.SetGravity(x, y); }

    // ---- sprites ---------------------------------------------------------------------------

    /// <summary>A box (<see cref="SpriteRenderer2D.Box"/>) or disc (<see cref="SpriteRenderer2D.Disc"/>) of this full size on a node, not yet enabled.</summary>
    public SpriteRenderer2D NewSprite(Node n, int shape, float width, float height)
    {
        return IsLive(n) ? Render.NewSprite(this, n, shape, width, height) : null;
    }

    /// <summary>A sprite with this tint, enabled at once.</summary>
    public SpriteRenderer2D AddSprite(Node n, int shape, float width, float height, float r, float g, float b)
    {
        SpriteRenderer2D sprite = NewSprite(n, shape, width, height);
        if (sprite == null) return null;
        sprite.SetColor(r, g, b, 1f);
        Finish(sprite.Self);
        return sprite;
    }

    /// <summary>
    /// A convex polygon mesh on a node, not yet enabled: <paramref name="count"/> points of <paramref name="xy"/> in the node's space with their
    /// texture coordinates <paramref name="uv"/>, drawn as a fan. Set its tint and layer, then <see cref="Finish"/> its <c>Self</c>. Null if there are too
    /// many or too few points or all meshes are in use.
    /// </summary>
    public MeshRenderer2D NewMesh(Node n, float[] xy, float[] uv, int count)
    {
        return IsLive(n) ? Render.NewMesh(this, n, xy, uv, count) : null;
    }

    /// <summary>Fills <c>Render.MeshData</c> with the frame's meshes as a triangle list and returns how many vertices: hand both to the renderer.</summary>
    public int CollectMeshes() { return Render.CollectMeshes(); }

    /// <summary>Fills <c>Render.DrawData</c> with the frame's sprites and returns how many: hand both to the renderer.</summary>
    public int CollectSprites() { return Render.Collect(); }

    // ---- self-check ----------------------------------------------------------------------

    /// <summary>
    /// Checks the scene's own bookkeeping and returns how many things are wrong (0 when it is sound): every node's and component's active state
    /// against what it must be from its parents, the registration array against which components should be in it, the free lists against the
    /// live counts, and each node's component list. For tests and for debug builds; it changes nothing.
    /// </summary>
    public int Validate()
    {
        int bad = 0;
        int liveNodes = 0, liveComps = 0, shouldRegister = 0;
        for (int i = 0; i < _nodeHigh; i++)
        {
            Node n = _nodes[i];
            if (n == null || !n.Alive) continue;
            liveNodes++;
            bool expected = n.ActiveSelf && (n.Parent == null || n.Parent.ActiveInHierarchy) && !n.Destroyed;
            if (n.ActiveInHierarchy != expected) bad++;
            if (n.Parent != null && !n.Parent.Alive) bad++;
            int count = 0;
            for (Component c = _firstComp[i]; c != null; c = c.NextOnNode)
            {
                count++;
                if (c.Node != n) bad++;
                if (count > CoreLimits.Components) { bad++; break; }
            }
            if (count > 0 && _lastComp[i] == null) bad++;
        }
        for (int i = 0; i < _compHigh; i++)
        {
            Component c = _comps[i];
            if (c == null || c.Node == null) continue;
            liveComps++;
            bool expected = c.Enabled && !c.Destroyed && c.Node.ActiveInHierarchy;
            if (c.EnabledInHierarchy != expected) bad++;
            bool shouldBeIn = c.EnabledInHierarchy && !c.Destroyed && IsActive && (c.Callbacks & Callbacks.AnyFrame) != 0;
            if (shouldBeIn) shouldRegister++;
            if ((c.DispatchSlot != 0) != shouldBeIn) bad++;
        }
        if (liveNodes != NodeCount) bad++;
        if (liveComps != ComponentCount) bad++;
        if (_freeNodes.Count + liveNodes != _nodeHigh) bad++;
        if (_freeComps.Count + liveComps != _compHigh) bad++;
        if (shouldRegister != _regCount) bad++;
        for (int i = 0; i < _regCount; i++)
        {
            Component c = _registered[i];
            if (c == null || c.DispatchSlot != i + 1) bad++;
        }
        return bad;
    }

    // ---- the stream of calls -------------------------------------------------------------

    /// <summary>Begins a fixed step: Start, FixedUpdate, the physics step and its messages, the end of the frame.</summary>
    public void BeginFixedStep(float dt)
    {
        FixedDeltaTime = dt;
        FixedIndex++;
        _mode = 1;
        _phase = 0;
        _cursor = 0;
        _phaseStarted = false;
    }

    /// <summary>Begins a frame: Start, Update, the poses of physics bodies written into their nodes, LateUpdate, the end of the frame.</summary>
    public void BeginFrame(float dt, float alpha)
    {
        DeltaTime = dt;
        FixedAlpha = alpha;
        FrameIndex++;
        _mode = 2;
        _phase = 0;
        _cursor = 0;
        _phaseStarted = false;
    }

    /// <summary>
    /// The next call, in <see cref="CallKind"/> and <see cref="CallComponent"/>; false when the step or frame is over. Lifecycle messages that the
    /// last callback caused come first, so a component that was enabled has had its OnEnable before anything else runs.
    /// </summary>
    public bool NextCall()
    {
        CallKind = 0;
        CallComponent = null;

        if (_pendHead < _pendKind.Count)
        {
            CallKind = _pendKind[_pendHead];
            CallComponent = _pendComp[_pendHead];
            _pendHead++;
            if (_pendHead == _pendKind.Count)
            {
                _pendKind.Clear();
                _pendComp.Clear();
                _pendHead = 0;
            }
            return true;
        }

        while (_mode != 0)
        {
            bool got = _mode == 1 ? NextFixed() : NextFrame();
            if (got) return true;
            _phase++;
            _cursor = 0;
            _phaseStarted = false;
        }
        return false;
    }

    private bool NextFixed()
    {
        if (_phase == 0) return NextChannel(_chStart, Callbacks.Start);
        if (_phase == 1) return NextChannel(_chFixed, Callbacks.FixedUpdate);
        if (_phase == 2)
        {
            Physics.PreStep(this);               // a node moved by the game is pushed into its body, then the world steps
            Physics.Step(FixedDeltaTime);
            return false;
        }
        if (_phase == 3) return NextPhysicsCall();
        if (_phase == 4) return NextRelease();
        FreeDoomed();
        _mode = 0;
        return false;
    }

    private bool NextFrame()
    {
        if (_phase == 0) return NextChannel(_chStart, Callbacks.Start);
        if (_phase == 1) return NextChannel(_chUpdate, Callbacks.Update);
        if (_phase == 2)
        {
            Physics.Sync(this, FixedAlpha);      // the bodies' poses, interpolated, into their nodes
            return false;
        }
        if (_phase == 3) return NextChannel(_chLate, Callbacks.LateUpdate);
        if (_phase == 4) return NextRelease();
        FreeDoomed();
        _mode = 0;
        return false;
    }

    private bool NextChannel(List<Component> channel, int bit)
    {
        if (!_phaseStarted)
        {
            _phaseStarted = true;
            if (bit == Callbacks.Start && _dStart) { Rebuild(channel, bit); _dStart = false; }
            else if (bit == Callbacks.FixedUpdate && _dFixed) { Rebuild(channel, bit); _dFixed = false; }
            else if (bit == Callbacks.Update && _dUpdate) { Rebuild(channel, bit); _dUpdate = false; }
            else if (bit == Callbacks.LateUpdate && _dLate) { Rebuild(channel, bit); _dLate = false; }
        }
        while (_cursor < channel.Count)
        {
            Component c = channel[_cursor];
            _cursor++;
            if (c.Destroyed || !c.EnabledInHierarchy) continue;
            if (bit == Callbacks.Start)
            {
                if (c.HasStarted) continue;
                c.HasStarted = true;
                _dStart = true;                  // it drops out of the channel at the next rebuild
            }
            CallKind = bit;
            CallComponent = c;
            return true;
        }
        return false;
    }

    // Physics messages: each event the world reports names a collider; every component on its node that has the callback hears it.
    private bool NextPhysicsCall()
    {
        while (true)
        {
            while (_eventComp != null)
            {
                Component c = _eventComp;
                _eventComp = c.NextOnNode;
                if (c.Destroyed || !c.EnabledInHierarchy || (c.Callbacks & _eventBit) == 0) continue;
                CallKind = _eventBit;
                CallComponent = c;
                return true;
            }
            if (!Physics.NextEvent()) return false;
            Collider2D self = Physics.EventSelf;
            if (self == null || self.Self == null || self.Self.Destroyed) continue;
            Collision.Self = self;
            Collision.Other = Physics.EventOther;
            Collision.X = Physics.EventX;
            Collision.Y = Physics.EventY;
            Collision.NX = -Physics.EventNX;               // the core's normal points self to other; the message's points from the other toward self, as the engine's Collision2D.Normal does
            Collision.NY = -Physics.EventNY;
            Collision.Impulse = Physics.EventImpulse;
            _eventBit = Physics.EventKind;
            _eventComp = _firstComp[self.Self.Node.Index];
        }
    }

    // Tell each script, once, that its component is gone, before the record is recycled.
    private bool NextRelease()
    {
        while (_cursor < _doomedComps.Count)
        {
            Component c = _doomedComps[_cursor];
            _cursor++;
            if (c.Kind < ComponentKind.FirstScript) continue;
            CallKind = Callbacks.Release;
            CallComponent = c;
            return true;
        }
        return false;
    }
}
