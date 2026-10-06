// dna
// Script2D: Headless2D's ball, with a script that is outside the C subset (a lambda and try/catch). A class Crust cannot lower runs managed, on
// DotNetAnywhere linked into the same executable (the `// dna` line above makes --dna the default); everything else stays native C.
using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

[Script, MaxInstances(4)]
class Ball
{
    public Component Self;
    public int Hits;
    public int Score;
    public int Ticks;

    public void Reset() { Hits = 0; Score = 0; Ticks = 0; }

    public void Update()
    {
        Ticks++;
        try
        {
            if (Ticks % 100 == 0) throw new InvalidOperationException("tick " + Ticks);
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine("caught: " + e.Message);
        }
    }

    public void OnCollisionBegin2D(Collision2D hit)
    {
        Hits++;
        Func<int, int> bonus = k => k * 10 + (int)(hit.NY * 100f);   // a lambda: not in the Crust subset
        try
        {
            Score += bonus(Hits);
            if (Hits > 2) throw new InvalidOperationException("too many hits");
        }
        catch (InvalidOperationException e)                          // nor is try/catch
        {
            Score = -Score;
            Console.WriteLine("caught: " + e.Message);
        }
        Console.WriteLine("hit " + Hits + " score=" + Score);
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
        scene.AddBoxCollider(ground, 100f, 1f);

        Node ball = scene.NewNode(null);
        ball.SetPosition(0f, 5f);
        scene.AddRigidbody(ball, PB2.BodyDynamic);
        scene.AddCircleCollider(ball, 0.5f);
        Ball script = Scripts.AddBall(ball);

        for (int frame = 0; frame < 240; frame++)
        {
            Scripts.Tick(scene, 1f / 60f);
            if (frame % 60 == 0)
                Console.WriteLine("frame " + frame + " y*1000=" + (int)(ball.WorldY() * 1000f));
        }
        int rest = (int)(ball.WorldY() * 1000f);
        Console.WriteLine("rest y*1000=" + rest + " hits=" + script.Hits + " score=" + script.Score + " ticks=" + script.Ticks);
        return (rest > 450 && rest < 550 && script.Hits >= 1) ? 0 : 1;
    }
}
