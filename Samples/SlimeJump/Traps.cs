
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

// Ported from the original DissolveOnHit (Crumbly Wall prefab): the first collision starts a timer;
// the wall fades over CrumblyDissolveTime and then deactivates. It comes back on respawn.
[Script(Order = 6), MaxInstances(96)]
class CrumblyScript
{
    public Component Self;
    public int Index;
    Scene2D scene;
    Node node;
    bool hit;
    float timer;

    public void Start()
    {
        scene = Scene2D.Current;
        node = Self.Node;
    }

    public void OnCollisionBegin2D(Collision2D contact)
    {
        if (hit) return;
        hit = true;
        timer = Cfg.CrumblyDissolveTime;
    }

    public void FixedUpdate()
    {
        if (Shared.CrumblyResetFlag(Index))
        {
            Shared.ClearCrumblyReset(Index);
            hit = false;
            timer = 0f;
        }
        if (!hit) return;
        timer -= Cfg.Dt;
        if (timer <= 0f)
        {
            Shared.SetCrumblyAlpha(Index, 0f);
            scene.SetActive(node, false);
        }
        else
            Shared.SetCrumblyAlpha(Index, timer / Cfg.CrumblyDissolveTime);
    }
}

// Ported from the original ShooterTrap + Arrow Shooter prefab: every step it casts a ray along its
// facing; when the first thing hit is the player and a second has passed since the last shot (the
// 1s shoot animation) it fires an arrow ("Aim Where Facing": one arrow along its facing).
[Script(Order = 7), MaxInstances(32)]
class ShooterScript
{
    public Component Self;
    public float DirX;
    public float DirY;
    Scene2D scene;
    Node node;
    uint mask;
    float sinceShot;

    public void Start()
    {
        scene = Scene2D.Current;
        node = Self.Node;
        mask = (1u << Layers.Wall) | (1u << Layers.Climbable) | (1u << Layers.Player);
        sinceShot = Cfg.ShooterCooldown;
    }

    public void FixedUpdate()
    {
        sinceShot += Cfg.Dt;
        if (Shared.DeathRequested) sinceShot = Cfg.ShooterCooldown;      // ready again after a respawn
        // start just outside our own tile, which is solid
        float ox = node.WorldX() + DirX * 0.55f;
        float oy = node.WorldY() + DirY * 0.55f;
        int hit = scene.Physics.Sim.Raycast(ox, oy, DirX, DirY, 500f, mask, false);
        if (hit == Shared.PlayerColliderIndex && sinceShot >= Cfg.ShooterCooldown)
        {
            sinceShot = 0f;
            BulletScript.SpawnArrow(ox, oy, DirX * Cfg.ArrowSpeed, DirY * Cfg.ArrowSpeed, Cfg.ArrowDamage, Cfg.ArrowLifetime);
        }
    }
}
