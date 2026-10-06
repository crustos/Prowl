// GENERATED from gen_slime.CONFIG (worm = kind 0, bat = kind 1)
static class EnemyCfg
{
    public static bool IsFlying(int kind) { return kind == 1; }
    public static float Hp(int kind) { if (kind == 1) return 2f; return 3f; }
    public static float MoveSpeed(int kind) { if (kind == 1) return 7.5f; return 3.5f; }
    public static float VisionRange(int kind) { if (kind == 1) return 10f; return 9f; }
    public static float VisionAngle(int kind) { if (kind == 1) return 90f; return 90f; }
    public static float PatrolRange(int kind) { if (kind == 1) return 4f; return 5f; }
    public static float PatrolStopDist(int kind) { if (kind == 1) return 0.5f; return 0.3f; }
    public static float MinPatrolAngle(int kind) { if (kind == 1) return 90f; return 90f; }
    public static float PatrolStopMin(int kind) { if (kind == 1) return 0.25f; return 0.25f; }
    public static float PatrolStopMax(int kind) { if (kind == 1) return 0.75f; return 0.75f; }
    public static float LookToHurt(int kind) { if (kind == 1) return 0.5f; return 0.5f; }
    public static float AttackMin(int kind) { if (kind == 1) return 0f; return 3f; }
    public static float AttackMax(int kind) { if (kind == 1) return 0f; return 12f; }
    public static float ChaseStopMin(int kind) { if (kind == 1) return 0f; return 3f; }
    public static float ChaseStopMax(int kind) { if (kind == 1) return 0f; return 6f; }
    public static float ShootInterval(int kind) { if (kind == 1) return 0f; return 1.6f; }
    public static float BulletSpeed(int kind) { if (kind == 1) return 0f; return 10f; }
    public static float BulletDamage(int kind) { if (kind == 1) return 0f; return 1f; }
    public static float BulletLifetime(int kind) { if (kind == 1) return 0f; return 3f; }
    public static float ContactDamage(int kind) { if (kind == 1) return 1f; return 1f; }
    public static float ColliderW(int kind) { if (kind == 1) return 1f; return 1.1f; }
    public static float ColliderH(int kind) { if (kind == 1) return 0.6f; return 0.6f; }
    public static float GravityScale(int kind) { if (kind == 1) return 0f; return 1f; }
}
