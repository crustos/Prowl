using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

// Ported from the Unity Player: move, variable-height jump, wall climbing, kill plane,
// death/respawn. (Lasso, blaster, enemies come in later steps.)
[Script(Order = 10), MaxInstances(1)]
class PlayerScript
{
    public Component Self;
    public bool IsGrounded;
    public bool IsJumping;
    public bool IsClimbing;
    public bool IsHittingWall;
    public float Facing;
    bool climbedSinceJumped;
    float prevX;
    float prevY;
    float shootTimer;
    float timeSinceJump;
    float jumpVel;
    float maxJumpDuration;
    float lockTimer;
    Rigidbody2D body;
    Node node;
    Scene2D scene;
    uint solidMask;
    uint climbMask;

    public void Start()
    {
        scene = Scene2D.Current;
        node = Self.Node;
        Shared.PlayerNode = node;
        body = scene.Find(node, ComponentKind.Rigidbody2D).Body;
        solidMask = (1u << Layers.Wall) | (1u << Layers.Climbable);
        climbMask = 1u << Layers.Climbable;
        Shared.SaveX = Level.SpawnX();
        Shared.SaveY = Level.SpawnY();
        prevX = node.WorldX();
        prevY = node.WorldY();
        Facing = 1f;
        // How long a full-height jump lasts (same estimate as the Unity Awake).
        float v = Cfg.JumpSpeed;
        maxJumpDuration = 0f;
        while (v > 0f)
        {
            v += Cfg.Gravity * Cfg.Dt;
            v *= 1f - Cfg.LinearDamping * Cfg.Dt;
            maxJumpDuration += Cfg.Dt;
        }
    }

    bool Probe(float cx, float cy, float halfW, float halfH, uint mask)
    {
        return scene.Physics.Sim.OverlapBox(cx, cy, halfW, halfH, 0f, mask, false) > 0;
    }

    public void FixedUpdate()
    {
        float x = node.WorldX();
        float y = node.WorldY();
        if (lockTimer > 0f)
        {
            lockTimer -= Cfg.Dt;
            Shared.DeathRequested = false;
            body.SetVelocity(0f, 0f);
            prevX = x;
            prevY = y;
            return;
        }
        if (Shared.DeathRequested || y < Level.MinY() - Cfg.KillPlaneMargin)
        {
            Death();
            return;
        }
        float halfW = Cfg.ColliderW * 0.5f;
        float halfH = Cfg.ColliderH * 0.5f;

        // Lasso: while the rope is taut the player swings on it. The Unity code makes the player a
        // child of the hook and rotates the hook; rotating the player's position about the hook point
        // by the same angle is the same motion, without parenting a physics body.
        if (Shared.LassoAttached)
        {
            float toX = Shared.LassoTipX - x;
            float toY = Shared.LassoTipY - y;
            float dist = MathF.Sqrt(toX * toX + toY * toY);
            float remaining = Shared.LassoLength - dist;
            if (remaining <= 0f && dist > 0.0001f && Shared.LassoLength > 0.0001f)
            {
                x += toX / dist * -remaining;                       // pull back onto the circle
                y += toY / dist * -remaining;
                float acc = Cfg.LassoSwingSpeed * MathF.Cos(Shared.SwingAngle * 0.017453292f);
                Shared.SwingW += acc * Cfg.Dt;
                Shared.SwingW *= 1f - Cfg.LinearDamping * Cfg.Dt;
                float dAngle = Shared.SwingW / Shared.LassoLength * Cfg.Dt;      // degrees
                Shared.SwingAngle += dAngle;
                float rx = x - Shared.LassoTipX;
                float ry = y - Shared.LassoTipY;
                float ca = MathF.Cos(dAngle * 0.017453292f);
                float sa = MathF.Sin(dAngle * 0.017453292f);
                x = Shared.LassoTipX + rx * ca - ry * sa;
                y = Shared.LassoTipY + rx * sa + ry * ca;
                node.SetPosition(x, y);
            }
        }
        float vy = body.VelocityY();

        // blaster: fires toward the aim point (a stand-in for the Unity weapon; no knockback)
        shootTimer -= Cfg.Dt;
        if (InputState.Attack && shootTimer <= 0f)
        {
            float ax = InputState.AimX - x;
            float ay = InputState.AimY - y;
            float al = MathF.Sqrt(ax * ax + ay * ay);
            if (al < 0.001f) { ax = Facing; ay = 0f; al = 1f; }
            shootTimer = Cfg.ShootCooldown;
            BulletScript.Spawn(x, y, ax / al * Cfg.BulletSpeed, ay / al * Cfg.BulletSpeed, Cfg.BulletDamage, Cfg.BulletLifetime, true);
        }

        // sense the world (probes, not contact normals)
        IsGrounded = Probe(x, y - halfH - 0.03f, halfW * 0.9f, 0.05f, solidMask);

        // moving
        float moveInput = InputState.Move;
        bool jumpInput = InputState.Jump;
        IsHittingWall = false;
        if (moveInput != 0f)
        {
            float side = moveInput > 0f ? 1f : -1f;
            IsHittingWall = Probe(x + side * (halfW + 0.03f), y, 0.03f, halfH * 0.9f, solidMask);
            Facing = side;
        }
        float vx = 0f;
        if (!IsHittingWall) vx = moveInput * Cfg.MoveSpeed;

        // jumping (releasing early cuts the jump short)
        if (!IsClimbing)
        {
            if (jumpInput && IsGrounded && !IsJumping)
            {
                IsJumping = true;
                vy += Cfg.JumpSpeed;
                jumpVel = Cfg.JumpSpeed;
                timeSinceJump = 0f;
            }
            else if (IsJumping)
            {
                timeSinceJump += Cfg.Dt;
                if (!jumpInput && timeSinceJump < maxJumpDuration)
                    vy = StopJump(vy);
                else if (vy <= 0f)
                {
                    IsJumping = false;
                    climbedSinceJumped = false;
                }
            }
        }

        // climbing: touching a Climbable surface; jump climbs, otherwise slide down slowly
        bool wasClimbing = IsClimbing;
        IsClimbing = Probe(x, y, (Cfg.ColliderW + 0.3f) * 0.5f, Cfg.ColliderH * 0.3f, climbMask);
        if (IsClimbing)
        {
            climbedSinceJumped = true;
            if (jumpInput)
            {
                IsJumping = true;
                vy = Cfg.ClimbSpeed;
                timeSinceJump = 0f;
            }
            else
                vy = -Cfg.ClimbFallSpeed;
        }
        else if (wasClimbing && jumpInput)
            vy = Cfg.ClimbSpeed;                     // pop over the lip

        // gravity is applied here (the body has GravityScale 0) so climbing can switch it off
        if (!IsClimbing) vy += Cfg.Gravity * Cfg.Dt;
        jumpVel += Cfg.Gravity * Cfg.Dt;
        jumpVel *= 1f - Cfg.LinearDamping * Cfg.Dt;
        body.SetVelocity(vx, vy);
        Shared.PlayerGrounded = IsGrounded;
        Shared.PlayerClimbing = IsClimbing;
        Shared.PlayerJumping = IsJumping;
        Shared.PlayerFacing = Facing;
        float cx = node.WorldX();
        float cy = node.WorldY();
        Shared.PlayerLastMoveX = cx - prevX;
        Shared.PlayerLastMoveY = cy - prevY;
        prevX = cx;
        prevY = cy;
    }

    float StopJump(float vy)
    {
        if (climbedSinceJumped) vy = 0f;
        else vy -= jumpVel;
        IsJumping = false;
        climbedSinceJumped = false;
        jumpVel = 0f;
        return vy;
    }

    void Death()
    {
        Shared.DeathRequested = false;
        Shared.Deaths++;
        lockTimer = Cfg.RespawnDelay;
        IsJumping = false;
        IsClimbing = false;
        climbedSinceJumped = false;
        jumpVel = 0f;
        Shared.LassoDetach();
        node.SetPosition(Shared.SaveX, Shared.SaveY);
        body.SetVelocity(0f, 0f);
        prevX = Shared.SaveX;
        prevY = Shared.SaveY;
        Shared.ResetWorld();
    }
}
