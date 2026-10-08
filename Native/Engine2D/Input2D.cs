// Input2D: what the keyboard and the mouse are doing, for scripts. The host (the editor's viewport) tells the engine through p2d_set_key and p2d_set_mouse; a script
// asks here:   if (Input2D.Key(Input2D.Left)) ...   Input2D.MouseX / MouseY are world units, Input2D.MouseDown(0) is the left button.
// It is part of the engine's own sources (Native/Engine2D), translated with the scripts, so a script can use it by name with no `using`.
static class Input2D
{
    public const int Left = 263;
    public const int Right = 262;
    public const int Down = 264;
    public const int Up = 265;
    public const int Space = 32;
    public const int Escape = 256;
    // letters and digits are their capital letter's and digit's character code: Input2D.Key('A'), Input2D.Key('1')

    static int[] keys;
    static int ready;
    static int buttons;
    static float mouseX;
    static float mouseY;

    public static void Init()
    {
        if (ready == 0)
        {
            keys = new int[512];
            ready = 1;
        }
    }

    public static void SetKey(int key, int down)
    {
        if (ready == 0 || key < 0 || key >= 512) return;
        keys[key] = down != 0 ? 1 : 0;
    }

    public static void SetMouse(float x, float y, int pressed)
    {
        mouseX = x;
        mouseY = y;
        buttons = pressed;
    }

    public static void Clear()
    {
        if (ready == 0) return;
        for (int i = 0; i < 512; i++) keys[i] = 0;
        buttons = 0;
    }

    /// <summary>True while the key is held.</summary>
    public static bool Key(int key)
    {
        if (ready == 0 || key < 0 || key >= 512) return false;
        return keys[key] != 0;
    }

    /// <summary>True while the mouse button (0 left, 1 middle, 2 right) is held.</summary>
    public static bool MouseDown(int button)
    {
        return ((buttons >> button) & 1) != 0;
    }

    public static float MouseX { get { return mouseX; } }
    public static float MouseY { get { return mouseY; } }
}
