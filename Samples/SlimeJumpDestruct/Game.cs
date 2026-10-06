// SlimeJumpDestruct: SlimeJump (the slime, its bot, the level) with a destructible world added: a wall of dirt across the way that the slime's
// bullets dig out (PixelTerrain2D), and crates that a bullet shatters into fragments and a blast (Shatter2D) that pushes things away, kills
// the enemies it reaches, digs a crater and sets off the next crate; all on Box2D-Packed. See Destruct.cs. Headless, like SlimeJump: the bot
// plays, the program prints, and the same source runs on .NET (the reference) and as a native or wasm executable, with the same output.
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

static class Game
{
    const int MaxFrames = 3000;

    static int Milli(float v) { return (int)(v * 1000f); }

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

    public static int Main()
    {
        Scripts.Init();
        Scene2D scene = new Scene2D();
        scene.FixedDeltaTime = Cfg.Dt;
        scene.SetGravity(0f, Cfg.Gravity);
        Level.Init();
        uint[] layerRows = Level.LayerRows();
        scene.Physics.Sim.SetLayerMatrix(layerRows);

        // the destructible part: a wall of dirt (x 34..36, floor to 9 units up) and crates along the floor behind it. Two of them stand together,
        // so one's blast sets the other off.
        Destruct.Init(scene, 4);
        Destruct.AddCrate(scene, 36.6f, 4.6f);       // against the dirt's far side: its blast widens the slime's tunnel
        Destruct.AddCrate(scene, 44f, 4.6f);
        Destruct.AddCrate(scene, 45.3f, 4.6f);
        Destruct.AddCrate(scene, 52f, 4.6f);
        int dirtAtStart = Destruct.SolidPixels();

        for (int i = 0; i < Level.WallCount; i++)
            MakeBox(scene, Level.Wall(i, 0) + Level.Wall(i, 2) * 0.5f, Level.Wall(i, 1) + Level.Wall(i, 3) * 0.5f,
                    Level.Wall(i, 2), Level.Wall(i, 3), Layers.Wall, Shared.TagWall, false, 0f);
        for (int i = 0; i < Level.ClimbCount; i++)
            MakeBox(scene, Level.Climb(i, 0) + Level.Climb(i, 2) * 0.5f, Level.Climb(i, 1) + Level.Climb(i, 3) * 0.5f,
                    Level.Climb(i, 2), Level.Climb(i, 3), Layers.Climbable, Shared.TagWall, false, 0f);
        for (int i = 0; i < Level.SpikeCount; i++)
        {
            float sw = Level.Spike(i, 2);
            float sh = Level.Spike(i, 3);
            Node n = MakeBox(scene, Level.Spike(i, 0) + sw * 0.5f, Level.Spike(i, 1) + sh * 0.5f, sw, sh * 0.6f, Layers.Hazard, Shared.TagHazard, true, -sh * 0.2f);
            Scripts.AddHazardScript(n);
        }
        Shared.InitSaves(Level.SaveCount);
        for (int i = 0; i < Level.SaveCount; i++)
        {
            float sx = Level.Save(i, 0);
            float sy = Level.Save(i, 1);
            Node n = MakeBox(scene, sx, sy, 1.2f, 1.4f, Layers.Gem, Shared.TagSavePoint, true, 0.7f);
            SavePointScript sp = Scripts.AddSavePointScript(n);
            sp.X = sx;
            sp.Y = sy + Cfg.ColliderH * 0.5f + 0.2f;
            sp.Index = i;
        }
        Shared.InitGems(Level.GemCount);
        for (int i = 0; i < Level.GemCount; i++)
        {
            Node n = scene.NewNode(null);
            n.SetPosition(Level.Gem(i, 0), Level.Gem(i, 1));
            n.Layer = Layers.Gem;
            n.Tag = Shared.TagGem;
            Collider2D gc = scene.NewCircleCollider(n, 0.45f);
            gc.IsTrigger = true;
            scene.Finish(gc.Self);
            GemScript gs = Scripts.AddGemScript(n);
            gs.Index = i;
            Shared.RegisterGem(i, n);
        }
        Shared.InitCrumbly(Level.CrumblyCount);
        for (int i = 0; i < Level.CrumblyCount; i++)
        {
            // each collider is 0.04 wider than its tile, so neighbours overlap: with exactly flush boxes the slime catches on the seam (a ghost collision)
            Node cn = MakeBox(scene, Level.CrumblyTile(i, 0) + 0.5f, Level.CrumblyTile(i, 1) + 0.5f, Level.CrumblyTile(i, 2) + 0.04f, Level.CrumblyTile(i, 3), Layers.Wall, Shared.TagWall, false, 0f);
            CrumblyScript cs = Scripts.AddCrumblyScript(cn);
            cs.Index = i;
            Shared.RegisterCrumbly(i, cn);
        }
        for (int i = 0; i < Level.ShooterCount; i++)
        {
            // the shooter's tile is already part of the walls: this node only carries the trap
            Node sn = scene.NewNode(null);
            sn.SetPosition(Level.Shooter(i, 0), Level.Shooter(i, 1));
            ShooterScript ss = Scripts.AddShooterScript(sn);
            ss.DirX = Level.Shooter(i, 2);
            ss.DirY = Level.Shooter(i, 3);
        }
        Shared.InitEnemies(Level.EnemyCount);
        for (int i = 0; i < Level.EnemyCount; i++)
        {
            int kind = Level.EnemyKind(i);
            float ex = Level.EnemyX(i);
            float ey = Level.EnemyY(i);
            Node en = scene.NewNode(null);
            en.SetPosition(ex, ey);
            en.Layer = Layers.Enemy;
            en.Tag = Shared.TagEnemyBase + i;
            Rigidbody2D erb = scene.NewRigidbody(en, PB2.BodyDynamic);
            erb.GravityScale = EnemyCfg.GravityScale(kind);
            erb.LinearDamping = 0f;
            erb.FreezeRotation = true;
            erb.CanSleep = false;
            scene.Finish(erb.Self);
            MakeCollider(scene, en, EnemyCfg.ColliderW(kind), EnemyCfg.ColliderH(kind), false, 0f);
            EnemyScript es = Scripts.AddEnemyScript(en);
            es.Index = i;
            es.Kind = kind;
            es.InitX = ex;
            es.InitY = ey;
            Shared.RegisterEnemy(i, en, kind, ex, ey);
        }
        Node goal = MakeBox(scene, Level.GoalX(), Level.GoalY(), 1.4f, 2f, Layers.Gem, Shared.TagGoal, true, 0f);
        Scripts.AddGoalScript(goal);

        // the player: a dynamic box; gravity is applied by PlayerScript
        Node player = scene.NewNode(null);
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
        Scripts.AddLassoScript(player);
        Scripts.AddBotScript(player);

        int wonFrame = -1;
        int firstClimb = -1;
        int firstJump = -1;
        float maxX = Level.SpawnX();
        float maxY = Level.SpawnY();
        for (int frame = 0; frame < MaxFrames && !Shared.Won; frame++)
        {
            Destruct.Step(scene);                              // the terrain's rebuilds and the crates' fuses, before the physics step
            Scripts.Tick(scene, Cfg.Dt);
            float px = player.WorldX();
            float py = player.WorldY();
            if (px > maxX) maxX = px;
            if (py > maxY) maxY = py;
            if (Shared.PlayerClimbing && firstClimb < 0) firstClimb = frame;
            if (Shared.PlayerJumping && firstJump < 0) firstJump = frame;
            if (Shared.LassoActive && frame % 10 == 0)
            {
                int att = 0;
                if (Shared.LassoAttached) att = 1;
                Console.WriteLine("lasso t=" + frame + " attached=" + att + " len*1000=" + Milli(Shared.LassoLength) + " x*1000=" + Milli(px) + " y*1000=" + Milli(py));
            }
            if (frame % 100 == 0)
            {
                string line = "t=" + (frame / 100) + "s x*1000=" + Milli(px) + " y*1000=" + Milli(py);
                Console.WriteLine(line);
            }
            if (Shared.Won) wonFrame = frame;
        }
        int won = 0;
        if (Shared.Won) won = 1;
        int slain = 0;
        for (int i = 0; i < Shared.EnemyCount(); i++)
            if (!Shared.EnemyAlive(i)) slain++;
        Console.WriteLine("won=" + won + " frame=" + wonFrame + " deaths=" + Shared.Deaths + " gems=" + Shared.Gems + " enemies=" + Shared.EnemyCount() + " slain=" + slain + " crumbled=" + Shared.CrumbledCount());
        Console.WriteLine("first jump frame=" + firstJump + " first climb frame=" + firstClimb);
        Console.WriteLine("max x*1000=" + Milli(maxX) + " max y*1000=" + Milli(maxY) + " end x*1000=" + Milli(player.WorldX()) + " y*1000=" + Milli(player.WorldY()));
        int dirtLeft = Destruct.SolidPixels();
        int liveCrates = 0;
        for (int i = 0; i < Destruct.CrateCount(); i++)
            if (Destruct.CrateLive(scene, i)) liveCrates++;
        Console.WriteLine("dirt " + dirtAtStart + " -> " + dirtLeft + " pixels (digs=" + Destruct.Digs + "), crates=" + Destruct.CrateCount() + " detonated=" + Destruct.Detonations + " fragments=" + Destruct.Fragments
                          + " gone=" + Destruct.FragmentsGone + " enemies killed by blasts=" + Destruct.BlastKills);
        bool tunnel = true;                                  // is there a way through the dirt at the slime's height?
        for (float tx = Destruct.DirtX; tx < Destruct.DirtX + 2f; tx += 0.1f)
            if (Destruct.SolidAt(tx, Level.SpawnY() - 0.2f)) tunnel = false;
        Console.WriteLine("tunnel through the dirt=" + (tunnel ? 1 : 0));
        int problems = scene.Validate();
        Console.WriteLine("scene problems=" + problems);
        Destruct.Shutdown();
        bool ok = won == 1 && tunnel && dirtLeft < dirtAtStart && Destruct.Detonations > 0 && Destruct.Fragments > 0 && problems == 0;
        if (!ok) Console.WriteLine("FAIL: the run did not win, dig, shatter and stay consistent");
        return ok ? 0 : 1;
    }
}
