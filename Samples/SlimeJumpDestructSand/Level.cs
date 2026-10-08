// The shaft: where everything is, in world units (x to the right, y up). The whole world is one terrain bitmap (Destruct.cs) 18 units wide and 96 deep, rock with
// seven rooms cut into it, one above the other. Room i has 10 units of air above a floor of rock 3 units thick; the floors are what the slime digs through, and what
// the sand and the water are lying on.
static class Level
{
    public const int Rooms = 7;
    public const float Width = 18f;
    public const float Height = 96f;
    public const float SideWall = 1.5f;       // rock on either side of the air
    public const float AirHeight = 10f;
    public const float FloorThick = 3f;
    public const float Pitch = 13f;           // from one room's air to the next one's
    public const float Top = 92f;             // the top of the first room's air: 4 units of rock above it

    public static float AirTop(int room) { return Top - room * Pitch; }
    public static float AirBottom(int room) { return Top - room * Pitch - AirHeight; }         // = the top of the room's floor
    public static float FloorBottom(int room) { return AirBottom(room) - FloorThick; }

    // the room whose air or floor a height is in (the floor belongs to the room above it); -1 above the first room's air, Rooms - 1 for anything below
    public static int RoomOfY(float y)
    {
        for (int i = 0; i < Rooms; i++)
            if (y > FloorBottom(i)) return i;
        return Rooms - 1;
    }

    // where the bot digs down through each room's floor if nothing has opened it already
    public static float DigX(int room)
    {
        if (room == 0) return 12.2f;
        if (room == 1) return 13.5f;
        if (room == 2) return 7f;
        if (room == 3) return 7.5f;
        if (room == 4) return 9f;
        if (room == 5) return 14f;
        return 9f;
    }

    public static float SpawnX() { return 3f; }
    public static float SpawnY() { return AirBottom(0) + Cfg.ColliderH * 0.5f + 0.05f; }
    public static float GoalX() { return 4f; }
    public static float GoalY() { return AirBottom(Rooms - 1) + 1f; }
    public static float MinY() { return 0f; }

    // Layer collision matrix: rows[a] bit b set == layers a and b collide.
    public static uint[] LayerRows()
    {
        uint[] r = new uint[32];
        for (int i = 0; i < 32; i++) r[i] = 0xFFFFFFFFu;
        r[0] &= ~(1u << 20); r[20] &= ~(1u << 0);
        r[1] &= ~(1u << 20); r[20] &= ~(1u << 1);
        r[2] &= ~(1u << 20); r[20] &= ~(1u << 2);
        r[3] &= ~(1u << 20); r[20] &= ~(1u << 3);
        r[4] &= ~(1u << 20); r[20] &= ~(1u << 4);
        r[5] &= ~(1u << 20); r[20] &= ~(1u << 5);
        r[6] &= ~(1u << 19); r[19] &= ~(1u << 6);
        r[6] &= ~(1u << 20); r[20] &= ~(1u << 6);
        r[6] &= ~(1u << 25); r[25] &= ~(1u << 6);
        r[8] &= ~(1u << 9); r[9] &= ~(1u << 8);
        r[8] &= ~(1u << 19); r[19] &= ~(1u << 8);
        r[8] &= ~(1u << 20); r[20] &= ~(1u << 8);
        r[8] &= ~(1u << 25); r[25] &= ~(1u << 8);
        r[8] &= ~(1u << 26); r[26] &= ~(1u << 8);
        r[9] &= ~(1u << 9); r[9] &= ~(1u << 9);
        r[9] &= ~(1u << 10); r[10] &= ~(1u << 9);
        r[9] &= ~(1u << 19); r[19] &= ~(1u << 9);
        r[9] &= ~(1u << 20); r[20] &= ~(1u << 9);
        r[9] &= ~(1u << 25); r[25] &= ~(1u << 9);
        r[9] &= ~(1u << 26); r[26] &= ~(1u << 9);
        r[10] &= ~(1u << 19); r[19] &= ~(1u << 10);
        r[10] &= ~(1u << 20); r[20] &= ~(1u << 10);
        r[10] &= ~(1u << 25); r[25] &= ~(1u << 10);
        r[10] &= ~(1u << 26); r[26] &= ~(1u << 10);
        r[11] &= ~(1u << 20); r[20] &= ~(1u << 11);
        r[12] &= ~(1u << 20); r[20] &= ~(1u << 12);
        r[13] &= ~(1u << 20); r[20] &= ~(1u << 13);
        r[14] &= ~(1u << 20); r[20] &= ~(1u << 14);
        r[16] &= ~(1u << 20); r[20] &= ~(1u << 16);
        r[17] &= ~(1u << 20); r[20] &= ~(1u << 17);
        r[18] &= ~(1u << 20); r[20] &= ~(1u << 18);
        r[19] &= ~(1u << 20); r[20] &= ~(1u << 19);
        r[19] &= ~(1u << 25); r[25] &= ~(1u << 19);
        r[19] &= ~(1u << 26); r[26] &= ~(1u << 19);
        r[20] &= ~(1u << 20); r[20] &= ~(1u << 20);
        r[20] &= ~(1u << 21); r[21] &= ~(1u << 20);
        r[20] &= ~(1u << 22); r[22] &= ~(1u << 20);
        r[20] &= ~(1u << 23); r[23] &= ~(1u << 20);
        r[20] &= ~(1u << 24); r[24] &= ~(1u << 20);
        r[20] &= ~(1u << 25); r[25] &= ~(1u << 20);
        r[20] &= ~(1u << 26); r[26] &= ~(1u << 20);
        r[20] &= ~(1u << 27); r[27] &= ~(1u << 20);
        r[20] &= ~(1u << 28); r[28] &= ~(1u << 20);
        r[20] &= ~(1u << 29); r[29] &= ~(1u << 20);
        r[20] &= ~(1u << 30); r[30] &= ~(1u << 20);
        r[20] &= ~(1u << 31); r[31] &= ~(1u << 20);
        r[25] &= ~(1u << 25); r[25] &= ~(1u << 25);
        r[26] &= ~(1u << 26); r[26] &= ~(1u << 26);
        // SlimeJumpDestruct: crates (27) and debris (28) meet the walls (7), the climbable walls (15) and each other, and nothing else: the slime,
        // enemies, bullets and pickups pass through them, so the bot's route is the one it knows
        for (int k = 0; k < 32; k++)
        {
            if (k == Layers.Wall || k == Layers.Climbable || k == Layers.Crate || k == Layers.Debris) continue;
            r[Layers.Crate] &= ~(1u << k); r[k] &= ~(1u << Layers.Crate);
            r[Layers.Debris] &= ~(1u << k); r[k] &= ~(1u << Layers.Debris);
        }
        return r;
    }
}
