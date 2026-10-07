"""Tests of the engine as Python sees it: libprowl2d.so through ctypes (the C# runtime translated to C, with Box2D and the renderer).

The engine API tests need only the library (EGL renders without a display); the viewport tests also need a display (xvfb-run python3 -m unittest ...) and are skipped
without one. Everything is skipped, with the reason, if the library is not built:  python3 tools/prowl2d_so.py
"""
import os
import unittest

from .demo import make_demo_project
from .engine import (BODY_DYNAMIC, BODY_STATIC, BTN_LEFT, EV_MOUSE_DOWN, EV_WHEEL, KEY_HOME, Engine, EngineError, Viewport, find_library)

W, H = 800, 480           # the renderer's picture is made once per process, at the size it is first started with
_engine = None


def setUpModule():
    global _engine
    if find_library() is None:
        raise unittest.SkipTest("libprowl2d.so is not built (python3 tools/prowl2d_so.py)")
    try:
        _engine = Engine()
        _engine.start(W, H)
    except EngineError as e:
        raise unittest.SkipTest(str(e))


def tearDownModule():
    if _engine is not None:
        _engine.stop()


def rgb(v):
    return ((v >> 16) & 255, (v >> 8) & 255, v & 255)


class EngineApi(unittest.TestCase):
    def setUp(self):
        self.e = _engine.lib
        self.e.p2d_clear()

    def test_version_and_the_exports_the_header_promises(self):
        self.assertEqual(self.e.p2d_version(), 1)
        header = os.path.join(os.path.dirname(_engine.path), "libprowl2d.h")
        if os.path.exists(header):
            import re
            with open(header) as f:
                names = re.findall(r"\bp2d_\w+(?=\()", f.read())
            self.assertTrue(len(names) >= 20)
            for n in names:
                self.assertTrue(hasattr(self.e, n), n)

    def test_a_dynamic_ball_falls_and_rests_on_a_static_box(self):
        e = self.e
        ground = e.p2d_new_node()
        e.p2d_set_pos(ground, 0.0, -0.5)
        self.assertEqual(e.p2d_add_body(ground, BODY_STATIC), 1)
        self.assertEqual(e.p2d_add_box(ground, 20.0, 1.0), 1)
        ball = e.p2d_new_node()
        e.p2d_set_pos(ball, 0.0, 5.0)
        e.p2d_add_body(ball, BODY_DYNAMIC)
        e.p2d_add_circle(ball, 0.5)
        self.assertAlmostEqual(e.p2d_node_y(ball), 5.0, places=3)
        for _ in range(30):
            e.p2d_step(1 / 60.0)
        mid = e.p2d_node_y(ball)
        self.assertTrue(0.5 < mid < 5.0, mid)                           # half a second in: still falling
        for _ in range(240):
            e.p2d_step(1 / 60.0)
        self.assertAlmostEqual(e.p2d_node_y(ball), 0.5, delta=0.02)    # at rest on the box's top (y = 0), radius 0.5
        self.assertAlmostEqual(e.p2d_node_x(ball), 0.0, delta=0.01)

    def test_clear_and_the_node_limit(self):
        e = self.e
        made = 0
        while e.p2d_new_node() >= 0:
            made += 1
            self.assertLessEqual(made, 1000)
        self.assertEqual(e.p2d_node_count(), made)
        self.assertEqual(made, 256)                                     # CoreLimits.Nodes: refused cleanly (-1), not a crash
        e.p2d_clear()
        self.assertEqual(e.p2d_node_count(), 0)
        self.assertGreaterEqual(e.p2d_new_node(), 0)                    # the slots came back

    def test_a_node_index_that_is_not_a_node_is_harmless(self):
        e = self.e
        e.p2d_set_pos(200, 1.0, 1.0)
        e.p2d_destroy_node(-1)
        self.assertEqual(e.p2d_node_x(255), 0.0)
        self.assertEqual(e.p2d_add_sprite(255, 0, 1.0, 1.0, 1.0, 1.0, 1.0), 0)

    def test_draw_the_scenes_sprites(self):
        e = self.e
        e.p2d_camera(0.0, 0.0, 4.0, 0.0, 0.0, 0.0)
        n = e.p2d_new_node()
        e.p2d_add_sprite(n, 0, 2.0, 2.0, 1.0, 0.0, 0.0)                 # a red box in the middle
        self.assertEqual(e.p2d_draw(), 1)
        self.assertEqual(rgb(e.gfx_pixel(W // 2, H // 2)), (255, 0, 0))
        self.assertEqual(rgb(e.gfx_pixel(2, 2)), (0, 0, 0))             # the background
        e.p2d_set_pos(n, 100.0, 0.0)                                    # off screen
        e.p2d_draw()
        self.assertEqual(rgb(e.gfx_pixel(W // 2, H // 2)), (0, 0, 0))

    def test_textures_and_triangles_from_python(self):
        import array
        import ctypes as C
        e = self.e
        e.p2d_camera(0.0, 0.0, 4.0, 0.0, 0.0, 0.0)
        pix = (C.c_uint8 * 16)(0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255)       # a 2x2 green texture
        tex = e.gfx_texture(2, 2, 0, C.addressof(pix))
        self.assertGreater(tex, 0)
        e.gfx_draw(None, 0)
        quad = array.array("f", [-1, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, -1, 1, 1, 1, 1, 1, 1,
                                 -1, 1, 0, 0, 1, 1, 1, 1, 1, -1, 1, 1, 1, 1, 1, 1, -1, -1, 0, 1, 1, 1, 1, 1])
        self.assertEqual(e.gfx_triangles(quad.buffer_info()[0], 6, tex), 6)
        self.assertEqual(rgb(e.gfx_pixel(W // 2, H // 2)), (0, 255, 0))
        e.gfx_texture_free(tex)

    def test_events_can_be_injected_and_polled(self):
        e = self.e
        while _engine.poll_event():
            pass
        self.assertEqual(e.gfx_inject_event(EV_WHEEL, 10, 20, 0, 120), 1)
        self.assertEqual(_engine.poll_event(), (EV_WHEEL, 10, 20, 0, 120))
        self.assertIsNone(_engine.poll_event())


@unittest.skipUnless(os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"), "needs a display (run under xvfb-run)")
class ViewportWindow(unittest.TestCase):
    def setUp(self):
        self.project = make_demo_project()
        self.vp = Viewport(_engine, self.project, W, H)
        self.vp.show_level(self.project.levels[0])
        try:
            self.vp.open()
        except EngineError as e:
            self.skipTest(str(e))
        self.lib = _engine.lib

    def tearDown(self):
        self.vp.close()

    def cell_px(self, col, row):
        vp = self.vp
        wx, wy = col + 0.5, vp.level.height - row - 0.5
        return (int((wx - vp.cx) / (2 * vp.half * (W / H)) * W + W / 2), int(H / 2 - (wy - vp.cy) / (2 * vp.half) * H))

    def test_the_level_is_drawn_where_its_cells_are(self):
        self.assertTrue(self.vp.is_open)
        self.vp.tick(0.0)
        brick = rgb(self.lib.gfx_pixel(*self.cell_px(9, 4)))
        self.assertEqual(brick, (224, 60, 60))                                  # the brick sprite's red
        outside = rgb(self.lib.gfx_pixel(5, 5))
        self.assertEqual(outside, tuple(int(round(c * 255)) for c in self.vp.BACKGROUND))

    def test_the_coin_animates_and_the_palette_recolours_it(self):
        self.vp.tick(0.0)
        h0 = self.lib.gfx_frame_hash()
        for _ in range(8):
            self.vp.tick(0.05)
        self.assertNotEqual(h0, self.lib.gfx_frame_hash())                      # frames of the spinning coin
        self.vp.tick(0.0)
        before = self.lib.gfx_frame_hash()
        self.project.palette.set_color(self.project.palette.index_of("R"), (0, 0, 255))     # the bricks' index changes colour...
        self.project.touch()                                                    # ...and the viewport notices the edit
        self.vp.tick(0.0)
        self.assertNotEqual(before, self.lib.gfx_frame_hash())
        self.assertEqual(rgb(self.lib.gfx_pixel(*self.cell_px(9, 4))), (0, 0, 255))

    def test_wheel_zoom_and_home_arrive_through_the_windows_event_queue(self):
        half = self.vp.half
        self.lib.gfx_inject_event(EV_WHEEL, W // 2, H // 2, 0, 120)
        self.vp.tick(0.0)
        self.assertLess(self.vp.half, half)
        self.lib.gfx_inject_event(5, KEY_HOME, 0, 0, 0)
        self.vp.tick(0.0)
        self.assertAlmostEqual(self.vp.half, half, places=6)

    def test_play_mode_a_click_drops_a_ball_that_lands_on_the_bricks(self):
        self.vp.toggle_play()
        self.assertTrue(self.vp.playing)
        wx, wy = 12.0, 9.0                                                     # above the top brick platform (its top edge is y = 8)
        px = int((wx - self.vp.cx) / (2 * self.vp.half * (W / H)) * W + W / 2)
        py = int(H / 2 - (wy - self.vp.cy) / (2 * self.vp.half) * H)
        self.lib.gfx_inject_event(EV_MOUSE_DOWN, px, py, BTN_LEFT, 0)
        self.vp.tick(0.0)
        self.assertEqual(len(self.vp.balls), 1)
        for _ in range(240):
            self.vp.tick(1 / 60.0)
        self.assertAlmostEqual(self.lib.p2d_node_y(self.vp.balls[0]), 8.0 + 0.35, delta=0.03)
        self.vp.toggle_play()
        self.assertEqual(self.lib.p2d_node_count(), 0)                          # leaving play mode clears the scene

    def test_closing_the_window_ends_the_ticks(self):
        self.lib.gfx_inject_event(9, 0, 0, 0, 0)                                # GFX_EVENT_CLOSE, as the window's close button sends it
        self.assertFalse(self.vp.tick(0.0))
        self.assertTrue(self.vp.closed)


if __name__ == "__main__":
    unittest.main()
