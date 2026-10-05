
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

// Ported from the Unity Lasso: right-click shoots a hook toward the cursor; it flies (carried along
// with the player, as in the original) until it hits a Wall/Climbable surface, then the player swings
// (see PlayerScript). While attached W/S reel the rope in/out; letting go of the button releases it and
// keeps the swing's momentum. Dropped: moving anchors (walls here are static), Slippery walls.
[Script(Order = 11), MaxInstances(1)]
class LassoScript
{
    public Component Self;
    Scene2D scene;
    Node node;
    Rigidbody2D body;
    uint wallMask;
    bool prevInput;

    public void Start()
    {
        scene = Scene2D.Current;
        node = Self.Node;
        body = scene.Find(node, ComponentKind.Rigidbody2D).Body;
        wallMask = (1u << Layers.Wall) | (1u << Layers.Climbable);
    }

    public void FixedUpdate()
    {
        float px = node.WorldX();
        float py = node.WorldY();
        bool input = InputState.Lasso;
        if (input && !prevInput)
        {
            float ax = InputState.AimX - px;
            float ay = InputState.AimY - py;
            float al = MathF.Sqrt(ax * ax + ay * ay);
            if (al < 0.001f) { ax = 0f; ay = 1f; al = 1f; }
            Shared.LassoDirX = ax / al;
            Shared.LassoDirY = ay / al;
            Shared.LassoOffX = 0f;
            Shared.LassoOffY = 0f;
            Shared.LassoTipX = px;
            Shared.LassoTipY = py;
            Shared.LassoLength = 0f;
            Shared.LassoAttached = false;
            Shared.LassoActive = true;
        }
        else if (!input && prevInput && Shared.LassoActive)
            Release();
        if (Shared.LassoActive)
        {
            if (!Shared.LassoAttached) Fly(px, py);
            else Reel(px, py);
        }
        prevInput = input;
    }

    // The tip advances relative to the player; a surface along this step's path attaches it.
    void Fly(float px, float py)
    {
        float endX = Shared.LassoOffX;
        float endY = Shared.LassoOffY;
        float dx = Shared.LassoDirX;
        float dy = Shared.LassoDirY;
        float toNew = Cfg.LassoShootSpeed * Cfg.Dt;
        float lengthRemaining = Cfg.LassoMaxLength - MathF.Sqrt(endX * endX + endY * endY);
        if (toNew < lengthRemaining) lengthRemaining = toNew;
        int hit = -1;
        if (lengthRemaining > 0f) hit = scene.Physics.Sim.Raycast(px + endX, py + endY, dx, dy, lengthRemaining, wallMask, false);
        if (hit >= 0)
        {
            float hx = scene.Physics.Sim.HitX;
            float hy = scene.Physics.Sim.HitY;
            float toX = hx - px;
            float toY = hy - py;
            float len = MathF.Sqrt(toX * toX + toY * toY);
            float a = MathF.Atan2(toY, toX);
            Shared.LassoTipX = hx;
            Shared.LassoTipY = hy;
            Shared.LassoLength = len;
            Shared.SwingW = 0f;
            if (len > 0.0001f) Shared.SwingW = (body.VelocityX() * MathF.Cos(a) + body.VelocityY() * MathF.Sin(a)) / len;
            Shared.SwingAngle = a * 57.29578f;
            Shared.LassoAttached = true;
        }
        else
        {
            endX += dx * toNew;
            endY += dy * toNew;
            Shared.LassoOffX = endX;
            Shared.LassoOffY = endY;
            Shared.LassoTipX = px + endX;
            Shared.LassoTipY = py + endY;
            Shared.LassoLength = MathF.Sqrt(endX * endX + endY * endY);
            if (lengthRemaining <= 0f) Release();
        }
    }

    void Reel(float px, float py)
    {
        int change = InputState.ChangeLassoLength;
        if (change == 0) return;
        float amount = change * Cfg.LassoChangeLengthSpeed * Cfg.Dt;
        bool allowed = true;
        if (change < 0)
        {
            // reeling in is blocked if it would pull the player into a wall
            float toX = Shared.LassoTipX - px;
            float toY = Shared.LassoTipY - py;
            float d = MathF.Sqrt(toX * toX + toY * toY);
            if (d > 0.001f && scene.Physics.Sim.Raycast(px, py, toX / d, toY / d, MathF.Abs(amount) * 6f + Cfg.ColliderW * 0.5f, wallMask, false) >= 0)
                allowed = false;
        }
        if (!allowed) return;
        float len = Shared.LassoLength + amount;
        if (len < 0f) len = 0f;
        if (len > Cfg.LassoMaxLength) len = Cfg.LassoMaxLength;
        Shared.LassoLength = len;
    }

    // Letting go keeps the momentum of the swing: the displacement over the last step.
    void Release()
    {
        Shared.LassoActive = false;
        if (Shared.LassoAttached)
        {
            Shared.LassoAttached = false;
            body.SetVelocity(Shared.PlayerLastMoveX / Cfg.Dt, Shared.PlayerLastMoveY / Cfg.Dt);
        }
    }
}
