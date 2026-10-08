// The input seam. Neither engine has input yet: a platform layer (window, keyboard)
// or a bot writes these once per fixed step, before PlayerScript runs.
static class InputState
{
    public static float Move;      // -1 .. 1
    public static bool Jump;       // held
    public static bool Attack;     // held: fire the blaster
    public static float AimX;      // world position of the cursor
    public static float AimY;
    public static bool Lasso;      // held: shoot the lasso, release to let go
    public static bool BotOn = true;   // the bot (Bot.cs) writes the input; a page that lets a person play turns it off
    public static int ChangeLassoLength;   // -1 reel in (W), +1 let out (S)
}
