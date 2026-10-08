using System;
using Prowl.Core2D;
using Prowl.Native.Box2D;

// Spikes: touching the player kills.
[Script, MaxInstances(64)]
class HazardScript
{
    public Component Self;
    public void OnTriggerEnter2D(Collider2D other)
    {
        if (other.Self.Node.Tag == Shared.TagPlayer) Shared.DeathRequested = true;
    }
}

// Savepoint: stores the respawn position and makes picked-up gems permanent.
[Script, MaxInstances(32)]
class SavePointScript
{
    public Component Self;
    public float X;
    public float Y;
    public int Index;
    public void OnTriggerEnter2D(Collider2D other)
    {
        if (other.Self.Node.Tag != Shared.TagPlayer) return;
        Shared.TouchSave(Index);
        Shared.SaveX = X;
        Shared.SaveY = Y;
        Shared.CommitGems();
    }
}

// Gem: picking it up hides it; it only counts once a savepoint is touched.
[Script, MaxInstances(64)]
class GemScript
{
    public Component Self;
    public int Index;
    public void OnTriggerEnter2D(Collider2D other)
    {
        if (other.Self.Node.Tag != Shared.TagPlayer) return;
        Shared.PickUpGem(Index);
    }
}

// The teleporter at the bottom of the shaft: reaching it counts as a win, and it sends the slime back to the start (PlayerScript does the moving, next step). The shaft
// stays as it was left: the holes, the rubble and the sand are still there for the next trip down.
[Script, MaxInstances(1)]
class GoalScript
{
    public Component Self;
    public void OnTriggerEnter2D(Collider2D other)
    {
        if (other.Self.Node.Tag != Shared.TagPlayer) return;
        Shared.Won = true;
        Shared.Warps++;
        Shared.WarpRequested = true;
    }
}
