// Headless2D: a ball dropped on a floor. No window, no renderer. This is the first end-to-end test of Prowl.Core2D:
// the same source runs on .NET (the reference) and as a native executable translated to C, and must print the same thing.
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

[Script, MaxInstances(4)]
class Ball
{
    public Component Self;
    public int Hits;

    public void Reset() { Hits = 0; }

    public void OnCollisionBegin2D(Collision2D hit)
    {
        Hits++;
        Console.WriteLine("hit " + Hits + " normalY*1000=" + (int)(hit.NY * 1000f));
    }
}

static class Game
{
    public static int Main()
    {
        Scripts.Init();
        Scene2D scene = new Scene2D();

        Node ground = scene.NewNode(null);
        ground.SetPosition(0f, -0.5f);
        scene.AddBoxCollider(ground, 100f, 1f);          // no rigidbody: static; its top is at y = 0

        Node ball = scene.NewNode(null);
        ball.SetPosition(0f, 5f);
        scene.AddRigidbody(ball, PB2.BodyDynamic);
        scene.AddCircleCollider(ball, 0.5f);
        Ball script = Scripts.AddBall(ball);

        for (int frame = 0; frame < 240; frame++)
        {
            Scripts.Tick(scene, 1f / 60f);
            if (frame % 30 == 0)
                Console.WriteLine("frame " + frame + " y*1000=" + (int)(ball.WorldY() * 1000f));
        }
        int rest = (int)(ball.WorldY() * 1000f);
        Console.WriteLine("rest y*1000=" + rest + " hits=" + script.Hits);
        return (rest > 450 && rest < 550 && script.Hits >= 1) ? 0 : 1;   // it must come to rest ~0.5 above the floor
    }
}
