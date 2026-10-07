/* gfx2d_sdl.inc: the desktop window of gfx2d.h, included by gfx2d.c (it needs the renderer's frame state: g_pixels, g_w, g_h, sync()).
 *
 * The renderer still draws offscreen. The window only SHOWS the finished frame: gfx_present() reads it back (as gfx_pixel would) and copies it to
 * an SDL window, letterboxed to keep the picture's aspect, and the window's mouse, keyboard and close events are fed into the same queue as
 * gfx_inject_event, in picture pixels (so a game cannot tell a window from a script). Linux (X11 or Wayland, whatever SDL2 finds) is what this is for;
 * a page has its own backend (gfx2d_web.c).
 *
 * Built only with -DGFX_SDL (needs SDL2: apt install libsdl2-dev). Without it every function below exists and says "no window" (0), so a game that
 * calls them still links and runs headless.
 */
#ifdef GFX_SDL
#include <SDL2/SDL.h>

static SDL_Window *g_win;
static SDL_Renderer *g_ren;
static SDL_Texture *g_wtex;

/* where the picture lands in the window (letterboxed), and the scale: needed both to draw it and to turn a window position back into a picture pixel */
static void win_layout(SDL_Rect *dst, float *scale)
{
    int ww = 1, wh = 1;
    float s;
    SDL_GetWindowSize(g_win, &ww, &wh);
    s = (float)ww / (float)g_w;
    if ((float)wh / (float)g_h < s)
        s = (float)wh / (float)g_h;
    if (s <= 0.f)
        s = 1.f;
    dst->w = (int)((float)g_w * s);
    dst->h = (int)((float)g_h * s);
    dst->x = (ww - dst->w) / 2;
    dst->y = (wh - dst->h) / 2;
    *scale = s;
}

static void win_to_picture(int wx, int wy, int *px, int *py)
{
    SDL_Rect d;
    float s;
    win_layout(&d, &s);
    *px = (int)((float)(wx - d.x) / s);
    *py = (int)((float)(wy - d.y) / s);
}

static int win_mods(void)
{
    SDL_Keymod m = SDL_GetModState();
    int r = 0;
    if (m & KMOD_SHIFT) r |= GFX_MOD_SHIFT;
    if (m & KMOD_CTRL) r |= GFX_MOD_CTRL;
    if (m & KMOD_ALT) r |= GFX_MOD_ALT;
    if (m & KMOD_GUI) r |= GFX_MOD_SUPER;
    return r;
}

static int win_key(SDL_Keycode k)
{
    if (k >= 'a' && k <= 'z')
        return k - 'a' + 'A';
    if (k >= 32 && k < 127)
        return (int)k;
    if (k >= SDLK_F1 && k <= SDLK_F12)
        return GFX_KEY_F1 + (int)(k - SDLK_F1);
    switch (k) {
    case SDLK_ESCAPE: return GFX_KEY_ESCAPE;
    case SDLK_RETURN: case SDLK_KP_ENTER: return GFX_KEY_ENTER;
    case SDLK_TAB: return GFX_KEY_TAB;
    case SDLK_BACKSPACE: return GFX_KEY_BACKSPACE;
    case SDLK_INSERT: return GFX_KEY_INSERT;
    case SDLK_DELETE: return GFX_KEY_DELETE;
    case SDLK_RIGHT: return GFX_KEY_RIGHT;
    case SDLK_LEFT: return GFX_KEY_LEFT;
    case SDLK_DOWN: return GFX_KEY_DOWN;
    case SDLK_UP: return GFX_KEY_UP;
    case SDLK_PAGEUP: return GFX_KEY_PAGE_UP;
    case SDLK_PAGEDOWN: return GFX_KEY_PAGE_DOWN;
    case SDLK_HOME: return GFX_KEY_HOME;
    case SDLK_END: return GFX_KEY_END;
    case SDLK_LSHIFT: return GFX_KEY_LEFT_SHIFT;
    case SDLK_LCTRL: return GFX_KEY_LEFT_CTRL;
    case SDLK_LALT: return GFX_KEY_LEFT_ALT;
    case SDLK_LGUI: return GFX_KEY_LEFT_SUPER;
    case SDLK_RSHIFT: return GFX_KEY_RIGHT_SHIFT;
    case SDLK_RCTRL: return GFX_KEY_RIGHT_CTRL;
    case SDLK_RALT: return GFX_KEY_RIGHT_ALT;
    case SDLK_RGUI: return GFX_KEY_RIGHT_SUPER;
    default: return 0;
    }
}

static int win_button(int b)
{
    return b == SDL_BUTTON_LEFT ? GFX_BUTTON_LEFT : b == SDL_BUTTON_MIDDLE ? GFX_BUTTON_MIDDLE : b == SDL_BUTTON_RIGHT ? GFX_BUTTON_RIGHT : -1;
}

/* the first codepoint of a UTF-8 string */
static int utf8_first(const char *s)
{
    unsigned char c = (unsigned char)s[0];
    if (c < 0x80) return c;
    if ((c >> 5) == 6 && s[1]) return ((c & 31) << 6) | (s[1] & 63);
    if ((c >> 4) == 14 && s[1] && s[2]) return ((c & 15) << 12) | ((s[1] & 63) << 6) | (s[2] & 63);
    if ((c >> 3) == 30 && s[1] && s[2] && s[3]) return ((c & 7) << 18) | ((s[1] & 63) << 12) | ((s[2] & 63) << 6) | (s[3] & 63);
    return 0;
}

/* takes what the window has received and queues it: this is the backend's `poll` hook, so gfx_poll_event works between frames */
static void win_poll(void)
{
    SDL_Event e;
    int px, py;
    if (!g_win)
        return;
    while (SDL_PollEvent(&e)) {
        switch (e.type) {
        case SDL_QUIT:
            gfxui_close();
            break;
        case SDL_WINDOWEVENT:
            if (e.window.event == SDL_WINDOWEVENT_CLOSE)
                gfxui_close();
            else if (e.window.event == SDL_WINDOWEVENT_FOCUS_GAINED)
                gfxui_push(GFX_EVENT_FOCUS, 1, 0, 0, 0);
            else if (e.window.event == SDL_WINDOWEVENT_FOCUS_LOST)
                gfxui_push(GFX_EVENT_FOCUS, 0, 0, 0, 0);
            break;
        case SDL_MOUSEMOTION:
            win_to_picture(e.motion.x, e.motion.y, &px, &py);
            gfxui_push(GFX_EVENT_MOUSE_MOVE, px, py, 0, win_mods());
            break;
        case SDL_MOUSEBUTTONDOWN:
        case SDL_MOUSEBUTTONUP:
            if (win_button(e.button.button) >= 0) {
                win_to_picture(e.button.x, e.button.y, &px, &py);
                gfxui_push(e.type == SDL_MOUSEBUTTONDOWN ? GFX_EVENT_MOUSE_DOWN : GFX_EVENT_MOUSE_UP, px, py, win_button(e.button.button), win_mods());
            }
            break;
        case SDL_MOUSEWHEEL: {
            int mx, my;
            SDL_GetMouseState(&mx, &my);
            win_to_picture(mx, my, &px, &py);
            gfxui_push(GFX_EVENT_WHEEL, px, py, e.wheel.x * 120, e.wheel.y * 120);
            break;
        }
        case SDL_KEYDOWN:
        case SDL_KEYUP: {
            int k = win_key(e.key.keysym.sym);
            if (k)
                gfxui_push(e.type == SDL_KEYDOWN ? GFX_EVENT_KEY_DOWN : GFX_EVENT_KEY_UP, k, e.key.repeat ? 1 : 0, 0, win_mods());
            break;
        }
        case SDL_TEXTINPUT: {
            int cp = utf8_first(e.text.text);
            if (cp > 0 && cp < 256) /* the window gives Latin-1 (gfx2d.h) */
                gfxui_push(GFX_EVENT_TEXT, cp, 0, 0, 0);
            break;
        }
        default:
            break;
        }
    }
}

int gfx_window_is_open(void) { return g_win != NULL; }

void gfx_window_close(void)
{
    if (g_wtex) SDL_DestroyTexture(g_wtex);
    if (g_ren) SDL_DestroyRenderer(g_ren);
    if (g_win) SDL_DestroyWindow(g_win);
    g_wtex = NULL; g_ren = NULL; g_win = NULL;
    SDL_QuitSubSystem(SDL_INIT_VIDEO);
}

/* PROWL_SDL_DEBUG=1 says on stderr why a window could not be made */
static void win_why(const char *what)
{
    if (getenv("PROWL_SDL_DEBUG"))
        fprintf(stderr, "gfx_window_open: %s failed: %s\n", what, SDL_GetError());
}

int gfx_window_open(int visible)
{
    if (!g_ready || g_win)
        return 0;
    if (SDL_InitSubSystem(SDL_INIT_VIDEO) != 0) {
        win_why("SDL_InitSubSystem");
        return 0;
    }
    /* The window's own framebuffer must be plain memory. By default SDL backs even a software renderer's window with a GL texture, which makes a GLX context
     * current on this thread and collides with the (EGL) one the renderer is using: an X error, and the process is gone. */
    if (!getenv("PROWL_SDL_ACCEL"))
        SDL_SetHint(SDL_HINT_FRAMEBUFFER_ACCELERATION, "0");
    g_win = SDL_CreateWindow("Prowl2D", SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, g_w, g_h,
                             SDL_WINDOW_RESIZABLE | (visible ? SDL_WINDOW_SHOWN : SDL_WINDOW_HIDDEN));
    if (!g_win) {
        win_why("SDL_CreateWindow");
        SDL_QuitSubSystem(SDL_INIT_VIDEO);
        return 0;
    }
    /* A software renderer by default: the picture was already rendered (by GLES, offscreen) and only needs copying to the window, and it keeps SDL from
     * making a second GL context beside ours. PROWL_SDL_ACCEL=1 asks for SDL's accelerated one. */
    g_ren = SDL_CreateRenderer(g_win, -1, getenv("PROWL_SDL_ACCEL") ? 0 : SDL_RENDERER_SOFTWARE);
    if (!g_ren) {
        win_why("software SDL_CreateRenderer");
        g_ren = SDL_CreateRenderer(g_win, -1, 0);
    }
    if (g_ren)
        g_wtex = SDL_CreateTexture(g_ren, SDL_PIXELFORMAT_ABGR8888, SDL_TEXTUREACCESS_STREAMING, g_w, g_h); /* ABGR8888 is bytes R, G, B, A in memory */
    if (!g_ren || !g_wtex) {
        win_why(g_ren ? "SDL_CreateTexture" : "SDL_CreateRenderer");
        gfx_window_close();
        return 0;
    }
    SDL_StartTextInput();
    return 1;
}

int gfx_present(void)
{
    SDL_Rect dst;
    float s;
    if (!g_win || !g_ready)
        return 0;
    sync();
    SDL_UpdateTexture(g_wtex, NULL, g_pixels, g_w * 4);
    win_layout(&dst, &s);
    SDL_SetRenderDrawColor(g_ren, 24, 24, 28, 255);
    SDL_RenderClear(g_ren);
    SDL_RenderCopyEx(g_ren, g_wtex, NULL, &dst, 0.0, NULL, SDL_FLIP_VERTICAL); /* GL's rows run bottom to top */
    SDL_RenderPresent(g_ren);
    return 1;
}

#else /* no SDL in this build: headless only */

static void win_poll(void) {}
int gfx_window_open(int visible) { (void)visible; return 0; }
int gfx_window_is_open(void) { return 0; }
void gfx_window_close(void) {}
int gfx_present(void) { return 0; }

#endif
