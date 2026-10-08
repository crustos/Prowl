// Engine: the 2D runtime (Prowl.Core2D) as a flat, handle-based C API, for a host that is not C#: prowl.py calls it through ctypes.
//
// This file is translated to C with the engine by CC# (tools/prowl2d_so.py) and built into libprowl2d.so; the functions below become the exported
// p2d_* functions of Native/Engine2D/engine_exports.c. So every signature here is ints and floats only (nothing a ctypes caller would have to build or free),
// a node is its slot index in the scene (-1: none), and nothing throws. The subset's limits apply (tools/ccsharp/README.md): no strings, no arrays across
// the boundary, one Scene2D per process. A node index, like any Node in this runtime, names whatever reuses its slot once the node is destroyed.
//
// The renderer (Native/Gfx2D) is not wrapped here: libprowl2d.so exports the gfx_* functions itself (textures, triangles, the window), so a host can draw its own
// things (a tile map, sprite frames) in the same frame as the scene's sprites.
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;
using Prowl.Native.Gfx2D;

static class Engine
{
    static Scene2D scene;
    static int ready;
    static int sprites;
    static Rigidbody2D[] bodies;           // a node's rigidbody by node index (set by AddBody), for velocities and impulses
    static Shatter2D blaster;              // only used for its AddExplosionForce

    static Rigidbody2D BodyAt(int i) { return bodies[i]; }
    static void SetBodyAt(int i, Rigidbody2D rb) { bodies[i] = rb; }

    /// <summary>Bump when a function below changes its meaning: the host checks it.</summary>
    public static int Version() { return 4; }

    // ---- the process: renderer and scene -------------------------------------------------------------------------------------

    /// <summary>Makes the renderer's offscreen picture (width x height) and the scene. 1 if it worked, 0 if there is no GLES 3.1.</summary>
    public static int Init(int width, int height)
    {
        if (ready != 0) return 1;
        if (GFX.Init(width, height) == 0) return 0;
        if (scene == null)                    // the scene is an arena class of capacity 1: a second `new Scene2D()` would have no slot, so Init after Shutdown reuses it
        {
            Scripts.Init();
            scene = new Scene2D();
            bodies = new Rigidbody2D[CoreLimits.Nodes];
            blaster = new Shatter2D(scene, 1u);
        }
        ready = 1;
        return 1;
    }

    public static void Shutdown()
    {
        if (ready == 0) return;
        GFX.Shutdown();
        ready = 0;
    }

    public static void Camera(float x, float y, float halfHeight, float r, float g, float b)
    {
        GFX.Camera(x, y, halfHeight, r, g, b);
    }

    // ---- nodes -----------------------------------------------------------------------------------------------------------------

    /// <summary>A new root node: its index, or -1 if there is no room (CoreLimits.Nodes).</summary>
    public static int NewNode()
    {
        if (ready == 0) return -1;
        Node n = scene.NewNode(null);
        if (n == null) return -1;
        SetBodyAt(n.Index, null);
        return n.Index;
    }

    public static int NodeCount()
    {
        if (ready == 0) return 0;
        return scene.NodeCount;
    }

    public static void SetPos(int node, float x, float y)
    {
        if (ready == 0) return;
        Node n = scene.NodeAt(node);
        if (n == null) return;
        n.SetPosition(x, y);
    }

    public static void SetAngle(int node, float radians)
    {
        if (ready == 0) return;
        Node n = scene.NodeAt(node);
        if (n == null) return;
        n.SetAngle(radians);
    }

    public static void SetScale(int node, float sx, float sy)
    {
        if (ready == 0) return;
        Node n = scene.NodeAt(node);
        if (n == null) return;
        n.SetScale(sx, sy);
    }

    public static float NodeX(int node)
    {
        if (ready == 0) return 0f;
        Node n = scene.NodeAt(node);
        if (n == null) return 0f;
        return n.WorldX();
    }

    public static float NodeY(int node)
    {
        if (ready == 0) return 0f;
        Node n = scene.NodeAt(node);
        if (n == null) return 0f;
        return n.WorldY();
    }

    public static float NodeAngle(int node)
    {
        if (ready == 0) return 0f;
        Node n = scene.NodeAt(node);
        if (n == null) return 0f;
        return n.WorldAngle();
    }

    public static void DestroyNode(int node)
    {
        if (ready == 0) return;
        Node n = scene.NodeAt(node);
        if (n == null) return;
        scene.Destroy(n);
    }

    /// <summary>Destroys every node (the scene itself stays: there is one per process).</summary>
    public static void Clear()
    {
        if (ready == 0) return;
        int high = scene.NodeHighWater;
        for (int i = 0; i < high; i++)
        {
            Node n = scene.NodeAt(i);
            if (n != null) scene.Destroy(n);
        }
        scene.SetGravity(0f, -9.81f);
        // destroyed nodes are freed at the end of a frame: run one empty one so their slots come back before the next NewNode
        Scripts.Tick(scene, 1f / 60f);
    }

    // ---- components ------------------------------------------------------------------------------------------------------------

    /// <summary>A box or disc (shape 0 or 1: GFX_SHAPE_BOX, GFX_SHAPE_DISC) of the given full size and tint on a node. 1 if it was added.</summary>
    public static int AddSprite(int node, int shape, float width, float height, float r, float g, float b)
    {
        if (ready == 0) return 0;
        Node n = scene.NodeAt(node);
        if (n == null) return 0;
        SpriteRenderer2D s = scene.AddSprite(n, shape, width, height, r, g, b);
        if (s == null) return 0;
        return 1;
    }

    /// <summary>A rigidbody: bodyType 0 static, 1 kinematic, 2 dynamic (PB2_BODY_*). 1 if it was added.</summary>
    public static int AddBody(int node, int bodyType)
    {
        if (ready == 0) return 0;
        Node n = scene.NodeAt(node);
        if (n == null) return 0;
        Rigidbody2D rb = scene.AddRigidbody(n, bodyType);
        if (rb == null) return 0;
        SetBodyAt(node, rb);
        return 1;
    }

    /// <summary>A rigidbody that does not rotate (freezeRotation 1: a character) and has the given gravity scale. 1 if it was added.</summary>
    public static int AddBodyEx(int node, int bodyType, int freezeRotation, float gravityScale)
    {
        if (ready == 0) return 0;
        Node n = scene.NodeAt(node);
        if (n == null) return 0;
        Rigidbody2D rb = scene.NewRigidbody(n, bodyType);
        if (rb == null) return 0;
        rb.FreezeRotation = freezeRotation != 0;
        rb.GravityScale = gravityScale;
        scene.Finish(rb.Self);
        SetBodyAt(node, rb);
        return 1;
    }

    public static int AddBox(int node, float width, float height)
    {
        if (ready == 0) return 0;
        Node n = scene.NodeAt(node);
        if (n == null) return 0;
        Collider2D c = scene.AddBoxCollider(n, width, height);
        if (c == null) return 0;
        return 1;
    }

    /// <summary>A box collider with the given friction (0: slides along walls).</summary>
    public static int AddBoxEx(int node, float width, float height, float friction)
    {
        if (ready == 0) return 0;
        Node n = scene.NodeAt(node);
        if (n == null) return 0;
        Collider2D c = scene.NewBoxCollider(n, width, height);
        if (c == null) return 0;
        c.Friction = friction;
        scene.Finish(c.Self);
        return 1;
    }

    public static int AddCircle(int node, float radius)
    {
        if (ready == 0) return 0;
        Node n = scene.NodeAt(node);
        if (n == null) return 0;
        Collider2D c = scene.AddCircleCollider(n, radius);
        if (c == null) return 0;
        return 1;
    }

    /// <summary>Sets a body's linear velocity (units per second).</summary>
    public static void SetVelocity(int node, float vx, float vy)
    {
        if (ready == 0 || node < 0 || node >= CoreLimits.Nodes) return;
        Rigidbody2D rb = BodyAt(node);
        if (rb == null || scene.NodeAt(node) == null) return;
        rb.SetVelocity(vx, vy);
    }

    /// <summary>Applies an instant impulse (mass times velocity change) to a body.</summary>
    public static void Impulse(int node, float ix, float iy)
    {
        if (ready == 0 || node < 0 || node >= CoreLimits.Nodes) return;
        Rigidbody2D rb = BodyAt(node);
        if (rb == null || scene.NodeAt(node) == null) return;
        rb.AddImpulse(ix, iy);
    }

    public static float VelocityX(int node)
    {
        if (ready == 0 || node < 0 || node >= CoreLimits.Nodes) return 0f;
        Rigidbody2D rb = BodyAt(node);
        if (rb == null || scene.NodeAt(node) == null) return 0f;
        return rb.VelocityX();
    }

    public static float VelocityY(int node)
    {
        if (ready == 0 || node < 0 || node >= CoreLimits.Nodes) return 0f;
        Rigidbody2D rb = BodyAt(node);
        if (rb == null || scene.NodeAt(node) == null) return 0f;
        return rb.VelocityY();
    }

    /// <summary>An explosion at (x, y): pushes every body within radius away, the more the closer (Shatter2D.AddExplosionForce). Returns how many it pushed.</summary>
    public static int Blast(float x, float y, float radius, float force)
    {
        if (ready == 0) return 0;
        return blaster.AddExplosionForce(x, y, radius, force, 0f);
    }

    public static void Gravity(float x, float y)
    {
        if (ready == 0) return;
        scene.SetGravity(x, y);
    }

    // ---- a frame ---------------------------------------------------------------------------------------------------------------

    /// <summary>Advances the scene by dt seconds: the engine's fixed-step loop, with the frame's callbacks and physics.</summary>
    public static void Step(float dt)
    {
        if (ready == 0) return;
        Scripts.Tick(scene, dt);
    }

    /// <summary>Draws the scene's sprites into the renderer's picture (it clears it first). Returns how many it drew.</summary>
    public static int Draw()
    {
        if (ready == 0) return 0;
        sprites = GFX.Draw(scene.Render.DrawData, scene.CollectSprites());
        return sprites;
    }

    // The translator needs an entry point; the library has none of its own (its host calls the functions above), so this one only proves it links.
    public static int Main()
    {
        if (Init(64, 64) == 0) return 0;      // no GLES here is not a failure of the build
        int n = NewNode();
        AddSprite(n, 0, 1f, 1f, 1f, 1f, 1f);
        Draw();
        Shutdown();
        return 0;
    }
}
