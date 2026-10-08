"""Tests of the model and the text formats (no Qt, no engine):  python3 -m unittest prowl_editor.test_core   (or: python3 prowl.py --selftest)"""
import json
import os
import tempfile
import unittest

from .asciiart import (emoji_for_name, export_level, export_levels, export_sprite, export_sprites, import_levels, import_sprites, level_from_text,
                       level_to_text, natural_name, parse_level_text, scan_sprite_text, split_graphemes)
from .demo import make_demo_project
from .model import Level, Palette, Project, ProjectError, Sprite, TileDef, parse_color


def snapshot(project):
    """Everything about a project's sprites that matters to a viewer: each frame's pixels as RGBA, so key letters and index order may differ."""
    return {s.name: (s.width, s.height, s.fps, [s.rgba(project.palette, i) for i in range(len(s.frames))]) for s in project.sprites}


class Graphemes(unittest.TestCase):
    def test_emoji_sequences_are_one_cell(self):
        text = "\U0001f9f1\U0001fa99\U0001f468‍\U0001f469‍\U0001f467\U0001f1fa\U0001f1f8❤️#️⃣A"
        self.assertEqual(len(split_graphemes(text)), 7)

    def test_skin_tone_is_part_of_the_emoji(self):
        self.assertEqual(split_graphemes("\U0001f44d\U0001f3fd\U0001f44d"), ["\U0001f44d\U0001f3fd", "\U0001f44d"])

    def test_natural_names(self):
        self.assertEqual(natural_name("\U0001f9f1"), "brick")
        self.assertEqual(natural_name("\U0001f333"), "deciduous tree")
        self.assertEqual(natural_name("\U0001f1fa\U0001f1f8"), "flag us")
        self.assertEqual(emoji_for_name("Brick"), "\U0001f9f1")
        self.assertIsNone(emoji_for_name("no such emoji name"))


class Palette_(unittest.TestCase):
    def test_transparent_is_index_zero_and_keys_are_unique(self):
        p = Palette()
        self.assertEqual(p.key_of(0), ".")
        self.assertIsNone(p.rgba(0)[3] or None)
        with self.assertRaises(ProjectError):
            p.add("K", "again", (1, 2, 3))
        with self.assertRaises(ProjectError):
            p.add(" ", "bad", (1, 2, 3))
        with self.assertRaises(ProjectError):
            p.add("ab", "bad", (1, 2, 3))
        self.assertEqual(p.add("#", "hash", (1, 2, 3)), len(p) - 1)       # '#' is an ordinary key: ASCII art uses it a lot

    def test_recolouring_an_index_changes_every_frame_that_uses_it(self):
        """The blink: eye pixels use one index, and one colour change turns them all."""
        p = Project("t")
        eye = p.palette.add("E", "eye", (255, 255, 255))
        s = Sprite("face", 4, 2)
        s.frames = [bytearray([eye, 0, 0, eye, 0, 0, 0, 0]), bytearray([0, eye, 0, 0, 0, 0, eye, 0])]
        p.add_sprite(s)
        before = [s.rgba(p.palette, i) for i in range(2)]
        p.palette.set_color(eye, (10, 20, 30))
        after = [s.rgba(p.palette, i) for i in range(2)]
        for b, a in zip(before, after):
            self.assertEqual(len(b), len(a))
            self.assertNotEqual(b, a)
            self.assertEqual(sorted(set(a[i:i + 4] for i in range(0, len(a), 4))), sorted([bytes((10, 20, 30, 255)), bytes((0, 0, 0, 0))]))

    def test_removing_and_moving_entries_keeps_every_pixel_colour(self):
        p = make_demo_project()
        want = snapshot(p)
        p.move_palette_entry(3, 9)
        p.move_palette_entry(9, 2)
        self.assertEqual(snapshot(p), want)
        victim = p.palette.index_of("T")
        used_before = {n for s in p.sprites for f in s.frames for n in f}
        p.remove_palette_entry(victim)
        # the pixels of the removed colour are transparent now, and nothing else changed colour
        grass = p.sprite("grass")
        self.assertIn(0, grass.frames[0])
        self.assertEqual(p.sprite("brick").rgba(p.palette, 0), want["brick"][3][0])
        self.assertTrue(victim in used_before)


class SpriteText(unittest.TestCase):
    def test_export_then_import_is_lossless(self):
        a = make_demo_project()
        text = export_sprites(a)
        b = Project("b")
        b.palette = Palette(with_defaults=False)               # an empty palette: the header alone must bring the colours back
        import_sprites(text, b)
        self.assertEqual(snapshot(a), snapshot(b))
        self.assertEqual([s.name for s in a.sprites], [s.name for s in b.sprites])

    def test_frames_are_blank_line_separated_and_fps_survives(self):
        p = make_demo_project()
        coin = p.sprite("coin")
        self.assertEqual(len(coin.frames), 4)
        text = export_sprite(coin, p.palette)
        self.assertEqual(text.count("\n\n"), 3)
        q = Project("q")
        import_sprites(text, q)
        self.assertEqual(q.sprite("coin").fps, 6.0)
        self.assertEqual(len(q.sprite("coin").frames), 4)

    def test_plain_ascii_art_imports_with_a_mapping(self):
        art = "..##..\n.#@@#.\n..##..\n"
        p = Project("p")
        blocks, unknown = scan_sprite_text(art, p.palette)
        self.assertEqual(sorted(unknown), ["#", "@"])
        sprites, added = import_sprites(art, p, {"#": (200, 0, 0), "@": None})
        s = sprites[0]
        self.assertEqual((s.width, s.height), (6, 3))
        self.assertEqual(s.get(0, 2, 0), p.palette.index_of("#"))
        self.assertEqual(s.get(0, 2, 1), 0)                               # '@' was mapped to transparent
        self.assertEqual(p.palette.entries[p.palette.index_of("#")].rgb, (200, 0, 0))
        self.assertEqual(added, ["#"])
        self.assertIsNone(p.palette.index_of("@"))                        # and a transparent choice adds nothing to the palette

    def test_a_space_is_transparent_and_ragged_rows_are_padded(self):
        p = Project("p")
        (s,), _ = import_sprites("R  R\nRR\nR", p)
        self.assertEqual((s.width, s.height), (4, 3))
        self.assertEqual(s.rows(p.palette, 0), ["R..R", "RR..", "R..."])

    def test_importing_never_recolours_the_existing_palette(self):
        p = Project("p")
        yellow = p.palette.entries[p.palette.index_of("Y")].rgb
        (s,), added = import_sprites("# sprite: x\n# palette: Y=#0000ff\nYY\n", p)
        self.assertEqual(p.palette.entries[p.palette.index_of("Y")].rgb, yellow)     # the project's Y is still its Y
        self.assertEqual(len(added), 1)                                              # the file's blue Y is a new entry under a free key
        self.assertEqual(s.rgba(p.palette, 0)[:4], bytes((0, 0, 255, 255)))

    def test_header_lines_need_their_keyword(self):
        # a row that merely starts with '#' is art, not a header
        p = Project("p")
        (s,), added = import_sprites("# #\n#  \n", p)
        self.assertEqual((s.width, s.height), (3, 2))
        self.assertEqual(added, ["#"])

    def test_errors_say_where(self):
        with self.assertRaises(ProjectError) as e:
            import_sprites("# palette: K=notacolour\nKK\n", Project("p"))
        self.assertIn("line 1", str(e.exception))
        with self.assertRaises(ProjectError):
            import_sprites("\n\n", Project("p"))


class LevelText(unittest.TestCase):
    def test_demo_level_round_trips_through_text(self):
        a = make_demo_project()
        text = export_levels(a)
        b = Project("b")
        b.sprites = [s for s in a.sprites]
        import_levels(text, b)
        la, lb = a.levels[0], b.levels[0]
        self.assertEqual((la.width, la.height, la.cells), (lb.width, lb.height, lb.cells))
        self.assertEqual({k: (t.name, t.solid) for k, t in a.tiles.items()}, {k: (t.name, t.solid) for k, t in b.tiles.items()})

    def test_legend_and_header_are_readable(self):
        a = make_demo_project()
        lines = export_level(a.levels[0], a).splitlines()
        self.assertEqual(lines[0], "# level: first steps")
        self.assertIn("# \U0001f9f1 = brick (solid)", lines)
        self.assertIn("# \U0001fa99 = coin", lines)
        self.assertTrue(all(len(split_graphemes(l)) == 24 for l in lines if not l.startswith("#")))

    def test_tiles_take_their_sprite_by_name(self):
        p = make_demo_project()
        self.assertEqual(p.sprite_for_tile(p.tiles["\U0001f9f1"]).name, "brick")
        # an emoji whose natural name is a sprite's name needs no mapping at all
        q = Project("q")
        import_sprites("# sprite: brick\nRR\nRR\n", q)
        import_levels("\U0001f9f1\U0001f9f1\n", q)
        self.assertEqual(q.tiles["\U0001f9f1"].name, "brick")
        self.assertEqual(q.sprite_for_tile(q.tiles["\U0001f9f1"]).name, "brick")

    def test_ascii_levels_work_even_with_hash_walls(self):
        p = Project("p")
        (lv,), new = import_levels(". . .\n# #  #\n", p)           # a row starting "# " is a row, not a comment
        self.assertEqual(lv.height, 2)
        self.assertEqual(sorted(t.emoji for t in new), ["#"])
        self.assertEqual(natural_name("#"), "number sign")

    def test_ragged_rows_are_padded_with_empty(self):
        p = Project("p")
        (lv,), _ = import_levels("# empty: .\n#..#\n#\n", p)
        self.assertEqual((lv.width, lv.height), (4, 2))
        self.assertEqual(lv.get(3, 1), "")

    def test_two_levels_in_one_file(self):
        p = Project("p")
        made, _ = import_levels("# level: a\n\U0001f9f1\n# level: b\n\U0001fa99\U0001fa99\n", p)
        self.assertEqual([l.name for l in made], ["a", "b"])
        self.assertEqual(made[1].width, 2)

    def test_text_pane_edit(self):
        p = make_demo_project()
        lv = p.levels[0]
        text = level_to_text(lv, p)
        self.assertEqual(len(text.splitlines()), lv.height)
        new_tiles = level_from_text(lv, text.replace("\U0001fa99", "\U0001f333", 1), p)
        self.assertEqual([t.name for t in new_tiles], ["deciduous tree"])
        self.assertEqual(level_to_text(lv, p).count("\U0001f333"), 1)

    def test_resize_and_fill(self):
        lv = Level("l", 4, 3)
        lv.flood_fill(0, 0, "X")
        self.assertEqual(lv.cells.count("X"), 12)
        lv.set(1, 1, "Y")
        lv.resize(2, 2)
        self.assertEqual(lv.cells, ["X", "X", "X", "Y"])

    def test_no_level_is_an_error(self):
        with self.assertRaises(ProjectError):
            parse_level_text("# level: nothing\n")


class JsonProject(unittest.TestCase):
    def test_round_trip_through_a_file(self):
        a = make_demo_project()
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "demo.json")
            a.save(path)
            b = Project.load(path)
            self.assertEqual(snapshot(a), snapshot(b))
            self.assertEqual(a.levels[0].cells, b.levels[0].cells)
            with open(path, encoding="utf-8") as f:
                self.assertEqual(json.load(f), b.to_json())
            self.assertFalse(b.dirty)

    def test_the_file_reads_like_the_art(self):
        data = make_demo_project().to_json()
        self.assertEqual(data["sprites"][0]["frames"][0][0], "SSSSSSSS")
        self.assertEqual(data["levels"][0]["rows"][10], "\U0001f7e9" * 24)

    def test_bad_files_say_where(self):
        good = make_demo_project().to_json()
        bad = json.loads(json.dumps(good))
        bad["sprites"][1]["frames"][0][2] = "ZZZZZZZZ"
        with self.assertRaises(ProjectError) as e:
            Project.from_json(bad)
        self.assertIn("sprites[1].frames[0]", str(e.exception))
        self.assertIn("'Z'", str(e.exception))
        bad = json.loads(json.dumps(good))
        bad["levels"][0]["rows"][0] = "\U0001f4a5" * 24
        with self.assertRaises(ProjectError) as e:
            Project.from_json(bad)
        self.assertIn("levels[0]", str(e.exception))
        with self.assertRaises(ProjectError):
            Project.from_json({"format": "something else"})
        bad = json.loads(json.dumps(good))
        bad["version"] = 99
        with self.assertRaises(ProjectError):
            Project.from_json(bad)

    def test_parse_color(self):
        self.assertEqual(parse_color("#fff"), (255, 255, 255))
        self.assertEqual(parse_color("0a0b0c"), (10, 11, 12))
        with self.assertRaises(ProjectError):
            parse_color("red")

    def test_sprite_edit_operations(self):
        s = Sprite("s", 3, 2)
        s.set(0, 1, 1, 5)
        n = s.add_frame(0, copy=True)
        self.assertEqual(s.get(n, 1, 1), 5)
        s.set(n, 0, 0, 7)
        s.resize(2, 3)
        self.assertEqual((s.width, s.height, s.get(n, 0, 0), s.get(n, 1, 1)), (2, 3, 7, 5))
        s.flood_fill(0, 0, 0, 9)
        self.assertEqual(s.get(0, 0, 0), 9)
        self.assertEqual(s.move_frame(0, 1), 1)
        s.delete_frame(0)
        self.assertEqual(len(s.frames), 1)
        s.delete_frame(0)
        self.assertEqual(len(s.frames), 1)                    # the last frame stays


class SlimeProject(unittest.TestCase):
    def test_the_sample_level_becomes_a_project_that_round_trips(self):
        from .slime_demo import make_slime_project
        from .asciiart import export_level, import_levels
        p = make_slime_project()
        lv = p.levels[0]
        self.assertEqual((lv.width, lv.height), (64, 20))
        self.assertEqual(lv.cells.count("\U0001f7eb"), 18)               # the dirt wall: 2 x 9 cells
        self.assertEqual(lv.cells.count("\U0001f4e6"), 4)                # four crates
        self.assertTrue(p.tiles["\U0001f4e6"].dynamic and p.tiles["\U0001f7eb"].diggable)
        p2 = Project.from_json(p.to_json())
        self.assertEqual(p2.levels[0].cells, lv.cells)
        self.assertTrue(p2.tiles["\U0001f7eb"].diggable and p2.tiles["\U0001f4e6"].dynamic)
        q = Project("again")                                                # the emoji text keeps the traits too
        import_levels(export_level(lv, p), q)
        self.assertTrue(q.tiles["\U0001f7eb"].diggable and q.tiles["\U0001f7eb"].solid and q.tiles["\U0001f4e6"].dynamic)
        self.assertEqual(q.levels[0].cells, lv.cells)


if __name__ == "__main__":
    unittest.main()
