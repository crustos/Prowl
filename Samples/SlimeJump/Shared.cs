using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

// All state that scripts share. Scripts write to it and read from it; it never refers to a
// script type, so the translator has no dependency cycle to order (a static variable must be
// declared before use in the generated C). Static arrays are only touched through helpers
// that take the array as a parameter (CC# cannot subscript a static array field directly).
//
// Data flow is one way: a script that wants to change something it does not own (damage an
// enemy, kill the player) writes a request here; the owner reads and applies it next step.
static class Shared
{
    public const int TagPlayer = 1;
    public const int TagWall = 2;
    public const int TagHazard = 3;
    public const int TagSavePoint = 4;
    public const int TagGem = 5;
    public const int TagGoal = 6;
    public const int TagBulletPlayer = 7;
    public const int TagBulletEnemy = 8;
    public const int TagEnemyBase = 100;        // enemy i has Tag TagEnemyBase + i

    public static bool Won;
    public static int Deaths;
    public static int Gems;
    public static bool DeathRequested;          // set by hazards, enemies, bullets; PlayerScript acts on it next step
    public static float SaveX;
    public static float SaveY;
    public static Node PlayerNode;
    public static int PlayerColliderIndex;
    public static bool PlayerGrounded;          // published by PlayerScript for the bot / renderer
    public static bool PlayerClimbing;
    public static bool PlayerJumping;
    public static float PlayerFacing = 1f;
    // ---- lasso: written by LassoScript, read by PlayerScript (the swing) and the renderer ----
    public static bool LassoActive;             // a rope is out (flying or attached)
    public static bool LassoAttached;
    public static float LassoOffX;              // while flying: the tip's offset from the player (as in the Unity Lasso)
    public static float LassoOffY;
    public static float LassoDirX;
    public static float LassoDirY;
    public static float LassoTipX;              // the hook's world position
    public static float LassoTipY;
    public static float LassoLength;
    public static float SwingW;                 // swingAngularVelocity of the Unity Player
    public static float SwingAngle;             // degrees
    public static float PlayerLastMoveX;        // displacement over the last step, for the momentum on release
    public static float PlayerLastMoveY;
    public static void LassoDetach() { LassoActive = false; LassoAttached = false; }

    static Node[] gemNodes;
    static bool[] gemPending;
    static bool[] gemCollected;
    static bool[] saveTouched;
    static int gemCount;

    static Node[] enemyNodes;
    static float[] enemyX;
    static float[] enemyY;
    static float[] enemyFace;
    static float[] enemyDamage;
    static float[] enemyHitDX;
    static float[] enemyHitDY;
    static float[] enemyInitX;
    static float[] enemyInitY;
    static int[] enemyKind;
    static bool[] enemyAlive;
    static bool[] enemyChase;
    static bool[] enemyReset;
    static int enemyCount;
    static uint rng = 2463534242u;

    static void SetB(bool[] a, int i, bool v) { a[i] = v; }
    static bool GetB(bool[] a, int i) { return a[i]; }
    static void SetN(Node[] a, int i, Node v) { a[i] = v; }
    static Node GetN(Node[] a, int i) { return a[i]; }
    static void SetF(float[] a, int i, float v) { a[i] = v; }
    static float GetF(float[] a, int i) { return a[i]; }
    static void SetI(int[] a, int i, int v) { a[i] = v; }
    static int GetI(int[] a, int i) { return a[i]; }

    // deterministic random numbers (xorshift32): the same on .NET and in C
    public static float Rand01()
    {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        return (float)(rng & 0xFFFFFFu) / 16777216f;
    }

    // ---- gems / savepoints ----
    public static void InitGems(int count)
    {
        gemCount = count;
        gemNodes = new Node[count + 1];
        gemPending = new bool[count + 1];
        gemCollected = new bool[count + 1];
    }

    public static void RegisterGem(int i, Node n) { SetN(gemNodes, i, n); }

    public static void InitSaves(int count) { saveTouched = new bool[count + 1]; }
    public static void TouchSave(int i) { SetB(saveTouched, i, true); }
    public static bool SaveTouched(int i) { return GetB(saveTouched, i); }

    // 0 = on the map, 1 = picked up but not yet saved, 2 = saved (drawn as a faint ghost)
    public static int GemState(int i)
    {
        if (GetB(gemCollected, i)) return 2;
        if (GetB(gemPending, i)) return 1;
        return 0;
    }

    // Picking a gem up hides it; it only counts once a savepoint is touched.
    public static void PickUpGem(int i)
    {
        SetB(gemPending, i, true);
        Scene2D.Current.SetActive(GetN(gemNodes, i), false);
    }

    public static void CommitGems()
    {
        for (int i = 0; i < gemCount; i++)
            if (GetB(gemPending, i))
            {
                SetB(gemPending, i, false);
                SetB(gemCollected, i, true);
                Gems++;
            }
    }

    // On death: gems picked up since the last savepoint come back.
    public static void ResetGems()
    {
        for (int i = 0; i < gemCount; i++)
            if (GetB(gemPending, i))
            {
                SetB(gemPending, i, false);
                Scene2D.Current.SetActive(GetN(gemNodes, i), true);
            }
    }

    // ---- enemies ----
    public static void InitEnemies(int count)
    {
        enemyCount = count;
        enemyNodes = new Node[count + 1];
        enemyX = new float[count + 1];
        enemyY = new float[count + 1];
        enemyFace = new float[count + 1];
        enemyDamage = new float[count + 1];
        enemyHitDX = new float[count + 1];
        enemyHitDY = new float[count + 1];
        enemyInitX = new float[count + 1];
        enemyInitY = new float[count + 1];
        enemyKind = new int[count + 1];
        enemyAlive = new bool[count + 1];
        enemyChase = new bool[count + 1];
        enemyReset = new bool[count + 1];
    }

    public static void RegisterEnemy(int i, Node n, int kind, float x, float y)
    {
        SetN(enemyNodes, i, n);
        SetI(enemyKind, i, kind);
        SetF(enemyInitX, i, x);
        SetF(enemyInitY, i, y);
        SetF(enemyX, i, x);
        SetF(enemyY, i, y);
        SetF(enemyFace, i, 1f);
        SetB(enemyAlive, i, true);
    }

    public static int EnemyCount() { return enemyCount; }
    public static bool EnemyAlive(int i) { return GetB(enemyAlive, i); }
    public static void SetEnemyAlive(int i, bool a) { SetB(enemyAlive, i, a); }
    public static int EnemyKind(int i) { return GetI(enemyKind, i); }
    public static float EnemyX(int i) { return GetF(enemyX, i); }
    public static float EnemyY(int i) { return GetF(enemyY, i); }
    public static float EnemyFace(int i) { return GetF(enemyFace, i); }
    public static bool EnemyChase(int i) { return GetB(enemyChase, i); }
    public static float EnemyHitDX(int i) { return GetF(enemyHitDX, i); }
    public static float EnemyHitDY(int i) { return GetF(enemyHitDY, i); }

    public static void PublishEnemy(int i, float x, float y, float face, bool chase)
    {
        SetF(enemyX, i, x);
        SetF(enemyY, i, y);
        SetF(enemyFace, i, face);
        SetB(enemyChase, i, chase);
    }

    // A bullet hit enemy i: the enemy applies it on its next step.
    public static void DamageEnemy(int i, float dmg, float dx, float dy)
    {
        SetF(enemyDamage, i, GetF(enemyDamage, i) + dmg);
        SetF(enemyHitDX, i, dx);
        SetF(enemyHitDY, i, dy);
    }

    public static float TakeEnemyDamage(int i)
    {
        float d = GetF(enemyDamage, i);
        SetF(enemyDamage, i, 0f);
        return d;
    }

    public static bool EnemyResetFlag(int i) { return GetB(enemyReset, i); }
    public static void ClearEnemyReset(int i) { SetB(enemyReset, i, false); }

    // On player respawn every enemy returns to its start, dead ones come back.
    public static void ResetEnemies()
    {
        for (int i = 0; i < enemyCount; i++)
        {
            SetB(enemyReset, i, true);
            SetF(enemyDamage, i, 0f);
            if (!GetB(enemyAlive, i))
            {
                Node n = GetN(enemyNodes, i);
                n.SetPosition(GetF(enemyInitX, i), GetF(enemyInitY, i));
                SetB(enemyAlive, i, true);
                Scene2D.Current.SetActive(n, true);
            }
        }
    }

    // ---- bullets ----
    public static void ResetBullets()
    {
        Scene2D scene = Scene2D.Current;
        for (int i = 0; i < scene.NodeHighWater; i++)
        {
            Node b = scene.NodeAt(i);
            if (b != null && b.Alive && !b.Destroyed && (b.Tag == TagBulletPlayer || b.Tag == TagBulletEnemy))
                scene.Destroy(b);
        }
    }

    public static void ResetWorld()
    {
        ResetGems();
        ResetEnemies();
        ResetBullets();
    }
}
