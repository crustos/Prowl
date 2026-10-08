"""Tests of the engine as Python sees it: libprowl2d.so through ctypes (the C# runtime translated to C, with Box2D and the renderer).

The engine API tests need only the library (EGL renders without a display); the viewport tests also need a display (xvfb-run python3 -m unittest ...) and are skipped
without one. Everything is skipped, with the reason, if the library is not built:  python3 tools/prowl2d_so.py
"""
import array
import os
import unittest

from .demo import make_demo_project
from .slime_demo import SlimeDriver, make_slime_project
from .engine import (BODY_DYNAMIC, BODY_STATIC, BTN_LEFT, EV_MOUSE_DOWN, EV_WHEEL, KEY_HOME, Engine, EngineError, Viewport, find_library, floats_ptr)

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
        self.assertEqual(self.e.p2d_version(), 5)
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

    def test_velocity_impulse_and_blast_move_bodies(self):
        e = self.e
        e.p2d_gravity(0.0, 0.0)
        a = e.p2d_new_node()
        e.p2d_set_pos(a, 0.0, 0.0)
        e.p2d_add_body(a, BODY_DYNAMIC)
        e.p2d_add_box(a, 1.0, 1.0)
        e.p2d_set_velocity(a, 3.0, 0.0)
        e.p2d_step(1 / 60.0)
        self.assertAlmostEqual(e.p2d_velocity_x(a), 3.0, delta=0.05)
        e.p2d_set_velocity(a, 0.0, 0.0)
        e.p2d_impulse(a, 0.0, 5.0)
        e.p2d_step(1 / 60.0)
        self.assertGreater(e.p2d_velocity_y(a), 0.5)
        e.p2d_set_velocity(a, 0.0, 0.0)
        e.p2d_set_pos(a, 1.0, 0.0)
        e.p2d_step(1 / 60.0)
        self.assertEqual(e.p2d_blast(0.0, 0.0, 3.0, 60.0), 1)
        e.p2d_step(1 / 60.0)
        self.assertGreater(e.p2d_velocity_x(a), 0.0)                       # pushed away from the blast
        self.assertEqual(e.p2d_velocity_x(-1), 0.0)
        e.p2d_set_velocity(300, 1.0, 1.0)                                  # harmless

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


class Effects(unittest.TestCase):
    """gfx_effect through Engine.effect: an effect changes the picture drawn before it, inside the clip, and nothing drawn after it."""

    def setUp(self):
        ensure_started()
        self.e = _engine.lib

    def box(self, x, grey):
        """A square 4 world units wide, centred at (x, 0), of one grey (the camera below shows 10 units over the picture's height: 48 pixels to the unit)."""
        return array.array("f", [x, 0.0, 2.0, 2.0, 0.0, grey, grey, grey, 1.0, 0.0, 0.0, 0.0])

    def pixel(self, wx):
        return rgb(self.e.gfx_pixel(int(W / 2 + wx * (H / 10.0)), H // 2))

    def test_an_effect_changes_what_was_drawn_before_it_and_not_what_comes_after(self):
        e = self.e
        e.gfx_camera(0.0, 0.0, 5.0, 0.0, 0.0, 0.0)
        e.gfx_begin()
        before, after = self.box(-4.0, 0.2), self.box(4.0, 0.2)
        e.gfx_sprites(floats_ptr(before), 1)
        self.assertTrue(_engine.effect("bright_contrast", {"brightness": 0.3}))
        e.gfx_sprites(floats_ptr(after), 1)
        e.gfx_end()
        for got, want in zip(self.pixel(-4.0), (128, 128, 128)):          # 0.2 + 0.3 = 0.5
            self.assertAlmostEqual(got, want, delta=2)
        for got, want in zip(self.pixel(4.0), (51, 51, 51)):              # drawn after the effect: 0.2 as it was
            self.assertAlmostEqual(got, want, delta=1)
        for got, want in zip(rgb(e.gfx_pixel(5, H - 5)), (77, 77, 77)):   # the background, black, is brightened as well
            self.assertAlmostEqual(got, want, delta=2)

    def test_an_effect_stays_inside_the_clip(self):
        e = self.e
        e.gfx_camera(0.0, 0.0, 5.0, 0.0, 0.0, 0.0)
        e.gfx_begin()
        b = self.box(0.0, 0.2)
        e.gfx_sprites(floats_ptr(b), 1)
        e.gfx_clip(W // 2, 0, W // 2, H)                                   # the right half only
        _engine.effect("bright_contrast", {"brightness": 0.3})
        e.gfx_clip_reset()
        e.gfx_end()
        left, right = rgb(e.gfx_pixel(W // 2 - 20, H // 2)), rgb(e.gfx_pixel(W // 2 + 20, H // 2))
        for got, want in zip(left, (51, 51, 51)):
            self.assertAlmostEqual(got, want, delta=1)
        for got, want in zip(right, (128, 128, 128)):
            self.assertAlmostEqual(got, want, delta=2)

    def test_what_the_api_refuses(self):
        self.assertEqual(self.e.gfx_effect(999, None, 0), 0)
        self.assertEqual(self.e.gfx_effect(0, None, 0), 0)
        with self.assertRaises(KeyError):
            _engine.effect("no_such_effect")


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


@unittest.skipUnless(os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"), "needs a display (run under xvfb-run)")
class SandInTheViewport(unittest.TestCase):
    """The GPU sand in the viewport: painted with the mouse, falls, stops at the level's solid tiles, and is drawn."""

    cell_px = ViewportWindow.cell_px

    def setUp(self):
        self.project = make_demo_project()
        self.vp = Viewport(_engine, self.project, W, H)
        self.vp.show_level(self.project.levels[0])
        try:
            self.vp.open()
        except EngineError as e:
            self.skipTest(str(e))
        self.lib = _engine.lib
        if not _engine.has_sand:
            self.skipTest("this libprowl2d.so has no GPU sand")
        self.vp.sand.enabled = True
        self.vp.sand.element = 2
        self.vp.sand.radius = 3
        self.vp.fit()
        self.vp.tick(0.0)
        if not self.vp.sand.enabled:
            self.skipTest(self.vp.sand.error)

    def tearDown(self):
        self.vp.close()

    def paint_at_tile(self, col, row, held=1):
        from .engine import EV_MOUSE_UP
        x, y = self.cell_px(col, row)
        self.lib.gfx_inject_event(EV_MOUSE_DOWN, x, y, BTN_LEFT, 0)
        for _ in range(held):
            self.vp.tick(0.0)
        self.lib.gfx_inject_event(EV_MOUSE_UP, x, y, BTN_LEFT, 0)
        self.vp.tick(0.0)

    def test_the_stone_of_the_level_is_in_the_grid(self):
        sand, water, stone = self.vp.sand_counts()
        solid = {k for k, t in self.project.tiles.items() if t.solid and not t.dynamic}
        lv = self.vp.level
        tiles = sum(1 for c in lv.cells if c in solid)
        self.assertGreater(tiles, 0)
        self.assertEqual(stone, tiles * self.vp.sand_dims()[2] ** 2)
        self.assertEqual((sand, water), (0, 0))

    def test_the_mouse_paints_the_chosen_element_and_nothing_is_lost_as_it_falls(self):
        self.vp.sand.element = 3
        self.paint_at_tile(8, 1)
        _, water, _ = self.vp.sand_counts()
        self.assertGreater(water, 10)
        for _ in range(120):
            self.vp.tick(0.0)
        self.assertEqual(self.vp.sand_counts()[1], water)                      # water falls and spreads; it is not made or lost

    def test_it_is_drawn_and_the_pause_stops_it(self):
        self.paint_at_tile(8, 1)
        h = self.lib.gfx_frame_hash()
        self.vp.tick(0.0)
        self.assertNotEqual(h, self.lib.gfx_frame_hash())                      # a falling pile changes the picture
        self.vp.sand.paused = True
        self.vp.tick(0.0)
        h = self.lib.gfx_frame_hash()
        self.vp.tick(0.0)
        self.assertEqual(h, self.lib.gfx_frame_hash())

    def test_keys_pick_the_element_and_c_clears_the_grid(self):
        from .engine import EV_KEY_DOWN
        self.lib.gfx_inject_event(EV_KEY_DOWN, ord("3"), 0, 0, 0)
        self.vp.tick(0.0)
        self.assertEqual(self.vp.sand.element, 3)
        self.paint_at_tile(8, 1)
        self.assertGreater(self.vp.sand_counts()[1], 0)
        self.lib.gfx_inject_event(EV_KEY_DOWN, ord("C"), 0, 0, 0)
        self.vp.tick(0.0)
        self.assertEqual(self.vp.sand_counts()[1], 0)


class SlimeJumpDestructInTheViewport(unittest.TestCase):
    def test_the_slime_digs_through_the_dirt_bursts_the_crates_and_reaches_the_goal(self):
        project = make_slime_project()
        vp = Viewport(_engine, project, W, H)
        vp.show_level(project.levels[0])
        vp.driver = SlimeDriver()
        try:
            vp.open()
        except EngineError as e:
            self.skipTest(str(e))
        try:
            vp.toggle_play()
            lv = vp.level
            dirt_before = sum(1 for c in vp.cells if c == "\U0001f7eb")
            crates_before = sum(1 for a in vp.actors if a.get("tile") == "\U0001f4e6")
            self.assertEqual((dirt_before, crates_before), (18, 4))
            for _ in range(60 * 30):
                if not vp.tick(1 / 60.0) or vp.driver.done:
                    break
            d = vp.driver
            self.assertTrue(d.won, d.log)
            self.assertEqual(len([l for l in d.log if l.startswith("boom")]), 4)       # all four crates went off
            self.assertLess(sum(1 for c in vp.cells if c == "\U0001f7eb"), dirt_before)   # the dirt was dug
            self.assertEqual(lv.cells.count("\U0001f7eb"), 18)                         # ...in the play copy only: the level is as it was
            self.assertFalse(project.dirty)                                            # and playing (the blinking eyes) is not an edit
        finally:
            vp.close()


if __name__ == "__main__":
    unittest.main()
