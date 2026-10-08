using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

// The driver of the slime (a stand-in for a keyboard and mouse), going down the shaft room by room:
//   * it shoots a crate or a boulder it can see within 9 units (they are the things to break), and a silo's plug (a wall of rock that holds sand or water back);
//   * otherwise, if the floor has an opening it walks to the nearest one and drops through; if the floor is whole it walks to where it digs (Level.DigX) and shoots straight
//     down until it is through;
//   * in the last room it walks to the goal.
// It uses the same probes as the player: it jumps what is in its way.
[Script(Order = 0), MaxInstances(1)]
class BotScript
{
    public Component Self;
    Scene2D scene;
    uint solidMask;

    public void Start()
    {
        scene = Scene2D.Current;
        solidMask = (1u << Layers.Wall) | (1u << Layers.Climbable);
    }

    bool Probe(float cx, float cy, float halfW, float halfH)
    {
        return scene.Physics.Sim.OverlapBox(cx, cy, halfW, halfH, 0f, solidMask, false) > 0;
    }

    // is there ground straight below within d units (its feet may be wedged in a crater, not quite on it)?
    static bool GroundWithin(float x, float y, float d)
    {
        for (float k = 0.3f; k <= d; k += 0.1f)
            if (Destruct.SolidAt(x, y - k) || Destruct.SolidAt(x - 0.5f, y - k) || Destruct.SolidAt(x + 0.5f, y - k)) return true;
        return false;
    }

    // is there a clear line to a point (stopping a little short of it: the point may be inside the rock that is to be shot)?
    bool Clear(float x, float y, float tx, float ty, float short_)
    {
        float dx = tx - x;
        float dy = ty - y;
        float d = MathF.Sqrt(dx * dx + dy * dy) - short_;
        if (d <= 0.01f) return true;
        float l = d + short_;
        return scene.Physics.Sim.Raycast(x, y, dx / l, dy / l, d, solidMask, false) < 0;
    }

    public void FixedUpdate()
    {
        if (!InputState.BotOn) return;
        Node pn = Shared.PlayerNode;
        if (pn == null) return;
        float x = pn.WorldX();
        float y = pn.WorldY();
        float halfW = Cfg.ColliderW * 0.5f;
        float halfH = Cfg.ColliderH * 0.5f;
        int room = Level.RoomOfY(y);
        if (room < 0) room = 0;

        // the nearest thing to shoot: a crate or boulder in sight, else a plug of this room
        bool engage = false;
        float bestD = 9f;
        float aimX = 0f;
        float aimY = 0f;
        for (int i = 0; i < Destruct.ItemCount(); i++)
        {
            if (!Destruct.ItemLive(scene, i)) continue;
            float dx = Destruct.ItemX(i) - x;
            float dy = Destruct.ItemY(i) - y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d >= bestD || d < 0.001f) continue;
            if (!Clear(x, y, Destruct.ItemX(i), Destruct.ItemY(i), 0f)) continue;
            bestD = d;
            aimX = Destruct.ItemX(i);
            aimY = Destruct.ItemY(i);
            engage = true;
        }
        if (!engage)
        {
            for (int i = 0; i < Destruct.PlugCount(); i++)
            {
                if (Destruct.PlugRoom(i) != room || !Destruct.PlugClosed(i)) continue;
                float dx = Destruct.PlugX(i) - x;
                float dy = Destruct.PlugY(i) - y;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d >= bestD || d < 0.001f) continue;
                if (!Clear(x, y, Destruct.PlugX(i), Destruct.PlugY(i), 1.2f)) continue;
                bestD = d;
                aimX = Destruct.PlugX(i);
                aimY = Destruct.PlugY(i);
                engage = true;
            }
        }

        // where to go: through an opening in the floor, or to the spot to dig one, or (last room) to the goal
        float target = Level.GoalX();
        bool digging = false;
        if (room < Level.Rooms - 1)
        {
            float hole = Destruct.HoleX(room, x);
            if (hole >= 0f) target = hole;
            else
            {
                target = Level.DigX(room);
                if (MathF.Abs(x - target) < 0.35f && (Shared.PlayerGrounded || GroundWithin(x, y, 1.3f))) digging = true;       // (it may be wedged in a crater, with its feet not quite on the ground)
            }
        }
        if (!engage && digging)
        {
            engage = true;
            aimX = x;
            aimY = y - 4f;
        }
        InputState.Attack = engage;
        InputState.AimX = aimX;
        InputState.AimY = aimY;
        InputState.Lasso = false;
        InputState.ChangeLassoLength = 0;

        float dir = 0f;
        if (target - x > 0.3f) dir = 1f;
        else if (target - x < -0.3f) dir = -1f;
        // stand still to shoot (not in the air, and not before it has anything to dig through)
        if (engage && (Shared.PlayerGrounded || digging) && !Shared.PlayerClimbing && (digging || bestD < 9f)) dir = 0f;

        bool jump = false;
        if (Shared.PlayerGrounded && !Shared.PlayerClimbing)
        {
            bool wallAhead = Probe(x + dir * (halfW + 0.25f), y, 0.15f, halfH * 0.6f);
            if (dir != 0f && wallAhead) jump = true;
        }
        else if (Shared.PlayerJumping)
            jump = true;                                   // full-height jumps
        InputState.Move = dir;
        InputState.Jump = jump;
    }
}
