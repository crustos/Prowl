// GENERATED from the level data
static class Level
{
    static float spawnX, spawnY, goalX, goalY, minY, minX, maxX, maxY;
    public static float MinX() { return minX; }
    public static float MaxX() { return maxX; }
    public static float MaxY() { return maxY; }
    public static float SpawnX() { return spawnX; }
    public static float SpawnY() { return spawnY; }
    public static float GoalX() { return goalX; }
    public static float GoalY() { return goalY; }
    public static float MinY() { return minY; }
    static float[] Walls;
    public static int WallCount;
    public static float Wall(int i, int k) { return At(Walls, i * 4 + k); }
    static float[] Climbs;
    public static int ClimbCount;
    public static float Climb(int i, int k) { return At(Climbs, i * 4 + k); }
    static float[] Spikes;
    public static int SpikeCount;
    public static float Spike(int i, int k) { return At(Spikes, i * 4 + k); }
    static float[] Saves;
    public static int SaveCount;
    public static float Save(int i, int k) { return At(Saves, i * 2 + k); }
    static float[] Gems;
    public static int GemCount;
    public static float Gem(int i, int k) { return At(Gems, i * 2 + k); }
    static float[] Anchors;
    public static int AnchorCount;
    public static float Anchor(int i, int k) { return At(Anchors, i * 2 + k); }
    static float[] Crumbles;
    public static int CrumblyCount;
    public static float CrumblyTile(int i, int k) { return At(Crumbles, i * 4 + k); }
    static float[] Shooters;
    public static int ShooterCount;
    public static float Shooter(int i, int k) { return At(Shooters, i * 4 + k); }
    static float[] Enemies;
    public static int EnemyCount;
    public static int EnemyKind(int i) { return (int)At(Enemies, i * 3); }
    public static float EnemyX(int i) { return At(Enemies, i * 3 + 1); }
    public static float EnemyY(int i) { return At(Enemies, i * 3 + 2); }
    static float At(float[] a, int i) { return a[i]; }
    static void Set4(float[] a, int i, float x, float y, float w, float h) { a[i * 4] = x; a[i * 4 + 1] = y; a[i * 4 + 2] = w; a[i * 4 + 3] = h; }
    static void Set2(float[] a, int i, float x, float y) { a[i * 2] = x; a[i * 2 + 1] = y; }

    public static void Init()
    {
        spawnX = 3.5f; spawnY = 4.575f;
        goalX = 61.5f; goalY = 14f;
        minY = 0f;
        minX = 0f; maxX = 64f; maxY = 20f;
        Walls = new float[32]; WallCount = 8;
        Set4(Walls, 0, 0f, 0f, 64f, 2f);
        Set4(Walls, 1, 0f, 2f, 24f, 2f);
        Set4(Walls, 2, 0f, 4f, 1f, 15f);
        Set4(Walls, 3, 0f, 19f, 64f, 1f);
        Set4(Walls, 4, 14f, 4f, 7f, 2f);
        Set4(Walls, 5, 31f, 2f, 33f, 2f);
        Set4(Walls, 6, 57f, 4f, 7f, 9f);
        Set4(Walls, 7, 63f, 13f, 1f, 6f);
        Climbs = new float[4]; ClimbCount = 1;
        Set4(Climbs, 0, 56f, 4f, 1f, 9f);
        Spikes = new float[4]; SpikeCount = 1;
        Set4(Spikes, 0, 24f, 2f, 7f, 1f);
        Saves = new float[4]; SaveCount = 2;
        Set2(Saves, 0, 8.5f, 4f);
        Set2(Saves, 1, 50.5f, 4f);
        Gems = new float[6]; GemCount = 3;
        Set2(Gems, 0, 27.5f, 7.5f);
        Set2(Gems, 1, 45.5f, 6.5f);
        Set2(Gems, 2, 58.5f, 15.5f);
        Anchors = new float[2]; AnchorCount = 0;
        Crumbles = new float[4]; CrumblyCount = 0;
        Shooters = new float[4]; ShooterCount = 0;
        Enemies = new float[6]; EnemyCount = 2;
        Enemies[0] = 0; Enemies[1] = 41.5f; Enemies[2] = 4.3f;
        Enemies[3] = 1; Enemies[4] = 60.5f; Enemies[5] = 16.5f;
    }

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
        return r;
    }
}
