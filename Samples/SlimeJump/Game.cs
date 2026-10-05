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
        Console.WriteLine("won=" + won + " frame=" + wonFrame + " deaths=" + Shared.Deaths + " gems=" + Shared.Gems + " enemies=" + Shared.EnemyCount() + " slain=" + slain);
        Console.WriteLine("first jump frame=" + firstJump + " first climb frame=" + firstClimb);
        Console.WriteLine("max x*1000=" + Milli(maxX) + " max y*1000=" + Milli(maxY) + " end x*1000=" + Milli(player.WorldX()) + " y*1000=" + Milli(player.WorldY()));
        return won == 1 ? 0 : 1;
    }
}
