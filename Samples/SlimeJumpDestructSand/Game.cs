// SlimeJumpDestructSand: SlimeJumpDestruct taken down a shaft, with falling sand and flowing water. The world is one destructible terrain (PixelTerrain2D with the
// sand simulation switched on): a rock shaft of seven rooms, one above the other, with sand dunes, a pool, a sand silo and a water tank in it, and crates and
// boulders (Shatter2D) lying about. The slime digs down through the floors with its blaster and shatters what it meets; every hole lets the sand and the water above it
// run into the room below. See Destruct.cs. Headless, like SlimeJumpDestruct: a bot plays, the program prints, and the same source runs on .NET (the reference) and as
// a native or wasm executable with the same output.
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;
using Prowl.Runtime.Destruction2D;

static class Game
{
    const int MaxFrames = 14000;

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

    // sand and water by room, as "sand/water" pairs
    static void PrintRooms(string title)
    {
        string line = title;
        for (int i = 0; i < Level.Rooms; i++)
            line = line + " " + i + ":" + Destruct.GrainsIn(i, SandSim.Sand) + "/" + Destruct.GrainsIn(i, SandSim.Water);
        Console.WriteLine(line);
    }

    public static int Main()
    {
        Scripts.Init();
        Scene2D scene = new Scene2D();
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
        Scripts.AddBotScript(player);

        int sand0 = Destruct.GrainsIn(0, SandSim.Sand);
        int totalSand = 0;
        int totalWater = 0;
        for (int i = 0; i < Level.Rooms; i++)
        {
            totalSand += Destruct.GrainsIn(i, SandSim.Sand);
            totalWater += Destruct.GrainsIn(i, SandSim.Water);
        }
        int items0 = Destruct.ItemCount();
        int plugs0 = Destruct.PlugCount();
        int ground0 = Destruct.GroundPixels();
        string head = "shaft " + Level.Rooms + " rooms, sand=" + totalSand + " water=" + totalWater + " items=" + items0 + " plugs=" + plugs0 + " ground=" + ground0;
        Console.WriteLine(head);
        PrintRooms("start  ");

        int wonFrame = -1;
        int deepestRoom = 0;
        int roomFrames = 0;
        float minY = Level.SpawnY();
        int maxAwake = 0;
        for (int frame = 0; frame < MaxFrames && !Shared.Won; frame++)
        {
            Destruct.Step(scene);                              // the sand and water, the terrain's rebuilds and the fuses, before the physics step
            Scripts.Tick(scene, Cfg.Dt);
            float py = player.WorldY();
            if (py < minY) minY = py;
            int room = Level.RoomOfY(py);
            if (room > deepestRoom)
            {
                deepestRoom = room;
                int awakeNow = Destruct.AwakeCount();
                string entered = "room " + room + " frame=" + frame + " x*1000=" + Milli(player.WorldX()) + " y*1000=" + Milli(py) + " digs=" + Destruct.Digs + " awake chunks=" + awakeNow;
                Console.WriteLine(entered);
                PrintRooms("       ");
            }
            int awake = Destruct.AwakeCount();
            if (awake > maxAwake) maxAwake = awake;
            if (frame % 500 == 0)
            {
                int hashNow = Destruct.SandHash();
                string tick = "t=" + (frame / 100) + "s x*1000=" + Milli(player.WorldX()) + " y*1000=" + Milli(py) + " sand hash=" + hashNow;
                Console.WriteLine(tick);
            }
            if (Shared.Won) wonFrame = frame;
            roomFrames++;
        }
        int won = 0;
        if (Shared.Won) won = 1;
        string result = "won=" + won + " frame=" + wonFrame + " deaths=" + Shared.Deaths + " deepest room=" + deepestRoom + " min y*1000=" + Milli(minY);
        Console.WriteLine(result);
        PrintRooms("end    ");
        int endSand = 0;
        int endWater = 0;
        for (int i = 0; i < Level.Rooms; i++)
        {
            endSand += Destruct.GrainsIn(i, SandSim.Sand);
            endWater += Destruct.GrainsIn(i, SandSim.Water);
        }
        int plugsOpen = 0;
        for (int i = 0; i < Destruct.PlugCount(); i++)
            if (!Destruct.PlugClosed(i)) plugsOpen++;
        int liveItems = 0;
        for (int i = 0; i < Destruct.ItemCount(); i++)
            if (Destruct.ItemLive(scene, i)) liveItems++;
        string flow = "sand " + totalSand + " -> " + endSand + " water " + totalWater + " -> " + endWater + " (rubble made " + Destruct.RubbleGrains + ") digs=" + Destruct.Digs + " plugs open=" + plugsOpen + "/" + plugs0;
        Console.WriteLine(flow);
        int endHash = Destruct.SandHash();
        string tail = "items=" + items0 + " detonated=" + Destruct.Detonations + " live=" + liveItems + " fragments=" + Destruct.Fragments + " gone=" + Destruct.FragmentsGone + " most awake chunks=" + maxAwake + " sand hash=" + endHash;
        Console.WriteLine(tail);
        int problems = scene.Validate();
        string pr = "scene problems=" + problems;
        Console.WriteLine(pr);
        Destruct.Shutdown();
        bool ok = won == 1 && deepestRoom == Level.Rooms - 1 && plugsOpen == Destruct.PlugCount() && Destruct.Detonations > 3 && problems == 0 && Shared.Deaths == 0;
        if (!ok) Console.WriteLine("FAIL: the run did not get to the bottom, open both silos, break things and stay consistent");
        return ok ? 0 : 1;
    }
}
