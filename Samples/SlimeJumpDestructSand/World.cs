// The world of SlimeJumpDestructSand: builds the scene (the shaft, the crates and boulders, the goal, the slime and its bot) and steps it. Game.cs runs it headless and prints;
// a page front end (tools/sand_web) draws it.
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;
using Prowl.Runtime.Destruction2D;

static class World
{
    public static Scene2D scene;
    public static Node player;

    static Collider2D MakeCollider(Scene2D scene, Node n, float w, float h, bool trigger, float offY)
    {
        Collider2D c = scene.NewBoxCollider(n, w, h);
        c.Friction = 0f;
        c.IsTrigger = trigger;
        c.OffsetY = offY;
        scene.Finish(c.Self);
        return c;
    }

    static Node MakeBox(Scene2D scene, float x, float y, float w, float h, int layer, int tag, bool trigger, float offY)
    {
        Node n = scene.NewNode(null);
        n.SetPosition(x, y);
        n.Layer = layer;
        n.Tag = tag;
        MakeCollider(scene, n, w, h, trigger, offY);
        return n;
    }

    // the scene: the shaft, the things in it, the goal and the bot-driven slime
    public static void Build()
    {
        Scripts.Init();
        scene = new Scene2D();
        scene.FixedDeltaTime = Cfg.Dt;
        scene.SetGravity(0f, Cfg.Gravity);
        uint[] layerRows = Level.LayerRows();
        scene.Physics.Sim.SetLayerMatrix(layerRows);
        Shared.InitSaves(0);
        Shared.InitGems(0);
        Shared.InitCrumbly(0);
        Shared.InitEnemies(0);

        Destruct.Init(scene, 16);
        // room 0: a stack of crates on the floor; the first blast opens the floor under it
        Destruct.AddCrate(scene, 11.5f, Level.AirBottom(0) + 0.5f);
        Destruct.AddCrate(scene, 12.8f, Level.AirBottom(0) + 0.5f);
        Destruct.AddCrate(scene, 12.15f, Level.AirBottom(0) + 1.5f);
        // room 1: a boulder beside where the floor will open
        Destruct.AddBoulder(scene, 15.4f, Level.AirBottom(1) + 0.7f);
        // room 3: boulders and crates on the floor
        Destruct.AddBoulder(scene, 5f, Level.AirBottom(3) + 0.7f);
        Destruct.AddBoulder(scene, 9f, Level.AirBottom(3) + 0.7f);
        Destruct.AddBoulder(scene, 12.5f, Level.AirBottom(3) + 0.7f);
        Destruct.AddCrate(scene, 7f, Level.AirBottom(3) + 0.5f);
        Destruct.AddCrate(scene, 14.5f, Level.AirBottom(3) + 0.5f);
        // room 6, the vault
        Destruct.AddCrate(scene, 10f, Level.AirBottom(6) + 0.5f);
        Destruct.AddCrate(scene, 11.3f, Level.AirBottom(6) + 0.5f);
        Destruct.AddBoulder(scene, 14f, Level.AirBottom(6) + 0.7f);

        Node goal = MakeBox(scene, Level.GoalX(), Level.GoalY(), 1.4f, 2f, Layers.Gem, Shared.TagGoal, true, 0f);
        Scripts.AddGoalScript(goal);

        // the player: a dynamic box; gravity is applied by PlayerScript
        player = scene.NewNode(null);
        player.SetPosition(Level.SpawnX(), Level.SpawnY());
        player.Layer = Layers.Player;
        player.Tag = Shared.TagPlayer;
        Rigidbody2D rb = scene.NewRigidbody(player, PB2.BodyDynamic);
        rb.GravityScale = 0f;
        rb.LinearDamping = Cfg.LinearDamping;
        rb.FreezeRotation = true;
        rb.IsBullet = true;
        rb.CanSleep = false;
        scene.Finish(rb.Self);
        Collider2D playerCol = MakeCollider(scene, player, Cfg.ColliderW, Cfg.ColliderH, false, 0f);
        Shared.PlayerColliderIndex = playerCol.ColliderIndex;
        Scripts.AddPlayerScript(player);
        Scripts.AddBotScript(player);

    }

    // one fixed step: the sand and water, the terrain's rebuilds and the fuses, before the physics step, then the scripts
    public static void Tick()
    {
        Destruct.Step(scene);
        Scripts.Tick(scene, Cfg.Dt);
    }
}
