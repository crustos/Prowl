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
        World.Build();
        Scene2D scene = World.scene;
        Node player = World.player;
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
            World.Tick();
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
        // the teleporter: a few steps on, the slime is back at the start (the shaft stays as it was left)
        for (int i = 0; i < 40; i++) World.Tick();
        float wy = player.WorldY();
        string wl = "warps=" + Shared.Warps + " back at x*1000=" + Milli(player.WorldX()) + " y*1000=" + Milli(wy);
        Console.WriteLine(wl);
        bool warped = Shared.Warps == 1 && wy > Level.SpawnY() - 0.5f && wy < Level.SpawnY() + 1.5f;
        Destruct.Shutdown();
        bool ok = won == 1 && deepestRoom == Level.Rooms - 1 && plugsOpen == Destruct.PlugCount() && Destruct.Detonations > 3 && problems == 0 && Shared.Deaths == 0 && warped;
        if (!ok) Console.WriteLine("FAIL: the run did not get to the bottom, open both silos, break things and stay consistent, and the teleporter must send it back");
        return ok ? 0 : 1;
    }
}
