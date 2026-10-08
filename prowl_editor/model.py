"""model.py: what a Prowl2D project is, with no GUI and no engine in it (so it can be tested, and scripted, on its own).

A project is JSON on disk and these classes in memory:

  Palette   an ordered list of colours, each with a one-character KEY. Sprites do not hold colours, they hold palette INDICES, which is what makes a
            pixel-level effect cheap (a blink is "change the colour of index 5"), and what lets ASCII art say a pixel with a letter. Index 0 is always transparent.
  Sprite    a size and a list of frames; a frame is one palette index per pixel.
  TileDef   an emoji (any grapheme) with a natural name and, optionally, the sprite that draws it and whether it is solid.
  Level     a grid of tiles, each cell an emoji or "" for empty.
  Project   all of the above.

In JSON a sprite frame is a list of strings, one per row, written with the palette's keys (so a project file reads like the ASCII art it can be exported
as), and a level is a list of strings, one per row, of emoji. Loading checks them, and refuses what it cannot understand with a message that says where.
"""
import copy
import json
import re

from .lights import DEFAULT_LIGHTING, clean_light, clean_lighting

FORMAT = "prowl2d-project"
VERSION = 1
TRANSPARENT_KEY = "."
MAX_PALETTE = 256            # an index fits a byte
MAX_SPRITE_SIDE = 256
MAX_LEVEL_SIDE = 512
DEFAULT_EMPTY = "⬛"     # black large square: what an empty cell is written as in a level's text


class ProjectError(ValueError):
    """The project (or text being imported) is not valid; the message says where."""


def parse_color(text):
    """'#rrggbb' (or 'rrggbb', or '#rgb') -> (r, g, b)."""
    s = text.strip().lstrip("#")
    if re.fullmatch(r"[0-9a-fA-F]{3}", s):
        s = "".join(c * 2 for c in s)
    if not re.fullmatch(r"[0-9a-fA-F]{6}", s):
        raise ProjectError("'%s' is not a colour (expected #rrggbb)" % text)
    return (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16))


def color_hex(rgb):
    return "#%02x%02x%02x" % tuple(rgb)


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# palette

# a friendly default: letters that mean what they say, so ASCII art written without a legend mostly looks right
DEFAULT_COLORS = [
    ("K", "black", (0x14, 0x14, 0x1c)),
    ("W", "white", (0xf2, 0xf2, 0xf2)),
    ("R", "red", (0xe0, 0x3c, 0x3c)),
    ("O", "orange", (0xf0, 0x8c, 0x2e)),
    ("Y", "yellow", (0xf2, 0xd8, 0x3b)),
    ("G", "green", (0x3c, 0xb4, 0x4b)),
    ("L", "lime", (0x9a, 0xe0, 0x60)),
    ("C", "cyan", (0x3c, 0xc8, 0xd8)),
    ("B", "blue", (0x3c, 0x64, 0xe0)),
    ("P", "purple", (0x8a, 0x3c, 0xe0)),
    ("M", "magenta", (0xe0, 0x3c, 0xb4)),
    ("N", "brown", (0x8a, 0x5a, 0x2e)),
    ("S", "silver", (0xa0, 0xa0, 0xa8)),
    ("D", "dark gray", (0x50, 0x50, 0x58)),
    ("T", "tan", (0xd8, 0xb0, 0x80)),
]


class PaletteEntry:
    def __init__(self, key, name, rgb):
        self.key = key          # one character, unique in the palette
        self.name = name
        self.rgb = rgb          # (r, g, b), or None for the transparent entry at index 0

    def copy(self):
        return PaletteEntry(self.key, self.name, self.rgb)


class Palette:
    def __init__(self, with_defaults=True):
        self.entries = [PaletteEntry(TRANSPARENT_KEY, "transparent", None)]
        if with_defaults:
            for key, name, rgb in DEFAULT_COLORS:
                self.entries.append(PaletteEntry(key, name, rgb))
        self.version = 0        # bumped by every change: a renderer that cached colours knows to rebuild

    def __len__(self):
        return len(self.entries)

    def touch(self):
        self.version += 1

    def index_of(self, key):
        for i, e in enumerate(self.entries):
            if e.key == key:
                return i
        return None

    def key_of(self, index):
        return self.entries[index].key if 0 <= index < len(self.entries) else None

    def check_key(self, key, ignore_index=None):
        if len(key) != 1 or key.isspace():
            raise ProjectError("a palette key is one visible character (got %r)" % key)
        i = self.index_of(key)
        if i is not None and i != ignore_index:
            raise ProjectError("the palette key '%s' is already used by '%s'" % (key, self.entries[i].name))

    def add(self, key, name, rgb):
        if len(self.entries) >= MAX_PALETTE:
            raise ProjectError("a palette holds at most %d colours" % MAX_PALETTE)
        self.check_key(key)
        self.entries.append(PaletteEntry(key, name, tuple(rgb)))
        self.touch()
        return len(self.entries) - 1

    def set_color(self, index, rgb):
        if index == 0:
            raise ProjectError("index 0 is transparent: it has no colour")
        self.entries[index].rgb = tuple(rgb)
        self.touch()

    def set_key(self, index, key):
        if index == 0:
            raise ProjectError("the transparent entry's key is fixed ('%s')" % TRANSPARENT_KEY)
        self.check_key(key, ignore_index=index)
        self.entries[index].key = key
        self.touch()

    def first_free_key(self):
        """A key not in use, preferring letters."""
        for c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789@%&*+=~^$?!":
            if self.index_of(c) is None:
                return c
        raise ProjectError("the palette has no free key left")

    def rgba(self, index):
        """(r, g, b, a) bytes of an index; an index past the palette is transparent."""
        if 0 < index < len(self.entries) and self.entries[index].rgb is not None:
            r, g, b = self.entries[index].rgb
            return (r, g, b, 255)
        return (0, 0, 0, 0)

    def to_json(self):
        return [{"key": e.key, "name": e.name, "color": None if e.rgb is None else color_hex(e.rgb)} for e in self.entries]

    @staticmethod
    def from_json(items):
        p = Palette(with_defaults=False)
        if not isinstance(items, list) or not items:
            raise ProjectError("palette: expected a list of colours")
        for n, item in enumerate(items):
            where = "palette[%d]" % n
            if not isinstance(item, dict) or "key" not in item:
                raise ProjectError("%s: expected {key, name, color}" % where)
            key = item["key"]
            if n == 0:
                if key != TRANSPARENT_KEY:
                    raise ProjectError("%s: the first entry must be the transparent one, key '%s'" % (where, TRANSPARENT_KEY))
                continue
            if item.get("color") is None:
                raise ProjectError("%s: only the first entry may be transparent" % where)
            try:
                p.add(key, item.get("name", key), parse_color(item["color"]))
            except ProjectError as e:
                raise ProjectError("%s: %s" % (where, e))
        p.version = 0
        return p


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# sprites

class Sprite:
    def __init__(self, name, width=8, height=8, fps=8.0):
        self.name = name
        self.width = width
        self.height = height
        self.fps = fps
        self.scripts = []                              # the names of the script classes (Project.scripts) that run on every tile showing this sprite, in play
        self.frames = [bytearray(width * height)]      # a frame: width * height palette indices, row by row from the top; 0 is transparent

    def check_size(self, width, height):
        if not (1 <= width <= MAX_SPRITE_SIDE and 1 <= height <= MAX_SPRITE_SIDE):
            raise ProjectError("a sprite is between 1x1 and %dx%d pixels (got %dx%d)" % (MAX_SPRITE_SIDE, MAX_SPRITE_SIDE, width, height))

    def get(self, frame, x, y):
        return self.frames[frame][y * self.width + x]

    def set(self, frame, x, y, index):
        if 0 <= x < self.width and 0 <= y < self.height:
            self.frames[frame][y * self.width + x] = index

    def add_frame(self, after=None, copy=False):
        """Adds a frame after frame `after` (default: at the end), a copy of it or a blank one. Returns its number."""
        at = len(self.frames) if after is None else after + 1
        frame = bytearray(self.frames[after]) if (copy and after is not None) else bytearray(self.width * self.height)
        self.frames.insert(at, frame)
        return at

    def delete_frame(self, frame):
        if len(self.frames) > 1:
            del self.frames[frame]

    def move_frame(self, frame, to):
        to = max(0, min(len(self.frames) - 1, to))
        self.frames.insert(to, self.frames.pop(frame))
        return to

    def resize(self, width, height):
        """Changes the size, keeping the pixels at the top left that still fit."""
        self.check_size(width, height)
        new_frames = []
        for f in self.frames:
            nf = bytearray(width * height)
            for y in range(min(height, self.height)):
                nf[y * width:y * width + min(width, self.width)] = f[y * self.width:y * self.width + min(width, self.width)]
            new_frames.append(nf)
        self.frames, self.width, self.height = new_frames, width, height

    def flood_fill(self, frame, x, y, index):
        f = self.frames[frame]
        if not (0 <= x < self.width and 0 <= y < self.height):
            return
        target = f[y * self.width + x]
        if target == index:
            return
        stack = [(x, y)]
        while stack:
            cx, cy = stack.pop()
            if 0 <= cx < self.width and 0 <= cy < self.height and f[cy * self.width + cx] == target:
                f[cy * self.width + cx] = index
                stack += [(cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)]

    def rgba(self, palette, frame):
        """The frame as width * height * 4 bytes (r, g, b, a: straight alpha), the way a texture takes them."""
        out = bytearray()
        lut = [palette.rgba(i) for i in range(len(palette))]
        for idx in self.frames[frame]:
            out += bytes(lut[idx] if idx < len(lut) else (0, 0, 0, 0))
        return bytes(out)

    def remap(self, mapping):
        """mapping[old index] -> new index, for every pixel of every frame."""
        table = bytes(mapping.get(i, 0) for i in range(256))
        for f in self.frames:
            f[:] = f.translate(table)

    def rows(self, palette, frame):
        """The frame as a list of strings written with the palette's keys."""
        keys = [e.key for e in palette.entries]
        f = self.frames[frame]
        return ["".join(keys[f[y * self.width + x]] if f[y * self.width + x] < len(keys) else TRANSPARENT_KEY for x in range(self.width))
                for y in range(self.height)]

    def frame_from_rows(self, palette, rows, where="frame"):
        if len(rows) != self.height:
            raise ProjectError("%s: %d rows, the sprite is %d high" % (where, len(rows), self.height))
        out = bytearray()
        for y, row in enumerate(rows):
            if len(row) != self.width:
                raise ProjectError("%s: row %d is %d wide, the sprite is %d wide" % (where, y + 1, len(row), self.width))
            for x, ch in enumerate(row):
                i = palette.index_of(ch)
                if i is None:
                    raise ProjectError("%s: row %d, column %d: '%s' is not in the palette" % (where, y + 1, x + 1, ch))
                out.append(i)
        return out

    def to_json(self, palette):
        d = {"name": self.name, "width": self.width, "height": self.height, "fps": self.fps,
             "frames": [self.rows(palette, i) for i in range(len(self.frames))]}
        if self.scripts:
            d["scripts"] = list(self.scripts)
        return d

    @staticmethod
    def from_json(item, palette, where):
        if not isinstance(item, dict) or "name" not in item or "frames" not in item:
            raise ProjectError("%s: expected {name, width, height, fps, frames}" % where)
        frames = item["frames"]
        if not isinstance(frames, list) or not frames or not isinstance(frames[0], list) or not frames[0]:
            raise ProjectError("%s: frames must be a non-empty list of frames, each a list of row strings" % where)
        height = len(frames[0])
        width = len(frames[0][0]) if isinstance(frames[0][0], str) else 0
        s = Sprite(str(item["name"]), width, height, float(item.get("fps", 8)))
        s.scripts = [str(n) for n in item.get("scripts", [])]
        s.check_size(width, height)
        s.frames = []
        for n, rows in enumerate(frames):
            if not isinstance(rows, list) or not all(isinstance(r, str) for r in rows):
                raise ProjectError("%s.frames[%d]: expected a list of row strings" % (where, n))
            s.frames.append(s.frame_from_rows(palette, rows, "%s.frames[%d]" % (where, n)))
        return s


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# tiles and levels

def slug(name):
    """'Deciduous Tree' -> 'deciduous_tree': what makes a tile's name and a sprite's name the same name."""
    return re.sub(r"[\s_\-]+", "_", name.strip().lower())


class TileDef:
    def __init__(self, emoji, name, sprite="", solid=False, dynamic=False, diggable=False):
        self.emoji = emoji
        self.name = name         # natural, e.g. 'brick' or 'deciduous tree'
        self.sprite = sprite     # a sprite's name; "" means: the sprite with this tile's name, if there is one
        self.solid = solid       # the viewport's play mode gives it a collider
        self.dynamic = dynamic   # ...a movable body (a crate) instead of part of the static ground; implies a collider
        self.diggable = diggable  # a blast removes it (dirt); the viewport's play mode only

    def to_json(self):
        d = {"emoji": self.emoji, "name": self.name}
        if self.sprite:
            d["sprite"] = self.sprite
        if self.solid:
            d["solid"] = True
        if self.dynamic:
            d["dynamic"] = True
        if self.diggable:
            d["diggable"] = True
        return d


class Level:
    def __init__(self, name, width=20, height=12):
        self.name = name
        self.width = width
        self.height = height
        self.cells = [""] * (width * height)   # row by row from the top; "" is empty, otherwise an emoji
        self.lights = []    # the lights in the level (lights.py)
        self.lighting = copy.deepcopy(DEFAULT_LIGHTING)   # ...and what they light over: {"enabled", "ambient", "falloff", "glow", "exposure"}
        self.effects = []   # the picture effects over the level, first to last: {"effect": name, "values": {parameter: value}, "enabled": bool} (fx.py, fxdefs.py)

    def check_size(self, width, height):
        if not (1 <= width <= MAX_LEVEL_SIDE and 1 <= height <= MAX_LEVEL_SIDE):
            raise ProjectError("a level is between 1x1 and %dx%d cells (got %dx%d)" % (MAX_LEVEL_SIDE, MAX_LEVEL_SIDE, width, height))

    def get(self, x, y):
        return self.cells[y * self.width + x] if (0 <= x < self.width and 0 <= y < self.height) else ""

    def set(self, x, y, emoji):
        if 0 <= x < self.width and 0 <= y < self.height:
            self.cells[y * self.width + x] = emoji

    def resize(self, width, height):
        self.check_size(width, height)
        cells = [""] * (width * height)
        for y in range(min(height, self.height)):
            for x in range(min(width, self.width)):
                cells[y * width + x] = self.cells[y * self.width + x]
        self.cells, self.width, self.height = cells, width, height

    def flood_fill(self, x, y, emoji):
        if not (0 <= x < self.width and 0 <= y < self.height):
            return
        target = self.get(x, y)
        if target == emoji:
            return
        stack = [(x, y)]
        while stack:
            cx, cy = stack.pop()
            if 0 <= cx < self.width and 0 <= cy < self.height and self.cells[cy * self.width + cx] == target:
                self.cells[cy * self.width + cx] = emoji
                stack += [(cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)]

    def used_emoji(self):
        seen = []
        for c in self.cells:
            if c and c not in seen:
                seen.append(c)
        return seen


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the project

def _effects_from_json(items, where):
    """A level's "effects" list, checked against the effect registry: an unknown effect or parameter, or a value that cannot be stored, is an error that names it."""
    from .fx import pack
    if not isinstance(items, list):
        raise ProjectError("%s: effects must be a list" % where)
    out = []
    for n, item in enumerate(items):
        at = "%s.effects[%d]" % (where, n)
        if not isinstance(item, dict) or "effect" not in item:
            raise ProjectError("%s: expected {effect, values}" % at)
        values = item.get("values", {})
        if not isinstance(values, dict):
            raise ProjectError("%s: values must be an object" % at)
        try:
            pack(item["effect"], values)
        except (KeyError, ValueError, TypeError) as e:
            raise ProjectError("%s: %s" % (at, str(e).strip("'\"")))
        out.append({"effect": item["effect"], "values": dict(values), "enabled": bool(item.get("enabled", True))})
    return out


def _lights_from_json(item, where):
    """A level's "lights" and "lighting", checked and brought into range; an error names the place."""
    lights = item.get("lights", [])
    if not isinstance(lights, list):
        raise ProjectError("%s: lights must be a list" % where)
    out = []
    for n, l in enumerate(lights):
        try:
            out.append(clean_light(l))
        except ValueError as e:
            raise ProjectError("%s.lights[%d]: %s" % (where, n, e))
    try:
        lighting = clean_lighting(item.get("lighting", DEFAULT_LIGHTING))
    except ValueError as e:
        raise ProjectError("%s.lighting: %s" % (where, e))
    return out, lighting


SCRIPT_CLASS = re.compile(r"\[\s*Script\b[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal)\s+)?(?:sealed\s+)?class\s+(\w+)")

SCRIPT_TEMPLATE = """using System;
using Prowl.Core2D;

// A script: C# that runs on a node while the level plays.
// Keep to the C# subset (no lambdas, try/catch, ...): Build says where a line does not fit.
// Callbacks: Start, Update, FixedUpdate, LateUpdate, OnEnable, OnDisable,
//   OnCollisionBegin2D(Collision2D hit), OnCollisionEnd2D(Collision2D hit),
//   OnTriggerEnter2D(Collider2D other), OnTriggerStay2D, OnTriggerExit2D.
// Input: Input2D.Key(Input2D.Left), Input2D.Key('A'), Input2D.MouseX, Input2D.MouseDown(0).
[Script, MaxInstances(16)]
class %(name)s
{
    public Component Self;
    public float Time;

    public void Update()
    {
        Time += 1f / 60f;
        // Self.Node.SetPosition(Self.Node.WorldX() + 0.02f, Self.Node.WorldY());
    }
}
"""


class ScriptFile:
    """A C# file of the project (written to the build folder as <name>.cs). The classes marked [Script] in it are what sprites and the game attach by name."""

    def __init__(self, name, text=""):
        self.name = name
        self.text = text

    def classes(self):
        return SCRIPT_CLASS.findall(re.sub(r"//[^\n]*", "", self.text))


class Project:
    def __init__(self, name="untitled"):
        self.name = name
        self.palette = Palette()
        self.sprites = []
        self.tiles = {}                 # emoji -> TileDef (insertion order is the tile palette's order)
        self.levels = []
        self.scripts = []               # ScriptFile: the project's C# (the editor's code editor; Build compiles them into the engine)
        self.game_scripts = []          # script class names that run once, on a node of the game itself, whenever a level plays
        self.empty = DEFAULT_EMPTY      # what an empty cell is written as when a level is exported as text
        self.path = None                # the file it was loaded from or saved to
        self.dirty = False
        self.revision = 0               # bumped by every edit (touch): what draws the project (the viewport's textures) rebuilds when it changes

    def touch(self):
        """Says the project was edited."""
        self.revision += 1
        self.dirty = True

    # ---- lookups
    def sprite(self, name):
        for s in self.sprites:
            if s.name == name:
                return s
        return None

    def script_classes(self):
        """Every [Script] class of the project's files: {class name: file name}."""
        return {c: f.name for f in self.scripts for c in f.classes()}

    def script_file(self, name):
        for f in self.scripts:
            if f.name == name:
                return f
        return None

    def level(self, name):
        for l in self.levels:
            if l.name == name:
                return l
        return None

    def unique_name(self, base, existing):
        names = set(existing)
        if base not in names:
            return base
        n = 2
        while "%s %d" % (base, n) in names:
            n += 1
        return "%s %d" % (base, n)

    def sprite_for_tile(self, tile):
        """The sprite that draws a tile: the one it names, else the one that has the tile's name (by slug), else None."""
        if tile.sprite:
            return self.sprite(tile.sprite)
        want = slug(tile.name)
        for s in self.sprites:
            if slug(s.name) == want:
                return s
        return None

    def add_sprite(self, sprite):
        sprite.name = self.unique_name(sprite.name, [s.name for s in self.sprites])
        self.sprites.append(sprite)
        self.dirty = True
        return sprite

    def add_level(self, level):
        level.name = self.unique_name(level.name, [l.name for l in self.levels])
        self.levels.append(level)
        self.dirty = True
        return level

    def remove_palette_entry(self, index):
        """Removes a colour; pixels that used it become transparent and the indices above it move down, in every sprite."""
        if index == 0:
            raise ProjectError("the transparent entry cannot be removed")
        mapping = {}
        for i in range(len(self.palette.entries)):
            mapping[i] = 0 if i == index else (i - 1 if i > index else i)
        for s in self.sprites:
            s.remap(mapping)
        del self.palette.entries[index]
        self.palette.touch()
        self.dirty = True

    def move_palette_entry(self, index, to):
        """Reorders the palette (never moving index 0); sprites follow, so no pixel changes colour."""
        n = len(self.palette.entries)
        if index == 0 or to == 0 or not (0 < to < n) or index == to:
            return index
        order = list(range(n))
        order.insert(to, order.pop(index))        # order[new position] = old index
        mapping = {old: new for new, old in enumerate(order)}
        for s in self.sprites:
            s.remap(mapping)
        self.palette.entries = [self.palette.entries[old] for old in order]
        self.palette.touch()
        self.dirty = True
        return to

    # ---- JSON
    def to_json(self):
        return {
            "format": FORMAT,
            "version": VERSION,
            "name": self.name,
            "palette": self.palette.to_json(),
            "sprites": [s.to_json(self.palette) for s in self.sprites],
            "empty": self.empty,
            "tiles": [t.to_json() for t in self.tiles.values()],
            "levels": [self._level_json(l) for l in self.levels],
            **({"scripts": [{"name": f.name, "text": f.text} for f in self.scripts]} if self.scripts else {}),
            **({"game_scripts": list(self.game_scripts)} if self.game_scripts else {}),
        }

    def _level_json(self, l):
        d = {"name": l.name, "rows": ["".join(l.get(x, y) or self.empty for x in range(l.width)) for y in range(l.height)]}
        if l.lights or l.lighting != DEFAULT_LIGHTING:
            d["lighting"] = copy.deepcopy(l.lighting)
        if l.lights:
            d["lights"] = copy.deepcopy(l.lights)
        if l.effects:
            d["effects"] = [{"effect": e["effect"], "values": dict(e["values"]), "enabled": bool(e.get("enabled", True))} for e in l.effects]
        return d

    @staticmethod
    def from_json(data):
        from .asciiart import split_graphemes      # (a level's rows are emoji: graphemes, not characters)
        if not isinstance(data, dict) or data.get("format") != FORMAT:
            raise ProjectError("not a Prowl2D project (its \"format\" should be \"%s\")" % FORMAT)
        if data.get("version", 0) > VERSION:
            raise ProjectError("this project is version %s, newer than this editor understands (%d)" % (data.get("version"), VERSION))
        p = Project(str(data.get("name", "untitled")))
        p.palette = Palette.from_json(data.get("palette"))
        for n, item in enumerate(data.get("sprites", [])):
            p.sprites.append(Sprite.from_json(item, p.palette, "sprites[%d]" % n))
        p.empty = data.get("empty", DEFAULT_EMPTY)
        for n, item in enumerate(data.get("tiles", [])):
            if not isinstance(item, dict) or "emoji" not in item or "name" not in item:
                raise ProjectError("tiles[%d]: expected {emoji, name}" % n)
            p.tiles[item["emoji"]] = TileDef(item["emoji"], item["name"], item.get("sprite", ""), bool(item.get("solid", False)),
                                          bool(item.get("dynamic", False)), bool(item.get("diggable", False)))
        for n, item in enumerate(data.get("scripts", [])):
            if not isinstance(item, dict) or "name" not in item:
                raise ProjectError("scripts[%d]: expected {name, text}" % n)
            p.scripts.append(ScriptFile(str(item["name"]), str(item.get("text", ""))))
        p.game_scripts = [str(n) for n in data.get("game_scripts", [])]
        for n, item in enumerate(data.get("levels", [])):
            where = "levels[%d]" % n
            if not isinstance(item, dict) or "rows" not in item or not isinstance(item["rows"], list) or not item["rows"]:
                raise ProjectError("%s: expected {name, rows}, rows a non-empty list of strings" % where)
            grid = [split_graphemes(r) for r in item["rows"]]
            width = max(len(r) for r in grid)
            lv = Level(str(item.get("name", "level")), width, len(grid))
            lv.check_size(width, len(grid))
            for y, row in enumerate(grid):
                for x, g in enumerate(row):
                    if g == p.empty:
                        continue
                    if g not in p.tiles:
                        raise ProjectError("%s: row %d, column %d: %s is not a tile of this project" % (where, y + 1, x + 1, g))
                    lv.cells[y * width + x] = g
            lv.lights, lv.lighting = _lights_from_json(item, where)
            lv.effects = _effects_from_json(item.get("effects", []), where)
            p.levels.append(lv)
        return p

    def save(self, path):
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            json.dump(self.to_json(), f, ensure_ascii=False, indent=1)
            f.write("\n")
        self.path = path
        self.dirty = False

    @staticmethod
    def load(path):
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
        except json.JSONDecodeError as e:
            raise ProjectError("%s is not valid JSON: %s" % (path, e))
        p = Project.from_json(data)
        p.path = path
        p.dirty = False
        return p
