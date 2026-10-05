
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

// Ported from the Unity Enemy: patrol -> (vision cone + line of sight) -> chase -> attack, with
// hit reaction and hp. One script serves both kinds (EnemyCfg): worm = ground walker that spits,
// bat = flyer that hurts by contact. Differences: vision is evaluated directly (no sensor
// trigger), "blocked" uses a ray (no Rigidbody2D.Cast), randomness is Shared.Rand01, and a dead
// enemy is deactivated and brought back by Shared.ResetEnemies when the player respawns.
[Script(Order = 5), MaxInstances(24)]
class EnemyScript
{
    public Component Self;
    public int Index;
    public int Kind;
    public float InitX;
    public float InitY;
    Scene2D scene;
    Node node;
    Rigidbody2D body;
    uint wallMask;
    uint visionMask;
    bool started;
    float hp;
    bool chase;
    float destX;
    float destY;
    float stopTimeRemaining;
    bool prevAtDest;
    float prevToX;
    float prevToY;
    float hurtTimer;
    float faceX;
    float faceY;
    float shootTimer;
    float ex;
    float ey;

    public void Start()
    {
        scene = Scene2D.Current;
        node = Self.Node;
        body = scene.Find(node, ComponentKind.Rigidbody2D).Body;
        wallMask = (1u << Layers.Wall) | (1u << Layers.Climbable);
        visionMask = wallMask | (1u << Layers.Player);
        started = true;
        Restart();
    }

    // Brought back to life (SetActive(true)): back to the start state.
    public void OnEnable()
    {
        if (started) Restart();
    }

    void Restart()
    {
        hp = EnemyCfg.Hp(Kind);
        chase = false;
        hurtTimer = 0f;
        shootTimer = EnemyCfg.ShootInterval(Kind);
        faceX = 1f;
        faceY = 0f;
        destX = InitX;
        destY = InitY;
        if (!EnemyCfg.IsFlying(Kind)) destY = InitY - EnemyCfg.ColliderH(Kind) * 0.5f;
        prevAtDest = false;
        prevToX = 0f;
        prevToY = 0f;
        stopTimeRemaining = 0f;
    }

    public void FixedUpdate()
    {
        if (Shared.EnemyResetFlag(Index))
        {
            Shared.ClearEnemyReset(Index);
            node.SetPosition(InitX, InitY + 0.01f);
            body.SetVelocity(0f, 0f);
            Restart();
        }
        ex = node.WorldX();
        ey = node.WorldY();
        float dmg = Shared.TakeEnemyDamage(Index);
        if (dmg > 0f)
        {
            hp -= dmg;
            if (hp <= 0f)
            {
                Shared.SetEnemyAlive(Index, false);
                scene.SetActive(node, false);
                return;
            }
            if (!chase)
            {
                // turn toward whatever hit us for a moment
                faceX = Shared.EnemyHitDX(Index);
                faceY = Shared.EnemyHitDY(Index);
                if (!EnemyCfg.IsFlying(Kind)) { faceX = faceX > 0f ? 1f : -1f; faceY = 0f; }
                hurtTimer = EnemyCfg.LookToHurt(Kind);
            }
        }
        if (Shared.PlayerNode != null)
        {
            HandleMoving();
            if (chase) HandleAttacking();
        }
        Shared.PublishEnemy(Index, ex, ey, faceX, chase);
    }

    // touching an enemy hurts (the Unity enemies hurt through their weapon)
    public void OnCollisionBegin2D(Collision2D hit)
    {
        if (hit.Other.Self.Node.Tag == Shared.TagPlayer) Shared.DeathRequested = true;
    }

    static float Angle(float ax, float ay, float bx, float by)
    {
        float la = MathF.Sqrt(ax * ax + ay * ay);
        float lb = MathF.Sqrt(bx * bx + by * by);
        if (la < 0.0001f || lb < 0.0001f) return 0f;
        float c = (ax * bx + ay * by) / (la * lb);
        if (c > 1f) c = 1f;
        if (c < -1f) c = -1f;
        return MathF.Acos(c) * 57.29578f;
    }

    // Rays to the player's centre and four bounds corners: true if any reaches the player first.
    bool CanSeePlayer(float fx, float fy, float range)
    {
        float px = Shared.PlayerNode.WorldX();
        float py = Shared.PlayerNode.WorldY();
        float hw = Cfg.ColliderW * 0.5f;
        float hh = Cfg.ColliderH * 0.5f;
        for (int k = 0; k < 5; k++)
        {
            float tx = px;
            float ty = py;
            if (k == 1) { tx = px - hw; ty = py - hh; }
            if (k == 2) { tx = px + hw; ty = py + hh; }
            if (k == 3) { tx = px - hw; ty = py + hh; }
            if (k == 4) { tx = px + hw; ty = py - hh; }
            float dx = tx - fx;
            float dy = ty - fy;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < 0.0001f) return true;
            if (scene.Physics.Sim.Raycast(fx, fy, dx / d, dy / d, range, visionMask, false) == Shared.PlayerColliderIndex) return true;
        }
        return false;
    }

    bool CheckVision()
    {
        float px = Shared.PlayerNode.WorldX();
        float py = Shared.PlayerNode.WorldY();
        float dx = px - ex;
        float dy = py - ey;
        float range = EnemyCfg.VisionRange(Kind);
        float hw = (Cfg.ColliderW + EnemyCfg.ColliderW(Kind)) * 0.5f;
        float hh = (Cfg.ColliderH + EnemyCfg.ColliderH(Kind)) * 0.5f;
        if (MathF.Abs(dx) < hw && MathF.Abs(dy) < hh) return true;              // touching
        if (MathF.Sqrt(dx * dx + dy * dy) > range + 0.5f) return false;          // outside the vision circle
        if (Angle(faceX, faceY, dx, dy) > EnemyCfg.VisionAngle(Kind)) return false;
        return CanSeePlayer(ex, ey, range);
    }

    bool Blocked(float dx, float dy)
    {
        float m = MathF.Sqrt(dx * dx + dy * dy);
        if (m < 0.0001f) return false;
        int hit = scene.Physics.Sim.Raycast(ex, ey, dx / m, dy / m, m + EnemyCfg.ColliderW(Kind) * 0.5f, wallMask, false);
        if (hit < 0) return false;
        if (EnemyCfg.IsFlying(Kind)) return true;
        return scene.Physics.Sim.HitNY < 0.5f;                                   // ignore the floor we stand on
    }

    void Move(float mx, float my)
    {
        float len = MathF.Sqrt(mx * mx + my * my);
        if (len > 1f) { mx /= len; my /= len; }
        float speed = EnemyCfg.MoveSpeed(Kind);
        if (EnemyCfg.IsFlying(Kind))
        {
            body.SetVelocity(mx * speed, my * speed);
            if (mx != 0f || my != 0f) { faceX = mx; faceY = my; }
        }
        else
        {
            float sgn = 0f;
            if (mx > 0f) sgn = 1f;
            if (mx < 0f) sgn = -1f;
            body.SetVelocity(sgn * speed, body.VelocityY());
            if (sgn != 0f) { faceX = sgn; faceY = 0f; }
        }
    }

    void HandleMoving()
    {
        bool flying = EnemyCfg.IsFlying(Kind);
        float halfH = EnemyCfg.ColliderH(Kind) * 0.5f;
        if (chase)
        {
            float tx = Shared.PlayerNode.WorldX() - ex;
            float ty = Shared.PlayerNode.WorldY() - ey;
            float dist = MathF.Sqrt(tx * tx + ty * ty);
            if (dist >= EnemyCfg.ChaseStopMin(Kind) && dist <= EnemyCfg.ChaseStopMax(Kind)) Move(0f, 0f);
            else if (dist < EnemyCfg.ChaseStopMin(Kind)) Move(-tx, -ty);          // too close: back off
            else Move(tx, ty);
            // lost line of sight: stop chasing
            if (!CanSeePlayer(ex, ey, 200f)) chase = false;
            return;
        }
        if (CheckVision())
        {
            chase = true;
            hurtTimer = 0f;
            return;
        }
        if (hurtTimer > 0f)
        {
            hurtTimer -= Cfg.Dt;
            Move(0f, 0f);
            return;
        }
        float footY = ey - halfH;
        float toX = destX - ex;
        float toY = destY - ey;
        if (!flying) toY = destY - footY;
        float stop = EnemyCfg.PatrolStopDist(Kind);
        bool atDest = toX * toX + toY * toY <= stop * stop;
        if (atDest)
        {
            if (!prevAtDest) stopTimeRemaining = EnemyCfg.PatrolStopMin(Kind) + Shared.Rand01() * (EnemyCfg.PatrolStopMax(Kind) - EnemyCfg.PatrolStopMin(Kind));
            else
            {
                stopTimeRemaining -= Cfg.Dt;
                if (stopTimeRemaining <= 0f) PickDestination(flying, footY);
            }
            Move(0f, 0f);
        }
        else
            Move(toX, toY);
        prevAtDest = atDest;
    }

    // An unobstructed destination that turns us around by at least the configured angle
    // (the Unity code loops until it finds one; bounded here).
    void PickDestination(bool flying, float footY)
    {
        float toX = 0f;
        float toY = 0f;
        float range = EnemyCfg.PatrolRange(Kind);
        for (int tries = 0; tries < 8; tries++)
        {
            if (flying)
            {
                float a = Shared.Rand01() * 6.2831853f;
                destX = InitX + MathF.Cos(a) * range;
                destY = InitY + MathF.Sin(a) * range;
                toX = destX - ex;
                toY = destY - ey;
            }
            else
            {
                destX = InitX + (Shared.Rand01() * 2f - 1f) * range;
                toX = destX - ex;
                toY = destY - footY;
            }
            float minAngle = EnemyCfg.MinPatrolAngle(Kind);
            bool turned = true;
            if (prevToX != 0f || prevToY != 0f) turned = Angle(toX, toY, prevToX, prevToY) >= minAngle;
            if (!turned) continue;
            bool blocked = Blocked(toX, toY);
            if (!blocked) break;
        }
        prevToX = toX;
        prevToY = toY;
    }

    void HandleAttacking()
    {
        shootTimer -= Cfg.Dt;
        float interval = EnemyCfg.ShootInterval(Kind);
        if (interval <= 0f || shootTimer > 0f) return;
        float tx = Shared.PlayerNode.WorldX() - ex;
        float ty = Shared.PlayerNode.WorldY() - ey;
        float d = MathF.Sqrt(tx * tx + ty * ty);
        if (d >= EnemyCfg.AttackMin(Kind) && d <= EnemyCfg.AttackMax(Kind) && d > 0.001f)
        {
            shootTimer = interval;
            float sp = EnemyCfg.BulletSpeed(Kind);
            BulletScript.Spawn(ex, ey, tx / d * sp, ty / d * sp, EnemyCfg.BulletDamage(Kind), EnemyCfg.BulletLifetime(Kind), false);
        }
    }
}

// A bullet is just a node that moves itself: each step it rays ahead for walls and overlaps its
// targets (no rigidbody or trigger events, so it works against static geometry and uses no
// physics slots). Player bullets hurt enemies, enemy bullets hurt the player.
[Script(Order = 20), MaxInstances(48)]
class BulletScript
{
    public Component Self;
    float vx;
    float vy;
    float damage;
    float life;
    bool fromPlayer;
    Scene2D scene;
    Node node;
    uint wallMask;
    uint targetMask;

    public static void Spawn(float x, float y, float vx, float vy, float damage, float life, bool fromPlayer)
    {
        Scene2D sc = Scene2D.Current;
        Node n = sc.NewNode(null);
        if (n == null) return;
        n.SetPosition(x, y);
        if (fromPlayer) n.Tag = Shared.TagBulletPlayer;
        else n.Tag = Shared.TagBulletEnemy;
        BulletScript b = Scripts.AddBulletScript(n);
        if (b == null)
        {
            sc.Destroy(n);
            return;
        }
        b.vx = vx;
        b.vy = vy;
        b.damage = damage;
        b.life = life;
        b.fromPlayer = fromPlayer;
    }

    public void Start()
    {
        scene = Scene2D.Current;
        node = Self.Node;
        wallMask = (1u << Layers.Wall) | (1u << Layers.Climbable);
        if (fromPlayer) targetMask = 1u << Layers.Enemy;
        else targetMask = 1u << Layers.Player;
    }

    public void FixedUpdate()
    {
        float x = node.WorldX();
        float y = node.WorldY();
        float stepX = vx * Cfg.Dt;
        float stepY = vy * Cfg.Dt;
        float step = MathF.Sqrt(stepX * stepX + stepY * stepY);
        life -= Cfg.Dt;
        if (life <= 0f || step < 0.00001f)
        {
            scene.Destroy(node);
            return;
        }
        if (scene.Physics.Sim.Raycast(x, y, stepX / step, stepY / step, step + 0.15f, wallMask, false) >= 0)
        {
            scene.Destroy(node);
            return;
        }
        x += stepX;
        y += stepY;
        node.SetPosition(x, y);
        if (scene.Physics.Sim.OverlapCircle(x, y, 0.2f, targetMask, false) > 0)
        {
            Collider2D c = scene.Physics.ColliderByIndex(scene.Physics.Sim.OverlapResult(0));
            if (c != null)
            {
                int tag = c.Self.Node.Tag;
                if (tag == Shared.TagPlayer) Shared.DeathRequested = true;
                else if (tag >= Shared.TagEnemyBase) Shared.DamageEnemy(tag - Shared.TagEnemyBase, damage, vx, vy);
            }
            scene.Destroy(node);
        }
    }
}
