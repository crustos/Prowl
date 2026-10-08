"""unity_import.py: basic, 2D-only import of a Unity project's sprites into a Prowl2D project (no Qt, so the command line can use it too).

What it reads: the PNG textures under a Unity project's Assets/ folder and their .meta files. A texture in Single sprite mode becomes one sprite; a texture
in Multiple mode (a sprite sheet) becomes one sprite per slice, except that slices named like `walk_0`, `walk_1`, ... of the same size become the frames
of one sprite `walk`. Colours are folded into the project's palette (exact where there is room, else quantized); a pixel that is mostly transparent is
transparent. A texture bigger than a sprite can be (MAX_SPRITE_SIDE) is scaled down with nearest-neighbour and the report says so.

The sprite-sheet and .meta parsing is a subset of crust's tools/unity_pack_sprites.py (MIT), rewritten to give top-down rows (what Prowl sprites use) and to
report problems as messages instead of aborting.
"""
import os
import re
import struct
import zlib

from .model import MAX_PALETTE, MAX_SPRITE_SIDE, ProjectError, Sprite

SKIP_DIRS = {"Library", "Temp", "obj", "Logs", "Packages", "UserSettings", ".git", "__pycache__", "node_modules"}
ALPHA_CUTOFF = 128           # a pixel with less alpha than this is transparent (Prowl pixels are opaque or transparent)
DEFAULT_FPS = 8.0
_PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


class UnityImportError(ProjectError):
    """A Unity file could not be read; the message says which and why."""


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# PNG: (width, height, RGBA bytes, rows from the top)

def _paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def _unfilter(raw, w, h, bpp, bits_per_row_bytes, path):
    """Undoes the PNG row filters of a non-interlaced image; returns the rows (bytes each), top first."""
    stride = bits_per_row_bytes
    if len(raw) < (stride + 1) * h:
        raise UnityImportError("%s: the PNG data is cut short" % path)
    rows = []
    prev = bytearray(stride)
    off = 0
    for _ in range(h):
        ftype = raw[off]
        row = bytearray(raw[off + 1:off + 1 + stride])
        off += stride + 1
        if ftype == 1:
            for i in range(bpp, stride):
                row[i] = (row[i] + row[i - bpp]) & 255
        elif ftype == 2:
            for i in range(stride):
                row[i] = (row[i] + prev[i]) & 255
        elif ftype == 3:
            for i in range(stride):
                row[i] = (row[i] + (((row[i - bpp] if i >= bpp else 0) + prev[i]) >> 1)) & 255
        elif ftype == 4:
            for i in range(stride):
                row[i] = (row[i] + _paeth(row[i - bpp] if i >= bpp else 0, prev[i], prev[i - bpp] if i >= bpp else 0)) & 255
        elif ftype != 0:
            raise UnityImportError("%s: bad PNG filter type %d" % (path, ftype))
        rows.append(bytes(row))
        prev = row
    return rows


def decode_png(data, path="png"):
    """8-bit (and 1/2/4-bit indexed) non-interlaced PNG of any colour type -> (w, h, rgba), top row first. Anything else is tried through Pillow if it is
    installed (16-bit, interlaced), else refused with a message."""
    if data[:8] != _PNG_MAGIC:
        raise UnityImportError("%s: not a PNG" % path)
    pos, ihdr, idat, plte, trns = 8, None, [], None, None
    while pos + 8 <= len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        tag, chunk = data[pos + 4:pos + 8], data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if tag == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", chunk)
        elif tag == b"PLTE":
            plte = chunk
        elif tag == b"tRNS":
            trns = chunk
        elif tag == b"IDAT":
            idat.append(chunk)
        elif tag == b"IEND":
            break
    if ihdr is None or not idat:
        raise UnityImportError("%s: incomplete PNG" % path)
    w, h, depth, ctype, _comp, _filt, interlace = ihdr
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}.get(ctype)
    if channels is None or interlace != 0 or depth not in (1, 2, 4, 8) or (depth != 8 and ctype != 3):
        return _decode_with_pillow(data, path, "%d-bit colour type %d%s" % (depth, ctype, ", interlaced" if interlace else ""))
    if w < 1 or h < 1:
        raise UnityImportError("%s: the PNG is empty" % path)
    bpp = max(1, channels * depth // 8)
    row_bytes = (w * channels * depth + 7) // 8
    rows = _unfilter(zlib.decompress(b"".join(idat)), w, h, bpp, row_bytes, path)
    out = bytearray(w * h * 4)
    palette = []
    if ctype == 3:
        if plte is None:
            raise UnityImportError("%s: an indexed PNG with no palette" % path)
        alphas = trns or b""
        palette = [(plte[i], plte[i + 1], plte[i + 2], alphas[i // 3] if i // 3 < len(alphas) else 255) for i in range(0, len(plte) - 2, 3)]
    o = 0
    for row in rows:
        if ctype == 6:
            out[o:o + w * 4] = row
            o += w * 4
        elif ctype == 2:
            for x in range(w):
                out[o:o + 4] = bytes((row[3 * x], row[3 * x + 1], row[3 * x + 2], 255))
                o += 4
        elif ctype == 0:
            for x in range(w):
                g = row[x]
                out[o:o + 4] = bytes((g, g, g, 255))
                o += 4
        elif ctype == 4:
            for x in range(w):
                g = row[2 * x]
                out[o:o + 4] = bytes((g, g, g, row[2 * x + 1]))
                o += 4
        else:                                                   # indexed, 1 to 8 bits
            mask = (1 << depth) - 1
            for x in range(w):
                bit = x * depth
                i = (row[bit >> 3] >> (8 - depth - (bit & 7))) & mask
                out[o:o + 4] = bytes(palette[i]) if i < len(palette) else b"\0\0\0\0"
                o += 4
    return w, h, bytes(out)


def _decode_with_pillow(data, path, what):
    try:
        import io
        from PIL import Image
    except ImportError:
        raise UnityImportError("%s: a %s PNG needs Pillow to read (pip install pillow), or re-save it as a plain 8-bit PNG" % (path, what))
    try:
        im = Image.open(io.BytesIO(data)).convert("RGBA")
    except Exception as e:                                       # Pillow raises many kinds
        raise UnityImportError("%s: cannot read the PNG (%s)" % (path, e))
    return im.width, im.height, im.tobytes()


def load_png(path):
    try:
        with open(path, "rb") as f:
            data = f.read()
    except OSError as e:
        raise UnityImportError("%s: %s" % (path, e))
    try:
        return decode_png(data, path)
    except (zlib.error, struct.error) as e:
        raise UnityImportError("%s: damaged PNG (%s)" % (path, e))


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# .meta

def read_meta(png_path):
    """The text of <png>.meta, or "" when there is none (Unity then uses its defaults: one sprite, 100 pixels per unit)."""
    try:
        with open(png_path + ".meta", encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


def sprite_mode(meta):
    """Unity's TextureImporter.spriteMode: 0 none (not a sprite), 1 single, 2 multiple. A texture with no .meta counts as single."""
    if not meta:
        return 1
    m = re.search(r"(?m)^\s*spriteMode:\s*(\d+)\s*$", meta)
    return int(m.group(1)) if m else 1


def texture_type(meta):
    """TextureImporter.textureType: 8 is Sprite (2D and UI); others (default 0, normal map 1, ...) are not sprites. No .meta: assume a sprite."""
    if not meta:
        return 8
    m = re.search(r"(?m)^\s*textureType:\s*(\d+)\s*$", meta)
    return int(m.group(1)) if m else 8


_SLICE = re.compile(
    r"(?ms)^\s{4}-\s+serializedVersion:\s*\d+\s*\n"
    r"\s+name:\s*(.*?)\n"
    r"\s+rect:\s*\n"
    r"\s+serializedVersion:\s*\d+\s*\n"
    r"\s+x:\s*([^\n]+)\s*\n"
    r"\s+y:\s*([^\n]+)\s*\n"
    r"\s+width:\s*([^\n]+)\s*\n"
    r"\s+height:\s*([^\n]+)\s*\n")


def parse_slices(meta):
    """The spriteSheet of a Multiple-mode texture -> [{name, x, y, w, h}] in the order the .meta lists them. y counts from the texture's BOTTOM (Unity)."""
    sheet = re.search(r"(?m)^\s*spriteSheet:\s*$", meta)
    if not sheet:
        return []
    body = meta[sheet.end():]
    stop = re.search(r"(?m)^(mipmapLimitGroupName|userData|assetBundleName):", body)
    if stop:
        body = body[:stop.start()]
    out = []
    found = list(_SLICE.finditer(body))
    for n, m in enumerate(found):
        try:
            x, y, w, h = (float(m.group(i)) for i in (2, 3, 4, 5))
        except ValueError:
            continue
        name = m.group(1).strip().strip("\"'")
        rest = body[m.end():found[n + 1].start() if n + 1 < len(found) else len(body)]
        iid = re.search(r"(?m)^\s+internalID:\s*(-?\d+)", rest)                  # what a scene's m_Sprite {fileID} names
        out.append({"name": name, "x": x, "y": y, "w": w, "h": h, "id": int(iid.group(1)) if iid else None})
    return out


SINGLE_SPRITE_ID = 21300000          # the fileID a scene uses for a texture's one sprite


def meta_guid(meta):
    m = re.search(r"(?m)^guid:\s*([0-9a-fA-F]+)\s*$", meta)
    return m.group(1).lower() if m else None


def pixels_per_unit(meta):
    """TextureImporter.spritePixelsToUnits; Unity's default is 100."""
    m = re.search(r"(?m)^\s*spritePixelsToUnits:\s*([0-9.]+)\s*$", meta)
    v = float(m.group(1)) if m else 100.0
    return v if v > 0 else 100.0


def crop_top_down(rgba, tw, th, sl):
    """The slice `sl` of a top-down RGBA image. Unity's rect y is from the bottom, so the slice's top row is th - (y + h). Returns (w, h, rgba) or None
    when the slice lies outside the texture."""
    x0 = max(0, int(round(sl["x"])))
    w = int(round(sl["w"]))
    h = int(round(sl["h"]))
    top = th - (int(round(sl["y"])) + h)
    if top < 0:                                    # partly below the image: keep what is there
        h += top
        top = 0
    w = min(w, tw - x0)
    h = min(h, th - top)
    if w < 1 or h < 1:
        return None
    rows = [rgba[((top + r) * tw + x0) * 4:((top + r) * tw + x0 + w) * 4] for r in range(h)]
    return w, h, b"".join(rows)


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# images -> sprites

_FRAME_NAME = re.compile(r"^(.*?)[_\-\s]?(\d+)$")


class Pending:
    """A sprite waiting for its palette indices: frames of (w, h, rgba), all the same size."""
    def __init__(self, name, source):
        self.name = name
        self.source = source           # the png it came from, for the report
        self.frames = []
        self.scaled_from = None
        self.guid = None               # the texture's Unity guid, and the fileIDs a scene uses to name this sprite (the slices' internalIDs)
        self.file_ids = []
        self.orig = (0, 0)             # the first frame's size in pixels before any scaling
        self.ppu = 100.0               # Unity pixels per world unit


def _shrink(w, h, rgba, limit=MAX_SPRITE_SIDE):
    """Nearest-neighbour down to at most limit x limit, keeping the proportions. Returns (w, h, rgba, scaled)."""
    if w <= limit and h <= limit:
        return w, h, rgba, False
    k = limit / float(max(w, h))
    nw, nh = max(1, int(w * k)), max(1, int(h * k))
    out = bytearray(nw * nh * 4)
    for y in range(nh):
        sy = min(h - 1, int((y + 0.5) / k))
        for x in range(nw):
            sx = min(w - 1, int((x + 0.5) / k))
            out[(y * nw + x) * 4:(y * nw + x) * 4 + 4] = rgba[(sy * w + sx) * 4:(sy * w + sx) * 4 + 4]
    return nw, nh, bytes(out), True


def group_slices(slices, texture_name=None):
    """Slices named `walk_0`, `walk_1`, ... (a base name and a number) of the same size become the frames of one sprite, in number order; the rest stay
    alone. Returns [(name, [slice, ...])] in the order the first slice of each appeared. A base with only one slice is not an animation: it keeps its
    full name. Slices named after the texture itself (`tiles_0`, `tiles_1` in tiles.png) are what Unity's automatic slicing writes for ANY sheet, a tileset
    as much as an animation, and a scene points at each slice by itself, so those are never merged."""
    groups = {}
    order = []
    for sl in slices:
        m = _FRAME_NAME.match(sl["name"])
        auto = m is not None and texture_name is not None and (m.group(1) or "").lower() == texture_name.lower()
        key = (m.group(1) or sl["name"], int(round(sl["w"])), int(round(sl["h"]))) if m and not auto else (sl["name"], None, None)
        num = int(m.group(2)) if m else 0
        if key not in groups:
            groups[key] = []
            order.append(key)
        groups[key].append((num, sl))
    out = []
    for key in order:
        items = groups[key]
        if len(items) == 1 or key[1] is None:
            out.extend((sl["name"], [sl]) for _, sl in items)
        else:
            out.append((key[0].rstrip("_- ") or key[0], [sl for _, sl in sorted(items, key=lambda t: t[0])]))
    return out


def png_to_pending(path, rel):
    """What one texture contributes: (list of Pending, list of notes). Textures that are not sprites in Unity (normal maps, cursors...) give nothing."""
    meta = read_meta(path)
    notes = []
    if texture_type(meta) != 8 or sprite_mode(meta) == 0:
        return [], ["%s: not a sprite texture in Unity (skipped)" % rel]
    tw, th, rgba = load_png(path)
    base = os.path.splitext(os.path.basename(path))[0]
    pending = []

    guid, ppu = meta_guid(meta), pixels_per_unit(meta)

    def add(name, frames, file_ids):
        p = Pending(name, rel)
        p.guid, p.ppu, p.file_ids, p.orig = guid, ppu, file_ids, (frames[0][0], frames[0][1])
        for fw, fh, frgba in frames:
            nw, nh, nrgba, scaled = _shrink(fw, fh, frgba)
            if scaled and p.scaled_from is None:
                p.scaled_from = (fw, fh)
            p.frames.append((nw, nh, nrgba))
        pending.append(p)

    slices = parse_slices(meta) if sprite_mode(meta) == 2 else []
    if not slices:
        if sprite_mode(meta) == 2:
            notes.append("%s: marked as a sprite sheet but has no slices; imported whole" % rel)
        add(base, [(tw, th, rgba)], [SINGLE_SPRITE_ID])
    else:
        for name, group in group_slices(slices, base):
            frames, ids = [], []
            for sl in group:
                c = crop_top_down(rgba, tw, th, sl)
                if c is None:
                    notes.append("%s: slice '%s' lies outside the texture (skipped)" % (rel, sl["name"]))
                else:
                    frames.append(c)
                    ids.append(sl.get("id"))
            if frames:
                w0, h0 = frames[0][0], frames[0][1]
                keep = [i for i, f in enumerate(frames) if (f[0], f[1]) == (w0, h0)]     # a clipped slice that no longer matches is dropped from the animation
                add(name or base, [frames[i] for i in keep], [ids[i] for i in keep])
    return pending, notes


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# palette

def _key_pool():
    """Keys for the palette beyond the letters and digits: printable non-space characters from Latin-1 and Latin Extended."""
    for cp in range(0xA1, 0x250):
        c = chr(cp)
        if c.isprintable() and not c.isspace() and cp != 0xAD:            # 0xAD is a soft hyphen: invisible
            yield c


def _free_key(palette):
    try:
        return palette.first_free_key()
    except ProjectError:
        for c in _key_pool():
            if palette.index_of(c) is None:
                return c
    raise ProjectError("the palette has no free key left")


def _median_cut(colors, n):
    """colors: {(r, g, b): count}; returns up to n representative colours (the weighted mean of each box). The classic algorithm: split the box with the
    widest channel range at the weighted median until there are n boxes."""
    boxes = [list(colors.items())]
    while len(boxes) < n:
        best, best_range, best_ch = None, 0, 0
        for bi, box in enumerate(boxes):
            if len(box) < 2:
                continue
            for ch in range(3):
                lo = min(c[0][ch] for c in box)
                hi = max(c[0][ch] for c in box)
                if hi - lo > best_range:
                    best, best_range, best_ch = bi, hi - lo, ch
        if best is None:
            break
        box = sorted(boxes.pop(best), key=lambda c: c[0][best_ch])
        half, acc = sum(c[1] for c in box) / 2.0, 0
        cut = len(box) - 1
        for i, c in enumerate(box):
            acc += c[1]
            if acc >= half:
                cut = min(max(i + 1, 1), len(box) - 1)
                break
        boxes += [box[:cut], box[cut:]]
    out = []
    for box in boxes:
        total = float(sum(c[1] for c in box))
        out.append(tuple(int(round(sum(c[0][ch] * c[1] for c in box) / total)) for ch in range(3)))
    return out


def _nearest(rgb, table):
    best, best_d = 0, None
    for idx, (r, g, b) in table:
        d = (r - rgb[0]) ** 2 * 3 + (g - rgb[1]) ** 2 * 4 + (b - rgb[2]) ** 2 * 2     # a cheap perceptual weighting
        if best_d is None or d < best_d:
            best, best_d = idx, d
    return best


def build_palette_mapping(project, pendings):
    """Makes room in the project's palette for the colours the pending sprites use; returns (rgb -> palette index, number of colours that had to be merged).
    A colour the palette already holds keeps its entry. New colours get entries (most used first) while there is room; past that they are quantized."""
    pal = project.palette
    counts = {}
    for p in pendings:
        for _w, _h, rgba in p.frames:
            for i in range(0, len(rgba), 4):
                if rgba[i + 3] >= ALPHA_CUTOFF:
                    key = (rgba[i], rgba[i + 1], rgba[i + 2])
                    counts[key] = counts.get(key, 0) + 1
    have = {e.rgb: i for i, e in enumerate(pal.entries) if e.rgb is not None}
    mapping = {c: have[c] for c in counts if c in have}
    new = {c: n for c, n in counts.items() if c not in have}
    room = MAX_PALETTE - len(pal.entries)
    merged = 0
    if new:
        if len(new) <= room:
            chosen = sorted(new, key=lambda c: -new[c])
        else:
            if room < 1:
                chosen = []
            else:
                chosen = _median_cut(new, room)
        added = {}
        for rgb in chosen:
            if rgb in have or rgb in added:
                continue
            added[rgb] = pal.add(_free_key(pal), "unity %s" % ("#%02x%02x%02x" % rgb), rgb)
        table = [(i, e.rgb) for i, e in enumerate(pal.entries) if e.rgb is not None]
        for c in new:
            if c in added:
                mapping[c] = added[c]
            else:
                mapping[c] = _nearest(c, table)
                merged += 1
    return mapping, merged


def pending_to_sprite(p, mapping):
    w, h = p.frames[0][0], p.frames[0][1]
    s = Sprite(p.name, w, h, DEFAULT_FPS)
    s.check_size(w, h)
    s.frames = []
    for _fw, _fh, rgba in p.frames:
        f = bytearray(w * h)
        for i in range(w * h):
            o = i * 4
            if rgba[o + 3] >= ALPHA_CUTOFF:
                f[i] = mapping[(rgba[o], rgba[o + 1], rgba[o + 2])]
        s.frames.append(f)
    return s


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the project walk and the entry point

def find_pngs(root):
    """The PNG textures of a Unity project (under Assets/ when there is one, else under `root`) or the one PNG that `root` is. Sorted, so an import is
    repeatable."""
    if os.path.isfile(root):
        return [root] if root.lower().endswith(".png") else []
    base = os.path.join(root, "Assets")
    if not os.path.isdir(base):
        base = root
    out = []
    for dirpath, dirnames, names in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS and not d.startswith("."))
        out += [os.path.join(dirpath, n) for n in sorted(names) if n.lower().endswith(".png")]
    return out


class ImportReport:
    def __init__(self):
        self.sprites = []          # the Sprites added to the project
        self.levels = []           # the Levels added from scenes (see unity_scene)
        self.notes = []            # one line per thing the user should know (skipped, scaled, merged...)
        self.pngs = 0
        self.refs = {}             # (texture guid, fileID) -> (sprite name, width, height in world units, frame): how a scene's m_Sprite finds its sprite

    def summary(self):
        frames = sum(len(s.frames) for s in self.sprites)
        text = "%d sprite%s (%d frame%s) from %d texture%s" % (len(self.sprites), "" if len(self.sprites) == 1 else "s", frames, "" if frames == 1 else "s",
                                                               self.pngs, "" if self.pngs == 1 else "s")
        if self.levels:
            text += "; %d level%s" % (len(self.levels), "" if len(self.levels) == 1 else "s")
        return text


def import_unity_sprites(project, root):
    """Adds the sprites of the Unity project (or folder, or single PNG) at `root` to `project` and returns an ImportReport. Nothing is added if nothing could
    be read; a texture that fails is reported in the notes and the rest still import."""
    if not os.path.exists(root):
        raise UnityImportError("%s does not exist" % root)
    report = ImportReport()
    pendings = []
    base = root if os.path.isdir(root) else os.path.dirname(root)
    for path in find_pngs(root):
        rel = os.path.relpath(path, base).replace(os.sep, "/")
        try:
            got, notes = png_to_pending(path, rel)
        except UnityImportError as e:
            report.notes.append(str(e))
            continue
        report.pngs += 1
        report.notes += notes
        pendings += got
    if not pendings:
        if not report.notes:
            report.notes.append("no PNG textures found under %s" % root)
        return report
    mapping, merged = build_palette_mapping(project, pendings)
    if merged:
        report.notes.append("%d colours did not fit in the palette (at most %d) and were merged into their nearest" % (merged, MAX_PALETTE))
    for p in pendings:
        if p.scaled_from:
            report.notes.append("%s: %dx%d is bigger than a sprite can be (%d), scaled to %dx%d" % (
                p.source, p.scaled_from[0], p.scaled_from[1], MAX_SPRITE_SIDE, p.frames[0][0], p.frames[0][1]))
        sprite = project.add_sprite(pending_to_sprite(p, mapping))
        report.sprites.append(sprite)
        if p.guid:
            for frame, fid in enumerate(p.file_ids):                  # (name, width, height in world units, frame): a scene names one slice, i.e. one frame
                if fid is not None:
                    report.refs[(p.guid, fid)] = (sprite.name, p.orig[0] / p.ppu, p.orig[1] / p.ppu, frame)
    project.touch()
    return report
