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


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the Unity importer: a Unity project is built in a temp dir (PNG files written here, so there are no binary fixtures)

def make_png(w, h, rows, ctype=6, depth=8, palette=None, trns=None, filt=0):
    """A PNG file's bytes. `rows` are the raw (unfiltered) bytes of each row, as many as `h`. filt 0 = none, 1 = Sub, 2 = Up."""
    import struct
    import zlib

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
    bpp = max(1, channels * depth // 8)
    raw = bytearray()
    prev = bytes(len(rows[0]))
    for row in rows:
        if filt == 1:
            out = bytes((row[i] - (row[i - bpp] if i >= bpp else 0)) & 255 for i in range(len(row)))
        elif filt == 2:
            out = bytes((row[i] - prev[i]) & 255 for i in range(len(row)))
        else:
            out = bytes(row)
        raw += bytes([filt]) + out
        prev = row
    data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, depth, ctype, 0, 0, 0))
    if palette:
        data += chunk(b"PLTE", b"".join(bytes(c) for c in palette))
    if trns:
        data += chunk(b"tRNS", bytes(trns))
    return data + chunk(b"IDAT", zlib.compress(bytes(raw))) + chunk(b"IEND", b"")


def rgba_rows(rows):
    """[[(r, g, b, a), ...], ...] -> raw row bytes of an RGBA image."""
    return [b"".join(bytes(px) for px in row) for row in rows]


RED, GREEN, BLUE, YELLOW, CLEAR = (224, 60, 60, 255), (60, 180, 75, 255), (60, 100, 224, 255), (242, 216, 59, 255), (0, 0, 0, 0)


def sheet_meta(mode, slices=(), texture_type=8, guid="0123456789abcdef0123456789abcdef", ppu=16, ids=None):
    """A .meta in the layout Unity writes. slices: [(name, x, y, w, h)], y counted from the bottom."""
    text = "fileFormatVersion: 2\nguid: %s\nTextureImporter:\n  textureType: %d\n  spriteMode: %d\n  spritePixelsToUnits: %d\n" % (guid, texture_type, mode, ppu)
    if slices:
        text += "  spriteSheet:\n    serializedVersion: 2\n    sprites:\n"
        for n, (name, x, y, w, h) in enumerate(slices):
            text += ("    - serializedVersion: 2\n      name: %s\n      rect:\n        serializedVersion: 2\n        x: %d\n        y: %d\n        width: %d\n"
                     "        height: %d\n      alignment: 0\n      pivot: {x: 0.5, y: 0.5}\n      border: {x: 0, y: 0, z: 0, w: 0}\n      outline: []\n"
                     "      internalID: %d\n" % (name, x, y, w, h, ids[n] if ids else 21300000 + 2 * n))
    return text + "  mipmapLimitGroupName: \nuserData: \n"


class UnityFiles(unittest.TestCase):
    """Helpers for the Unity tests: a temp Unity project with an Assets/Textures folder. (No tests here, so subclasses do not re-run each other's.)"""
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = self._tmp.name
        self.tex = os.path.join(self.root, "Assets", "Textures")
        os.makedirs(self.tex)

    def tearDown(self):
        self._tmp.cleanup()

    def put(self, name, data, meta=None):
        path = os.path.join(self.tex, name)
        with open(path, "wb") as f:
            f.write(data)
        if meta is not None:
            with open(path + ".meta", "w", encoding="utf-8") as f:
                f.write(meta)
        return path

    def rgba_png(self, rows, **kw):
        return make_png(len(rows[0]), len(rows), rgba_rows(rows), **kw)

    def colors(self, project, sprite, frame=0):
        """A sprite's frame as rows of RGBA tuples."""
        px = sprite.rgba(project.palette, frame)
        return [[tuple(px[(y * sprite.width + x) * 4:(y * sprite.width + x) * 4 + 4]) for x in range(sprite.width)] for y in range(sprite.height)]



class UnityImport(UnityFiles):
    # ---- PNG
    def test_png_colour_types_and_filters_decode_the_same_pixels(self):
        from .unity_import import decode_png
        img = [[RED, GREEN, BLUE], [YELLOW, CLEAR, RED]]
        want = b"".join(bytes(px) for row in img for px in row)
        for filt in (0, 1, 2):
            self.assertEqual(decode_png(self.rgba_png(img, filt=filt))[2], want, "RGBA, filter %d" % filt)
        rgb = make_png(2, 1, [bytes([10, 20, 30, 40, 50, 60])], ctype=2)
        self.assertEqual(decode_png(rgb), (2, 1, bytes([10, 20, 30, 255, 40, 50, 60, 255])))
        gray = make_png(2, 1, [bytes([0, 200])], ctype=0)
        self.assertEqual(decode_png(gray)[2], bytes([0, 0, 0, 255, 200, 200, 200, 255]))
        ga = make_png(1, 1, [bytes([90, 7])], ctype=4)
        self.assertEqual(decode_png(ga)[2], bytes([90, 90, 90, 7]))

    def test_indexed_png_with_transparency_and_packed_bits(self):
        from .unity_import import decode_png
        pal = [(255, 0, 0), (0, 255, 0), (0, 0, 255)]
        eight = make_png(3, 1, [bytes([0, 1, 2])], ctype=3, palette=pal, trns=[0, 255])        # entry 0 is clear; the rest are opaque
        self.assertEqual(decode_png(eight)[2], bytes([255, 0, 0, 0, 0, 255, 0, 255, 0, 0, 255, 255]))
        four = make_png(3, 1, [bytes([0x01, 0x20])], ctype=3, depth=4, palette=pal)            # pixels 0, 1, 2 packed two to a byte
        self.assertEqual(decode_png(four)[2], bytes([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255]))
        two = make_png(4, 1, [bytes([0b00011011])], ctype=3, depth=2, palette=pal + [(9, 9, 9)])
        self.assertEqual([decode_png(two)[2][i] for i in (0, 4, 8, 12)], [255, 0, 0, 9])

    def test_a_png_that_is_not_one_is_a_message(self):
        from .unity_import import UnityImportError, decode_png
        with self.assertRaises(UnityImportError):
            decode_png(b"GIF89a....", "x.png")

    # ---- single sprites
    def test_a_single_texture_is_one_sprite_with_rows_from_the_top(self):
        from .unity_import import import_unity_sprites
        custom = (10, 20, 30, 255)                                    # not in the default palette; red, green and blue are
        self.put("hero.png", self.rgba_png([[RED, custom], [BLUE, CLEAR]]), sheet_meta(1))
        p = Project("t")
        n_colors = len(p.palette)
        r = import_unity_sprites(p, self.root)
        self.assertEqual([s.name for s in p.sprites], ["hero"])
        self.assertEqual(self.colors(p, p.sprites[0]), [[RED, custom], [BLUE, CLEAR]])
        self.assertEqual(len(p.palette), n_colors + 1)                 # only the custom colour needed a new entry
        self.assertEqual(r.summary(), "1 sprite (1 frame) from 1 texture")

    def test_colours_the_palette_already_has_are_reused_not_added(self):
        from .unity_import import import_unity_sprites
        self.put("a.png", self.rgba_png([[RED, RED]]), sheet_meta(1))
        p = Project("t")
        before = len(p.palette)
        import_unity_sprites(p, self.root)
        self.assertEqual(len(p.palette), before)
        self.assertEqual(p.sprites[0].get(0, 0, 0), p.palette.index_of("R"))

    def test_a_texture_with_no_meta_is_a_single_sprite(self):
        from .unity_import import import_unity_sprites
        self.put("plain.png", self.rgba_png([[RED]]))
        p = Project("t")
        import_unity_sprites(p, self.root)
        self.assertEqual([s.name for s in p.sprites], ["plain"])

    def test_textures_that_are_not_sprites_are_skipped_with_a_note(self):
        from .unity_import import import_unity_sprites
        self.put("normal.png", self.rgba_png([[RED]]), sheet_meta(1, texture_type=1))
        p = Project("t")
        r = import_unity_sprites(p, self.root)
        self.assertEqual(p.sprites, [])
        self.assertTrue(any("not a sprite texture" in n for n in r.notes))

    def test_two_textures_with_the_same_name_get_different_sprite_names(self):
        from .unity_import import import_unity_sprites
        os.makedirs(os.path.join(self.root, "Assets", "Other"))
        self.put("tile.png", self.rgba_png([[RED]]), sheet_meta(1))
        with open(os.path.join(self.root, "Assets", "Other", "tile.png"), "wb") as f:
            f.write(self.rgba_png([[GREEN]]))
        p = Project("t")
        import_unity_sprites(p, self.root)
        self.assertEqual(sorted(s.name for s in p.sprites), ["tile", "tile 2"])

    def test_library_and_package_folders_are_not_walked(self):
        from .unity_import import find_pngs
        for d in ("Library", "Packages"):
            os.makedirs(os.path.join(self.root, d))
            with open(os.path.join(self.root, d, "junk.png"), "wb") as f:
                f.write(b"x")
        self.put("keep.png", self.rgba_png([[RED]]))
        self.assertEqual([os.path.basename(x) for x in find_pngs(self.root)], ["keep.png"])

    # ---- sprite sheets
    def test_a_multiple_mode_sheet_is_cut_into_slices_with_y_counted_from_the_bottom(self):
        from .unity_import import import_unity_sprites
        top = [RED, RED, GREEN, GREEN]
        bottom = [BLUE, BLUE, YELLOW, YELLOW]
        meta = sheet_meta(2, [("lowleft", 0, 0, 2, 1), ("upright", 2, 1, 2, 1)])
        self.put("sheet.png", self.rgba_png([top, bottom]), meta)
        p = Project("t")
        import_unity_sprites(p, self.root)
        by = {s.name: s for s in p.sprites}
        self.assertEqual(sorted(by), ["lowleft", "upright"])
        self.assertEqual(self.colors(p, by["lowleft"]), [[BLUE, BLUE]])
        self.assertEqual(self.colors(p, by["upright"]), [[GREEN, GREEN]])

    def test_numbered_slices_of_one_size_become_the_frames_of_one_sprite(self):
        from .unity_import import group_slices, import_unity_sprites
        row = [RED, GREEN, BLUE, YELLOW]
        meta = sheet_meta(2, [("walk_1", 1, 0, 1, 1), ("walk_0", 0, 0, 1, 1), ("walk_2", 2, 0, 1, 1), ("idle", 3, 0, 1, 1)])
        self.put("hero.png", self.rgba_png([row]), meta)
        p = Project("t")
        import_unity_sprites(p, self.root)
        self.assertEqual([s.name for s in p.sprites], ["walk", "idle"])
        walk = p.sprite("walk")
        self.assertEqual([self.colors(p, walk, i)[0][0] for i in range(3)], [RED, GREEN, BLUE])          # in number order, not .meta order
        self.assertEqual(len(p.sprite("idle").frames), 1)
        # a lone numbered slice keeps its whole name, and different sizes do not mix
        lone = group_slices([{"name": "rock_3", "x": 0, "y": 0, "w": 4, "h": 4}, {"name": "rock_4", "x": 4, "y": 0, "w": 8, "h": 8}])
        self.assertEqual([n for n, _ in lone], ["rock_3", "rock_4"])

    def test_slices_named_after_their_texture_are_never_merged_into_an_animation(self):
        from .unity_import import group_slices
        sl = lambda n: {"name": n, "x": 0, "y": 0, "w": 4, "h": 4}
        self.assertEqual([n for n, _ in group_slices([sl("Hero_0"), sl("Hero_1")], "hero")], ["Hero_0", "Hero_1"])        # Unity's automatic names: tiles or frames, a scene names each
        self.assertEqual([n for n, _ in group_slices([sl("walk_0"), sl("walk_1")], "hero")], ["walk"])                       # a name somebody chose is an animation

    def test_a_slice_outside_the_texture_is_skipped_with_a_note(self):
        from .unity_import import import_unity_sprites
        self.put("s.png", self.rgba_png([[RED, GREEN]]), sheet_meta(2, [("ok", 0, 0, 1, 1), ("gone", 50, 50, 4, 4)]))
        p = Project("t")
        r = import_unity_sprites(p, self.root)
        self.assertEqual([s.name for s in p.sprites], ["ok"])
        self.assertTrue(any("gone" in n for n in r.notes))

    # ---- limits
    def test_more_colours_than_the_palette_holds_are_merged_and_reported(self):
        from .unity_import import import_unity_sprites
        rows = [[(x * 8 % 256, y * 17 % 256, (x * 5 + y * 3) % 256, 255) for x in range(32)] for y in range(16)]
        self.assertGreater(len({px for row in rows for px in row}), 256)
        self.put("rainbow.png", self.rgba_png(rows), sheet_meta(1))
        p = Project("t")
        r = import_unity_sprites(p, self.root)
        self.assertLessEqual(len(p.palette), 256)
        self.assertTrue(any("did not fit in the palette" in n for n in r.notes))
        self.assertEqual(p.sprites[0].frames[0].count(0), 0)                              # every pixel got a colour
        Project.from_json(p.to_json())                                                    # and the keys it made are valid, so the project saves and loads

    def test_a_texture_wider_than_a_sprite_is_scaled_down_and_reported(self):
        from .unity_import import import_unity_sprites
        self.put("wide.png", self.rgba_png([[RED] * 512] * 8), sheet_meta(1))
        p = Project("t")
        r = import_unity_sprites(p, self.root)
        s = p.sprites[0]
        self.assertEqual((s.width, s.height), (256, 4))
        self.assertTrue(all(c == RED for row in self.colors(p, s) for c in row))
        self.assertTrue(any("512x8" in n and "256x4" in n for n in r.notes))

    def test_a_broken_png_is_reported_and_the_others_still_import(self):
        from .unity_import import import_unity_sprites
        self.put("bad.png", b"not a png at all")
        self.put("good.png", self.rgba_png([[RED]]))
        p = Project("t")
        r = import_unity_sprites(p, self.root)
        self.assertEqual([s.name for s in p.sprites], ["good"])
        self.assertTrue(any("bad.png" in n for n in r.notes))

    def test_a_folder_with_no_textures_adds_nothing(self):
        from .unity_import import import_unity_sprites
        p = Project("t")
        r = import_unity_sprites(p, self.root)
        self.assertEqual(p.sprites, [])
        self.assertTrue(r.notes)

    def test_the_import_round_trips_through_the_project_file(self):
        from .unity_import import import_unity_sprites
        self.put("a.png", self.rgba_png([[RED, GREEN], [BLUE, YELLOW]]), sheet_meta(1))
        p = Project("t")
        import_unity_sprites(p, self.root)
        q = Project.from_json(p.to_json())
        self.assertEqual(snapshot(p), snapshot(q))


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# Unity scenes -> levels

G_BRICK, G_COIN, G_SHEET = "b" * 32, "c" * 32, "d" * 32


class SceneBuilder:
    """Writes the text of a Unity scene (the YAML Unity serializes) object by object. Each add_* returns the GameObject's fileID."""
    def __init__(self):
        self.parts = ["%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"]
        self.next = 100

    def _id(self):
        self.next += 10
        return self.next

    def add(self, name, x=0.0, y=0.0, sprite=None, order=0, scale=(1, 1), father=None, active=1, enabled=1, collider=None, trigger=0, body=None, rot_z=0.0,
            extra_components=()):
        """sprite: (guid, fileID) or None; collider: a class id (61 = box) or None; body: Rigidbody2D m_BodyType or None."""
        go, xf = self._id(), self._id()
        comps = [xf]
        extra = []
        self.sr = None
        if sprite:
            sr = self.sr = self._id()
            comps.append(sr)
            extra.append("--- !u!212 &%d\nSpriteRenderer:\n  m_GameObject: {fileID: %d}\n  m_Enabled: %d\n  m_Sprite: {fileID: %d, guid: %s, type: 3}\n  m_SortingOrder: %d\n"
                         % (sr, go, enabled, sprite[1], sprite[0], order))
        if collider:
            c = self._id()
            comps.append(c)
            extra.append("--- !u!%d &%d\nBoxCollider2D:\n  m_GameObject: {fileID: %d}\n  m_Enabled: 1\n  m_IsTrigger: %d\n" % (collider, c, go, trigger))
        if body is not None:
            b = self._id()
            comps.append(b)
            extra.append("--- !u!50 &%d\nRigidbody2D:\n  m_GameObject: {fileID: %d}\n  m_BodyType: %d\n" % (b, go, body))
        for class_id, make in extra_components:                              # make(go) -> the component's text, starting with its class name
            c = self._id()
            comps.append(c)
            extra.append("--- !u!%d &%d\n%s" % (class_id, c, make(go)))
        self.parts.append("--- !u!1 &%d\nGameObject:\n  m_Component:\n%s  m_Name: %s\n  m_IsActive: %d\n" % (go, "".join("  - component: {fileID: %d}\n" % c for c in comps), name, active))
        qz = rot_z / 2.0                                                    # a small angle's quaternion: z = sin(angle / 2)
        self.parts.append("--- !u!4 &%d\nTransform:\n  m_GameObject: {fileID: %d}\n  m_LocalRotation: {x: 0, y: 0, z: %g, w: 1}\n  m_LocalPosition: {x: %g, y: %g, z: 0}\n"
                          "  m_LocalScale: {x: %g, y: %g, z: 1}\n  m_Father: {fileID: %d}\n" % (xf, go, qz, x, y, scale[0], scale[1], father or 0))
        self.parts += extra
        self.xf = xf
        return go, xf

    def add_grid(self, name="Grid", cell=(1, 1), x=0.0, y=0.0, layout=0, father=None, scale=(1, 1)):
        """A GameObject with a Grid component (a tilemap's cell size lives there)."""
        def grid(go):
            return ("Grid:\n  m_GameObject: {fileID: %d}\n  m_Enabled: 1\n  m_CellSize: {x: %g, y: %g, z: 0}\n  m_CellGap: {x: 0, y: 0, z: 0}\n"
                    "  m_CellLayout: %d\n  m_CellSwizzle: 0\n" % (go, cell[0], cell[1], layout))
        return self.add(name, x, y, father=father, scale=scale, extra_components=[(156049354, grid)])

    def add_tilemap(self, name, tiles, sprites, father=None, x=0.0, y=0.0, scale=(1, 1), anchor=(0.5, 0.5), matrices=None, collider=False, renderer=True,
                    renderer_enabled=1, order=0, enabled=1, active=1):
        """A GameObject with a Tilemap (+ TilemapRenderer, + TilemapCollider2D). tiles: [(cell x, cell y, sprite index[, matrix index])]; sprites: [(guid, fileID) or
        None] (None is an unused slot, written {fileID: 0}); matrices: [(e00, e01, e10, e11)], identity by default."""
        def tilemap(go):
            out = ["Tilemap:\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: %d}\n  m_Enabled: %d\n" % (go, enabled)]
            if tiles:
                out.append("  m_Tiles:\n")
                for t in tiles:
                    out.append("  - first: {x: %d, y: %d, z: 0}\n    second:\n      serializedVersion: 2\n      m_TileIndex: 0\n      m_TileSpriteIndex: %d\n"
                               "      m_TileMatrixIndex: %d\n      m_TileColorIndex: 0\n      m_TileObjectToInstantiateIndex: 65535\n      dummyAlignment: 0\n"
                               "      m_AllTileFlags: 1073741825\n" % (t[0], t[1], t[2], t[3] if len(t) > 3 else 0))
            else:
                out.append("  m_Tiles: {}\n")
            out.append("  m_AnimatedTiles: {}\n  m_TileAssetArray:\n  - m_RefCount: 1\n    m_Data: {fileID: 11400000, guid: %s, type: 2}\n  m_TileSpriteArray:\n" % ("e" * 32))
            for sp in sprites:
                out.append("  - m_RefCount: 1\n    m_Data: {fileID: %d, guid: %s, type: 3}\n" % (sp[1], sp[0]) if sp else "  - m_RefCount: 0\n    m_Data: {fileID: 0}\n")
            out.append("  m_TileMatrixArray:\n")
            for e00, e01, e10, e11 in (matrices or [(1, 0, 0, 1)]):
                out.append("  - m_RefCount: 1\n    m_Data:\n      e00: %g\n      e01: %g\n      e02: 0\n      e03: 0\n      e10: %g\n      e11: %g\n      e12: 0\n"
                           "      e13: 0\n      e20: 0\n      e21: 0\n      e22: 1\n      e23: 0\n      e30: 0\n      e31: 0\n      e32: 0\n      e33: 1\n" % (e00, e01, e10, e11))
            out.append("  m_TileColorArray:\n  - m_RefCount: 1\n    m_Data: {r: 1, g: 1, b: 1, a: 1}\n  m_TileObjectToInstantiateArray: []\n  m_AnimationFrameRate: 1\n"
                       "  m_Color: {r: 1, g: 1, b: 1, a: 1}\n  m_Origin: {x: 0, y: 0, z: 0}\n  m_Size: {x: 10, y: 10, z: 1}\n  m_TileAnchor: {x: %g, y: %g, z: 0}\n"
                       "  m_TileOrientation: 0\n" % anchor)
            return "".join(out)
        parts = [(1839735485, tilemap)]
        if renderer:
            parts.append((483693784, lambda go: "TilemapRenderer:\n  m_GameObject: {fileID: %d}\n  m_Enabled: %d\n  m_SortingOrder: %d\n" % (go, renderer_enabled, order)))
        if collider:
            parts.append((19719996, lambda go: "TilemapCollider2D:\n  m_GameObject: {fileID: %d}\n  m_Enabled: 1\n  m_IsTrigger: 0\n" % go))
        return self.add(name, x, y, father=father, scale=scale, active=active, extra_components=parts)

    def instance(self, guid, mods=(), parent=None):
        """A PrefabInstance of the prefab `guid`. mods: [(target fileID, propertyPath, value[, (objectReference fileID, guid)])]; parent: a transform fileID in this file."""
        fid = self._id()
        lines = ""
        for m in mods:
            ref = m[3] if len(m) > 3 else (0, None)
            objref = "{fileID: %d}" % ref[0] if not ref[1] else "{fileID: %d, guid: %s, type: 3}" % (ref[0], ref[1])
            lines += "    - target: {fileID: %d, guid: %s, type: 3}\n      propertyPath: %s\n      value: %s\n      objectReference: %s\n" % (m[0], guid, m[1], m[2], objref)
        self.parts.append("--- !u!1001 &%d\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Modification:\n    m_TransformParent: {fileID: %d}\n"
                          "    m_Modifications:\n%s    m_RemovedComponents: []\n  m_SourcePrefab: {fileID: 100100000, guid: %s, type: 3}\n" % (fid, parent or 0, lines, guid))
        return fid

    def stripped_transform(self, instance, source_fid):
        """The stand-in Unity writes for an object of a prefab instance, so that this file's own objects can be parented to it."""
        fid = self._id()
        self.parts.append("--- !u!4 &%d stripped\nTransform:\n  m_CorrespondingSourceObject: {fileID: %d, guid: x, type: 3}\n  m_PrefabInstance: {fileID: %d}\n" % (fid, source_fid, instance))
        return fid

    def raw(self, text):
        self.parts.append(text)

    def text(self):
        return "".join(self.parts)


class UnityScenes(UnityFiles):
    def setUp(self):
        super().setUp()
        self.scenes = os.path.join(self.root, "Assets", "Scenes")
        os.makedirs(self.scenes)
        self.put("brick.png", self.rgba_png([[RED] * 16] * 16), sheet_meta(1, guid=G_BRICK))                    # 16 px at 16 px per unit: one unit
        self.put("coin.png", self.rgba_png([[YELLOW] * 16] * 16), sheet_meta(1, guid=G_COIN))
        self.BRICK, self.COIN = (G_BRICK, 21300000), (G_COIN, 21300000)

    def scene(self, name, builder):
        with open(os.path.join(self.scenes, name + ".unity"), "w", encoding="utf-8") as f:
            f.write(builder.text())

    def run_import(self, **kw):
        from .unity_scene import import_unity_project
        self.project = Project("t")
        self.report = import_unity_project(self.project, self.root, **kw)
        return self.report

    def rows(self, level, project=None):
        p = project or self.project
        return ["".join(c or "." for c in [level.get(x, y) for x in range(level.width)]) for y in range(level.height)]

    def test_objects_land_on_the_grid_with_y_up_and_each_sprite_gets_a_tile(self):
        b = SceneBuilder()
        for x in range(3):
            b.add("Brick%d" % x, x, 0, self.BRICK, collider=61)
        b.add("Coin", 1, 1, self.COIN)
        self.scene("Level1", b)
        r = self.run_import()
        lv = self.project.level("Level1")
        self.assertEqual((lv.width, lv.height), (3, 2))
        brick, coin = lv.get(0, 1), lv.get(1, 0)
        self.assertEqual(self.rows(lv), [".%s." % coin, brick * 3])                    # the coin is above the bricks: Unity's y points up
        self.assertEqual((self.project.tiles[brick].sprite, self.project.tiles[brick].solid), ("brick", True))
        self.assertEqual((self.project.tiles[coin].sprite, self.project.tiles[coin].solid), ("coin", False))
        self.assertIn("1 level", r.summary())
        self.assertEqual(r.notes, [])

    def test_a_child_is_placed_by_its_parents_position_and_scale(self):
        b = SceneBuilder()
        _, parent = b.add("Parent", 5, 0, scale=(2, 2))
        b.add("Child", 1, 0, self.BRICK, father=parent)                                   # world x = 5 + 2 * 1
        b.add("Anchor", 3, 0, self.BRICK)
        self.scene("Nested", b)
        self.run_import()
        lv = self.project.level("Nested")
        self.assertEqual(lv.width, 5)                                                      # x from 3 to 7
        self.assertTrue(lv.get(0, 0) and lv.get(4, 0) and lv.get(1, 0) == "")

    def test_the_highest_sorting_order_wins_a_shared_cell_and_it_is_reported(self):
        b = SceneBuilder()
        b.add("Back", 0, 0, self.BRICK, order=0)
        b.add("Front", 0, 0, self.COIN, order=5)
        b.add("AlsoBack", 0, 0, self.BRICK, order=-1)
        self.scene("Stack", b)
        r = self.run_import()
        lv = self.project.level("Stack")
        self.assertEqual(self.project.tiles[lv.get(0, 0)].sprite, "coin")
        self.assertTrue(any("2 objects were hidden" in n for n in r.notes))

    def test_collider_trigger_and_body_decide_the_tile_traits(self):
        b = SceneBuilder()
        b.add("Wall", 0, 0, self.BRICK, collider=61, body=2)                              # static body: solid
        b.add("Crate", 1, 0, self.BRICK, collider=61, body=0)                             # dynamic body: solid and dynamic
        b.add("Zone", 2, 0, self.BRICK, collider=61, trigger=1)                           # a trigger is not solid
        b.add("Plain", 3, 0, self.BRICK)
        b.add("Lone", 4, 0, self.BRICK, body=0)                                           # a body with no collider is not solid
        self.scene("Traits", b)
        self.run_import()
        lv = self.project.level("Traits")
        traits = [(self.project.tiles[lv.get(x, 0)].solid, self.project.tiles[lv.get(x, 0)].dynamic) for x in range(5)]
        self.assertEqual(traits, [(True, False), (True, True), (False, False), (False, False), (False, False)])
        self.assertEqual(lv.get(2, 0), lv.get(3, 0))                                       # same sprite, same traits: one tile
        self.assertEqual(len({lv.get(x, 0) for x in range(5)}), 3)                         # wall / crate / plain

    def test_inactive_disabled_and_spriteless_objects_are_left_out(self):
        b = SceneBuilder()
        b.add("On", 0, 0, self.BRICK)
        b.add("Off", 1, 0, self.BRICK, active=0)
        b.add("Disabled", 2, 0, self.BRICK, enabled=0)
        _, hidden_parent = b.add("HiddenParent", 3, 0, active=0)
        b.add("UnderOff", 0, 0, self.COIN, father=hidden_parent)                            # inactive because its parent is
        self.scene("Filter", b)
        self.run_import()
        lv = self.project.level("Filter")
        self.assertEqual((lv.width, lv.height), (1, 1))
        self.assertEqual(self.project.tiles[lv.get(0, 0)].sprite, "brick")

    def test_sheet_slices_are_found_by_their_internal_id(self):
        sheet = self.rgba_png([[RED, RED, GREEN, GREEN]])
        self.put("sheet.png", sheet, sheet_meta(2, [("left", 0, 0, 2, 1), ("right", 2, 0, 2, 1)], guid=G_SHEET, ppu=2))        # slices are 2x1 px: one unit wide at 2 ppu
        b = SceneBuilder()
        b.add("L", 0, 0, (G_SHEET, 21300000))
        b.add("R", 1, 0, (G_SHEET, 21300002))
        self.scene("Sheet", b)
        self.run_import()
        lv = self.project.level("Sheet")
        self.assertEqual([self.project.tiles[lv.get(x, 0)].sprite for x in (0, 1)], ["left", "right"])

    def test_a_cell_size_can_be_chosen_and_the_default_is_the_common_sprite_size(self):
        from .unity_scene import choose_cell
        b = SceneBuilder()
        b.add("A", 0, 0, self.BRICK)
        b.add("B", 0.4, 0, self.BRICK)
        self.scene("Close", b)
        self.run_import()
        self.assertEqual(self.project.level("Close").width, 1)                             # at the default cell (the sprite's 1 unit) they share a cell
        self.run_import(cell=0.4)
        self.assertEqual(self.project.level("Close").width, 2)                             # at 0.4 they are one cell apart
        self.assertEqual(choose_cell([]), 1.0)

    def test_what_cannot_be_imported_is_counted_in_the_notes(self):
        b = SceneBuilder()
        b.add("Rotated", 0, 0, self.BRICK, rot_z=0.5)
        b.add("OffGrid", 1.4, 0, self.BRICK)
        b.add("Big", 3, 0, self.BRICK, scale=(3, 3))
        b.add("Unknown", 5, 0, ("e" * 32, 21300000))
        b.raw("--- !u!1001 &9\nPrefabInstance:\n  m_Modification: {}\n--- !u!1839735485 &10\nTilemap:\n  m_Enabled: 1\n")
        self.scene("Notes", b)
        r = self.run_import()
        text = "\n".join(r.notes)
        for what in ("1 object is rotated", "1 object was not on the grid", "1 object is not the size of a cell", "1 prefab instance uses a prefab that was not found",
                     "could not be found among the imported textures"):
            self.assertIn(what, text)

    def test_a_scene_too_big_for_a_level_is_skipped_with_a_note(self):
        b = SceneBuilder()
        b.add("A", 0, 0, self.BRICK)
        b.add("B", 600, 0, self.BRICK)
        self.scene("Huge", b)
        r = self.run_import()
        self.assertIsNone(self.project.level("Huge"))
        self.assertTrue(any("bigger than a level can be" in n for n in r.notes))
        self.run_import(cell=2000)                                                           # a big enough cell fits it: x = 600 is 0.3 cells, which rounds to cell 0
        self.assertEqual(self.project.level("Huge").width, 1)

    def test_a_binary_scene_is_refused_with_a_message(self):
        with open(os.path.join(self.scenes, "Bin.unity"), "wb") as f:
            f.write(b"UnityFS\0\0\0\0")
        r = self.run_import()
        self.assertEqual(self.project.levels, [])
        self.assertTrue(any("Force Text" in n for n in r.notes))

    def test_sprites_only_when_scenes_are_off_and_levels_survive_the_project_file_and_the_text_format(self):
        from .asciiart import export_level, import_levels
        b = SceneBuilder()
        b.add("W", 0, 0, self.BRICK, collider=61)
        b.add("C", 1, 0, self.COIN)
        self.scene("RT", b)
        self.run_import(scenes=False)
        self.assertEqual(self.project.levels, [])
        self.run_import()
        q = Project.from_json(self.project.to_json())
        self.assertEqual(q.levels[0].cells, self.project.levels[0].cells)
        again = Project("again")
        import_levels(export_level(self.project.levels[0], self.project), again)
        self.assertEqual(again.levels[0].cells, self.project.levels[0].cells)
        self.assertTrue(any(t.solid and t.sprite == "brick" for t in again.tiles.values()))

    # ---- prefab instances
    P_CRATE, P_TOWER, P_LOOP = "1" * 32, "2" * 32, "3" * 32

    def prefab(self, name, guid, builder):
        d = os.path.join(self.root, "Assets", "Prefabs")
        os.makedirs(d, exist_ok=True)
        path = os.path.join(d, name + ".prefab")
        with open(path, "w", encoding="utf-8") as f:
            f.write(builder.text())
        with open(path + ".meta", "w", encoding="utf-8") as f:
            f.write("fileFormatVersion: 2\nguid: %s\nPrefabImporter:\n  userData: \n" % guid)

    def sprites_at(self, level, project=None):
        """{(x, y): sprite name} of a level's filled cells."""
        p = project or self.project
        return {(x, y): p.tiles[level.get(x, y)].sprite for y in range(level.height) for x in range(level.width) if level.get(x, y)}

    def crate_prefab(self, sprite=None, collider=61, body=0):
        b = SceneBuilder()
        go, xf = b.add("Crate", 0, 0, sprite or self.BRICK, collider=collider, body=body)
        self.prefab("Crate", self.P_CRATE, b)
        return go, xf, b.sr

    def test_prefab_instances_are_placed_by_their_overrides_and_keep_the_prefabs_traits(self):
        _, xf, _ = self.crate_prefab()
        b = SceneBuilder()
        for x in (2, 3, 4):
            b.add("Ground%d" % x, x, 0, self.BRICK)
        b.instance(self.P_CRATE, [(xf, "m_LocalPosition.x", 2), (xf, "m_LocalPosition.y", 1)])
        b.instance(self.P_CRATE, [(xf, "m_LocalPosition.x", 4), (xf, "m_LocalPosition.y", 1)])
        self.scene("Prefabs", b)
        r = self.run_import()
        lv = self.project.level("Prefabs")
        self.assertEqual((lv.width, lv.height), (3, 2))
        self.assertEqual(self.rows(lv)[0].count("."), 1)                                                      # the crates at x = 2 and 4, a gap between
        top = [self.project.tiles[lv.get(x, 0)] if lv.get(x, 0) else None for x in range(3)]
        self.assertTrue(top[0].solid and top[0].dynamic and top[2].solid and top[2].dynamic and top[1] is None)    # the prefab's collider and dynamic body came with it
        self.assertFalse(self.project.tiles[lv.get(0, 1)].solid)                                               # the plain ground bricks are not those tiles
        self.assertEqual(r.notes, [])

    def test_a_prefabs_children_follow_the_instance(self):
        b = SceneBuilder()
        _, root = b.add("Root", 0, 0)
        b.add("Brick", 1, 0, self.BRICK, father=root)
        b.add("Coin", 2, 0, self.COIN, father=root)
        self.prefab("Tower", self.P_TOWER, b)
        s = SceneBuilder()
        s.add("Anchor", 5, 0, self.BRICK)
        s.instance(self.P_TOWER, [(root, "m_LocalPosition.x", 5)])                                             # children are at 6 and 7
        self.scene("Kids", s)
        self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Kids")), {(0, 0): "brick", (1, 0): "brick", (2, 0): "coin"})

    def test_prefabs_that_contain_prefabs_and_overrides_that_reach_into_them(self):
        _, crate_xf, _ = self.crate_prefab(collider=None, body=None)
        b = SceneBuilder()
        b.instance(self.P_CRATE, [(crate_xf, "m_LocalPosition.y", 1)])                                          # the tower holds a crate, one up...
        b.add("Top", 0, 2, self.COIN)                                                                           # ...and a coin two up
        self.prefab("Tower", self.P_TOWER, b)
        s = SceneBuilder()
        s.add("Anchor", 0, 0, self.BRICK)
        s.instance(self.P_TOWER, [(crate_xf, "m_LocalPosition.x", 3)])                                          # the scene moves the nested crate across
        self.scene("Nest", s)
        r = self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Nest")), {(0, 0): "coin", (3, 1): "brick", (0, 2): "brick"})
        self.assertEqual(r.notes, [])

    def test_an_override_can_change_the_sprite_and_switch_an_object_off(self):
        go, xf, sr = self.crate_prefab(collider=None, body=None)
        s = SceneBuilder()
        s.add("Anchor", 0, 0, self.BRICK)
        s.instance(self.P_CRATE, [(xf, "m_LocalPosition.x", 1), (sr, "m_Sprite", 0, (21300000, G_COIN))])      # a coin, not a brick
        s.instance(self.P_CRATE, [(xf, "m_LocalPosition.x", 2), (go, "m_IsActive", 0)])                         # switched off: not placed
        self.scene("Over", s)
        self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Over")), {(0, 0): "brick", (1, 0): "coin"})

    def test_an_instance_under_a_scene_object_is_offset_by_that_object(self):
        _, xf, _ = self.crate_prefab(collider=None, body=None)
        s = SceneBuilder()
        _, mover = s.add("Mover", 10, 0, self.BRICK)
        s.instance(self.P_CRATE, [(xf, "m_LocalPosition.x", 1)], parent=mover)                                   # world x = 10 + 1
        self.scene("Under", s)
        self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Under")), {(0, 0): "brick", (1, 0): "brick"})

    def test_a_scene_object_parented_to_an_object_inside_a_prefab_instance(self):
        _, xf, _ = self.crate_prefab(collider=None, body=None)
        s = SceneBuilder()
        inst = s.instance(self.P_CRATE, [(xf, "m_LocalPosition.x", 5)])
        stand_in = s.stripped_transform(inst, xf)                                                               # how the scene names the crate's transform
        s.add("Coin", 1, 0, self.COIN, father=stand_in)                                                         # world x = 5 + 1
        self.scene("Child", s)
        self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Child")), {(0, 0): "brick", (1, 0): "coin"})

    def test_missing_prefabs_loops_and_unplaceable_overrides_are_reported_not_fatal(self):
        loop = SceneBuilder()
        loop.add("Brick", 0, 0, self.BRICK)
        loop.instance(self.P_LOOP)                                                                              # a prefab that contains itself
        self.prefab("Loop", self.P_LOOP, loop)
        _, xf, _ = self.crate_prefab(collider=None, body=None)
        s = SceneBuilder()
        s.add("Anchor", 0, 0, self.COIN)
        s.instance(self.P_LOOP, [(xf, "m_LocalPosition.x", 1)])
        s.instance("9" * 32)                                                                                    # no such prefab
        s.instance(self.P_CRATE, [(987654, "m_LocalPosition.x", 4)])                                            # names an object the prefab does not have
        self.scene("Bad", s)
        r = self.run_import()                                                                                   # (returning at all means the loop did not spin)
        text = "\n".join(r.notes)
        self.assertIn("1 prefab instance uses a prefab that was not found", text)
        self.assertIn("nested in a loop", text)
        self.assertIn("1 prefab override names an object", text)
        self.assertIsNotNone(self.project.level("Bad"))

    def test_prefab_files_are_not_scenes_and_a_scene_without_prefabs_still_imports(self):
        self.crate_prefab()
        self.assertEqual(self.run_import().levels, [])                                                          # a prefab alone makes no level
        s = SceneBuilder()
        s.add("A", 0, 0, self.BRICK)
        self.scene("Plain", s)
        self.assertEqual(len(self.run_import().levels), 1)

    # ---- tilemaps (blocks written the way Unity serializes them: `first:`/`second:` tiles indexing a sprite array whose unused slots are {fileID: 0})
    def tilemap_scene(self, name, **kw):
        """A scene with a Grid and, under it, a Tilemap of `tiles` (kw: tiles, sprites, ... for add_tilemap; grid_cell, grid_x, grid_y for the Grid)."""
        b = SceneBuilder()
        _, grid = b.add_grid(cell=kw.pop("grid_cell", (1, 1)), x=kw.pop("grid_x", 0), y=kw.pop("grid_y", 0), layout=kw.pop("layout", 0))
        b.add_tilemap("Tiles", father=grid, **kw)
        self.scene(name, b)
        return b

    def test_a_tilemap_becomes_tiles_with_the_collider_deciding_solid(self):
        self.tilemap_scene("Floor", tiles=[(x, 0, 0) for x in range(4)], sprites=[self.BRICK], collider=True)
        r = self.run_import()
        lv = self.project.level("Floor")
        self.assertEqual((lv.width, lv.height), (4, 1))
        self.assertEqual(set(self.sprites_at(lv).values()), {"brick"})
        self.assertTrue(all(self.project.tiles[lv.get(x, 0)].solid for x in range(4)))
        self.assertEqual(r.notes, [])
        self.tilemap_scene("Soft", tiles=[(0, 0, 0)], sprites=[self.BRICK], collider=False)
        self.run_import()
        self.assertFalse(self.project.tiles[self.project.level("Soft").get(0, 0)].solid)

    def test_cells_can_be_negative_and_y_points_up(self):
        self.tilemap_scene("Neg", tiles=[(-2, -1, 0), (-1, -1, 0), (0, -1, 0), (0, 0, 1)], sprites=[self.BRICK, self.COIN])
        self.run_import()
        lv = self.project.level("Neg")
        self.assertEqual(self.sprites_at(lv), {(2, 0): "coin", (0, 1): "brick", (1, 1): "brick", (2, 1): "brick"})

    def test_the_sprite_index_picks_from_the_array_and_an_empty_slot_is_counted(self):
        self.tilemap_scene("Slots", tiles=[(0, 0, 0), (1, 0, 1), (2, 0, 2), (3, 0, 9)], sprites=[self.BRICK, None, self.COIN])     # slot 1 is {fileID: 0}; 9 is out of range
        r = self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Slots")), {(0, 0): "brick", (2, 0): "coin"})
        self.assertTrue(any("2 tiles have no sprite and were skipped" in n for n in r.notes))

    def test_sliced_sprites_are_found_by_their_64_bit_internal_ids(self):
        ids = [-7963366471572989131, 5027406166034386782]                                    # what Unity writes for sprites cut in the sprite editor
        sheet = self.rgba_png([[RED, RED, GREEN, GREEN]] * 2)
        self.put("big.png", sheet, sheet_meta(2, [("left", 0, 0, 2, 2), ("right", 2, 0, 2, 2)], guid=G_SHEET, ppu=2, ids=ids))
        self.tilemap_scene("Sheet64", tiles=[(0, 0, 0), (1, 0, 1)], sprites=[(G_SHEET, ids[0]), (G_SHEET, ids[1])])
        self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Sheet64")), {(0, 0): "left", (1, 0): "right"})

    def test_a_sprite_that_was_not_imported_is_reported(self):
        self.tilemap_scene("Gone", tiles=[(0, 0, 0), (1, 0, 1)], sprites=[self.BRICK, ("e" * 32, 21300000)])
        r = self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Gone")), {(0, 0): "brick"})
        self.assertTrue(any("could not be found among the imported textures" in n for n in r.notes))

    def test_the_tilemaps_transform_and_scale_carry_its_tiles(self):
        self.tilemap_scene("Moved", tiles=[(0, 0, 0), (1, 0, 0)], sprites=[self.BRICK], x=10, y=5, grid_x=100)                     # both are offsets: one level, 2 wide
        self.run_import()
        self.assertEqual(self.project.level("Moved").width, 2)
        b = SceneBuilder()
        _, grid = b.add_grid(scale=(2, 2))                                                   # a Grid scaled 2x: cells are 2 world units, so sprites are half a cell
        b.add_tilemap("Tiles", [(0, 0, 0), (1, 0, 0)], [self.BRICK], father=grid)
        self.scene("Scaled", b)
        r = self.run_import()
        self.assertEqual(self.project.level("Scaled").width, 2)
        self.assertEqual(r.notes, [])                                                        # the scale makes the 1-unit sprites 2 units: exactly the 2-unit cell

    def test_sprites_centred_in_a_tiles_cell_share_it_and_others_are_reported_off_grid(self):
        b = SceneBuilder()
        _, grid = b.add_grid()
        b.add_tilemap("Tiles", [(0, 0, 0), (1, 0, 0)], [self.BRICK], father=grid, x=10, y=5, order=0)           # cell 0 is centred at (10.5, 5.5)
        b.add("Coin", 10.5, 5.5, self.COIN, order=5)                                           # in that cell, drawn above the tile
        b.add("Stray", 13, 5, self.BRICK)                                                     # on a cell's corner, not in one
        self.scene("Phase", b)
        r = self.run_import()
        lv = self.project.level("Phase")
        sprites = self.sprites_at(lv)
        self.assertEqual(sprites[(0, 0)], "coin")                                              # the coin took the tile's cell
        self.assertEqual(sprites[(1, 0)], "brick")
        text = "\n".join(r.notes)
        self.assertIn("1 object was hidden behind another sprite", text)
        self.assertIn("1 object was not on the grid", text)

    def test_the_grids_cell_size_sets_the_level_grid(self):
        self.tilemap_scene("Fine", tiles=[(0, 0, 0), (1, 0, 0), (2, 0, 0)], sprites=[self.BRICK], grid_cell=(0.5, 0.5))                # a 1-unit sprite on a half-unit grid
        r = self.run_import()
        lv = self.project.level("Fine")
        self.assertEqual(lv.width, 3)                                                          # a tile is a cell, whatever its sprite's size
        self.assertTrue(any("not the size of a cell" in n for n in r.notes))

    def test_off_and_disabled_tilemaps_place_nothing(self):
        self.tilemap_scene("Off", tiles=[(0, 0, 0)], sprites=[self.BRICK], renderer_enabled=0)
        self.assertEqual(self.run_import().levels, [])
        self.tilemap_scene("Inactive", tiles=[(0, 0, 0)], sprites=[self.BRICK], active=0)
        self.run_import()
        self.assertIsNone(self.project.level("Inactive"))

    def test_an_empty_tilemap_is_harmless(self):
        b = SceneBuilder()
        _, grid = b.add_grid()
        b.add_tilemap("Empty", [], [], father=grid)                                           # `m_Tiles: {}`
        b.add("Brick", 0, 0, self.BRICK)
        self.scene("Blank", b)
        r = self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Blank")), {(0, 0): "brick"})

    def test_flipped_tiles_and_isometric_grids_are_reported(self):
        self.tilemap_scene("Flip", tiles=[(0, 0, 0, 0), (1, 0, 0, 1)], sprites=[self.BRICK], matrices=[(1, 0, 0, 1), (-1, 0, 0, 1)])
        r = self.run_import()
        self.assertTrue(any("1 tile is rotated or flipped" in n for n in r.notes))
        self.tilemap_scene("Iso", tiles=[(0, 0, 0)], sprites=[self.BRICK], layout=2)
        r = self.run_import()
        self.assertTrue(any("1 tilemap is on an isometric or hexagonal grid" in n for n in r.notes))

    def test_a_tilemap_inside_a_prefab_is_placed_by_the_instance(self):
        pb = SceneBuilder()
        _, grid = pb.add_grid()
        pb.add_tilemap("Tiles", [(0, 0, 0), (1, 0, 0)], [self.BRICK], father=grid)
        self.prefab("Room", self.P_TOWER, pb)
        s = SceneBuilder()
        s.instance(self.P_TOWER, [(grid, "m_LocalPosition.x", 5)])                           # the room's grid is moved to x = 5: tile 0 is centred at 5.5
        s.add("Coin", 5.5, 0.5, self.COIN, order=3)                                          # on that tile's cell
        self.scene("Rooms", s)
        r = self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Rooms")), {(0, 0): "coin", (1, 0): "brick"})

    def test_an_auto_named_tileset_keeps_each_tile_its_own_sprite(self):
        ids = [11, 22]
        self.put("tiles.png", self.rgba_png([[RED, RED, GREEN, GREEN]] * 2), sheet_meta(2, [("tiles_0", 0, 0, 2, 2), ("tiles_1", 2, 0, 2, 2)], guid=G_SHEET, ppu=2, ids=ids))
        self.tilemap_scene("Set", tiles=[(0, 0, 0), (1, 0, 1)], sprites=[(G_SHEET, 11), (G_SHEET, 22)])
        r = self.run_import()
        lv = self.project.level("Set")
        self.assertEqual(self.sprites_at(lv), {(0, 0): "tiles_0", (1, 0): "tiles_1"})                          # not both "tiles": grass and dirt stay different tiles
        self.assertNotEqual(lv.get(0, 0), lv.get(1, 0))
        self.assertEqual(r.notes, [])

    def test_a_later_frame_of_a_hand_named_animation_is_reported(self):
        ids = [31, 32]
        self.put("hero.png", self.rgba_png([[RED, RED, GREEN, GREEN]] * 2), sheet_meta(2, [("walk_0", 0, 0, 2, 2), ("walk_1", 2, 0, 2, 2)], guid=G_SHEET, ppu=2, ids=ids))
        s = SceneBuilder()
        s.add("A", 0, 0, (G_SHEET, 31))                                                                        # frame 0: fine
        s.add("B", 1, 0, (G_SHEET, 32))                                                                        # frame 1: the level can only show frame 0
        self.scene("Walk", s)
        r = self.run_import()
        self.assertEqual(self.sprites_at(self.project.level("Walk")), {(0, 0): "walk", (1, 0): "walk"})
        self.assertTrue(any("1 object or tile shows a later frame of an animated sprite" in n for n in r.notes))
        self.assertEqual(len(self.project.sprite("walk").frames), 2)

    def test_importing_again_reuses_the_tiles(self):
        from .unity_scene import import_unity_scenes
        b = SceneBuilder()
        b.add("W", 0, 0, self.BRICK, collider=61)
        self.scene("Twice", b)
        self.run_import()
        n_tiles = len(self.project.tiles)
        import_unity_scenes(self.project, self.root, self.report)
        self.assertEqual(len(self.project.tiles), n_tiles)
        self.assertEqual([l.name for l in self.project.levels], ["Twice", "Twice 2"])


if __name__ == "__main__":
    unittest.main()
