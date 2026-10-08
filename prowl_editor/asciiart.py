"""asciiart.py: sprites and levels as text, both ways.

SPRITES. Every pixel is one character, and a character names a palette entry (palette.py's keys). A frame is a block of rows; a blank line starts the next frame;
`.` and a space are transparent. Lines that start `# sprite:`, `# fps:` or `# palette:` are the header (a plain ASCII-art file has none, and importing it asks
which letter is which colour):

    # sprite: coin
    # fps: 6
    # palette: Y=#f2d83b O=#f08c2e
    ..YY..
    .YOOY.
    ..YY..

    ..Y...                      <- the next frame
    .YOY..

Several sprites may follow one another in one file; each starts with its `# sprite:` line.

LEVELS. Every cell is one emoji, which has a natural name (Unicode's: U+1F9F1 is BRICK) and an appearance, so the text is readable as it is, and a tile is
drawn by the sprite that has its name. A header names the level and says which emoji means "empty" and what the rest are:

    # level: first
    # empty: ⬛
    # 🧱 = brick (solid)
    # 🪙 = coin
    ⬛⬛⬛⬛⬛
    ⬛⬛🪙⬛⬛
    🧱🧱🧱🧱🧱

Any character can be a tile, so a level drawn with ASCII ('#', '.', 'o') imports as well, one tile per distinct character.
"""
import re
import unicodedata

from .model import DEFAULT_COLORS, DEFAULT_EMPTY, TRANSPARENT_KEY, Level, ProjectError, Sprite, TileDef, color_hex, parse_color

ZWJ = 0x200D
VS16 = 0xFE0F
KEYCAP = 0x20E3


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# graphemes and names

def split_graphemes(text):
    """The user-perceived characters of a string: an emoji with its variation selector, skin tone, keycap or ZWJ joins is ONE, a flag (two regional
    indicators) is ONE, a letter with a combining mark is ONE. (Not the full Unicode algorithm; what emoji levels use.)"""
    out = []
    i, n = 0, len(text)
    while i < n:
        j = i + 1
        cp = ord(text[i])
        if 0x1F1E6 <= cp <= 0x1F1FF and j < n and 0x1F1E6 <= ord(text[j]) <= 0x1F1FF:
            j += 1
        while j < n:
            o = ord(text[j])
            if o in (VS16, KEYCAP) or 0x1F3FB <= o <= 0x1F3FF or 0xE0020 <= o <= 0xE007F or 0xFE00 <= o <= 0xFE0F or unicodedata.category(text[j]) in ("Mn", "Me"):
                j += 1
            elif o == ZWJ and j + 1 < n:
                j += 2
            else:
                break
        out.append(text[i:j])
        i = j
    return out


def natural_name(g):
    """The name a person would give an emoji: 'brick', 'deciduous tree', 'flag us'. For anything Unicode names, that name in lower case."""
    cps = [c for c in g if ord(c) not in (VS16, 0xFE0E, ZWJ) and not (0x1F3FB <= ord(c) <= 0x1F3FF)]
    if len(cps) == 2 and all(0x1F1E6 <= ord(c) <= 0x1F1FF for c in cps):
        return "flag " + "".join(chr(ord(c) - 0x1F1E6 + ord("a")) for c in cps)
    keycap = KEYCAP in [ord(c) for c in cps]
    names = []
    for c in cps:
        if ord(c) == KEYCAP:
            continue
        try:
            names.append(unicodedata.name(c).lower())
        except ValueError:
            names.append("u+%04x" % ord(c))
    text = " + ".join(names) if names else "empty"
    return ("keycap " + text) if keycap else text


def emoji_for_name(name):
    """'brick' -> the emoji of that name, or None. (Unicode's names: 'deciduous tree', 'coin'.)"""
    try:
        return unicodedata.lookup(name.strip().upper())
    except KeyError:
        return None


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# sprites

# light to dark: the classic ASCII-art ramp, used to guess a colour for a symbol the palette does not know
_RAMP = " .'`^\",:;Il!i><~+_-?][}{1)(|\\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$"

_HEADER = re.compile(r"^#\s*(sprite|name|fps|palette)\s*:\s*(.*)$", re.IGNORECASE)


class SpriteBlock:
    """One sprite as it was written in text, before it meets a palette."""
    def __init__(self, name):
        self.name = name
        self.fps = None
        self.header_colors = {}      # key -> (r, g, b), from `# palette:`
        self.frames = []             # list of list of row strings

    def chars(self):
        seen = []
        for frame in self.frames:
            for row in frame:
                for c in row:
                    if c not in seen:
                        seen.append(c)
        return seen


def parse_sprite_text(text, default_name="sprite"):
    """Reads ASCII art into SpriteBlocks. Raises ProjectError (with the line) if a header is malformed or there is no art."""
    blocks = []
    cur = None
    frame = None
    for ln, raw in enumerate(text.splitlines(), 1):
        line = raw.rstrip("\r\n")
        m = _HEADER.match(line)
        if m:
            kind, value = m.group(1).lower(), m.group(2).strip()
            if kind in ("sprite", "name"):
                cur = SpriteBlock(value or default_name)
                blocks.append(cur)
                frame = None
            else:
                if cur is None:
                    cur = SpriteBlock(default_name)
                    blocks.append(cur)
                    frame = None
                if kind == "fps":
                    try:
                        cur.fps = float(value)
                    except ValueError:
                        raise ProjectError("line %d: fps must be a number (got '%s')" % (ln, value))
                else:
                    for tok in value.split():
                        if len(tok) < 4 or tok[1] != "=":
                            raise ProjectError("line %d: palette entries look like K=#14141c (got '%s')" % (ln, tok))
                        try:
                            cur.header_colors[tok[0]] = parse_color(tok[2:])
                        except ProjectError as e:
                            raise ProjectError("line %d: %s" % (ln, e))
            continue
        if line.strip() == "":
            frame = None                       # a blank line ends the frame
            continue
        if cur is None:
            cur = SpriteBlock(default_name)
            blocks.append(cur)
        if frame is None:
            frame = []
            cur.frames.append(frame)
        frame.append(line.replace("\t", "    "))
    blocks = [b for b in blocks if b.frames]
    if not blocks:
        raise ProjectError("no ASCII art found: expected rows of characters (blank line between frames)")
    return blocks


def guess_color(ch):
    """A colour for a character the palette does not know: a letter gets its default colour (lower case: darker), a symbol a grey by how dense it looks."""
    up = ch.upper()
    for key, _name, rgb in DEFAULT_COLORS:
        if key == up:
            return rgb if ch == up else tuple(int(c * 0.6) for c in rgb)
    if ch in _RAMP:
        level = 235 - int(210 * _RAMP.index(ch) / (len(_RAMP) - 1))     # dense = dark
        return (level, level, level)
    h = (ord(ch) * 2654435761) & 0xFFFFFF
    return (64 + (h & 0x7F), 64 + ((h >> 8) & 0x7F), 64 + ((h >> 16) & 0x7F))


def scan_sprite_text(text, palette):
    """What importing this text needs to know: (blocks, unknown) where `unknown` maps each character that is not yet a palette key (and is not
    transparent) to the colour the importer would give it: the header's, else a guess. The GUI shows it for the user to change."""
    blocks = parse_sprite_text(text)
    unknown = {}
    for b in blocks:
        for c in b.chars():
            if c in (TRANSPARENT_KEY, " ") or palette.index_of(c) is not None or c in unknown:
                continue
            unknown[c] = b.header_colors.get(c) or guess_color(c)
    return blocks, unknown


def import_sprites(text, project, mapping=None):
    """Adds the sprites in `text` to the project and returns (sprites, added_keys). `mapping` is {char: (r, g, b) or None for transparent} and wins over
    everything: it is what the user chose for the characters scan_sprite_text reported. A character not in the palette and not in `mapping` gets the header's
    colour or a guess. New colours are added to the palette under the character itself (a key the palette cannot take, such as '#', gets a free letter)."""
    mapping = mapping or {}
    blocks = parse_sprite_text(text)
    pal = project.palette
    added = []

    def index_for(ch, block, key_for):
        """The palette index a character of this block means. The palette is the project's: a colour already there is never changed by an import; a
        colour that disagrees with it (another yellow under the same letter) is added as a new entry under a free key."""
        if ch in key_for:
            return key_for[ch]
        if ch in (TRANSPARENT_KEY, " ") or (ch in mapping and mapping[ch] is None):
            key_for[ch] = 0
            return 0
        rgb = mapping[ch] if ch in mapping else block.header_colors.get(ch)
        existing = pal.index_of(ch)
        if rgb is None:
            if existing is not None:
                key_for[ch] = existing
                return existing
            rgb = guess_color(ch)
        elif existing is not None and pal.entries[existing].rgb == tuple(rgb):
            key_for[ch] = existing
            return existing
        try:
            pal.check_key(ch)
            key = ch
        except ProjectError:
            key = pal.first_free_key()
        idx = pal.add(key, "color %s" % ch, tuple(rgb))
        added.append(key)
        key_for[ch] = idx
        return idx

    made = []
    for b in blocks:
        height = max(len(f) for f in b.frames)
        width = max(len(r) for f in b.frames for r in f)
        s = Sprite(b.name, width, height, b.fps if b.fps else 8.0)
        s.check_size(width, height)
        s.frames = []
        key_for = {}
        for rows in b.frames:
            f = bytearray(width * height)
            for y, row in enumerate(rows):
                for x, ch in enumerate(row):
                    f[y * width + x] = index_for(ch, b, key_for)
            s.frames.append(f)
        made.append(project.add_sprite(s))
    return made, added


def export_sprite(sprite, palette):
    """One sprite as text, with its header (so the colours come back exactly)."""
    used = sorted({i for f in sprite.frames for i in f if i != 0})
    lines = ["# sprite: %s" % sprite.name, "# fps: %g" % sprite.fps]
    if used:
        lines.append("# palette: " + " ".join("%s=%s" % (palette.entries[i].key, color_hex(palette.entries[i].rgb)) for i in used if i < len(palette.entries)))
    for n in range(len(sprite.frames)):
        if n:
            lines.append("")
        lines += sprite.rows(palette, n)
    return "\n".join(lines) + "\n"


def export_sprites(project):
    return "\n".join(export_sprite(s, project.palette) for s in project.sprites)


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# levels

_LEVEL_HEADER = re.compile(r"^#\s+level\s*:\s*(.*)$", re.IGNORECASE)
_EMPTY_HEADER = re.compile(r"^#\s+empty\s*:\s*(.*)$", re.IGNORECASE)
_LEGEND = re.compile(r"^#\s+(\S+)\s*=\s*(.*?)\s*$")
_EMPTY_CANDIDATES = [DEFAULT_EMPTY, "⬜", ".", " ", "・", "　", "·", "_", "-"]
_BLANKS = (" ", "\u3000")        # a space is never a tile: wherever it appears in a level it means "nothing here"


class LevelBlock:
    def __init__(self, name):
        self.name = name
        self.empty = None
        self.legend = {}         # grapheme -> (name, sprite, solid)
        self.rows = []           # list of list of graphemes


def parse_level_text(text, default_name="level"):
    blocks, cur = [], None
    for ln, raw in enumerate(text.splitlines(), 1):
        line = raw.rstrip("\r\n")
        m = _LEVEL_HEADER.match(line)
        if m:
            cur = LevelBlock(m.group(1).strip() or default_name)
            blocks.append(cur)
            continue
        m_empty = _EMPTY_HEADER.match(line)
        m_legend = None if m_empty else _LEGEND.match(line)
        if m_empty or m_legend:
            if cur is None:
                cur = LevelBlock(default_name)
                blocks.append(cur)
            if m_empty:
                g = split_graphemes(m_empty.group(1).strip())
                cur.empty = g[0] if g else None
                continue
            g = split_graphemes(m_legend.group(1))
            if len(g) != 1:
                raise ProjectError("line %d: a legend line looks like '# 🧱 = brick (solid)' (got '%s')" % (ln, line))
            name, solid, sprite = m_legend.group(2), False, ""
            sm = re.search(r"\s*->\s*(.+)$", name)
            if sm:
                sprite, name = sm.group(1).strip(), name[:sm.start()]
            traits = ()
            tm = re.search(r"\s*\(((?:solid|dynamic|diggable)(?:\s*,\s*(?:solid|dynamic|diggable))*)\)\s*$", name)
            if tm:
                traits = tuple(t.strip() for t in tm.group(1).split(","))
                name = name[:tm.start()]
            cur.legend[g[0]] = (name.strip() or natural_name(g[0]), sprite, traits)
            continue
        # (any other line is a row of the level, even one that starts with '#': an ASCII level may use '#' for walls)
        if line == "":
            continue
        if cur is None:
            cur = LevelBlock(default_name)
            blocks.append(cur)
        cur.rows.append(split_graphemes(line))
    blocks = [b for b in blocks if b.rows]
    if not blocks:
        raise ProjectError("no level found: expected rows of emoji (or characters), one cell each")
    return blocks


def import_levels(text, project, default_name="level"):
    """Adds the levels in `text` to the project; every distinct character that is not already a tile becomes one, named as the legend says or by its Unicode
    name. Returns (levels, new_tiles)."""
    made, new_tiles = [], []
    for b in parse_level_text(text, default_name):
        used = {g for row in b.rows for g in row}
        empty = b.empty
        if empty is None:
            for cand in ([project.empty] if project.empty else []) + _EMPTY_CANDIDATES:
                if cand in used:
                    empty = cand
                    break
        width = max(len(r) for r in b.rows)
        lv = Level(b.name, width, len(b.rows))
        lv.check_size(width, len(b.rows))
        for y, row in enumerate(b.rows):
            for x, g in enumerate(row):
                if g == empty or g in _BLANKS:
                    continue
                if g not in project.tiles:
                    name, sprite, traits = b.legend.get(g, (natural_name(g), "", ()))
                    project.tiles[g] = TileDef(g, name, sprite, "solid" in traits, "dynamic" in traits, "diggable" in traits)
                    new_tiles.append(project.tiles[g])
                lv.cells[y * width + x] = g
        if empty and empty not in (project.empty,) and not project.levels:
            project.empty = empty                      # the first level decides what empty looks like, unless the project already says
        made.append(project.add_level(lv))
    return made, new_tiles


def export_level(level, project):
    """One level as emoji text with a header and a legend of the tiles it uses."""
    empty = project.empty
    lines = ["# level: %s" % level.name, "# empty: %s" % empty]
    for g in level.used_emoji():
        t = project.tiles.get(g)
        if t is None:
            lines.append("# %s = %s" % (g, natural_name(g)))
            continue
        traits = [w for w, on in (("solid", t.solid), ("dynamic", t.dynamic), ("diggable", t.diggable)) if on]
        extra = (" (%s)" % ", ".join(traits) if traits else "") + (" -> %s" % t.sprite if t.sprite else "")
        lines.append("# %s = %s%s" % (g, t.name, extra))
    for y in range(level.height):
        lines.append("".join(level.get(x, y) or empty for x in range(level.width)))
    return "\n".join(lines) + "\n"


def export_levels(project):
    return "\n".join(export_level(l, project) for l in project.levels)


def level_to_text(level, project):
    """Just the rows (no header): the level as the editor's text pane shows it."""
    return "\n".join("".join(level.get(x, y) or project.empty for x in range(level.width)) for y in range(level.height))


def level_from_text(level, text, project):
    """Replaces a level's cells with the rows in `text` (the editor's text pane): new characters become tiles. Returns the new tiles."""
    rows = [split_graphemes(l) for l in text.splitlines() if l.strip() != ""]
    if not rows:
        raise ProjectError("the level text has no rows")
    width = max(len(r) for r in rows)
    level.check_size(width, len(rows))
    new_tiles = []
    cells = [""] * (width * len(rows))
    for y, row in enumerate(rows):
        for x, g in enumerate(row):
            if g == project.empty or g in _BLANKS:
                continue
            if g not in project.tiles:
                project.tiles[g] = TileDef(g, natural_name(g))
                new_tiles.append(project.tiles[g])
            cells[y * width + x] = g
    level.width, level.height, level.cells = width, len(rows), cells
    return new_tiles

