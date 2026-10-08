"""unity_scene.py: basic, 2D-only import of Unity scenes (.unity, the YAML text format) as Prowl2D levels (no Qt, so the command line can use it too).

A level is a grid of emoji tiles, a Unity scene is free-floating objects, so the import is a deliberate simplification:

  - every enabled SpriteRenderer on an active GameObject is placed by its WORLD position (parents' positions and scales are applied; rotation is not);
  - the grid's cell is the size of the most common sprite (override with `cell`), so a scene built on a 1-unit grid maps one object to one cell;
  - an object goes to its nearest cell; when several land in the same cell, the one with the highest sorting order (then the later in the file) is shown;
  - a sprite becomes a tile: an enabled, non-trigger 2D collider on the object makes it `solid`; a dynamic Rigidbody2D as well makes it `dynamic`; the same
    sprite with other traits is another tile;
  - the level's top row is the scene's highest y (Unity's y points up);
  - prefab instances are unpacked from their .prefab files (overrides, nesting, variants), then placed like any other object;
  - every tile of a Tilemap is placed like a sprite at its cell's centre; when a scene has tilemaps the grid's cell is the Grid's cell size (not a sprite's), and
    objects are snapped on the tiles' grid (a sprite centred in a tile's cell shares that cell).

What is not imported is counted in the report's notes (rotation, objects off the grid, hidden or oversized objects, tiles that are flipped or have no sprite,
prefabs that were not found...), so nothing is dropped silently. Sprite references are resolved through the (guid, fileID) pairs the sprite importer recorded: see unity_import.import_unity_sprites.
"""
import math
import os
import re
import unicodedata

from .model import MAX_LEVEL_SIDE, Level, TileDef, slug
from .unity_import import ImportReport, UnityImportError, import_unity_sprites, meta_guid

# Unity's class ids in a scene's `--- !u!<id> &<fileID>` headers
GAME_OBJECT, TRANSFORM, SPRITE_RENDERER, RIGIDBODY_2D, PREFAB_INSTANCE, TILEMAP = 1, 4, 212, 50, 1001, 1839735485
TILEMAP_RENDERER, TILEMAP_COLLIDER_2D, GRID = 483693784, 19719996, 156049354
COLLIDERS_2D = {58, 60, 61, 66, 68, 70, TILEMAP_COLLIDER_2D}        # circle, polygon, box, composite, edge, capsule, tilemap
SKIP_DIRS = {"Library", "Temp", "obj", "Logs", "Packages", "UserSettings", ".git", "__pycache__", "node_modules"}

_BLOCK = re.compile(r"(?m)^--- !u!(\d+) &(-?\d+)([^\n]*)$")
_NUM = r"([-+0-9.eE]+)"


def split_blocks(text):
    """[(class id, fileID, stripped, body)] of a Unity YAML file, in file order."""
    found = list(_BLOCK.finditer(text))
    out = []
    for n, m in enumerate(found):
        body = text[m.end():found[n + 1].start() if n + 1 < len(found) else len(text)]
        out.append((int(m.group(1)), m.group(2), "stripped" in m.group(3), body))
    return out


def _ref(body, field):
    """`field: {fileID: N}` -> N as a string, or None."""
    m = re.search(r"(?m)^\s*%s:\s*\{fileID:\s*(-?\d+)" % re.escape(field), body)
    return m.group(1) if m else None


def _vec(body, field, default):
    m = re.search(r"(?m)^\s*%s:\s*\{x:\s*%s,\s*y:\s*%s(?:,\s*z:\s*%s)?(?:,\s*w:\s*%s)?\s*\}" % (re.escape(field), _NUM, _NUM, _NUM, _NUM), body)
    if not m:
        return default
    try:
        return tuple(float(g) if g is not None else d for g, d in zip(m.groups(), default))
    except ValueError:
        return default


def _int(body, field, default):
    m = re.search(r"(?m)^\s*%s:\s*(-?\d+)\s*$" % re.escape(field), body)
    return int(m.group(1)) if m else default


class Placement:
    def __init__(self, go, x, y, sx, sy, sprite, wu, hu, order, index):
        self.go, self.x, self.y, self.sx, self.sy = go, x, y, sx, sy
        self.sprite, self.wu, self.hu = sprite, wu, hu           # the Prowl sprite's name and its size in world units
        self.order, self.index = order, index
        self.solid = self.dynamic = False
        self.rotated = False


class SceneObjects:
    """What the 2D importer takes from one scene: placements plus counts of what it leaves out."""
    def __init__(self):
        self.placements = []
        self.prefab_instances = 0          # every PrefabInstance met, nested ones included
        self.prefab_missing = 0            # ...whose prefab file was not found (a model, a package prefab) and so was skipped
        self.prefab_unapplied = 0          # overrides of things the importer reads (position, sprite...) that name an object it could not find
        self.prefab_too_deep = 0           # instances skipped because prefabs nest in a loop or too deeply
        self.tilemaps = 0                  # tilemaps that put at least one tile in the level
        self.later_frames = 0              # objects and tiles that name a later frame of a multi-frame sprite (a level shows frame 0)
        self.tiles_no_sprite = 0           # tiles whose sprite slot is empty (e.g. a rule tile with nothing resolved)
        self.tiles_transformed = 0         # tiles rotated or flipped by their matrix (not imported)
        self.tilemaps_nonrect = 0          # tilemaps on an isometric or hexagonal Grid, imported as if square
        self.grid_cell = None              # the Grid's cell size in world units, when the scene has tilemaps
        self.phase = (0.0, 0.0)            # where a cell's centre sits within the cell, in cells: the tiles' anchor (0.5, 0.5), else 0
        self.missing_sprites = set()       # (guid, fileID) of renderers whose sprite was not imported
        self.unused_renderers = 0          # a renderer with no sprite


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# one file (a scene or a prefab) as objects

MAX_PREFAB_DEPTH = 8
# the properties of a prefab instance's overrides that the importer acts on
HANDLED = {"m_LocalPosition.x", "m_LocalPosition.y", "m_LocalScale.x", "m_LocalScale.y", "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z",
           "m_IsActive", "m_Sprite", "m_Enabled", "m_SortingOrder", "m_IsTrigger", "m_BodyType"}

_MOD = re.compile(
    r"(?m)^[ \t]*-[ \t]+target:[ \t]*\{fileID:[ \t]*(-?\d+)[^}\n]*\}[ \t]*\n"
    r"[ \t]*propertyPath:[ \t]*([^\n]*)\n"
    r"[ \t]*value:[ \t]*([^\n]*)\n"
    r"[ \t]*objectReference:[ \t]*\{fileID:[ \t]*(-?\d+)(?:,[ \t]*guid:[ \t]*([0-9a-fA-F]+))?")


class Doc:
    """One parsed Unity YAML file: the objects it holds directly and the prefab instances it contains. Object ids are the file's own fileIDs (strings)."""
    def __init__(self):
        self.go_active = {}      # GameObject fileID -> its own m_IsActive
        self.go_xf = {}          # GameObject fileID -> its Transform's fileID
        self.xf = {}             # Transform fileID -> {go, father, pos, scale, rot}
        self.renderers = []      # {fid, go, enabled, sprite: (guid or None, fileID), order}
        self.colliders = []      # {fid, go, enabled, trigger}
        self.bodies = []         # {fid, go, type}
        self.instances = []      # {fid, parent, guid, mods: [(target fileID, propertyPath, value, (objectReference fileID, guid))]}
        self.alias = {}          # a `stripped` stand-in's fileID -> (the instance's fileID, the fileID of the prefab's own object it stands for)
        self.ids = set()         # every fileID in the file
        self.tilemaps = []       # {fid, go, enabled, tiles: [(cell x, cell y, sprite index, matrix index)], sprites: [(guid, fileID) or None], transformed: [bool], anchor}
        self.tilemap_renderers = []   # {go, enabled, order}
        self.grids = []          # {go, size: (x, y), layout}


def _parse_instance(fid, body):
    src = re.search(r"(?m)^\s*(?:m_SourcePrefab|m_ParentPrefab):\s*\{fileID:\s*-?\d+(?:,\s*guid:\s*([0-9a-fA-F]+))?", body)
    mods = [(m.group(1), m.group(2).strip(), m.group(3).strip(), (m.group(4), m.group(5))) for m in _MOD.finditer(body)]
    return {"fid": fid, "parent": _ref(body, "m_TransformParent"), "guid": src.group(1).lower() if src and src.group(1) else None, "mods": mods}


# a Tilemap's sections, in the order Unity writes them; the sections are told apart by name, not by indentation
_TM_KEYS = ("m_Tiles", "m_AnimatedTiles", "m_TileAssetArray", "m_TileSpriteArray", "m_TileMatrixArray", "m_TileColorArray", "m_TileObjectToInstantiateArray",
            "m_AnimationFrameRate", "m_Color", "m_Origin", "m_Size", "m_TileAnchor", "m_TileOrientation")
_TM_SECTION = re.compile(r"(?m)^[ \t]*(%s):" % "|".join(_TM_KEYS))
_IDENTITY_2X2 = {"e00": 1.0, "e01": 0.0, "e10": 0.0, "e11": 1.0}


def _tilemap_sections(body):
    found = list(_TM_SECTION.finditer(body))
    return {m.group(1): body[m.end():found[i + 1].start() if i + 1 < len(found) else len(body)] for i, m in enumerate(found)}


def _parse_tilemap(fid, body):
    """A Tilemap block. Tiles are `- first: {x, y, z}` (the cell) with a `second:` that indexes the sprite, matrix and colour arrays; an empty map is `m_Tiles: {}`."""
    sec = _tilemap_sections(body)
    tiles = []
    for part in re.split(r"(?m)^[ \t]*-[ \t]+first:", sec.get("m_Tiles", ""))[1:]:
        c = re.match(r"\s*\{x:\s*(-?\d+),\s*y:\s*(-?\d+)", part)
        if not c:
            continue
        si = re.search(r"m_TileSpriteIndex:\s*(-?\d+)", part)
        mi = re.search(r"m_TileMatrixIndex:\s*(-?\d+)", part)
        tiles.append((int(c.group(1)), int(c.group(2)), int(si.group(1)) if si else -1, int(mi.group(1)) if mi else 0))
    sprites = []                                     # a slot with no guid ({fileID: 0}) is an unused one
    for m in re.finditer(r"m_Data:\s*\{fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-fA-F]+))?", sec.get("m_TileSpriteArray", "")):
        sprites.append((m.group(2).lower(), int(m.group(1))) if m.group(2) else None)
    transformed = []                                 # per matrix: is its 2x2 part anything but the identity (a rotation or a flip)?
    for entry in re.split(r"(?m)^[ \t]*-[ \t]+m_RefCount:", sec.get("m_TileMatrixArray", ""))[1:]:
        off = False
        for key, want in _IDENTITY_2X2.items():
            m = re.search(r"(?m)^\s*%s:\s*([-+0-9.eE]+)" % key, entry)
            if m:
                try:
                    off = off or abs(float(m.group(1)) - want) > 1e-4
                except ValueError:
                    pass
        transformed.append(off)
    anchor = _vec(body, "m_TileAnchor", (0.5, 0.5, 0.0))
    return {"fid": fid, "go": _ref(body, "m_GameObject"), "enabled": _int(body, "m_Enabled", 1) != 0, "tiles": tiles, "sprites": sprites,
            "transformed": transformed, "anchor": (anchor[0], anchor[1])}


def parse_doc(text):
    doc = Doc()
    for cid, fid, stripped, body in split_blocks(text):
        doc.ids.add(fid)
        if stripped:                         # a stand-in for an object inside a prefab instance, so the file's own objects can point at it
            src = re.search(r"(?m)^\s*(?:m_CorrespondingSourceObject|m_PrefabParentObject):\s*\{fileID:\s*(-?\d+)", body)
            inst = _ref(body, "m_PrefabInstance") or _ref(body, "m_PrefabInternal")
            if src and inst and src.group(1) != "0":
                doc.alias[fid] = (inst, src.group(1))
            continue
        if cid == GAME_OBJECT:
            doc.go_active[fid] = _int(body, "m_IsActive", 1) != 0
        elif cid == TRANSFORM:
            go = _ref(body, "m_GameObject")
            if go is not None:
                doc.go_xf[go] = fid
            doc.xf[fid] = {"go": go, "father": _ref(body, "m_Father"), "pos": _vec(body, "m_LocalPosition", (0.0, 0.0, 0.0)),
                           "scale": _vec(body, "m_LocalScale", (1.0, 1.0, 1.0)), "rot": _vec(body, "m_LocalRotation", (0.0, 0.0, 0.0, 1.0))}
        elif cid == SPRITE_RENDERER:
            m = re.search(r"(?m)^\s*m_Sprite:\s*\{fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-fA-F]+))?", body)
            doc.renderers.append({"fid": fid, "go": _ref(body, "m_GameObject"), "enabled": _int(body, "m_Enabled", 1) != 0,
                                  "sprite": (m.group(2).lower() if m and m.group(2) else None, int(m.group(1)) if m else 0),
                                  "order": _int(body, "m_SortingOrder", 0)})
        elif cid in COLLIDERS_2D:
            doc.colliders.append({"fid": fid, "go": _ref(body, "m_GameObject"), "enabled": _int(body, "m_Enabled", 1) != 0, "trigger": _int(body, "m_IsTrigger", 0) != 0})
        elif cid == RIGIDBODY_2D:
            doc.bodies.append({"fid": fid, "go": _ref(body, "m_GameObject"), "type": _int(body, "m_BodyType", 0)})        # 0 dynamic, 1 kinematic, 2 static
        elif cid == PREFAB_INSTANCE:
            doc.instances.append(_parse_instance(fid, body))
        elif cid == TILEMAP:
            doc.tilemaps.append(_parse_tilemap(fid, body))
        elif cid == TILEMAP_RENDERER:
            doc.tilemap_renderers.append({"go": _ref(body, "m_GameObject"), "enabled": _int(body, "m_Enabled", 1) != 0, "order": _int(body, "m_SortingOrder", 0)})
        elif cid == GRID:
            size = _vec(body, "m_CellSize", (1.0, 1.0, 0.0))
            doc.grids.append({"go": _ref(body, "m_GameObject"), "size": (size[0], size[1]), "layout": _int(body, "m_CellLayout", 0)})
    return doc


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# prefab instances: copying a prefab's objects into the scene's own graph

class Graph:
    """Everything in a scene after its prefab instances are unpacked: the file's own objects under their fileIDs, an instance's under `<instance>/<fileID>`
    (and so on for nested prefabs)."""
    def __init__(self):
        self.go_active, self.go_xf, self.xf, self.alias = {}, {}, {}, {}
        self.renderers, self.colliders, self.bodies = [], [], []
        self.tilemaps, self.tilemap_renderers, self.grids = [], [], []
        self.objs = SceneObjects()


class PrefabLoader:
    def __init__(self, prefabs):
        self.prefabs = prefabs or {}         # prefab guid -> path of its .prefab
        self.docs = {}
        self._reach = {}

    def load(self, guid):
        if guid in self.docs:
            return self.docs[guid]
        doc = None
        path = self.prefabs.get(guid)
        if path:
            try:
                with open(path, encoding="utf-8", errors="replace") as f:
                    text = f.read()
                if "--- !u!" in text:
                    doc = parse_doc(text)
            except OSError:
                doc = None
        self.docs[guid] = doc
        return doc

    def reach(self, guid, stack=()):
        """Every fileID in a prefab and in the prefabs it contains: where an override of this prefab's objects can land."""
        if guid in self._reach:
            return self._reach[guid]
        doc = self.load(guid)
        out = set(doc.ids) if doc else set()
        if doc and guid not in stack:
            for inst in doc.instances:
                if inst["guid"]:
                    out |= self.reach(inst["guid"], stack + (guid,))
        self._reach[guid] = out
        return out


def _num(ov, path, default):
    v = ov.get(path)
    if v is None:
        return default
    try:
        return float(v[0])
    except ValueError:
        return default


def _flag(ov, path, default):
    v = ov.get(path)
    return default if v is None else v[0].strip() not in ("0", "false", "False")


def instantiate(doc, g, prefix, parent_ref, mods, loader, chain=(), depth=0):
    """Copies `doc`'s objects into the graph under `prefix`, with the overrides `mods` ({target fileID: {propertyPath: (value, objectReference)}}) applied, then
    does the same for each prefab instance in `doc`. The prefab's root transform is parented to `parent_ref` (a graph id, or None)."""
    def nid(x):
        return prefix + x
    for go, active in doc.go_active.items():
        g.go_active[nid(go)] = _flag(mods.get(go, {}), "m_IsActive", active)
    for fid, node in doc.xf.items():
        ov = mods.get(fid, {})
        f = node["father"]
        g.xf[nid(fid)] = {
            "go": nid(node["go"]) if node["go"] is not None else None,
            "father": nid(f) if f not in (None, "0") else parent_ref,
            "pos": (_num(ov, "m_LocalPosition.x", node["pos"][0]), _num(ov, "m_LocalPosition.y", node["pos"][1])),
            "scale": (_num(ov, "m_LocalScale.x", node["scale"][0]), _num(ov, "m_LocalScale.y", node["scale"][1])),
            "rot": (_num(ov, "m_LocalRotation.x", node["rot"][0]), _num(ov, "m_LocalRotation.y", node["rot"][1]), _num(ov, "m_LocalRotation.z", node["rot"][2]))}
        if node["go"] is not None:
            g.go_xf[nid(node["go"])] = nid(fid)
    for r in doc.renderers:
        ov = mods.get(r["fid"], {})
        sprite = r["sprite"]
        if "m_Sprite" in ov:
            ref = ov["m_Sprite"][1]
            sprite = (ref[1].lower() if ref[1] else None, int(ref[0]))
        g.renderers.append({"go": nid(r["go"]) if r["go"] is not None else None, "enabled": _flag(ov, "m_Enabled", r["enabled"]), "sprite": sprite,
                            "order": int(_num(ov, "m_SortingOrder", r["order"]))})
    for c in doc.colliders:
        ov = mods.get(c["fid"], {})
        g.colliders.append({"go": nid(c["go"]) if c["go"] is not None else None, "enabled": _flag(ov, "m_Enabled", c["enabled"]), "trigger": _flag(ov, "m_IsTrigger", c["trigger"])})
    for b in doc.bodies:
        g.bodies.append({"go": nid(b["go"]) if b["go"] is not None else None, "type": int(_num(mods.get(b["fid"], {}), "m_BodyType", b["type"]))})
    for s, (inst, src) in doc.alias.items():
        g.alias[nid(s)] = prefix + inst + "/" + src
    for tm in doc.tilemaps:
        g.tilemaps.append(dict(tm, go=nid(tm["go"]) if tm["go"] is not None else None))
    for tr in doc.tilemap_renderers:
        g.tilemap_renderers.append(dict(tr, go=nid(tr["go"]) if tr["go"] is not None else None))
    for gr in doc.grids:
        g.grids.append(dict(gr, go=nid(gr["go"]) if gr["go"] is not None else None))

    for inst in doc.instances:
        g.objs.prefab_instances += 1
        src = loader.load(inst["guid"]) if inst["guid"] else None
        if src is None:
            g.objs.prefab_missing += 1
            continue
        if inst["guid"] in chain or depth >= MAX_PREFAB_DEPTH:
            g.objs.prefab_too_deep += 1
            continue
        reach = loader.reach(inst["guid"])
        merged = {}
        for target, path, value, objref in inst["mods"]:
            merged.setdefault(target, {})[path] = (value, objref)
            if path in HANDLED and target not in reach:
                g.objs.prefab_unapplied += 1
        for target, ov in mods.items():                       # the overrides of the file that contains this instance win over the instance's own
            merged.setdefault(target, {}).update(ov)
        parent = inst["parent"]
        instantiate(src, g, prefix + inst["fid"] + "/", nid(parent) if parent not in (None, "0") else parent_ref, merged, loader, chain + (inst["guid"],), depth + 1)


def read_scene(text, refs, prefabs=None):
    """Parses a scene's text into SceneObjects. `refs` is ImportReport.refs: (guid, fileID) -> (sprite name, w, h in world units). `prefabs` is {prefab guid:
    path of its .prefab file}, so the instances in the scene can be unpacked."""
    g = Graph()
    objs = g.objs
    instantiate(parse_doc(text), g, "", None, {}, PrefabLoader(prefabs))

    def parent(t):
        f = g.xf[t]["father"]
        f = g.alias.get(f, f)
        return f if f in g.xf else None

    world_cache = {}

    def world(t, seen=()):
        """(x, y, sx, sy) of transform `t` in the world: the parents' positions and scales applied, rotation ignored."""
        if t in world_cache:
            return world_cache[t]
        node = g.xf[t]
        x, y, sx, sy = node["pos"][0], node["pos"][1], node["scale"][0], node["scale"][1]
        f = parent(t)
        if f is not None and f not in seen and t not in seen:
            px, py, psx, psy = world(f, seen + (t,))
            x, y, sx, sy = px + psx * x, py + psy * y, psx * sx, psy * sy
        world_cache[t] = (x, y, sx, sy)
        return world_cache[t]

    def active(t, seen=()):
        node = g.xf[t]
        if node["go"] is not None and not g.go_active.get(node["go"], True):
            return False
        f = parent(t)
        return True if f is None or f in seen else active(f, seen + (t,))

    solid_go = {c["go"] for c in g.colliders if c["enabled"] and not c["trigger"]}
    body_of = {}
    for b in g.bodies:
        body_of[b["go"]] = b["type"]
    for index, r in enumerate(g.renderers):
        t = g.go_xf.get(r["go"])
        if t is None or not r["enabled"] or not active(t):
            continue
        guid, fid = r["sprite"]
        if guid is None:
            objs.unused_renderers += 1
            continue
        ref = refs.get((guid, fid))
        if ref is None:
            objs.missing_sprites.add((guid, fid))
            continue
        x, y, sx, sy = world(t)
        objs.later_frames += 1 if len(ref) > 3 and ref[3] > 0 else 0
        pl = Placement(r["go"], x, y, abs(sx), abs(sy), ref[0], ref[1], ref[2], r["order"], index)
        pl.solid = r["go"] in solid_go
        pl.dynamic = pl.solid and body_of.get(r["go"]) == 0
        rot = g.xf[t]["rot"]
        pl.rotated = abs(rot[0]) > 1e-4 or abs(rot[1]) > 1e-4 or abs(rot[2]) > 1e-4
        objs.placements.append(pl)

    # tilemaps: each tile is a sprite at the centre of its cell, (cell + anchor) * the Grid's cell size, carried through the tilemap's transform
    grids = {gr["go"]: gr for gr in g.grids}
    tm_renderer = {tr["go"]: tr for tr in g.tilemap_renderers}

    def grid_of(t):
        """The Grid above transform `t` (a Tilemap's Grid is its parent's component), or None."""
        seen, f = set(), parent(t)
        while f is not None and f not in seen:
            seen.add(f)
            gr = grids.get(g.xf[f]["go"])
            if gr is not None:
                return gr
            f = parent(f)
        return None

    next_index = len(g.renderers)
    cell_votes, phase_votes = {}, {}
    for tm in g.tilemaps:
        t = g.go_xf.get(tm["go"])
        tr = tm_renderer.get(tm["go"])
        if t is None or not tm["enabled"] or not active(t) or (tr is not None and not tr["enabled"]):
            continue
        gr = grid_of(t)
        csx, csy = gr["size"] if gr else (1.0, 1.0)
        if gr is not None and gr["layout"] != 0:
            objs.tilemaps_nonrect += 1
        wx, wy, wsx, wsy = world(t)
        ax, ay = tm["anchor"]
        placed = 0
        for cx, cy, si, mi in tm["tiles"]:
            key = tm["sprites"][si] if 0 <= si < len(tm["sprites"]) else None
            if key is None:
                objs.tiles_no_sprite += 1
                continue
            ref = refs.get(key)
            if ref is None:
                objs.missing_sprites.add(key)
                continue
            objs.later_frames += 1 if len(ref) > 3 and ref[3] > 0 else 0
            pl = Placement(tm["go"], wx + wsx * (cx + ax) * csx, wy + wsy * (cy + ay) * csy, abs(wsx), abs(wsy), ref[0], ref[1], ref[2],
                           tr["order"] if tr is not None else 0, next_index)
            next_index += 1
            pl.solid = tm["go"] in solid_go                                   # a TilemapCollider2D (or a composite) on the tilemap makes its tiles solid
            if 0 <= mi < len(tm["transformed"]) and tm["transformed"][mi]:
                objs.tiles_transformed += 1
            objs.placements.append(pl)
            placed += 1
        if placed:
            objs.tilemaps += 1
            ck, pk = round(csx * abs(wsx), 4), (ax, ay)
            cell_votes[ck] = cell_votes.get(ck, 0) + placed
            phase_votes[pk] = phase_votes.get(pk, 0) + placed
    if cell_votes:
        best = max(cell_votes.items(), key=lambda kv: (kv[1], -kv[0]))[0]
        objs.grid_cell = best if best > 0 else None
        objs.phase = max(phase_votes.items(), key=lambda kv: (kv[1], kv[0]))[0]
    return objs


def choose_cell(placements):
    """The grid cell: the most common world width of a placed sprite (sprite size x scale), else 1."""
    counts = {}
    for p in placements:
        w = round(p.wu * p.sx, 4)
        if w > 0:
            counts[w] = counts.get(w, 0) + 1
    if not counts:
        return 1.0
    return max(counts.items(), key=lambda kv: (kv[1], -kv[0]))[0]


def _emoji_pool():
    """Single-code-point emoji for new tiles: coloured squares and circles first, then objects, animals, symbols and so on."""
    for lo, hi in ((0x1F7E0, 0x1F7EB), (0x1F400, 0x1F4FF), (0x1F300, 0x1F3FA), (0x1F680, 0x1F6C5), (0x1F900, 0x1F9FF), (0x1FA70, 0x1FAFF)):
        for cp in range(lo, hi + 1):
            c = chr(cp)
            try:
                unicodedata.name(c)
            except ValueError:
                continue
            yield c


def tile_for(project, sprite, solid, dynamic, pool):
    """The emoji of the tile that shows `sprite` with these traits: an existing tile of the project when one matches, else a new one."""
    for g, t in project.tiles.items():
        same_sprite = (t.sprite == sprite) if t.sprite else (slug(t.name) == slug(sprite))
        if same_sprite and t.solid == solid and t.dynamic == dynamic and not t.diggable:
            return g
    for g in pool:
        if g not in project.tiles and g != project.empty:
            project.tiles[g] = TileDef(g, sprite, sprite, solid, dynamic)
            return g
    raise UnityImportError("no emoji left to name the tiles of this scene")


def build_level(project, name, objs, cell, report):
    """Lays the placements on a grid and adds the level to the project (or says in the notes why not). Returns the Level or None."""
    pls = objs.placements
    if not pls:
        report.notes.append("%s: no sprites to place (skipped)" % name)
        return None
    if cell is None:
        cell = objs.grid_cell or choose_cell(pls)
    if cell <= 0 or not math.isfinite(cell):
        raise UnityImportError("the cell size must be above zero (got %r)" % cell)
    # with tilemaps, a cell's centre is (k + anchor) cells, not k: objects are snapped to the same cells the tiles are in (unless the cell was chosen by hand)
    phx, phy = objs.phase if (objs.grid_cell and abs(cell - objs.grid_cell) < 1e-6) else (0.0, 0.0)
    cells = {}
    off_grid = odd_size = hidden = rotated = 0
    for p in sorted(pls, key=lambda p: (p.order, p.index)):               # later wins: sorted so the last one written to a cell is the one on top
        fx, fy = p.x / cell - phx, p.y / cell - phy
        ix, iy = int(math.floor(fx + 0.5)), int(math.floor(fy + 0.5))
        if abs(fx - ix) > 0.25 or abs(fy - iy) > 0.25:
            off_grid += 1
        w, h = p.wu * p.sx, p.hu * p.sy
        if w > cell * 1.25 or h > cell * 1.25 or w < cell * 0.75 or h < cell * 0.75:
            odd_size += 1
        rotated += 1 if p.rotated else 0
        if (ix, iy) in cells:
            hidden += 1
        cells[(ix, iy)] = p
    xs, ys = [k[0] for k in cells], [k[1] for k in cells]
    width, height = max(xs) - min(xs) + 1, max(ys) - min(ys) + 1
    if width > MAX_LEVEL_SIDE or height > MAX_LEVEL_SIDE:
        report.notes.append("%s: %dx%d cells is bigger than a level can be (%d); skipped (try a bigger cell size)" % (name, width, height, MAX_LEVEL_SIDE))
        return None
    level = Level(name, width, height)
    pool = _emoji_pool()
    for (ix, iy), p in cells.items():
        level.cells[(max(ys) - iy) * width + (ix - min(xs))] = tile_for(project, p.sprite, p.solid, p.dynamic, pool)
    for count, one, many in ((off_grid, "was not on the grid and moved to the nearest cell", "were not on the grid and moved to the nearest cell"),
                             (odd_size, "is not the size of a cell (drawn as one cell)", "are not the size of a cell (each drawn as one cell)"),
                             (hidden, "was hidden behind another sprite in the same cell", "were hidden behind another sprite in the same cell"),
                             (rotated, "is rotated (rotation is not imported)", "are rotated (rotation is not imported)")):
        if count:
            report.notes.append("%s: %d object %s" % (name, count, one) if count == 1 else "%s: %d objects %s" % (name, count, many))
    return project.add_level(level)


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the walk

def find_scenes(root):
    """The .unity scenes under a Unity project's Assets/ (or under `root` when it has none), or the one scene file `root` is. Sorted."""
    if os.path.isfile(root):
        return [root] if root.lower().endswith(".unity") else []
    base = os.path.join(root, "Assets")
    if not os.path.isdir(base):
        base = root
    out = []
    for dirpath, dirnames, names in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS and not d.startswith("."))
        out += [os.path.join(dirpath, n) for n in sorted(names) if n.lower().endswith(".unity")]
    return out


def find_prefabs(root):
    """{guid: path} of the .prefab files under a Unity project's Assets/ (or under `root`), read from their .meta files: how a scene's PrefabInstance finds its
    prefab. A prefab with no .meta cannot be referenced by a scene, so it is not listed."""
    base = root if os.path.isdir(root) else os.path.dirname(root)
    start = os.path.join(base, "Assets")
    if not os.path.isdir(start):
        start = base
    out = {}
    for dirpath, dirnames, names in os.walk(start):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS and not d.startswith("."))
        for n in sorted(names):
            if n.lower().endswith(".prefab"):
                path = os.path.join(dirpath, n)
                try:
                    with open(path + ".meta", encoding="utf-8", errors="replace") as f:
                        guid = meta_guid(f.read())
                except OSError:
                    continue
                if guid:
                    out[guid] = path
    return out


def import_unity_scenes(project, root, report, cell=None):
    """Adds a level for every scene under `root`, using the sprites `report` recorded. Notes go to the report; a scene that cannot be read is skipped."""
    base = root if os.path.isdir(root) else os.path.dirname(root)
    prefabs = find_prefabs(root)
    for path in find_scenes(root):
        name = os.path.splitext(os.path.basename(path))[0]
        rel = os.path.relpath(path, base).replace(os.sep, "/")
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                text = f.read()
        except OSError as e:
            report.notes.append("%s: %s" % (rel, e))
            continue
        if not text.lstrip().startswith("%YAML") and "--- !u!" not in text:
            report.notes.append("%s: not a text-serialized Unity scene (set Asset Serialization to Force Text); skipped" % rel)
            continue
        objs = read_scene(text, report.refs, prefabs)
        if objs.prefab_missing:
            n = objs.prefab_missing
            report.notes.append("%s: %d prefab instance%s %s a prefab that was not found (a model or a package prefab?) and %s skipped" % (
                name, n, "" if n == 1 else "s", "uses" if n == 1 else "use", "was" if n == 1 else "were"))
        if objs.prefab_too_deep:
            report.notes.append("%s: %d prefab instance%s skipped: prefabs nested in a loop or more than %d deep" % (
                name, objs.prefab_too_deep, "" if objs.prefab_too_deep == 1 else "s", MAX_PREFAB_DEPTH))
        if objs.prefab_unapplied:
            n = objs.prefab_unapplied
            report.notes.append("%s: %d prefab override%s name%s an object inside a nested prefab that could not be found, so %s not applied" % (
                name, n, "" if n == 1 else "s", "s" if n == 1 else "", "it was" if n == 1 else "they were"))
        for count, one, many in ((objs.later_frames, "object or tile shows a later frame of an animated sprite (a level shows its first frame)",
                                  "objects or tiles show a later frame of an animated sprite (a level shows its first frame)"),
                                 (objs.tiles_no_sprite, "tile has no sprite and was skipped", "tiles have no sprite and were skipped"),
                                 (objs.tiles_transformed, "tile is rotated or flipped (the transform is not imported)", "tiles are rotated or flipped (the transform is not imported)"),
                                 (objs.tilemaps_nonrect, "tilemap is on an isometric or hexagonal grid and was imported as if square",
                                  "tilemaps are on isometric or hexagonal grids and were imported as if square")):
            if count:
                report.notes.append("%s: %d %s" % (name, count, one if count == 1 else many))
        if objs.missing_sprites:
            report.notes.append("%s: %d sprite%s used by the scene could not be found among the imported textures (those objects are skipped)" % (
                name, len(objs.missing_sprites), "" if len(objs.missing_sprites) == 1 else "s"))
        level = build_level(project, name, objs, cell, report)
        if level is not None:
            report.levels.append(level)


def import_unity_project(project, root, scenes=True, cell=None):
    """The whole basic import: the sprites of the Unity project at `root` (a project folder, a folder of PNGs, or one PNG) and, with `scenes`, a level for
    each of its scenes. Returns the ImportReport."""
    report = import_unity_sprites(project, root)
    if scenes:
        import_unity_scenes(project, root, report, cell)
        if report.levels:
            project.touch()
    return report
