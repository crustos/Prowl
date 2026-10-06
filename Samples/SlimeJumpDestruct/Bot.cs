using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

// (SlimeJumpDestruct: it also shoots a crate it can see within 9 units, and a wall of dirt in its way, standing still to do it.)
// A reactive test driver standing in for a keyboard and mouse: runs toward the goal, jumps walls
// and gaps, holds jump to climb, stops to shoot any enemy it can see nearby, and swings on the
// level's anchor hints to cross gaps a jump cannot. It uses the same probes as the player.
[Script(Order = 0), MaxInstances(1)]
class BotScript
{
    public Component Self;
    Scene2D scene;
    uint solidMask;
    float releaseAngle;

    public void Start()
    {
        scene = Scene2D.Current;
        solidMask = (1u << Layers.Wall) | (1u << Layers.Climbable);
        releaseAngle = 50f;
    }

    bool Probe(float cx, float cy, float halfW, float halfH)
    {
        return scene.Physics.Sim.OverlapBox(cx, cy, halfW, halfH, 0f, solidMask, false) > 0;
    }

    public void FixedUpdate()
    {
        Node pn = Shared.PlayerNode;
        if (pn == null) return;
        float x = pn.WorldX();
        float y = pn.WorldY();
        float halfW = Cfg.ColliderW * 0.5f;
        float halfH = Cfg.ColliderH * 0.5f;

        // the nearest living enemy that is close and not behind a wall
        bool engage = false;
        float bestD = 11f;
        float aimX = 0f;
        float aimY = 0f;
        for (int i = 0; i < Shared.EnemyCount(); i++)
        {
            if (!Shared.EnemyAlive(i)) continue;
            float dx = Shared.EnemyX(i) - x;
            float dy = Shared.EnemyY(i) - y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d >= bestD || d < 0.001f) continue;
            if (scene.Physics.Sim.Raycast(x, y, dx / d, dy / d, d, solidMask, false) >= 0) continue;
            bestD = d;
            aimX = Shared.EnemyX(i);
            aimY = Shared.EnemyY(i);
            engage = true;
        }
        // crates in sight, within reach of a shot: shoot them too (a crate next to an enemy takes it with it)
        for (int i = 0; i < Destruct.CrateCount(); i++)
        {
            if (!Destruct.CrateLive(scene, i)) continue;
            float dx = Destruct.CrateX(i) - x;
            float dy = Destruct.CrateY(i) - y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d >= 9f || d >= bestD || d < 0.001f) continue;
            if (scene.Physics.Sim.Raycast(x, y, dx / d, dy / d, d, solidMask, false) >= 0) continue;
            bestD = d;
            aimX = Destruct.CrateX(i);
            aimY = Destruct.CrateY(i);
            engage = true;
        }
        // a wall of dirt right ahead: dig
        if (!engage && Destruct.DirtAhead(x, y))
        {
            engage = true;
            aimX = x + 3f;
            aimY = y;
        }
        InputState.Attack = engage;
        InputState.AimX = aimX;
        InputState.AimY = aimY;

        float dir = 0f;
        if (Level.GoalX() - x > 0.3f) dir = 1f;
        else if (Level.GoalX() - x < -0.3f) dir = -1f;
        // stand still to shoot (unless climbing or in the air)
        if (engage && Shared.PlayerGrounded && !Shared.PlayerClimbing) dir = 0f;

        // lasso: hook the nearest anchor hint ahead while airborne; let go once swung far enough forward
        bool lasso = false;
        if (Shared.LassoAttached)
        {
            lasso = true;
            float rx = x - Shared.LassoTipX;
            float ry = Shared.LassoTipY - y;
            float forward = MathF.Atan2(rx, ry) * 57.29578f;      // degrees ahead of straight down
            if (forward >= releaseAngle) lasso = false;
        }
        else if (!Shared.PlayerGrounded && !Shared.PlayerClimbing && dir > 0f)
        {
            float bestA = 6.5f;
            for (int i = 0; i < Level.AnchorCount; i++)
            {
                float ax = Level.Anchor(i, 0);
                float ay = Level.Anchor(i, 1);
                if (ax < x + 1.5f || ay < y + 1f) continue;
                float dx = ax - x;
                float dy = ay - y;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d >= bestA) continue;
                bestA = d;
                lasso = true;
                InputState.AimX = ax;
                InputState.AimY = ay + 4f;        // aim well up into the ceiling: the hook rides along with a moving player
            }
        }
        InputState.Lasso = lasso;
        InputState.ChangeLassoLength = 0;

        bool jump = false;
        if (Shared.LassoAttached)
            jump = false;
        else if (Shared.PlayerClimbing)
            jump = true;                                   // hold to climb
        else if (Shared.PlayerGrounded)
        {
            bool wallAhead = Probe(x + dir * (halfW + 0.25f), y, 0.15f, halfH * 0.6f);
            bool floorAhead = Probe(x + dir * (halfW + 0.2f), y - halfH - 0.15f, 0.1f, 0.12f);
            if (dir != 0f && (wallAhead || !floorAhead)) jump = true;
        }
        else if (Shared.PlayerJumping)
            jump = true;                                   // full-height jumps
        // an arrow coming straight at us along the floor: jump it
        if (Shared.PlayerGrounded && !Shared.PlayerClimbing && !Shared.LassoAttached)
        {
            for (int i = 0; i < scene.NodeHighWater; i++)
            {
                Node a = scene.NodeAt(i);
                if (a == null || !a.Alive || a.Destroyed || a.Tag != Shared.TagArrow) continue;
                float adx = a.WorldX() - x;
                float ady = a.WorldY() - y;
                if (MathF.Abs(ady) < 0.9f && MathF.Abs(adx) < 6.5f && adx * MathF.Cos(a.WorldAngle()) < 0f) jump = true;
            }
        }
        InputState.Move = dir;
        InputState.Jump = jump;
    }
}
