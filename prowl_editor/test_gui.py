"""Tests of the editor's windows (offscreen Qt; no display needed): real mouse events on the canvases, the undo stack, the file operations the File menu runs.
    QT_QPA_PLATFORM=offscreen python3 -m unittest prowl_editor.test_gui     (or: python3 prowl.py --selftest)"""
import os
import tempfile
import unittest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PyQt5 import QtCore, QtGui, QtWidgets
from PyQt5.QtCore import Qt
from PyQt5.QtTest import QTest

from . import asciiart, fileio
from .demo import make_demo_project
from .gui import Studio, create_windows
from .model import Project

_app = None


def setUpModule():
    global _app
    _app = QtWidgets.QApplication.instance() or QtWidgets.QApplication([])


def snapshot(project):
    return {s.name: [s.rgba(project.palette, i) for i in range(len(s.frames))] for s in project.sprites}


def drag(widget, a, b, button=Qt.LeftButton):
    """Press at a, move to b, release: the move is sent to the widget as the mouse-move event a real drag makes."""
    QTest.mousePress(widget, button, Qt.NoModifier, a)
    ev = QtGui.QMouseEvent(QtCore.QEvent.MouseMove, QtCore.QPointF(b), Qt.NoButton, button, Qt.NoModifier)
    QtWidgets.QApplication.sendEvent(widget, ev)
    QTest.mouseRelease(widget, button, Qt.NoModifier, b)


class Base(unittest.TestCase):
    def setUp(self):
        self.studio = Studio(make_demo_project())
        self.main, self.windows = create_windows(self.studio)
        for w in [self.main] + list(self.windows.values()):
            w.show()
        _app.processEvents()
        self.sprites = self.windows["sprites"]
        self.levels = self.windows["levels"]

    def tearDown(self):
        self.main.viewport.close()
        for w in [self.main] + list(self.windows.values()):
            w.hide()
            w.deleteLater()
        _app.processEvents()

    def sprite_pos(self, x, y):
        cell, ox, oy, _, _ = self.sprites.canvas._geometry()
        return QtCore.QPoint(ox + x * cell + cell // 2, oy + y * cell + cell // 2)

    def level_pos(self, x, y):
        c = self.levels.canvas.cell
        return QtCore.QPoint(x * c + c // 2, y * c + c // 2)


class SpriteEditing(Base):
    def test_click_paints_the_current_palette_index_and_undo_redo_restore_it(self):
        s = self.studio.sprite
        red = self.studio.project.palette.index_of("R")
        self.studio.select_color(red)
        before = bytes(s.frames[0])
        QTest.mouseClick(self.sprites.canvas, Qt.LeftButton, Qt.NoModifier, self.sprite_pos(2, 3))
        self.assertEqual(s.get(0, 2, 3), red)
        self.assertTrue(self.studio.project.dirty)
        self.assertIn("*", self.main.windowTitle())
        self.studio.do_undo()
        self.assertEqual(bytes(s.frames[0]), before)
        self.studio.do_redo()
        self.assertEqual(s.get(0, 2, 3), red)

    def test_right_click_and_eraser_make_transparent(self):
        s = self.studio.sprite
        QTest.mouseClick(self.sprites.canvas, Qt.RightButton, Qt.NoModifier, self.sprite_pos(0, 0))
        self.assertEqual(s.get(0, 0, 0), 0)
        self.sprites._tool("eraser")
        QTest.mouseClick(self.sprites.canvas, Qt.LeftButton, Qt.NoModifier, self.sprite_pos(1, 0))
        self.assertEqual(s.get(0, 1, 0), 0)

    def test_a_drag_is_one_undo_step_and_leaves_no_gaps(self):
        s = self.studio.sprite
        blue = self.studio.project.palette.index_of("B")
        self.studio.select_color(blue)
        canvas = self.sprites.canvas
        drag(canvas, self.sprite_pos(0, 4), self.sprite_pos(7, 4))          # a fast drag: the cells between must be painted too
        self.assertEqual([s.get(0, x, 4) for x in range(8)], [blue] * 8)
        self.studio.do_undo()
        self.assertNotIn(blue, [s.get(0, x, 4) for x in range(8)])

    def test_fill_and_picker(self):
        s = self.studio.sprite
        g = self.studio.project.palette.index_of("G")
        self.sprites._tool("fill")
        self.studio.select_color(g)
        QTest.mouseClick(self.sprites.canvas, Qt.LeftButton, Qt.NoModifier, self.sprite_pos(0, 1))      # a brick's red patch, closed on both sides
        self.assertEqual(s.get(0, 0, 1), g)
        self.assertEqual(s.get(0, 3, 1), g)
        self.assertNotEqual(s.get(0, 4, 1), g)                                                             # the mortar stops the fill
        self.sprites._tool("picker")
        QTest.mouseClick(self.sprites.canvas, Qt.LeftButton, Qt.NoModifier, self.sprite_pos(4, 1))
        self.assertEqual(self.studio.color, self.studio.project.palette.index_of("S"))

    def test_changing_a_palette_colour_changes_the_picture_everywhere(self):
        proj = self.studio.project
        idx = proj.palette.index_of("R")
        before = self.sprites.canvas.grab().toImage()
        proj.palette.set_color(idx, (0, 0, 255))
        self.studio.edited("palette")
        after = self.sprites.canvas.grab().toImage()
        self.assertNotEqual(before, after)
        self.assertEqual(proj.sprite("brick").rgba(proj.palette, 0)[8 * 4 * 1:8 * 4 * 1 + 4], bytes((0, 0, 255, 255)))     # pixel (0, 1) was red

    def test_ascii_text_pane_edits_the_frame_and_can_resize(self):
        s = self.studio.sprite
        self.sprites.text.setPlainText("RR.\nRYR\n")
        self.sprites._apply_text()
        self.assertEqual((s.width, s.height), (3, 2))
        self.assertEqual(s.rows(self.studio.project.palette, 0), ["RR.", "RYR"])
        self.studio.do_undo()
        self.assertEqual((s.width, s.height), (8, 8))

    def test_unknown_character_in_the_text_pane_is_refused(self):
        s = self.studio.sprite
        before = [bytes(f) for f in s.frames]
        from unittest import mock
        with mock.patch("prowl_editor.gui.error_box") as box:
            self.sprites.text.setPlainText("RR\n!!\n")
            self.sprites._apply_text()
        self.assertTrue(box.called)
        self.assertEqual([bytes(f) for f in s.frames], before)

    def test_frames_and_animation_controls(self):
        coin = self.studio.project.sprite("coin")
        self.studio.select_sprite(coin)
        _app.processEvents()
        self.assertEqual(self.sprites.frames.count(), 4)
        self.sprites._frame_op("dup")
        self.assertEqual(len(coin.frames), 5)
        self.sprites._frame_op("del")
        self.assertEqual(len(coin.frames), 4)
        self.sprites.play.setChecked(True)
        self.assertTrue(self.sprites.timer.isActive())
        n = self.sprites.anim_frame
        self.sprites._advance()
        self.assertEqual(self.sprites.anim_frame, (n + 1) % 4)
        self.sprites.play.setChecked(False)
        self.assertFalse(self.sprites.timer.isActive())


class LevelEditing(Base):
    def test_paint_drag_erase_and_undo(self):
        lv = self.studio.level
        brick = "\U0001f9f1"
        self.studio.select_tile(brick)
        canvas = self.levels.canvas
        drag(canvas, self.level_pos(0, 0), self.level_pos(5, 0))
        self.assertEqual([lv.get(x, 0) for x in range(6)], [brick] * 6)
        QTest.mouseClick(canvas, Qt.RightButton, Qt.NoModifier, self.level_pos(2, 0))
        self.assertEqual(lv.get(2, 0), "")
        self.studio.do_undo()
        self.assertEqual(lv.get(2, 0), brick)
        self.studio.do_undo()
        self.assertEqual([lv.get(x, 0) for x in range(6)], [""] * 6)

    def test_fill_and_pick(self):
        lv = self.studio.level
        coin = "\U0001fa99"
        self.levels._tool("fill")
        self.studio.select_tile(coin)
        QTest.mouseClick(self.levels.canvas, Qt.LeftButton, Qt.NoModifier, self.level_pos(0, 0))
        self.assertEqual(lv.get(23, 0), coin)                 # the whole open sky was one region
        self.assertEqual(lv.get(0, 10), "\U0001f7e9")         # the grass is not touched
        self.levels._tool("pick")
        QTest.mouseClick(self.levels.canvas, Qt.LeftButton, Qt.NoModifier, self.level_pos(0, 10))
        self.assertEqual(self.studio.tile, "\U0001f7e9")

    def test_resize_keeps_the_top_left(self):
        lv = self.studio.level
        corner = lv.get(0, 0)
        self.levels.w_spin.setValue(30)
        self.levels.h_spin.setValue(8)
        self.levels._resize()
        self.assertEqual((lv.width, lv.height), (30, 8))
        self.assertEqual(lv.get(0, 0), corner)
        self.studio.do_undo()
        self.assertEqual((lv.width, lv.height), (24, 12))

    def test_text_pane_creates_tiles_for_new_emoji(self):
        lv = self.studio.level
        text = asciiart.level_to_text(lv, self.studio.project).split("\n")
        text[0] = "\U0001f333" + text[0][1:]
        self.levels.text.setPlainText("\n".join(text))
        self.levels._apply_text()
        self.assertEqual(lv.get(0, 0), "\U0001f333")
        self.assertEqual(self.studio.project.tiles["\U0001f333"].name, "deciduous tree")

    def test_tile_operations(self):
        st = self.studio
        brick = "\U0001f9f1"
        st.edit_tile(brick, "\U0001f4e6", "crate", "brick", True)       # a new emoji for the tile: its cells follow
        self.assertNotIn(brick, st.project.tiles)
        self.assertEqual(st.project.sprite_for_tile(st.project.tiles["\U0001f4e6"]).name, "brick")
        self.assertIn("\U0001f4e6", st.level.cells)
        self.assertNotIn(brick, st.level.cells)
        st.remove_tile("\U0001f4e6")
        self.assertNotIn("\U0001f4e6", st.level.cells)

    def test_renaming_a_sprite_keeps_the_tile_that_names_it(self):
        st = self.studio
        t = st.project.tiles["\U0001f9f1"]
        t.sprite = "brick"
        st.select_sprite(st.project.sprite("brick"))
        st.rename_sprite("wall")
        self.assertEqual(t.sprite, "wall")
        self.assertEqual(st.project.sprite_for_tile(t).name, "wall")


class Files(Base):
    def test_save_open_roundtrip_through_the_window(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "game.json")
            self.assertTrue(self.main._save(path))
            self.assertFalse(self.studio.project.dirty)
            before = snapshot(self.studio.project)
            self.studio.set_project(Project("other"))
            self.assertTrue(self.main.open_path(path))
            self.assertEqual(snapshot(self.studio.project), before)
            self.assertEqual(self.studio.project.levels[0].name, "first steps")

    def test_opening_a_bad_file_says_so_and_keeps_the_project(self):
        from unittest import mock
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "bad.json")
            with open(path, "w") as f:
                f.write('{"format": "prowl2d-project", "version": 1, "palette": [{"key": "."}], "sprites": [{"name": "x", "frames": [["ZZ"]]}]}')
            keep = self.studio.project
            with mock.patch("prowl_editor.gui.error_box") as box:
                self.assertFalse(self.main.open_path(path))
            self.assertTrue(box.called)
            self.assertIs(self.studio.project, keep)

    def test_export_everything_then_import_it_all_back(self):
        with tempfile.TemporaryDirectory() as d:
            n = fileio.export_folder(self.studio.project, d)
            self.assertEqual(n, 4)                                                    # 3 sprites + 1 level
            self.assertEqual(sorted(os.listdir(os.path.join(d, "sprites"))), ["brick.txt", "coin.txt", "grass.txt"])
            text = fileio.read_text(os.path.join(d, "levels", "first_steps.txt"))
            self.assertIn("# \U0001fa99 = coin", text)
            self.assertIn("\U0001f7e9" * 24, text)
            fresh = Studio(Project("fresh"))
            main, windows = create_windows(fresh)
            main.import_sprites([os.path.join(d, "sprites", f) for f in ("brick.txt", "grass.txt", "coin.txt")])
            main.import_level([os.path.join(d, "levels", "first_steps.txt")])
            self.assertEqual({k: v for k, v in snapshot(fresh.project).items()}, snapshot(self.studio.project))
            self.assertEqual(fresh.project.levels[0].cells, self.studio.project.levels[0].cells)
            self.assertEqual({k: t.solid for k, t in fresh.project.tiles.items()}, {k: t.solid for k, t in self.studio.project.tiles.items()})
            main.viewport.close()

    def test_import_names_plain_art_after_its_file(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "heart.txt")
            with open(path, "w", encoding="utf-8") as f:
                f.write(".R.R.\nRRRRR\n.RRR.\n..R..\n")
            self.main.import_sprites([path])
            s = self.studio.project.sprite("heart")
            self.assertIsNotNone(s)
            self.assertEqual((s.width, s.height), (5, 4))
            self.assertIs(self.studio.sprite, s)

    def _unity_project(self, d):
        from .test_core import RED, make_png, rgba_rows, sheet_meta
        tex = os.path.join(d, "Assets", "Art")
        os.makedirs(tex)
        sheet = [[RED, RED, (1, 2, 3, 255), (1, 2, 3, 255)]]
        with open(os.path.join(tex, "sheet.png"), "wb") as f:
            f.write(make_png(4, 1, rgba_rows(sheet)))
        with open(os.path.join(tex, "sheet.png.meta"), "w", encoding="utf-8") as f:
            f.write(sheet_meta(2, [("left", 0, 0, 2, 1), ("right", 2, 0, 2, 1)]))
        return os.path.join(tex, "sheet.png")

    def _unity_scene_project(self, d):
        """A Unity project with a 2x2-px-per-slice sheet at 2 pixels per unit (each sprite is one world unit) and a scene with a solid 'left' at x=0 and a 'right' at x=1."""
        from .test_core import RED, SceneBuilder, make_png, rgba_rows, sheet_meta
        tex = os.path.join(d, "Assets", "Art")
        os.makedirs(tex)
        row = [RED, RED, (1, 2, 3, 255), (1, 2, 3, 255)]
        with open(os.path.join(tex, "sheet.png"), "wb") as f:
            f.write(make_png(4, 2, rgba_rows([row, row])))
        with open(os.path.join(tex, "sheet.png.meta"), "w", encoding="utf-8") as f:
            f.write(sheet_meta(2, [("left", 0, 0, 2, 2), ("right", 2, 0, 2, 2)], ppu=2))
        guid = "0123456789abcdef0123456789abcdef"
        b = SceneBuilder()
        b.add("A", 0, 0, (guid, 21300000), collider=61)
        b.add("B", 1, 0, (guid, 21300002))
        scenes = os.path.join(d, "Assets", "Scenes")
        os.makedirs(scenes)
        with open(os.path.join(scenes, "Main.unity"), "w", encoding="utf-8") as f:
            f.write(b.text())

    def test_import_unity_adds_sprites_from_a_project_folder_and_selects_the_first(self):
        with tempfile.TemporaryDirectory() as d:
            self._unity_project(d)
            before = len(self.studio.project.sprites)
            self.main.import_unity([d])
            names = [s.name for s in self.studio.project.sprites]
            self.assertEqual(names[before:], ["left", "right"])
            self.assertIs(self.studio.sprite, self.studio.project.sprite("left"))
            self.assertTrue(self.studio.project.dirty)
            self.assertEqual(self.main.sprites.count(), before + 2)                       # the project window's list shows them

    def test_import_unity_pngs_takes_files(self):
        with tempfile.TemporaryDirectory() as d:
            png = self._unity_project(d)
            self.main.import_unity_pngs([png])
            self.assertIsNotNone(self.studio.project.sprite("left"))

    def test_import_unity_says_what_it_skipped_and_a_bad_path_is_a_message(self):
        from unittest import mock
        with tempfile.TemporaryDirectory() as d:
            with mock.patch("prowl_editor.gui.QtWidgets.QMessageBox.information") as info, mock.patch("prowl_editor.gui.error_box") as box:
                self.main.import_unity([d])                                              # no textures: a note, no sprites, no crash
                self.assertTrue(info.called)
                self.main.import_unity([os.path.join(d, "missing")])
                self.assertTrue(box.called)

    def test_import_unity_project_adds_sprites_and_a_level_per_scene_and_selects_them(self):
        from unittest import mock
        with tempfile.TemporaryDirectory() as d:
            self._unity_scene_project(d)
            with mock.patch("prowl_editor.gui.QtWidgets.QMessageBox.information") as info:      # (a real dialog would block the test run: nothing here may need one)
                self.main.import_unity_project([d])
            self.assertFalse(info.called, "a clean project gives no notes")
            p = self.studio.project
            lv = p.level("Main")
            self.assertIsNotNone(lv)
            self.assertIs(self.studio.level, lv)
            self.assertEqual((lv.width, lv.height), (2, 1))
            self.assertEqual([p.tiles[lv.get(x, 0)].sprite for x in (0, 1)], ["left", "right"])
            self.assertTrue(p.tiles[lv.get(0, 0)].solid and not p.tiles[lv.get(1, 0)].solid)
            self.assertEqual(self.main.levels.count(), len(p.levels))                      # the project window's list shows it
            self.assertIn("Main", [self.main.levels.item(i).text().split("  ")[0] for i in range(self.main.levels.count())])

    def test_unity_import_notes_are_shown_in_one_dialog(self):
        from unittest import mock
        with tempfile.TemporaryDirectory() as d:
            self._unity_scene_project(d)
            os.remove(os.path.join(d, "Assets", "Art", "sheet.png"))                         # the scene now names sprites that were not imported
            with mock.patch("prowl_editor.gui.QtWidgets.QMessageBox.information") as info:
                self.main.import_unity_project([d])
            self.assertEqual(info.call_count, 1)
            self.assertIn("could not be found", info.call_args[0][2])

    def test_the_sprites_only_unity_import_makes_no_levels(self):
        from unittest import mock
        with tempfile.TemporaryDirectory() as d:
            self._unity_scene_project(d)
            before = len(self.studio.project.levels)
            with mock.patch("prowl_editor.gui.QtWidgets.QMessageBox.information"):
                self.main.import_unity([d])
            self.assertEqual(len(self.studio.project.levels), before)
            self.assertIsNotNone(self.studio.project.sprite("left"))

    def test_the_import_menu_lists_the_unity_entries(self):
        texts = [a.text() for m in self.main.menuBar().findChildren(QtWidgets.QMenu) for a in m.actions()]
        self.assertIn("Unity project: sprites and scenes as levels...", texts)
        self.assertIn("Unity 2D sprites from a project folder...", texts)
        self.assertIn("Unity PNG textures...", texts)

    def test_new_project_asks_nothing_when_clean(self):
        self.studio.project.dirty = False
        self.main.file_new()
        self.assertEqual(self.studio.project.name, "untitled")
        self.assertEqual(len(self.studio.project.sprites), 1)


class Palette_(Base):
    def test_remove_and_move_through_the_window_keep_colours(self):
        pw = self.windows["palette"]
        proj = self.studio.project
        want = snapshot(proj)
        pw.table.selectRow(5)
        pw.move_entry(1)
        self.assertEqual(snapshot(proj), want)
        self.assertEqual(self.studio.color, 6)
        self.assertEqual(pw.table.rowCount(), len(proj.palette))

    def test_editing_a_key_cell_changes_the_key_and_a_duplicate_is_refused(self):
        from unittest import mock
        pw = self.windows["palette"]
        proj = self.studio.project
        i = proj.palette.index_of("R")
        pw.table.item(i, 1).setText("Z")
        self.assertEqual(proj.palette.key_of(i), "Z")
        with mock.patch("prowl_editor.gui.error_box") as box:
            pw.table.item(i, 1).setText("K")
        self.assertTrue(box.called)
        self.assertEqual(proj.palette.key_of(i), "Z")


if __name__ == "__main__":
    unittest.main()


class Effects(Base):
    def test_add_edit_reorder_disable_duplicate_remove_and_undo_each(self):
        st, lv = self.studio, self.studio.level
        self.assertEqual(lv.effects, [])
        self.assertEqual(st.add_effect("blend"), 0)
        self.assertEqual(st.add_effect("tint"), 1)
        self.assertEqual(st.add_effect("nope"), -1)
        self.assertEqual(lv.effects[0]["values"]["mode"], 1)               # an enum is kept as its index, not as a float
        self.assertEqual(st.set_effect_value(1, "amount", 0.8), 0.8)
        self.assertEqual(st.set_effect_value(0, "mode", "Screen"), 8)       # a label is accepted
        self.assertEqual(st.set_effect_value(0, "opacity", 7), 1.0)         # brought into its range
        with self.assertRaises(KeyError):
            st.set_effect_value(0, "nope", 1)
        with self.assertRaises(ValueError):
            st.set_effect_value(0, "opacity", float("nan"))
        self.assertEqual(st.move_effect(1, -1), 0)
        self.assertEqual([e["effect"] for e in lv.effects], ["tint", "blend"])
        st.set_effect_enabled(0, False)
        self.assertFalse(lv.effects[0]["enabled"])
        self.assertEqual(st.duplicate_effect(1), 2)
        st.remove_effect(0)
        self.assertEqual([e["effect"] for e in lv.effects], ["blend", "blend"])
        for _ in range(4):                                                  # remove, duplicate, disable, move
            st.do_undo()
        self.assertEqual([e["effect"] for e in lv.effects], ["blend", "tint"])
        self.assertTrue(lv.effects[0]["enabled"])
        st.do_redo()
        self.assertEqual([e["effect"] for e in lv.effects], ["tint", "blend"])

    def test_a_drag_on_one_parameter_is_one_undo_step(self):
        st = self.studio
        st.add_effect("tint")
        for v in (0.1, 0.2, 0.3, 0.4):
            st.set_effect_value(0, "amount", v)
        st.set_effect_value(0, "color", [0, 0, 1, 1])
        st.do_undo()                                                         # the color
        self.assertEqual(st.level.effects[0]["values"]["amount"], 0.4)
        st.do_undo()                                                         # the whole drag of amount
        self.assertEqual(st.level.effects[0]["values"]["amount"], 0.5)

    def test_the_window_lists_the_stack_and_its_controls_edit_the_values(self):
        st, win = self.studio, self.windows["effects"]
        win.show()
        for name in ("tint", "bright_contrast"):
            win._add(name)
        self.assertEqual(win.stack.count(), 2)
        win.stack.setCurrentRow(0)
        spin = win.form.widgets["amount"]
        spin.setValue(0.9)
        self.assertEqual(st.level.effects[0]["values"]["amount"], 0.9)
        win.form.widgets["color"].set_color([0.2, 0.3, 0.4, 1.0])           # (what the color dialog does)
        win.form.widgets["color"].changed.emit([0.2, 0.3, 0.4, 1.0])
        self.assertEqual(st.level.effects[0]["values"]["color"], [0.2, 0.3, 0.4, 1.0])
        win.stack.item(1).setCheckState(Qt.Unchecked)
        self.assertFalse(st.level.effects[1]["enabled"])
        win.stack.setCurrentRow(1)
        self.assertEqual(set(win.form.widgets), {"brightness", "contrast"})
        self.assertFalse(win.buttons["down"].isEnabled())
        win._move(-1)
        self.assertEqual([e["effect"] for e in st.level.effects], ["bright_contrast", "tint"])
        self.assertEqual(win.stack.currentRow(), 0)
        st.do_undo()                                                         # the panel follows an undo
        self.assertEqual(win.stack.item(0).text(), "Tint")

    def test_effects_are_saved_with_the_project_and_a_copied_level_keeps_them(self):
        st = self.studio
        st.add_effect("blend")
        st.set_effect_value(0, "mode", "Overlay")
        st.duplicate_level()
        self.assertEqual(st.level.effects, st.project.levels[0].effects)
        self.assertIsNot(st.level.effects, st.project.levels[0].effects)
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "p.json")
            st.project.save(path)
            back = Project.load(path)
        self.assertEqual(back.levels[0].effects[0]["values"]["mode"], 12)
        self.assertEqual(len(back.levels), len(st.project.levels))


class SandWindow_(Base):
    def test_the_window_edits_the_settings_the_viewport_reads(self):
        w, st = self.windows["sand"], self.studio.sand
        self.assertFalse(st.enabled)
        w.enabled.setChecked(True)
        w.element_buttons[3].setChecked(True)
        w.radius.setValue(7)
        w.speed.setValue(5)
        w.pause.setChecked(True)
        self.assertEqual((st.enabled, st.element, st.radius, st.speed, st.paused), (True, 3, 7, 5, True))
        g = st.generation
        w.scale.setValue(4)
        self.assertEqual(st.scale, 4)
        self.assertGreater(st.generation, g)                                    # a new scale starts the grid again

    def test_the_window_shows_what_the_settings_say_and_a_sand_button_opens_it(self):
        w, st = self.windows["sand"], self.studio.sand
        st.enabled, st.element, st.radius = True, 1, 9
        w.refresh()
        self.assertTrue(w.enabled.isChecked())
        self.assertTrue(w.element_buttons[1].isChecked())
        self.assertEqual(w.radius.value(), 9)
        w.hide()
        self.main.show_window("sand")
        self.assertTrue(w.isVisible())


class Lights(Base):
    def test_add_edit_undo_and_the_limit(self):
        from . import lights as L
        st, lv = self.studio, self.studio.level
        self.assertEqual(st.add_light("point", 3, 2), 0)
        self.assertEqual(st.add_light("spot"), 1)
        self.assertEqual((lv.lights[1]["x"], lv.lights[1]["y"]), (lv.width / 2.0, lv.height / 2.0))
        self.assertEqual(st.add_light("laser"), -1)
        self.assertEqual(st.light, 1)
        for v in (4.0, 5.0, 6.0):                                       # a drag: one undo step
            st.set_light(0, x=v, y=2.5)
        self.assertEqual((lv.lights[0]["x"], lv.lights[0]["y"]), (6.0, 2.5))
        st.do_undo()
        self.assertEqual((lv.lights[0]["x"], lv.lights[0]["y"]), (3.0, 2.0))
        self.assertEqual(st.set_light(0, intensity=50)["intensity"], 4.0)
        with self.assertRaises(ValueError):
            st.set_light(0, intensity=float("nan"))
        st.set_lighting(glow=0.9)
        self.assertEqual(lv.lighting["glow"], 0.9)
        st.do_undo()
        self.assertEqual(lv.lighting["glow"], 0.15)
        st.remove_light(0)
        self.assertEqual(len(lv.lights), 1)
        st.do_undo()
        self.assertEqual(len(lv.lights), 2)
        while len(lv.lights) < L.MAX_LIGHTS:
            st.add_light("point")
        self.assertEqual(st.add_light("point"), -1)

    def test_the_lights_tool_adds_selects_drags_and_removes_on_the_level(self):
        st, canvas = self.studio, self.levels.canvas
        canvas.tool = "light"
        c = canvas.cell
        QTest.mouseClick(canvas, Qt.LeftButton, Qt.NoModifier, QtCore.QPoint(5 * c, 4 * c))      # empty: a new light, there
        lv = st.level
        self.assertEqual(len(lv.lights), 1)
        self.assertAlmostEqual(lv.lights[0]["x"], 5.0, places=1)
        drag(canvas, QtCore.QPoint(5 * c, 4 * c), QtCore.QPoint(9 * c, 6 * c))                  # on the light: move it
        self.assertEqual(len(lv.lights), 1)
        self.assertAlmostEqual(lv.lights[0]["x"], 9.0, places=1)
        self.assertAlmostEqual(lv.lights[0]["y"], 6.0, places=1)
        drag(canvas, QtCore.QPoint(9 * c, 6 * c), QtCore.QPoint(10000, 10000))                  # dragged off the level: kept inside it
        self.assertEqual((lv.lights[0]["x"], lv.lights[0]["y"]), (float(lv.width), float(lv.height)))
        self.assertEqual(lv.cells.count(""), len(lv.cells) - sum(1 for g in lv.cells if g))     # (the tool never paints tiles)
        before = list(lv.cells)
        QTest.mouseClick(canvas, Qt.RightButton, Qt.NoModifier, QtCore.QPoint(lv.width * c, lv.height * c))
        self.assertEqual(lv.lights, [])
        self.assertEqual(lv.cells, before)
        st.do_undo()
        self.assertEqual(len(lv.lights), 1)

    def test_the_window_lists_the_lights_and_its_controls_edit_them(self):
        st, win = self.studio, self.windows["lights"]
        win.show()
        win.buttons["spot"].click()
        win.buttons["point"].click()
        self.assertEqual(win.list.count(), 2)
        win.list.setCurrentRow(0)
        self.assertEqual(st.light, 0)
        self.assertTrue(win.form.widgets["cone"].isEnabled())
        win.form.widgets["radius"].setValue(7.5)
        self.assertEqual(st.level.lights[0]["radius"], 7.5)
        win.form.widgets["kind"].setCurrentIndex(0)                                 # a point light has no direction or cone
        _app.processEvents()
        self.assertEqual(st.level.lights[0]["kind"], "point")
        self.assertFalse(win.form.widgets["cone"].isEnabled())
        win.lighting.widgets["glow"].setValue(0.6)
        self.assertEqual(st.level.lighting["glow"], 0.6)
        win.list.item(1).setCheckState(Qt.Unchecked)
        self.assertFalse(st.level.lights[1]["enabled"])
        win.enabled.setChecked(False)
        self.assertFalse(st.level.lighting["enabled"])
        st.set_light(0, x=2.0)                                                       # moved elsewhere (the level editor): the controls follow
        self.assertEqual(win.form.widgets["x"].value(), 2.0)
        win.buttons["del"].click()
        self.assertEqual(win.list.count(), 1)

    def test_lights_are_saved_and_a_copied_level_keeps_them(self):
        st = self.studio
        st.add_light("spot", 4, 3)
        st.set_lighting(exposure=2.0)
        st.duplicate_level()
        self.assertEqual(st.level.lights, st.project.levels[0].lights)
        self.assertIsNot(st.level.lights, st.project.levels[0].lights)
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "p.json")
            st.project.save(path)
            back = Project.load(path)
        self.assertEqual(back.levels[0].lights[0]["kind"], "spot")
        self.assertEqual(back.levels[0].lighting["exposure"], 2.0)



# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the code editor

MOVER = """using Prowl.Core2D;

[Script, MaxInstances(64)]
class Teleporter
{
    public Component Self;

    public void Update()
    {
        Self.Node.SetPosition(-500f, 0f);
    }
}
"""


class CodeEditorWindow(Base):
    def setUp(self):
        super().setUp()
        self.win = self.windows["scripts"]

    def test_a_new_script_is_a_template_with_its_class_and_typing_edits_the_project(self):
        f = self.win.new_script("Spinner")
        self.assertEqual(self.studio.project.script_classes(), {"Spinner": "Spinner"})
        self.assertIn("class Spinner", self.win.editor.toPlainText())
        self.win.editor.setPlainText(MOVER)
        self.assertEqual(f.text, MOVER)                                             # the project holds what the editor shows
        self.assertEqual(self.studio.project.script_classes(), {"Teleporter": "Spinner"})
        self.assertEqual(self.win.status.text(), "not built yet")

    def test_names_do_not_collide(self):
        self.win.new_script("A")
        self.win.new_script("A")
        self.assertEqual([f.name for f in self.studio.project.scripts], ["A", "A2"])

    def test_the_editor_indents_and_tab_inserts_spaces(self):
        ed = self.win.editor
        self.win.new_script("T")
        ed.setPlainText("class T {")
        c = ed.textCursor()
        c.movePosition(QtGui.QTextCursor.End)
        ed.setTextCursor(c)
        QTest.keyClick(ed, Qt.Key_Return)
        QTest.keyClick(ed, Qt.Key_Tab)
        self.assertEqual(ed.toPlainText(), "class T {\n" + " " * 8)                   # one level for the brace, one for the Tab
        QTest.keyClick(ed, Qt.Key_Backtab)
        self.assertEqual(ed.toPlainText(), "class T {\n" + " " * 4)

    def test_attaching_is_saved_with_the_project_and_deleting_a_script_detaches_it(self):
        self.win.new_script("Spinner")
        self.win.editor.setPlainText(MOVER)
        sprite = self.studio.project.sprites[0]
        self.studio.set_sprite_script(sprite, "Teleporter", True)
        self.studio.set_game_script("Teleporter", True)
        again = Project.from_json(self.studio.project.to_json())
        self.assertEqual(again.sprites[0].scripts, ["Teleporter"])
        self.assertEqual(again.game_scripts, ["Teleporter"])
        self.assertEqual(again.script_classes(), {"Teleporter": "Spinner"})
        self.win.delete_script()
        self.assertEqual((sprite.scripts, self.studio.project.game_scripts), ([], []))

    def test_the_attach_lists_show_and_set_what_is_attached(self):
        self.win.new_script("Spinner")
        self.win.editor.setPlainText(MOVER)
        item = self.win.game_list.item(0)
        self.assertEqual(item.text(), "Teleporter")
        item.setCheckState(Qt.Checked)
        self.assertEqual(self.studio.project.game_scripts, ["Teleporter"])
        self.win.sprite_list.item(0).setCheckState(Qt.Checked)
        self.assertEqual(self.studio.project.sprites[self.win.sprite_box.currentIndex()].scripts, ["Teleporter"])

    def test_a_script_outside_the_subset_is_a_problem_at_its_line_and_the_old_engine_stays(self):
        from .engine import find_library
        if find_library() is None:
            self.skipTest("libprowl2d.so is not built (python3 tools/prowl2d_so.py)")
        self.win.new_script("Bad")
        self.win.editor.setPlainText("using System;\nusing Prowl.Core2D;\n\n[Script, MaxInstances(2)]\nclass Bad\n{\n    public Component Self;\n"
                                     "    public void Update()\n    {\n        Func<int, int> f = k => k;\n    }\n}\n")
        r = self.win.build(sync=True)
        self.assertFalse(r.ok)
        lines = [d.line for d in r.diagnostics if d.script == "Bad"]
        self.assertIn(10, lines)                                                     # the lambda's line
        self.assertIn(10, self.win.editor.error_lines)
        self.assertIsNone(self.main.viewport.engine_path)                            # nothing was swapped
        self.assertEqual(self.win.problems.count(), len(r.diagnostics))


class BuildAndReload(Base):
    """A real build: the script is translated, linked into a new library, swapped in under an open window, and runs on its sprite and in the game."""

    def setUp(self):
        super().setUp()
        self.boxes = []
        self._warning = QtWidgets.QMessageBox.warning
        QtWidgets.QMessageBox.warning = staticmethod(lambda parent, title, text, *a: self.boxes.append(text))     # (a modal box would wait for a click)

    def tearDown(self):
        QtWidgets.QMessageBox.warning = self._warning
        super().tearDown()

    def test_scripts_run_on_sprites_and_in_the_game_after_a_build_and_reload(self):
        from .engine import EngineError, find_library
        if find_library() is None:
            self.skipTest("libprowl2d.so is not built (python3 tools/prowl2d_so.py)")
        win, vc, proj = self.windows["scripts"], self.main.viewport, self.studio.project
        win.new_script("Teleporter")
        win.editor.setPlainText(MOVER)
        lv = self.studio.level
        counts = {}
        for g in lv.cells:
            t = proj.tiles.get(g)
            sp = proj.sprite_for_tile(t) if t is not None else None
            if sp is not None and not t.dynamic:
                counts[sp.name] = counts.get(sp.name, 0) + 1
        name = min(counts, key=counts.get)
        self.studio.set_sprite_script(proj.sprite(name), "Teleporter", True)
        vc.open()
        if vc.vp is None or not vc.vp.is_open:
            self.skipTest("no display for the engine's window: " + "; ".join(self.boxes))
        old_lib, old_vp = vc.engine.path, vc.vp
        r = win.build(sync=True)
        self.assertTrue(r.ok, [str(d) for d in r.diagnostics])
        self.assertEqual(r.scripts, {"Teleporter": 0})
        self.assertEqual(vc.engine_path, r.lib)
        self.assertEqual(vc.engine.path, r.lib)
        self.assertIsNot(vc.vp, old_vp)                                              # the window was closed and opened again, on the new engine
        self.assertTrue(vc.vp.is_open)
        self.assertEqual(vc.engine.script_ids, {"Teleporter": 0})
        self.assertEqual(win.status.text(), "built: 1 script class")
        vp, lib = vc.vp, vc.engine.lib
        vp.toggle_play()
        self.assertIn("1 script", vp.script_report) if counts[name] == 1 else self.assertIn("scripts running", vp.script_report)
        for _ in range(5):
            vp.tick(1 / 60.0)
        moved = [n for n in range(lib.p2d_node_count() + 8) if abs(lib.p2d_node_x(n) + 500.0) < 0.01]
        self.assertEqual(len(moved), counts[name])                                   # one per tile that shows the sprite, none else
        # and in the game: a script on no sprite, attached to the game
        self.studio.set_sprite_script(proj.sprite(name), "Teleporter", False)
        self.studio.set_game_script("Teleporter", True)
        vp.toggle_play()
        vp.toggle_play()
        for _ in range(5):
            vp.tick(1 / 60.0)
        moved = [n for n in range(lib.p2d_node_count() + 8) if abs(lib.p2d_node_x(n) + 500.0) < 0.01]
        self.assertEqual(len(moved), 1)
        # a library built without it says so instead of running nothing silently
        win.builder.forget_old(r.lib)
        vc.close()
