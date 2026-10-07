"""engine.py: the 2D engine, from Python. libprowl2d.so (built by tools/prowl2d_so.py: the C# runtime translated to C, plus the SDL2 renderer) is loaded with
ctypes; nothing here needs .NET.

    Engine    the library with its signatures declared: the p2d_* functions (the scene and its physics, from Native/Engine2D/Engine.cs) and the gfx_* functions
              (the renderer: textures, triangles, the window, input; Native/Gfx2D/gfx2d.h).
    Viewport  a window that shows a Level, animated, with a play mode in which the solid tiles are physics bodies and a click drops a ball. The engine's scene does the
              physics only; everything on screen is drawn through the renderer (one path), and the window's mouse and keyboard come back through gfx_poll_event.

One Engine per process (the C runtime has one scene and one renderer), driven from ONE thread: the Qt main thread, from a timer, as prowl.py does it.
"""
import array
import ctypes as C
import os
import time

ABI_VERSION = 1
_HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(_HERE)


class EngineError(RuntimeError):
    pass


def library_candidates():
    env = os.environ.get("PROWL2D_LIB")
    return ([env] if env else []) + ["/tmp/libprowl2d.so", os.path.join(ROOT, "Build", "libprowl2d.so")]


def find_library():
    for path in library_candidates():
        if os.path.isfile(path):
            return path
    return None


BUILD_HINT = "build it with:  python3 tools/prowl2d_so.py   (writes /tmp/libprowl2d.so; needs the .NET SDK once, for the C# to C translator)"

# the p2d_* functions: name -> (result, argument types). Generated into libprowl2d.h from Native/Engine2D/Engine.cs; check_api() compares.
_i, _f, _v = C.c_int, C.c_float, C.c_void_p
P2D = {
    "p2d_version": (_i, []), "p2d_init": (_i, [_i, _i]), "p2d_shutdown": (None, []),
    "p2d_camera": (None, [_f] * 6), "p2d_new_node": (_i, []), "p2d_node_count": (_i, []),
    "p2d_set_pos": (None, [_i, _f, _f]), "p2d_set_angle": (None, [_i, _f]), "p2d_set_scale": (None, [_i, _f, _f]),
    "p2d_node_x": (_f, [_i]), "p2d_node_y": (_f, [_i]), "p2d_node_angle": (_f, [_i]),
    "p2d_destroy_node": (None, [_i]), "p2d_clear": (None, []),
    "p2d_add_sprite": (_i, [_i, _i, _f, _f, _f, _f, _f]), "p2d_add_body": (_i, [_i, _i]),
    "p2d_add_box": (_i, [_i, _f, _f]), "p2d_add_circle": (_i, [_i, _f]), "p2d_gravity": (None, [_f, _f]),
    "p2d_step": (None, [_f]), "p2d_draw": (_i, []),
}
GFX = {
    "gfx_draw": (_i, [_v, _i]), "gfx_camera": (None, [_f] * 6), "gfx_pixel": (C.c_uint32, [_i, _i]), "gfx_frame_hash": (_i, []),
    "gfx_save_frame": (_i, [_i]), "gfx_stat": (_i, [_i]),
    "gfx_texture": (_i, [_i, _i, _i, _v]), "gfx_texture_update": (None, [_i, _i, _i, _i, _i, _v]), "gfx_texture_free": (None, [_i]),
    "gfx_triangles": (_i, [_v, _i, _i]), "gfx_clip": (_i, [_i] * 4), "gfx_clip_reset": (_i, []),
    "gfx_poll_event": (_i, [_v]), "gfx_inject_event": (_i, [_i] * 5),
    "gfx_window_open": (_i, [_i]), "gfx_window_is_open": (_i, []), "gfx_window_close": (None, []), "gfx_present": (_i, []),
}

# events (gfx2d.h)
EV_MOUSE_MOVE, EV_MOUSE_DOWN, EV_MOUSE_UP, EV_WHEEL, EV_KEY_DOWN, EV_KEY_UP, EV_TEXT, EV_FOCUS, EV_CLOSE = range(1, 10)
BTN_LEFT, BTN_MIDDLE, BTN_RIGHT = 0, 1, 2
KEY_ESCAPE, KEY_HOME, KEY_LEFT, KEY_RIGHT, KEY_DOWN, KEY_UP = 256, 268, 263, 262, 264, 265
BODY_STATIC, BODY_KINEMATIC, BODY_DYNAMIC = 0, 1, 2
FILTER_NEAREST = 0


def floats_ptr(arr):
    """The address of an array('f') (it must stay alive while the call runs)."""
    return arr.buffer_info()[0]


class Engine:
    def __init__(self, path=None):
        path = path or find_library()
        if not path:
            raise EngineError("libprowl2d.so was not found (looked in %s): %s" % (", ".join(library_candidates()), BUILD_HINT))
        try:
            self.lib = C.CDLL(path)
        except OSError as e:
            raise EngineError("cannot load %s: %s" % (path, e))
        self.path = path
        for table in (P2D, GFX):
            for name, (res, args) in table.items():
                try:
                    fn = getattr(self.lib, name)
                except AttributeError:
                    raise EngineError("%s has no %s: it is older than this editor. %s" % (path, name, BUILD_HINT))
                fn.restype = res
                fn.argtypes = args
        v = self.lib.p2d_version()
        if v != ABI_VERSION:
            raise EngineError("%s is engine API version %d, this editor wants %d. %s" % (path, v, ABI_VERSION, BUILD_HINT))
        self.started = False

    def __getattr__(self, name):
        """engine.new_node() is p2d_new_node, and engine.gfx_draw / engine.p2d_step name their functions in full: all plain library calls."""
        lib = self.__dict__.get("lib")
        if lib is None:
            raise AttributeError(name)
        return getattr(lib, name if name.startswith(("gfx_", "p2d_")) else "p2d_" + name)

    def start(self, width, height):
        """Makes the renderer's picture and the scene. Needs OpenGL ES 3.1 (Mesa's software one will do)."""
        if self.started:
            return
        if self.lib.p2d_init(width, height) != 1:
            raise EngineError("the renderer could not start: there is no OpenGL ES 3.1 here (Mesa llvmpipe is enough: apt install libgles2 libegl1 libgl1-mesa-dri)")
        self.started = True

    def stop(self):
        if self.started:
            self.lib.p2d_shutdown()
            self.started = False

    def poll_event(self):
        ev = (C.c_int * 5)()
        return tuple(ev) if self.lib.gfx_poll_event(ev) else None


# ---------------------------------------------------------------------------------------------------------------------------------------------------

def placeholder_color(emoji):
    """A steady colour for a tile that has no sprite, from the emoji itself."""
    h = 2166136261
    for ch in emoji:
        h = ((h ^ ord(ch)) * 16777619) & 0xFFFFFFFF
    return (0.35 + (h & 0xFF) / 700.0, 0.35 + ((h >> 8) & 0xFF) / 700.0, 0.35 + ((h >> 16) & 0xFF) / 700.0)


class Viewport:
    """The window. tick() is one frame: call it about 60 times a second from one thread."""
    BACKGROUND = (0.09, 0.10, 0.14)

    def __init__(self, engine, project, width=800, height=480):
        self.engine = engine
        self.project = project
        self.width, self.height = width, height
        self.level = None
        self.cx, self.cy, self.half = 0.0, 0.0, 6.0     # the camera: centre in world units and half the visible height
        self.playing = False
        self.balls = []                                  # engine node indexes
        self.clock = 0.0
        self._last = None
        self._acc = 0.0
        self._textures = {}                              # (sprite name, frame) -> texture id
        self._tex_rev = None
        self._pan = None                                 # (mouse x, y, camera x, y) while a pan drag is running
        self.mouse = (width // 2, height // 2)
        self.closed = False
        self.status = ""

    # ---- lifecycle
    def open(self):
        self.engine.start(self.width, self.height)
        if not self.engine.lib.gfx_window_open(1):
            raise EngineError("the window could not open: is a display available (DISPLAY or WAYLAND_DISPLAY set)? "
                              "or was libprowl2d.so built with --no-sdl (headless)? " + BUILD_HINT)
        self.closed = False
        self._last = None
        if self.level is not None:
            self.fit()

    def close(self):
        self._stop_play()
        self._free_textures()
        if self.engine.started:
            self.engine.lib.gfx_window_close()
        self.closed = True

    @property
    def is_open(self):
        return self.engine.started and bool(self.engine.lib.gfx_window_is_open()) and not self.closed

    # ---- the level and the camera
    def show_level(self, level):
        was_playing = self.playing
        self._stop_play()
        self.level = level
        if self.engine.started:
            self.fit()
        if was_playing:
            self._start_play()

    def fit(self):
        lv = self.level
        if lv is None:
            return
        self.cx, self.cy = lv.width / 2.0, lv.height / 2.0
        aspect = self.width / float(self.height)
        self.half = max(lv.height / 2.0 + 0.5, (lv.width / 2.0 + 0.5) / aspect)

    def screen_to_world(self, px, py):
        aspect = self.width / float(self.height)
        return (self.cx + (px / self.width - 0.5) * 2.0 * self.half * aspect, self.cy - (py / self.height - 0.5) * 2.0 * self.half)

    # ---- textures: one per sprite frame that a tile of the level uses
    def _free_textures(self):
        if self.engine.started:
            for tex in self._textures.values():
                self.engine.lib.gfx_texture_free(tex)
        self._textures = {}

    def _texture(self, sprite, frame):
        key = (sprite.name, frame)
        tex = self._textures.get(key)
        if tex is None:
            rgba = sprite.rgba(self.project.palette, frame)
            buf = (C.c_uint8 * len(rgba)).from_buffer_copy(rgba)
            tex = self.engine.lib.gfx_texture(sprite.width, sprite.height, FILTER_NEAREST, C.addressof(buf))
            self._textures[key] = tex            # 0 (no room: 63 textures) is remembered too, and the tile shows as a placeholder
        return tex

    def _sync_textures(self):
        rev = (self.project.revision, self.project.palette.version)
        if rev != self._tex_rev:
            self._free_textures()
            self._tex_rev = rev

    # ---- physics play mode
    def _start_play(self):
        lv = self.level
        e = self.engine.lib
        if lv is None:
            return
        e.p2d_clear()
        e.p2d_gravity(0.0, -18.0)
        made = skipped = 0
        solid = {k for k, t in self.project.tiles.items() if t.solid}
        for y in range(lv.height):                        # a run of solid cells in a row is ONE box: the scene holds 256 nodes
            x = 0
            while x < lv.width:
                if lv.get(x, y) in solid:
                    x0 = x
                    while x < lv.width and lv.get(x, y) in solid:
                        x += 1
                    node = e.p2d_new_node()
                    if node < 0:
                        skipped += 1
                        continue
                    w = x - x0
                    e.p2d_set_pos(node, x0 + w / 2.0, lv.height - y - 0.5)
                    e.p2d_add_body(node, BODY_STATIC)
                    e.p2d_add_box(node, float(w), 1.0)
                    made += 1
                else:
                    x += 1
        self.balls = []
        self.playing = True
        self._acc = 0.0
        self.status = "play: %d solid blocks%s - click to drop a ball" % (made, (", %d beyond the engine's limit skipped" % skipped) if skipped else "")

    def _stop_play(self):
        if self.playing and self.engine.started:
            self.engine.lib.p2d_clear()
        self.playing = False
        self.balls = []

    def toggle_play(self):
        if self.playing:
            self._stop_play()
            self.status = "edit view"
        else:
            self._start_play()

    def drop_ball(self, wx, wy):
        e = self.engine.lib
        if len(self.balls) >= 100:
            e.p2d_destroy_node(self.balls.pop(0))
        node = e.p2d_new_node()
        if node >= 0:
            e.p2d_set_pos(node, wx, wy)
            e.p2d_add_body(node, BODY_DYNAMIC)
            e.p2d_add_circle(node, 0.35)
            self.balls.append(node)

    # ---- input
    def _handle_events(self):
        while True:
            ev = self.engine.poll_event()
            if ev is None:
                return
            kind, a, b, c, d = ev
            if kind == EV_CLOSE:
                self.closed = True
            elif kind == EV_MOUSE_MOVE:
                self.mouse = (a, b)
                if self._pan:
                    px, py, cx, cy = self._pan
                    aspect = self.width / float(self.height)
                    self.cx = cx - (a - px) / float(self.width) * 2.0 * self.half * aspect
                    self.cy = cy + (b - py) / float(self.height) * 2.0 * self.half
            elif kind == EV_MOUSE_DOWN:
                if c == BTN_LEFT and self.playing:
                    self.drop_ball(*self.screen_to_world(a, b))
                elif c in (BTN_RIGHT, BTN_MIDDLE):
                    self._pan = (a, b, self.cx, self.cy)
            elif kind == EV_MOUSE_UP:
                if c in (BTN_RIGHT, BTN_MIDDLE):
                    self._pan = None
            elif kind == EV_WHEEL and d:
                self.half = min(200.0, max(1.5, self.half * (0.9 if d > 0 else 1.0 / 0.9)))
            elif kind == EV_KEY_DOWN:
                step = self.half * 0.1
                if a == ord("P"):
                    self.toggle_play()
                elif a == ord("R") and self.playing:
                    self._start_play()
                elif a == KEY_HOME:
                    self.fit()
                elif a == KEY_LEFT:
                    self.cx -= step
                elif a == KEY_RIGHT:
                    self.cx += step
                elif a == KEY_UP:
                    self.cy += step
                elif a == KEY_DOWN:
                    self.cy -= step
                elif a == KEY_ESCAPE:
                    self.closed = True

    # ---- a frame
    def tick(self, dt=None):
        """Handles the window's input, advances the animation (and the physics in play mode), draws and presents. Returns False once the window was closed."""
        if not self.is_open:
            return False
        now = time.perf_counter()
        if dt is None:
            dt = 0.0 if self._last is None else min(0.1, now - self._last)
        self._last = now
        self.clock += dt
        self._handle_events()
        if self.closed:
            return False
        e = self.engine.lib
        if self.playing:
            self._acc += dt
            n = 0
            while self._acc >= 1 / 60.0 and n < 4:
                e.p2d_step(1 / 60.0)
                self._acc -= 1 / 60.0
                n += 1
        self._draw()
        e.gfx_present()
        return True

    def _draw(self):
        e = self.engine.lib
        self._sync_textures()
        r, g, b = self.BACKGROUND
        e.gfx_camera(self.cx, self.cy, self.half, r, g, b)
        lv, proj = self.level, self.project
        flat = array.array("f")                                  # the sprite batch: tiles with no sprite, and the balls
        quads = {}                                               # texture id -> array of mesh vertices
        if lv is not None:
            aspect = self.width / float(self.height)
            x0 = max(0, int(self.cx - self.half * aspect) - 1)
            x1 = min(lv.width, int(self.cx + self.half * aspect) + 2)
            y0 = max(0, int(lv.height - (self.cy + self.half)) - 1)
            y1 = min(lv.height, int(lv.height - (self.cy - self.half)) + 2)
            sprite_cache = {}
            for y in range(y0, y1):
                for x in range(x0, x1):
                    g_ = lv.cells[y * lv.width + x]
                    if not g_:
                        continue
                    tile = proj.tiles.get(g_)
                    spr = None
                    if tile is not None:
                        if g_ not in sprite_cache:
                            sprite_cache[g_] = proj.sprite_for_tile(tile)
                        spr = sprite_cache[g_]
                    cx, cy = x + 0.5, lv.height - y - 0.5
                    tex = 0
                    if spr is not None:
                        frame = int(self.clock * spr.fps) % len(spr.frames) if len(spr.frames) > 1 and spr.fps > 0 else 0
                        tex = self._texture(spr, frame)
                    if tex:
                        v = quads.setdefault(tex, array.array("f"))
                        l, rr, t, bt = cx - 0.5, cx + 0.5, cy + 0.5, cy - 0.5
                        v.extend((l, t, 0, 0, 1, 1, 1, 1, rr, t, 1, 0, 1, 1, 1, 1, rr, bt, 1, 1, 1, 1, 1, 1,
                                  l, t, 0, 0, 1, 1, 1, 1, rr, bt, 1, 1, 1, 1, 1, 1, l, bt, 0, 1, 1, 1, 1, 1))
                    else:
                        pr, pg, pb = placeholder_color(g_)
                        flat.extend((cx, cy, 0.5, 0.5, 0.0, pr, pg, pb, 1.0, 0, 1, 0))      # layer 1: over the frame
            # a faint frame around the level (layer 0, under the tiles), so its edge shows against the background
            flat.extend((lv.width / 2.0, lv.height / 2.0, lv.width / 2.0 + 0.06, lv.height / 2.0 + 0.06, 0.0, 0.2, 0.22, 0.3, 1.0, 0, 0, 0))
        for node in self.balls:
            flat.extend((self.engine.lib.p2d_node_x(node), self.engine.lib.p2d_node_y(node), 0.35, 0.35, 0.0, 1.0, 0.55, 0.2, 1.0, 1, 5, 0))
        n = len(flat) // 12
        e.gfx_draw(floats_ptr(flat) if n else None, n)
        for tex, verts in quads.items():
            e.gfx_triangles(floats_ptr(verts), len(verts) // 8, tex)

