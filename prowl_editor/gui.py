"""gui.py: the Prowl2D editor, as a set of FLOATING windows (none is docked in another; arrange them as you like):

    Project        the File menu (open / save JSON, import and export ASCII art), the lists of sprites and levels
    Palette        the indexed colours: a letter for each, one colour each
    Sprite Editor  pixels and animation frames
    Level Editor   the tile-map grid, with the level also shown as emoji text
    Viewport       NOT a Qt window: the engine's own SDL2 window (libprowl2d.so through ctypes), animated, with a physics play mode

Everything edits one Project (model.py) held by the Studio, which also owns the selection and the undo stack and tells the windows what changed.
"""
import copy
import os
import re
import time

from PyQt5 import QtCore, QtGui, QtWidgets
from PyQt5.QtCore import Qt

from . import asciiart, fileio
from .engine import SAND_NAMES, Engine, EngineError, SandSettings, Viewport
from . import scriptbuild
from .gui_fx import ParamForm
from .gui_scripts import CodeEditor
from .gui_widgets import (LevelCanvas, MappingDialog, PixelCanvas, TileDialog, emoji_font, error_box, sprite_pixmap, swatch_icon)
from .model import Level, Project, ProjectError, Sprite, TileDef, color_hex

JSON_FILTER = "Prowl2D project (*.json);;All files (*)"
TEXT_FILTER = "ASCII art (*.txt);;All files (*)"
PNG_FILTER = "PNG textures (*.png);;All files (*)"


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the shared state

def _default_values(name):
    """The default values of the effect `name` as the project keeps them (an enum as its index, a bool as a bool, ...)."""
    from .fxdefs import EFFECTS
    from .fx import defaults
    d = defaults(name)
    return {p["name"]: _clean(p, d[p["name"]]) for p in EFFECTS[name]["params"]}


def _clean(p, value):
    """What a parameter's value is kept as in the project: a float for a float, int, bool or enum (a label is turned into its index), four floats for a color."""
    from .fx import _floats
    f = _floats(p, value)
    if p["type"] == "color":
        return f
    if p["type"] == "bool":
        return bool(f[0])
    if p["type"] in ("int", "enum"):
        return int(f[0])
    return f[0]


class Studio(QtCore.QObject):
    """The project being edited, what is selected in it, and the undo stack. `changed(kind)` tells the windows what to refresh:
    'all' (another project), 'structure' (sprites, levels or tiles added, removed, renamed), 'selection', 'pixels', 'level', 'palette'."""
    changed = QtCore.pyqtSignal(str)
    viewport_requested = QtCore.pyqtSignal()
    sand_changed = QtCore.pyqtSignal()
    message = QtCore.pyqtSignal(str)

    UNDO_LIMIT = 100

    def __init__(self, project):
        super().__init__()
        self.sand = SandSettings()          # the viewport's sand (engine.SandSettings); the Sand window edits it
        self._merge = None
        self.light = -1                   # the selected light of the current level (an index), or -1
        self.set_project(project, emit=False)

    def set_project(self, project, emit=True):
        self.project = project
        self.sprite = project.sprites[0] if project.sprites else None
        self.level = project.levels[0] if project.levels else None
        self.tile = next(iter(project.tiles), None)
        self.color = 1 if len(project.palette) > 1 else 0
        self.undo, self.redo = [], []
        self.light = -1
        if emit:
            self.changed.emit("all")

    # ---- editing notifications and undo
    def edited(self, kind):
        self.project.touch()
        self.changed.emit(kind)

    @staticmethod
    def _snap(obj):
        if isinstance(obj, Sprite):
            return (obj.width, obj.height, [bytes(f) for f in obj.frames])
        return (obj.width, obj.height, list(obj.cells), copy.deepcopy(obj.effects), copy.deepcopy(obj.lights), copy.deepcopy(obj.lighting))

    @staticmethod
    def _restore(obj, snap):
        obj.width, obj.height = snap[0], snap[1]
        if isinstance(obj, Sprite):
            obj.frames = [bytearray(f) for f in snap[2]]
        else:
            obj.cells = list(snap[2])
            obj.effects = copy.deepcopy(snap[3])
            obj.lights, obj.lighting = copy.deepcopy(snap[4]), copy.deepcopy(snap[5])

    def checkpoint(self, obj):
        """Call BEFORE changing a sprite or level: one call is one undo step."""
        self.undo.append((obj, self._snap(obj)))
        del self.undo[:-self.UNDO_LIMIT]
        self.redo.clear()
        self._merge = None

    def _step(self, src, dst):
        if not src:
            return
        self.light = -1
        obj, snap = src.pop()
        dst.append((obj, self._snap(obj)))
        self._restore(obj, snap)
        self.edited("pixels" if isinstance(obj, Sprite) else "level")

    def do_undo(self):
        self._step(self.undo, self.redo)

    def do_redo(self):
        self._step(self.redo, self.undo)

    # ---- the effects over the current level (one undo step each; a slider's drag is one step, see set_effect_value)
    def _fx_list(self):
        return self.level.effects if self.level is not None else None

    def add_effect(self, name):
        """Adds the effect `name` (fxdefs.EFFECTS) with its default values at the end of the level's stack. Returns its index, or -1."""
        from .fxdefs import EFFECTS
        if self.level is None or name not in EFFECTS:
            return -1
        self.checkpoint(self.level)
        self.level.effects.append({"effect": name, "values": _default_values(name), "enabled": True})
        self.edited("level")
        return len(self.level.effects) - 1

    def remove_effect(self, i):
        fx = self._fx_list()
        if fx is None or not 0 <= i < len(fx):
            return
        self.checkpoint(self.level)
        del fx[i]
        self.edited("level")

    def duplicate_effect(self, i):
        fx = self._fx_list()
        if fx is None or not 0 <= i < len(fx):
            return -1
        self.checkpoint(self.level)
        fx.insert(i + 1, copy.deepcopy(fx[i]))
        self.edited("level")
        return i + 1

    def move_effect(self, i, delta):
        """Moves the effect at i up (delta -1) or down (+1) the stack. Returns its new index."""
        fx = self._fx_list()
        j = i + delta
        if fx is None or not (0 <= i < len(fx) and 0 <= j < len(fx)):
            return i
        self.checkpoint(self.level)
        fx[i], fx[j] = fx[j], fx[i]
        self.edited("level")
        return j

    def set_effect_enabled(self, i, on):
        fx = self._fx_list()
        if fx is None or not 0 <= i < len(fx) or fx[i]["enabled"] == bool(on):
            return
        self.checkpoint(self.level)
        fx[i]["enabled"] = bool(on)
        self.edited("level")

    def reset_effect(self, i):
        fx = self._fx_list()
        if fx is None or not 0 <= i < len(fx):
            return
        self.checkpoint(self.level)
        fx[i]["values"] = _default_values(fx[i]["effect"])
        self.edited("level")

    def set_effect_value(self, i, param, value):
        """Sets one parameter. The value is checked (fx.pack: a number is brought into its range, a NaN or an unknown parameter is refused with ValueError/KeyError).
        Changes to the same parameter within a second of each other are one undo step, so a slider's drag is one. Returns the value as stored."""
        from .fx import pack
        fx = self._fx_list()
        if fx is None or not 0 <= i < len(fx):
            return None
        e = fx[i]
        pack(e["effect"], {param: value})                   # raises for what cannot be stored
        from .fxdefs import EFFECTS
        p = next(q for q in EFFECTS[e["effect"]]["params"] if q["name"] == param)
        stored = list(_clean(p, value)) if p["type"] == "color" else _clean(p, value)
        key, now = (id(self.level), i, param), time.monotonic()
        if not (self._merge and self._merge[0] == key and now - self._merge[1] < 1.0):
            self.checkpoint(self.level)
        self._merge = (key, now)
        e["values"][param] = stored
        self.edited("effects")
        return stored

    # ---- the lights of the current level (lights.py); a drag or a slider is one undo step, as with the effects
    def _merged(self, key):
        """True when this edit continues the last one (same thing, within a second): no new undo step."""
        now = time.monotonic()
        same = self._merge is not None and self._merge[0] == key and now - self._merge[1] < 1.0
        if not same:
            self.checkpoint(self.level)
        self._merge = (key, now)

    def add_light(self, kind="point", x=None, y=None):
        """Adds a light (at the middle of the level unless x, y are given, in cells). Returns its index, or -1 (no level, or MAX_LIGHTS reached)."""
        from . import lights as L
        lv = self.level
        if lv is None or len(lv.lights) >= L.MAX_LIGHTS or kind not in L.KINDS:
            return -1
        self.checkpoint(lv)
        lv.lights.append(L.new_light(kind, lv.width / 2.0 if x is None else x, lv.height / 2.0 if y is None else y))
        self.light = len(lv.lights) - 1
        self.edited("level")
        return self.light

    def remove_light(self, i):
        lv = self.level
        if lv is None or not 0 <= i < len(lv.lights):
            return
        self.checkpoint(lv)
        del lv.lights[i]
        self.light = min(self.light, len(lv.lights) - 1) if self.light >= i else self.light
        self.edited("level")

    def set_light(self, i, **values):
        """Sets some of light i's values ({kind, x, y, radius, intensity, color, angle, cone, softness, enabled}), checked and brought into range
        (ValueError for what cannot be stored). Returns the light."""
        from . import lights as L
        lv = self.level
        if lv is None or not 0 <= i < len(lv.lights):
            return None
        new = L.clean_light(dict(lv.lights[i], **values))
        if new == lv.lights[i]:
            return new
        self._merged((id(lv), "light", i, tuple(sorted(values))))
        lv.lights[i] = new
        self.edited("lights")
        return new

    def set_lighting(self, **values):
        """Sets some of the level-wide values ({enabled, ambient, falloff, glow, exposure})."""
        from . import lights as L
        lv = self.level
        if lv is None:
            return None
        new = L.clean_lighting(dict(lv.lighting, **values))
        if new == lv.lighting:
            return new
        self._merged((id(lv), "lighting", tuple(sorted(values))))
        lv.lighting = new
        self.edited("lights")
        return new

    # ---- the project's scripts (C# files). Their text is edited in the code editor's own undo; attaching is by class name.
    def add_script(self, name, text=None):
        """A new script file (a template with a [Script] class of the same name unless `text` is given). Returns it."""
        from .model import SCRIPT_TEMPLATE, ScriptFile
        name = scriptbuild.file_name(name.strip() or "Script")
        taken = {f.name for f in self.project.scripts}
        base, n = name, 2
        while name in taken or name in self.project.script_classes():
            name = "%s%d" % (base, n)
            n += 1
        f = ScriptFile(name, SCRIPT_TEMPLATE % {"name": name} if text is None else text)
        self.project.scripts.append(f)
        self.edited("scripts")
        return f

    def delete_script(self, name):
        f = self.project.script_file(name)
        if f is None:
            return
        gone = set(f.classes())
        self.project.scripts.remove(f)
        self.project.game_scripts = [c for c in self.project.game_scripts if c not in gone]
        for sp in self.project.sprites:
            sp.scripts = [c for c in sp.scripts if c not in gone]
        self.edited("scripts")

    def set_script_text(self, name, text):
        f = self.project.script_file(name)
        if f is None or f.text == text:
            return
        f.text = text
        self.project.dirty = True              # (no touch(): typing must not make the viewport rebuild its textures)
        self.changed.emit("scripttext")

    def set_sprite_script(self, sprite, cls, on):
        names = [c for c in sprite.scripts if c != cls] + ([cls] if on else [])
        if names != sprite.scripts:
            sprite.scripts = names
            self.edited("scripts")

    def set_game_script(self, cls, on):
        names = [c for c in self.project.game_scripts if c != cls] + ([cls] if on else [])
        if names != self.project.game_scripts:
            self.project.game_scripts = names
            self.edited("scripts")

    # ---- selection
    def select_sprite(self, sprite):
        if sprite is not self.sprite:
            self.sprite = sprite
            self.changed.emit("selection")

    def select_light(self, i):
        lv = self.level
        i = i if lv is not None and 0 <= i < len(lv.lights) else -1
        if i != self.light:
            self.light = i
            self.changed.emit("lightsel")

    def select_level(self, level):
        if level is not self.level:
            self.level = level
            self.light = -1
            self.changed.emit("selection")

    def select_tile(self, emoji):
        self.tile = emoji
        self.changed.emit("selection")

    def select_color(self, index):
        self.color = index
        self.changed.emit("selection")

    # ---- sprites
    def new_sprite(self, width=16, height=16):
        s = self.project.add_sprite(Sprite("sprite", width, height))
        self.sprite = s
        self.edited("structure")
        return s

    def duplicate_sprite(self):
        if self.sprite is None:
            return
        src = self.sprite
        s = Sprite(src.name + " copy", src.width, src.height, src.fps)
        s.frames = [bytearray(f) for f in src.frames]
        self.sprite = self.project.add_sprite(s)
        self.edited("structure")

    def rename_sprite(self, name):
        s = self.sprite
        name = name.strip()
        if s is None or not name or name == s.name:
            return
        if self.project.sprite(name) is not None:
            raise ProjectError("there is already a sprite called '%s'" % name)
        for t in self.project.tiles.values():            # a tile that names this sprite follows it
            if t.sprite == s.name:
                t.sprite = name
        s.name = name
        self.edited("structure")

    def delete_sprite(self):
        s = self.sprite
        if s is None:
            return
        i = self.project.sprites.index(s)
        self.project.sprites.remove(s)
        for t in self.project.tiles.values():
            if t.sprite == s.name:
                t.sprite = ""
        self.sprite = self.project.sprites[min(i, len(self.project.sprites) - 1)] if self.project.sprites else None
        self.edited("structure")

    # ---- levels
    def new_level(self, width=20, height=12):
        lv = self.project.add_level(Level("level", width, height))
        self.level = lv
        self.edited("structure")
        return lv

    def duplicate_level(self):
        if self.level is None:
            return
        src = self.level
        lv = Level(src.name + " copy", src.width, src.height)
        lv.cells = list(src.cells)
        lv.effects = copy.deepcopy(src.effects)
        lv.lights, lv.lighting = copy.deepcopy(src.lights), copy.deepcopy(src.lighting)
        self.level = self.project.add_level(lv)
        self.edited("structure")

    def rename_level(self, name):
        lv = self.level
        name = name.strip()
        if lv is None or not name or name == lv.name:
            return
        if self.project.level(name) is not None:
            raise ProjectError("there is already a level called '%s'" % name)
        lv.name = name
        self.edited("structure")

    def delete_level(self):
        lv = self.level
        if lv is None:
            return
        i = self.project.levels.index(lv)
        self.project.levels.remove(lv)
        self.level = self.project.levels[min(i, len(self.project.levels) - 1)] if self.project.levels else None
        self.edited("structure")

    # ---- tiles
    def add_tile(self, emoji, name, sprite, solid, dynamic=False, diggable=False):
        self.project.tiles[emoji] = TileDef(emoji, name, sprite, solid, dynamic, diggable)
        self.tile = emoji
        self.edited("structure")

    def edit_tile(self, old, emoji, name, sprite, solid, dynamic=False, diggable=False):
        tiles = {}
        for k, t in self.project.tiles.items():          # keep the tile's place in the tile list
            tiles[emoji if k == old else k] = TileDef(emoji, name, sprite, solid, dynamic, diggable) if k == old else t
        self.project.tiles = tiles
        if emoji != old:
            for lv in self.project.levels:
                lv.cells = [emoji if c == old else c for c in lv.cells]
        self.tile = emoji
        self.edited("structure")

    def remove_tile(self, emoji):
        self.project.tiles.pop(emoji, None)
        for lv in self.project.levels:                   # cells of a removed tile become empty
            lv.cells = ["" if c == emoji else c for c in lv.cells]
        self.tile = next(iter(self.project.tiles), None)
        self.edited("structure")


def new_project():
    p = Project("untitled")
    s = Sprite("sprite", 16, 16)
    p.add_sprite(s)
    p.add_level(Level("level 1", 24, 14))
    p.dirty = False
    return p


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# a floating window

class Floating(QtWidgets.QWidget):
    """A top-level window of the editor: it has its own title bar and place on screen, and closing it only hides it (Window menu to bring it back)."""

    def __init__(self, studio, title, size):
        super().__init__(None, Qt.Window)
        self.studio = studio
        self.setWindowTitle(title)
        self.resize(*size)
        studio.changed.connect(self.on_changed)
        undo = QtWidgets.QShortcut(QtGui.QKeySequence("Ctrl+Z"), self)
        undo.activated.connect(studio.do_undo)
        redo = QtWidgets.QShortcut(QtGui.QKeySequence("Ctrl+Y"), self)
        redo.activated.connect(studio.do_redo)
        redo2 = QtWidgets.QShortcut(QtGui.QKeySequence("Ctrl+Shift+Z"), self)
        redo2.activated.connect(studio.do_redo)

    def on_changed(self, kind):
        pass


def ask_text(parent, title, label, text=""):
    value, ok = QtWidgets.QInputDialog.getText(parent, title, label, text=text)
    return value if ok else None


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# palette

class PaletteWindow(Floating):
    def __init__(self, studio):
        super().__init__(studio, "Prowl2D - Palette", (360, 520))
        lay = QtWidgets.QVBoxLayout(self)
        note = QtWidgets.QLabel("Sprites hold palette INDICES, not colours. Change a colour here and every pixel with that index changes "
                                "(a blink is one entry's colour). The key is the letter the ASCII art uses.")
        note.setWordWrap(True)
        lay.addWidget(note)
        self.table = QtWidgets.QTableWidget(0, 3)
        self.table.setHorizontalHeaderLabels(["colour", "key", "name"])
        self.table.verticalHeader().setDefaultSectionSize(26)
        self.table.horizontalHeader().setStretchLastSection(True)
        self.table.setSelectionBehavior(QtWidgets.QAbstractItemView.SelectRows)
        self.table.setSelectionMode(QtWidgets.QAbstractItemView.SingleSelection)
        self.table.cellClicked.connect(self._clicked)
        self.table.cellDoubleClicked.connect(self._double_clicked)
        self.table.itemChanged.connect(self._item_changed)
        lay.addWidget(self.table)
        row = QtWidgets.QHBoxLayout()
        for text, fn in (("Add", self.add), ("Remove", self.remove), ("Move up", lambda: self.move_entry(-1)), ("Move down", lambda: self.move_entry(1))):
            b = QtWidgets.QPushButton(text)
            b.clicked.connect(fn)
            row.addWidget(b)
        lay.addLayout(row)
        self._busy = False
        self.refresh()

    def on_changed(self, kind):
        if kind in ("all", "palette", "structure"):
            self.refresh()
        elif kind == "selection":
            self._select_current()

    def refresh(self):
        pal = self.studio.project.palette
        self._busy = True
        self.table.setRowCount(len(pal.entries))
        for i, e in enumerate(pal.entries):
            c = QtWidgets.QTableWidgetItem(swatch_icon(e.rgb, 18), "transparent" if e.rgb is None else color_hex(e.rgb))
            c.setFlags(Qt.ItemIsEnabled | Qt.ItemIsSelectable)
            k = QtWidgets.QTableWidgetItem(e.key)
            k.setTextAlignment(Qt.AlignCenter)
            n = QtWidgets.QTableWidgetItem(e.name)
            if i == 0:
                k.setFlags(Qt.ItemIsEnabled)
                n.setFlags(Qt.ItemIsEnabled)
            self.table.setItem(i, 0, c)
            self.table.setItem(i, 1, k)
            self.table.setItem(i, 2, n)
        self.table.resizeColumnToContents(0)
        self.table.resizeColumnToContents(1)
        self._busy = False
        self._select_current()

    def _select_current(self):
        self._busy = True
        if 0 <= self.studio.color < self.table.rowCount():
            self.table.selectRow(self.studio.color)
        self._busy = False

    def _clicked(self, row, _col):
        if not self._busy:
            self.studio.select_color(row)

    def _double_clicked(self, row, col):
        if col != 0 or row == 0:
            return
        pal = self.studio.project.palette
        c = QtWidgets.QColorDialog.getColor(QtGui.QColor(*pal.entries[row].rgb), self, "Colour of '%s'" % pal.entries[row].key)
        if c.isValid():
            pal.set_color(row, (c.red(), c.green(), c.blue()))
            self.studio.edited("palette")

    def _item_changed(self, item):
        if self._busy:
            return
        pal = self.studio.project.palette
        row, col = item.row(), item.column()
        try:
            if col == 1:
                pal.set_key(row, item.text())
            elif col == 2:
                pal.entries[row].name = item.text()
                pal.touch()
        except ProjectError as e:
            error_box(self, "Palette", e)
        self.studio.edited("palette")

    def add(self):
        pal = self.studio.project.palette
        c = QtWidgets.QColorDialog.getColor(QtGui.QColor(200, 200, 200), self, "New colour")
        if not c.isValid():
            return
        try:
            idx = pal.add(pal.first_free_key(), "color", (c.red(), c.green(), c.blue()))
        except ProjectError as e:
            error_box(self, "Palette", e)
            return
        self.studio.color = idx
        self.studio.edited("palette")

    def remove(self):
        idx = self.table.currentRow()
        proj = self.studio.project
        if idx <= 0:
            return
        used = sum(1 for s in proj.sprites for f in s.frames for v in f if v == idx)
        if used and QtWidgets.QMessageBox.question(self, "Remove colour", "%d pixels use '%s'. They become transparent. Remove it?" % (used, proj.palette.entries[idx].key)) \
                != QtWidgets.QMessageBox.Yes:
            return
        proj.remove_palette_entry(idx)
        self.studio.color = min(self.studio.color, len(proj.palette) - 1)
        self.studio.edited("palette")

    def move_entry(self, delta):
        idx = self.table.currentRow()
        if idx <= 0:
            return
        new = self.studio.project.move_palette_entry(idx, idx + delta)
        self.studio.color = new
        self.studio.edited("palette")


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# sprite editor

class SpriteEditorWindow(Floating):
    def __init__(self, studio):
        super().__init__(studio, "Prowl2D - Sprite Editor", (980, 700))
        self.frame = 0
        self.anim_frame = 0
        self._busy = False
        root = QtWidgets.QVBoxLayout(self)

        top = QtWidgets.QHBoxLayout()
        self.combo = QtWidgets.QComboBox()
        self.combo.setMinimumWidth(160)
        self.combo.activated.connect(self._combo_picked)
        top.addWidget(QtWidgets.QLabel("Sprite"))
        top.addWidget(self.combo)
        for text, fn in (("New", self._new), ("Duplicate", studio.duplicate_sprite), ("Rename", self._rename), ("Delete", self._delete)):
            b = QtWidgets.QPushButton(text)
            b.clicked.connect(fn)
            top.addWidget(b)
        top.addSpacing(16)
        top.addWidget(QtWidgets.QLabel("Size"))
        self.w_spin, self.h_spin = QtWidgets.QSpinBox(), QtWidgets.QSpinBox()
        for sp in (self.w_spin, self.h_spin):
            sp.setRange(1, 256)
        top.addWidget(self.w_spin)
        top.addWidget(QtWidgets.QLabel("x"))
        top.addWidget(self.h_spin)
        rb = QtWidgets.QPushButton("Resize")
        rb.clicked.connect(self._resize)
        top.addWidget(rb)
        top.addStretch(1)
        root.addLayout(top)

        mid = QtWidgets.QHBoxLayout()
        left = QtWidgets.QVBoxLayout()
        self.tool_group = QtWidgets.QButtonGroup(self)
        for i, (key, label) in enumerate((("pencil", "Pencil"), ("eraser", "Eraser"), ("fill", "Fill"), ("picker", "Pick colour"))):
            b = QtWidgets.QToolButton()
            b.setText(label)
            b.setCheckable(True)
            b.setChecked(i == 0)
            b.setToolButtonStyle(Qt.ToolButtonTextOnly)
            b.setMinimumWidth(96)
            b.clicked.connect(lambda _=False, k=key: self._tool(k))
            self.tool_group.addButton(b)
            left.addWidget(b)
        left.addWidget(QtWidgets.QLabel("Colour (right click erases)"))
        self.color_host = QtWidgets.QWidget()
        self.color_grid = QtWidgets.QGridLayout(self.color_host)
        self.color_grid.setSpacing(2)
        self.color_grid.setContentsMargins(0, 0, 0, 0)
        left.addWidget(self.color_host)
        left.addStretch(1)
        mid.addLayout(left)

        center = QtWidgets.QVBoxLayout()
        self.canvas = PixelCanvas(studio)
        self.canvas.edited.connect(lambda: studio.edited("pixels"))
        self.canvas.picked.connect(lambda i: studio.select_color(i))
        center.addWidget(self.canvas, 1)
        self.info = QtWidgets.QLabel(" ")
        center.addWidget(self.info)
        self.canvas.hovered.connect(self.info.setText)
        self.frames = QtWidgets.QListWidget()
        self.frames.setViewMode(QtWidgets.QListView.IconMode)
        self.frames.setFlow(QtWidgets.QListView.LeftToRight)
        self.frames.setWrapping(False)
        self.frames.setIconSize(QtCore.QSize(56, 56))
        self.frames.setMovement(QtWidgets.QListView.Static)
        self.frames.setFixedHeight(102)
        self.frames.currentRowChanged.connect(self._frame_picked)
        center.addWidget(self.frames)
        fr = QtWidgets.QHBoxLayout()
        for text, fn in (("Add frame", lambda: self._frame_op("add")), ("Duplicate frame", lambda: self._frame_op("dup")),
                         ("Delete frame", lambda: self._frame_op("del")), ("< Move", lambda: self._frame_op("left")), ("Move >", lambda: self._frame_op("right"))):
            b = QtWidgets.QPushButton(text)
            b.clicked.connect(fn)
            fr.addWidget(b)
        center.addLayout(fr)
        mid.addLayout(center, 1)

        right = QtWidgets.QVBoxLayout()
        right.addWidget(QtWidgets.QLabel("Animation"))
        self.preview = QtWidgets.QLabel()
        self.preview.setFixedSize(150, 150)
        self.preview.setAlignment(Qt.AlignCenter)
        self.preview.setStyleSheet("background:#24242a; border:1px solid #555;")
        right.addWidget(self.preview)
        ar = QtWidgets.QHBoxLayout()
        self.play = QtWidgets.QPushButton("Play")
        self.play.setCheckable(True)
        self.play.toggled.connect(self._play_toggled)
        ar.addWidget(self.play)
        ar.addWidget(QtWidgets.QLabel("fps"))
        self.fps = QtWidgets.QDoubleSpinBox()
        self.fps.setRange(0.5, 60)
        self.fps.setDecimals(1)
        self.fps.valueChanged.connect(self._fps_changed)
        ar.addWidget(self.fps)
        right.addLayout(ar)
        self.onion = QtWidgets.QCheckBox("Onion skin (previous frame)")
        self.onion.toggled.connect(self._onion)
        right.addWidget(self.onion)
        right.addWidget(QtWidgets.QLabel("This frame as ASCII (keys = palette keys)"))
        self.text = QtWidgets.QPlainTextEdit()
        f = QtGui.QFontDatabase.systemFont(QtGui.QFontDatabase.FixedFont)
        f.setPointSize(10)
        self.text.setFont(f)
        self.text.setLineWrapMode(QtWidgets.QPlainTextEdit.NoWrap)
        self.text.setMinimumWidth(240)
        right.addWidget(self.text, 1)
        apply_btn = QtWidgets.QPushButton("Apply text to frame")
        apply_btn.clicked.connect(self._apply_text)
        right.addWidget(apply_btn)
        mid.addLayout(right)
        root.addLayout(mid, 1)

        self.timer = QtCore.QTimer(self)
        self.timer.timeout.connect(self._advance)
        self.refresh_all()

    # ---- refresh
    def on_changed(self, kind):
        if kind in ("all", "structure"):
            self.refresh_all()
        elif kind == "selection":
            self.refresh_all()
        elif kind in ("pixels", "palette"):
            self.refresh_colors()
            self.refresh_frames()
            self.refresh_text()
            self.canvas.update()
            self._show_preview()

    def refresh_all(self):
        s = self.studio.sprite
        self._busy = True
        self.combo.clear()
        for sp in self.studio.project.sprites:
            self.combo.addItem(sp.name)
        if s is not None:
            self.combo.setCurrentIndex(self.studio.project.sprites.index(s))
            self.frame = max(0, min(self.frame, len(s.frames) - 1))
            self.w_spin.setValue(s.width)
            self.h_spin.setValue(s.height)
            self.fps.setValue(s.fps)
        self._busy = False
        self.canvas.frame = self.frame
        self.refresh_colors()
        self.refresh_frames()
        self.refresh_text()
        self.canvas.update()
        self._show_preview()

    def refresh_colors(self):
        while self.color_grid.count():
            w = self.color_grid.takeAt(0).widget()
            if w:
                w.deleteLater()
        pal = self.studio.project.palette
        self.color_buttons = []
        for i, e in enumerate(pal.entries):
            b = QtWidgets.QToolButton()
            b.setCheckable(True)
            b.setChecked(i == self.studio.color)
            b.setIcon(swatch_icon(e.rgb, 22))
            b.setIconSize(QtCore.QSize(22, 22))
            b.setText(e.key)
            b.setToolButtonStyle(Qt.ToolButtonTextBesideIcon)
            b.setToolTip("%s  (index %d, key '%s')" % (e.name, i, e.key))
            b.setFixedSize(52, 28)
            b.clicked.connect(lambda _=False, n=i: self.studio.select_color(n))
            self.color_grid.addWidget(b, i // 3, i % 3)
            self.color_buttons.append(b)

    def refresh_frames(self):
        s = self.studio.sprite
        self._busy = True
        self.frames.clear()
        if s is not None:
            for n in range(len(s.frames)):
                it = QtWidgets.QListWidgetItem(QtGui.QIcon(sprite_pixmap(self.studio.project, s, n, 56)), str(n + 1))
                self.frames.addItem(it)
            self.frames.setCurrentRow(min(self.frame, len(s.frames) - 1))
        self._busy = False

    def refresh_text(self):
        s = self.studio.sprite
        if self.text.hasFocus():
            return
        self.text.setPlainText("\n".join(s.rows(self.studio.project.palette, self.frame)) if s is not None else "")

    # ---- the sprite
    def _combo_picked(self, i):
        if not self._busy and 0 <= i < len(self.studio.project.sprites):
            self.studio.select_sprite(self.studio.project.sprites[i])

    def _new(self):
        self.studio.new_sprite()

    def _rename(self):
        if self.studio.sprite is None:
            return
        name = ask_text(self, "Rename sprite", "Name", self.studio.sprite.name)
        if name:
            try:
                self.studio.rename_sprite(name)
            except ProjectError as e:
                error_box(self, "Rename", e)

    def _delete(self):
        s = self.studio.sprite
        if s is not None and QtWidgets.QMessageBox.question(self, "Delete sprite", "Delete '%s'?" % s.name) == QtWidgets.QMessageBox.Yes:
            self.studio.delete_sprite()

    def _resize(self):
        s = self.studio.sprite
        if s is None:
            return
        try:
            self.studio.checkpoint(s)
            s.resize(self.w_spin.value(), self.h_spin.value())
        except ProjectError as e:
            self.studio.undo.pop()
            error_box(self, "Resize", e)
            return
        self.studio.edited("pixels")

    def _tool(self, key):
        self.canvas.tool = key

    def _onion(self, on):
        self.canvas.onion = on
        self.canvas.update()

    # ---- frames
    def _frame_picked(self, row):
        s = self.studio.sprite
        if self._busy or s is None or row < 0:
            return
        self.frame = row
        self.canvas.frame = row
        self.refresh_text()
        self.canvas.update()

    def _frame_op(self, op):
        s = self.studio.sprite
        if s is None:
            return
        self.studio.checkpoint(s)
        if op == "add":
            self.frame = s.add_frame(self.frame)
        elif op == "dup":
            self.frame = s.add_frame(self.frame, copy=True)
        elif op == "del":
            s.delete_frame(self.frame)
            self.frame = min(self.frame, len(s.frames) - 1)
        elif op == "left":
            self.frame = s.move_frame(self.frame, self.frame - 1)
        elif op == "right":
            self.frame = s.move_frame(self.frame, self.frame + 1)
        self.canvas.frame = self.frame
        self.studio.edited("pixels")

    # ---- animation
    def _show_preview(self):
        s = self.studio.sprite
        if s is None:
            self.preview.clear()
            return
        n = self.anim_frame % len(s.frames)
        self.preview.setPixmap(sprite_pixmap(self.studio.project, s, n, 140))

    def _play_toggled(self, on):
        s = self.studio.sprite
        self.play.setText("Stop" if on else "Play")
        if on and s is not None:
            self.timer.start(int(1000 / max(0.5, s.fps)))
        else:
            self.timer.stop()
            self.anim_frame = self.frame
            self._show_preview()

    def _advance(self):
        s = self.studio.sprite
        if s is None:
            return
        self.anim_frame = (self.anim_frame + 1) % len(s.frames)
        self._show_preview()

    def _fps_changed(self, v):
        s = self.studio.sprite
        if self._busy or s is None:
            return
        s.fps = v
        self.studio.project.touch()
        if self.timer.isActive():
            self.timer.start(int(1000 / max(0.5, v)))

    # ---- text
    def _apply_text(self):
        s = self.studio.sprite
        if s is None:
            return
        rows = self.text.toPlainText().split("\n")
        while rows and rows[-1] == "":
            rows.pop()
        if not rows:
            return
        pal = self.studio.project.palette
        width, height = max(len(r) for r in rows), len(rows)
        data = bytearray(width * height)
        for y, row in enumerate(rows):
            for x, ch in enumerate(row):
                idx = 0 if ch == " " else pal.index_of(ch)
                if idx is None:
                    error_box(self, "ASCII", ProjectError("row %d, column %d: '%s' is not in the palette (add it in the Palette window, or import the art to be asked)" % (y + 1, x + 1, ch)))
                    return
                data[y * width + x] = idx
        try:
            self.studio.checkpoint(s)
            if (width, height) != (s.width, s.height):
                s.resize(width, height)
        except ProjectError as e:
            self.studio.undo.pop()
            error_box(self, "ASCII", e)
            return
        s.frames[self.frame] = data
        self.text.clearFocus()
        self.studio.edited("pixels")
        self.w_spin.setValue(s.width)
        self.h_spin.setValue(s.height)


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# level editor

class LevelEditorWindow(Floating):
    def __init__(self, studio):
        super().__init__(studio, "Prowl2D - Level Editor", (1060, 720))
        self._busy = False
        root = QtWidgets.QVBoxLayout(self)

        top = QtWidgets.QHBoxLayout()
        self.combo = QtWidgets.QComboBox()
        self.combo.setMinimumWidth(160)
        self.combo.activated.connect(self._combo_picked)
        top.addWidget(QtWidgets.QLabel("Level"))
        top.addWidget(self.combo)
        for text, fn in (("New", self._new), ("Duplicate", studio.duplicate_level), ("Rename", self._rename), ("Delete", self._delete)):
            b = QtWidgets.QPushButton(text)
            b.clicked.connect(fn)
            top.addWidget(b)
        top.addSpacing(12)
        top.addWidget(QtWidgets.QLabel("Size"))
        self.w_spin, self.h_spin = QtWidgets.QSpinBox(), QtWidgets.QSpinBox()
        for sp in (self.w_spin, self.h_spin):
            sp.setRange(1, 512)
        top.addWidget(self.w_spin)
        top.addWidget(QtWidgets.QLabel("x"))
        top.addWidget(self.h_spin)
        rb = QtWidgets.QPushButton("Resize")
        rb.clicked.connect(self._resize)
        top.addWidget(rb)
        top.addSpacing(12)
        top.addWidget(QtWidgets.QLabel("Show as"))
        self.view_group = QtWidgets.QButtonGroup(self)
        for key, label in (("sprites", "Sprites"), ("emoji", "Emoji")):
            r = QtWidgets.QRadioButton(label)
            r.setChecked(key == "sprites")
            r.toggled.connect(lambda on, k=key: on and self._view(k))
            self.view_group.addButton(r)
            top.addWidget(r)
        top.addWidget(QtWidgets.QLabel("Cell"))
        self.zoom = QtWidgets.QSlider(Qt.Horizontal)
        self.zoom.setRange(6, 72)
        self.zoom.setValue(32)
        self.zoom.setFixedWidth(90)
        self.zoom.valueChanged.connect(self._zoom)
        top.addWidget(self.zoom)
        fit = QtWidgets.QPushButton("Fit")
        fit.setToolTip("choose the cell size at which the whole level shows")
        fit.clicked.connect(self.fit_zoom)
        top.addWidget(fit)
        top.addStretch(1)
        vb = QtWidgets.QPushButton("Open viewport (SDL)")
        vb.clicked.connect(studio.viewport_requested.emit)
        top.addWidget(vb)
        root.addLayout(top)

        mid = QtWidgets.QHBoxLayout()
        left = QtWidgets.QVBoxLayout()
        self.tool_group = QtWidgets.QButtonGroup(self)
        for i, (key, label) in enumerate((("paint", "Paint"), ("erase", "Erase"), ("fill", "Fill"), ("pick", "Pick tile"), ("light", "Lights"))):
            b = QtWidgets.QToolButton()
            b.setText(label)
            b.setCheckable(True)
            b.setChecked(i == 0)
            b.setMinimumWidth(110)
            b.clicked.connect(lambda _=False, k=key: self._tool(k))
            self.tool_group.addButton(b)
            left.addWidget(b)
        left.addWidget(QtWidgets.QLabel("Tiles (right click erases)"))
        self.tiles = QtWidgets.QListWidget()
        self.tiles.setIconSize(QtCore.QSize(28, 28))
        self.tiles.setMinimumWidth(190)
        self.tiles.currentRowChanged.connect(self._tile_picked)
        self.tiles.itemDoubleClicked.connect(lambda _: self._edit_tile())
        left.addWidget(self.tiles, 1)
        tb = QtWidgets.QHBoxLayout()
        for text, fn in (("Add", self._add_tile), ("Edit", self._edit_tile), ("Remove", self._remove_tile)):
            b = QtWidgets.QPushButton(text)
            b.clicked.connect(fn)
            tb.addWidget(b)
        left.addLayout(tb)
        mid.addLayout(left)

        self.canvas = LevelCanvas(studio)
        self.canvas.edited.connect(lambda: studio.edited("level"))
        self.canvas.picked.connect(self._picked)
        scroll = QtWidgets.QScrollArea()
        self.scroll = scroll
        self.fitted = None                           # the level the cell size was last fitted to
        scroll.setWidget(self.canvas)
        scroll.setAlignment(Qt.AlignCenter)
        mid.addWidget(scroll, 1)

        right = QtWidgets.QVBoxLayout()
        right.addWidget(QtWidgets.QLabel("The level as text (one emoji per cell)"))
        self.text = QtWidgets.QPlainTextEdit()
        self.text.setFont(emoji_font(18))
        self.text.setLineWrapMode(QtWidgets.QPlainTextEdit.NoWrap)
        self.text.setMinimumWidth(300)
        right.addWidget(self.text, 1)
        ab = QtWidgets.QPushButton("Apply text to level")
        ab.clicked.connect(self._apply_text)
        right.addWidget(ab)
        mid.addLayout(right)
        root.addLayout(mid, 1)
        self.info = QtWidgets.QLabel(" ")
        root.addWidget(self.info)
        self.canvas.hovered.connect(self.info.setText)
        self.refresh_all()

    def on_changed(self, kind):
        if kind in ("all", "structure", "selection"):
            self.refresh_all()
        elif kind in ("level", "pixels", "palette"):
            self.canvas.fit_size()
            self.refresh_tiles()
            self.refresh_text()
        elif kind in ("lights", "lightsel"):
            self.canvas.update()

    def refresh_all(self):
        lv = self.studio.level
        self._busy = True
        self.combo.clear()
        for l in self.studio.project.levels:
            self.combo.addItem(l.name)
        if lv is not None:
            self.combo.setCurrentIndex(self.studio.project.levels.index(lv))
            self.w_spin.setValue(lv.width)
            self.h_spin.setValue(lv.height)
        self._busy = False
        self.canvas.fit_size()
        self.refresh_tiles()
        self.refresh_text()
        if lv is not None and lv is not self.fitted:        # a level that is new here: show all of it
            self.fitted = lv
            QtCore.QTimer.singleShot(0, self.fit_zoom)

    def fit_zoom(self):
        lv = self.studio.level
        if lv is None:
            return
        area = self.scroll.viewport().size()
        cell = int(min((area.width() - 4) / float(lv.width), (area.height() - 4) / float(lv.height)))
        self.zoom.setValue(max(self.zoom.minimum(), min(self.zoom.maximum(), cell)))

    def refresh_tiles(self):
        proj = self.studio.project
        self._busy = True
        self.tiles.clear()
        for e, t in proj.tiles.items():
            spr = proj.sprite_for_tile(t)
            it = QtWidgets.QListWidgetItem("%s  %s%s" % (e, t.name, "  (%s)" % ", ".join(w for w, on in (("solid", t.solid), ("dynamic", t.dynamic), ("diggable", t.diggable)) if on) if (t.solid or t.dynamic or t.diggable) else ""))
            it.setData(Qt.UserRole, e)
            it.setFont(emoji_font(16))
            if spr is not None:
                it.setIcon(QtGui.QIcon(sprite_pixmap(proj, spr, 0, 28)))
            self.tiles.addItem(it)
            if e == self.studio.tile:
                self.tiles.setCurrentItem(it)
        self._busy = False

    def refresh_text(self):
        lv = self.studio.level
        if self.text.hasFocus():
            return
        self.text.setPlainText(asciiart.level_to_text(lv, self.studio.project) if lv is not None else "")

    # ---- the level
    def _combo_picked(self, i):
        if not self._busy and 0 <= i < len(self.studio.project.levels):
            self.studio.select_level(self.studio.project.levels[i])

    def _new(self):
        self.studio.new_level()

    def _rename(self):
        if self.studio.level is None:
            return
        name = ask_text(self, "Rename level", "Name", self.studio.level.name)
        if name:
            try:
                self.studio.rename_level(name)
            except ProjectError as e:
                error_box(self, "Rename", e)

    def _delete(self):
        lv = self.studio.level
        if lv is not None and QtWidgets.QMessageBox.question(self, "Delete level", "Delete '%s'?" % lv.name) == QtWidgets.QMessageBox.Yes:
            self.studio.delete_level()

    def _resize(self):
        lv = self.studio.level
        if lv is None:
            return
        try:
            self.studio.checkpoint(lv)
            lv.resize(self.w_spin.value(), self.h_spin.value())
        except ProjectError as e:
            self.studio.undo.pop()
            error_box(self, "Resize", e)
            return
        self.studio.edited("level")

    def _tool(self, key):
        self.canvas.tool = key

    def _view(self, key):
        self.canvas.view = key
        self.canvas.update()

    def _zoom(self, v):
        self.canvas.cell = v
        self.canvas.fit_size()

    # ---- tiles
    def _tile_picked(self, row):
        if self._busy or row < 0:
            return
        e = self.tiles.item(row).data(Qt.UserRole)
        if e != self.studio.tile:
            self.studio.tile = e

    def _picked(self, emoji):
        if emoji:
            self.studio.select_tile(emoji)
            self.refresh_tiles()

    def _add_tile(self):
        d = TileDialog(self.studio.project, None, self)
        if d.exec_():
            self.studio.add_tile(*d.result_values)

    def _edit_tile(self):
        e = self.studio.tile
        t = self.studio.project.tiles.get(e)
        if t is None:
            return
        d = TileDialog(self.studio.project, t, self)
        if d.exec_():
            self.studio.edit_tile(e, *d.result_values)

    def _remove_tile(self):
        e = self.studio.tile
        if e is None:
            return
        used = sum(1 for lv in self.studio.project.levels for c in lv.cells if c == e)
        if QtWidgets.QMessageBox.question(self, "Remove tile", "Remove %s %s? %d cell(s) using it become empty." % (e, self.studio.project.tiles[e].name, used)) \
                == QtWidgets.QMessageBox.Yes:
            self.studio.remove_tile(e)

    # ---- text
    def _apply_text(self):
        lv = self.studio.level
        if lv is None:
            return
        try:
            self.studio.checkpoint(lv)
            new = asciiart.level_from_text(lv, self.text.toPlainText(), self.studio.project)
        except ProjectError as e:
            self.studio.undo.pop()
            error_box(self, "Level text", e)
            return
        self.text.clearFocus()
        self.studio.edited("structure" if new else "level")
        if new:
            self.studio.message.emit("%d new tile(s) from the text: %s" % (len(new), " ".join(t.emoji for t in new)))


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the engine's window

class ViewportController(QtCore.QObject):
    """Owns the engine (libprowl2d.so) and its SDL2 window, and drives it from a Qt timer: one thread, as the C runtime wants."""

    def __init__(self, studio, parent_widget):
        super().__init__()
        self.studio = studio
        self.parent_widget = parent_widget
        self.engine = None
        self.vp = None
        self.timer = QtCore.QTimer(self)
        self.timer.timeout.connect(self._tick)
        studio.changed.connect(self._changed)
        self.driver_factory = None       # a callable making the object that plays the level in the viewport (slime_demo.SlimeDriver)
        self.autoplay = False            # start in play mode as soon as the window is open
        self.window_pos = None           # (x, y) where the SDL window opens, or None to let the system place it
        self.engine_path = None          # the library in use (None: the one find_library finds); the code editor's Build makes a new one

    def open(self):
        try:
            if self.engine is None:
                self.engine = Engine(self.engine_path)
            if self.vp is None:
                self.vp = Viewport(self.engine, self.studio.project, 800, 480)
                self.vp.sand = self.studio.sand
            if self.vp.is_open:
                return True
            if self.window_pos:
                os.environ["PROWL_WINDOW_POS"] = "%d,%d" % self.window_pos
            self.vp.project = self.studio.project
            self.vp.show_level(self.studio.level)
            self.vp.open()
            if self.driver_factory is not None:
                self.vp.driver = self.driver_factory()
            if self.autoplay and not self.vp.playing:
                self.vp.toggle_play()
        except EngineError as e:
            QtWidgets.QMessageBox.warning(self.parent_widget, "Viewport", str(e))
            return False
        self.timer.start(16)
        self.studio.message.emit("viewport: P play/edit, click drops a ball (S: sand, left button paints), wheel zooms, right-drag pans, Home refits, Esc closes")
        return True

    def close(self):
        self.timer.stop()
        if self.vp is not None and self.engine is not None and self.vp.is_open:
            self.vp.close()

    def reload(self, lib):
        """Switches to the engine library `lib` (a build with new scripts): the graphics window is closed, the old library shut down and let go, the new one loaded, and the
        window opened again where it was (the same camera, playing again if it was). Returns True if it worked."""
        was_open = self.vp is not None and self.engine is not None and self.vp.is_open
        state = None if self.vp is None else (self.vp.cx, self.vp.cy, self.vp.half, self.vp.playing)
        self.timer.stop()
        if self.vp is not None and self.engine is not None and self.engine.started:
            self.vp.close()
        if self.engine is not None:
            self.engine.unload()
        self.engine, self.vp = None, None
        self.engine_path = lib
        try:
            self.engine = Engine(lib)
        except EngineError as e:
            QtWidgets.QMessageBox.warning(self.parent_widget, "Build", str(e))
            return False
        if was_open:
            if not self.open():
                return False
            if state is not None:
                self.vp.cx, self.vp.cy, self.vp.half = state[:3]
                if state[3] and not self.vp.playing:
                    self.vp.toggle_play()
        return True

    def _tick(self):
        if self.vp is None or not self.vp.tick():
            self.close()
        elif self.vp.status:
            self.studio.message.emit("viewport: " + self.vp.status)
            self.vp.status = ""

    def _changed(self, kind):
        if self.vp is not None and self.vp.is_open and kind in ("all", "selection"):
            self.vp.project = self.studio.project
            if self.vp.level is not self.studio.level:
                self.vp.show_level(self.studio.level)


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the project window and the File menu

class ProjectWindow(QtWidgets.QMainWindow):
    def __init__(self, studio, app_windows):
        super().__init__()
        self.studio = studio
        self.windows = app_windows
        self.viewport = ViewportController(studio, self)
        studio.viewport_requested.connect(self.viewport.open)
        studio.message.connect(lambda m: self.statusBar().showMessage(m, 8000))
        studio.changed.connect(self.refresh)
        self.resize(420, 560)

        central = QtWidgets.QWidget()
        lay = QtWidgets.QVBoxLayout(central)
        lay.addWidget(QtWidgets.QLabel("Sprites"))
        self.sprites = QtWidgets.QListWidget()
        self.sprites.setIconSize(QtCore.QSize(32, 32))
        self.sprites.currentRowChanged.connect(self._sprite_row)
        self.sprites.itemDoubleClicked.connect(lambda _: self.show_window("sprites"))
        lay.addWidget(self.sprites)
        lay.addWidget(QtWidgets.QLabel("Levels"))
        self.levels = QtWidgets.QListWidget()
        self.levels.currentRowChanged.connect(self._level_row)
        self.levels.itemDoubleClicked.connect(lambda _: self.show_window("levels"))
        lay.addWidget(self.levels)
        row = QtWidgets.QHBoxLayout()
        for text, key in (("Palette", "palette"), ("Sprite Editor", "sprites"), ("Level Editor", "levels"), ("Effects", "effects"), ("Lights", "lights"), ("Sand", "sand"), ("Scripts", "scripts")):
            b = QtWidgets.QPushButton(text)
            b.clicked.connect(lambda _=False, k=key: self.show_window(k))
            row.addWidget(b)
        vb = QtWidgets.QPushButton("Viewport")
        vb.clicked.connect(self.viewport.open)
        row.addWidget(vb)
        lay.addLayout(row)
        self.setCentralWidget(central)
        self.statusBar().showMessage("Ready")
        self._build_menus()
        self._busy = False
        self.refresh("all")

    # ---- menus
    def _action(self, menu, text, fn, shortcut=None):
        a = menu.addAction(text)
        if shortcut:
            a.setShortcut(QtGui.QKeySequence(shortcut))
        a.triggered.connect(lambda _=False: fn())
        return a

    def _build_menus(self):
        mb = self.menuBar()
        f = mb.addMenu("&File")
        self._action(f, "&New project", self.file_new, "Ctrl+N")
        self._action(f, "&Open JSON...", self.file_open, "Ctrl+O")
        self._action(f, "&Save", self.file_save, "Ctrl+S")
        self._action(f, "Save &as...", self.file_save_as, "Ctrl+Shift+S")
        f.addSeparator()
        imp = f.addMenu("&Import")
        self._action(imp, "ASCII art as sprites...", self.import_sprites)
        self._action(imp, "ASCII / emoji level...", self.import_level)
        imp.addSeparator()
        self._action(imp, "Unity project: sprites and scenes as levels...", self.import_unity_project)
        self._action(imp, "Unity 2D sprites from a project folder...", self.import_unity)
        self._action(imp, "Unity PNG textures...", self.import_unity_pngs)
        exp = f.addMenu("&Export")
        self._action(exp, "Sprites as ASCII art...", self.export_sprites)
        self._action(exp, "Levels as emoji text...", self.export_levels)
        self._action(exp, "Everything to a folder...", self.export_all)
        f.addSeparator()
        self._action(f, "&Quit", self.close, "Ctrl+Q")
        e = mb.addMenu("&Edit")
        self._action(e, "&Undo", self.studio.do_undo, "Ctrl+Z")
        self._action(e, "&Redo", self.studio.do_redo, "Ctrl+Y")
        w = mb.addMenu("&Window")
        self._action(w, "Palette", lambda: self.show_window("palette"))
        self._action(w, "Sprite Editor", lambda: self.show_window("sprites"))
        self._action(w, "Level Editor", lambda: self.show_window("levels"))
        self._action(w, "Effects", lambda: self.show_window("effects"))
        self._action(w, "Lights", lambda: self.show_window("lights"))
        self._action(w, "Sand", lambda: self.show_window("sand"))
        self._action(w, "Scripts (code editor)", lambda: self.show_window("scripts"))
        self._action(w, "Viewport (SDL)", self.viewport.open)

    def show_window(self, key):
        w = self.windows[key]
        w.show()
        w.raise_()
        w.activateWindow()

    # ---- the lists
    def refresh(self, kind="all"):
        p = self.studio.project
        self.setWindowTitle("Prowl2D - %s%s" % (p.name if not p.path else os.path.basename(p.path), " *" if p.dirty else ""))
        if kind not in ("all", "structure", "pixels", "palette", "selection", "level"):
            return
        self._busy = True
        if kind in ("all", "structure", "pixels", "palette"):
            self.sprites.clear()
            for s in p.sprites:
                it = QtWidgets.QListWidgetItem(QtGui.QIcon(sprite_pixmap(p, s, 0, 32)), "%s  (%dx%d, %d frame%s)" % (s.name, s.width, s.height, len(s.frames), "" if len(s.frames) == 1 else "s"))
                self.sprites.addItem(it)
            self.levels.clear()
            for l in p.levels:
                self.levels.addItem("%s  (%dx%d)" % (l.name, l.width, l.height))
        if self.studio.sprite in p.sprites:
            self.sprites.setCurrentRow(p.sprites.index(self.studio.sprite))
        if self.studio.level in p.levels:
            self.levels.setCurrentRow(p.levels.index(self.studio.level))
        self._busy = False

    def _sprite_row(self, row):
        if not self._busy and 0 <= row < len(self.studio.project.sprites):
            self.studio.select_sprite(self.studio.project.sprites[row])

    def _level_row(self, row):
        if not self._busy and 0 <= row < len(self.studio.project.levels):
            self.studio.select_level(self.studio.project.levels[row])

    # ---- files
    def maybe_save(self):
        """True if it is fine to discard the project in the editor (it was saved, or the user said to drop it)."""
        if not self.studio.project.dirty:
            return True
        r = QtWidgets.QMessageBox.question(self, "Unsaved changes", "Save changes to '%s' first?" % self.studio.project.name,
                                           QtWidgets.QMessageBox.Save | QtWidgets.QMessageBox.Discard | QtWidgets.QMessageBox.Cancel)
        if r == QtWidgets.QMessageBox.Save:
            return self.file_save()
        return r == QtWidgets.QMessageBox.Discard

    def file_new(self):
        if self.maybe_save():
            self.studio.set_project(new_project())

    def open_path(self, path):
        try:
            p = Project.load(path)
        except (ProjectError, OSError) as e:
            error_box(self, "Open", e)
            return False
        self.studio.set_project(p)
        self.statusBar().showMessage("opened %s: %d sprites, %d levels, %d tiles" % (path, len(p.sprites), len(p.levels), len(p.tiles)))
        return True

    def file_open(self):
        if not self.maybe_save():
            return
        path, _ = QtWidgets.QFileDialog.getOpenFileName(self, "Open project", "", JSON_FILTER)
        if path:
            self.open_path(path)

    def file_save(self):
        p = self.studio.project
        if not p.path:
            return self.file_save_as()
        return self._save(p.path)

    def file_save_as(self):
        path, _ = QtWidgets.QFileDialog.getSaveFileName(self, "Save project", (self.studio.project.name or "project") + ".json", JSON_FILTER)
        if not path:
            return False
        if not path.lower().endswith(".json"):
            path += ".json"
        return self._save(path)

    def _save(self, path):
        try:
            self.studio.project.name = os.path.splitext(os.path.basename(path))[0]
            self.studio.project.save(path)
        except OSError as e:
            error_box(self, "Save", e)
            return False
        self.studio.changed.emit("structure")
        self.statusBar().showMessage("saved " + path)
        return True

    def import_sprites(self, paths=None):
        if paths is None:
            paths, _ = QtWidgets.QFileDialog.getOpenFileNames(self, "Import ASCII art as sprites", "", TEXT_FILTER)
        for path in paths:
            try:
                with open(path, encoding="utf-8") as f:
                    text = f.read()
                text = fileio.name_first_sprite(text, fileio.stem(path))     # plain art with no header is named after its file
                blocks, unknown = asciiart.scan_sprite_text(text, self.studio.project.palette)
                mapping = {}
                if unknown:                                   # characters the palette does not know: ask which colour each is
                    d = MappingDialog(unknown, [b.name for b in blocks], self)
                    if not d.exec_():
                        continue
                    mapping = d.result_mapping()
                made, added = asciiart.import_sprites(text, self.studio.project, mapping)
            except (ProjectError, OSError, UnicodeDecodeError) as e:
                error_box(self, "Import " + os.path.basename(path), e)
                continue
            self.studio.sprite = made[0]
            self.studio.edited("structure")
            self.statusBar().showMessage("imported %s from %s%s" % (", ".join(s.name for s in made), os.path.basename(path), " (new palette keys: %s)" % " ".join(added) if added else ""))

    def import_level(self, paths=None):
        if paths is None:
            paths, _ = QtWidgets.QFileDialog.getOpenFileNames(self, "Import a level (emoji or ASCII text)", "", TEXT_FILTER)
        for path in paths:
            try:
                with open(path, encoding="utf-8") as f:
                    text = f.read()
                made, new = asciiart.import_levels(text, self.studio.project, fileio.stem(path))
            except (ProjectError, OSError, UnicodeDecodeError) as e:
                error_box(self, "Import " + os.path.basename(path), e)
                continue
            self.studio.level = made[0]
            if new:
                self.studio.tile = new[0].emoji
            self.studio.edited("structure")
            self.statusBar().showMessage("imported level %s%s" % (", ".join(l.name for l in made), "; new tiles: " + " ".join(t.emoji for t in new) if new else ""))

    def import_unity_project(self, paths=None):
        """File > Import > Unity project: the sprites of a Unity project folder, and a level for each of its scenes."""
        if paths is None:
            d = QtWidgets.QFileDialog.getExistingDirectory(self, "Import a Unity project (its sprites and scenes)")
            paths = [d] if d else []
        self._import_unity(paths, scenes=True)

    def import_unity(self, paths=None):
        """File > Import > Unity 2D sprites: just the PNG textures (and sprite sheets) of a Unity project folder, as sprites."""
        if paths is None:
            d = QtWidgets.QFileDialog.getExistingDirectory(self, "Import sprites from a Unity project (or any folder of PNGs)")
            paths = [d] if d else []
        self._import_unity(paths, scenes=False)

    def import_unity_pngs(self, paths=None):
        if paths is None:
            paths, _ = QtWidgets.QFileDialog.getOpenFileNames(self, "Import Unity PNG textures", "", PNG_FILTER)
        self._import_unity(paths, scenes=False)

    def _import_unity(self, paths, scenes=False):
        from .unity_scene import import_unity_project
        sprites, levels, notes, textures = [], [], [], 0
        for path in paths:
            try:
                report = import_unity_project(self.studio.project, path, scenes=scenes)
            except (ProjectError, OSError) as e:
                error_box(self, "Import " + os.path.basename(path), e)
                continue
            sprites += report.sprites
            levels += report.levels
            notes += report.notes
            textures += report.pngs
        if sprites or levels:
            if sprites:
                self.studio.sprite = sprites[0]
            if levels:
                self.studio.level = levels[0]
                self.studio.tile = next(iter(self.studio.project.tiles), self.studio.tile)
            self.studio.edited("structure")
            self.statusBar().showMessage("imported %d sprite%s from %d Unity texture%s%s" % (
                len(sprites), "" if len(sprites) == 1 else "s", textures, "" if textures == 1 else "s",
                " and %d level%s from scenes" % (len(levels), "" if len(levels) == 1 else "s") if levels else ""))
        if notes:                                                       # what was skipped, scaled or merged: said once, not buried in the status bar
            shown = notes[:12] + (["... and %d more" % (len(notes) - 12)] if len(notes) > 12 else [])
            QtWidgets.QMessageBox.information(self, "Unity import", "Imported %d sprite%s and %d level%s.\n\n" % (
                len(sprites), "" if len(sprites) == 1 else "s", len(levels), "" if len(levels) == 1 else "s") + "\n".join(shown))

    def _write(self, title, default, text):
        path, _ = QtWidgets.QFileDialog.getSaveFileName(self, title, default, TEXT_FILTER)
        if not path:
            return
        if not os.path.splitext(path)[1]:
            path += ".txt"
        try:
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
        except OSError as e:
            error_box(self, "Export", e)
            return
        self.statusBar().showMessage("wrote " + path)

    def export_sprites(self):
        self._write("Export sprites as ASCII art", self.studio.project.name + "_sprites.txt", asciiart.export_sprites(self.studio.project))

    def export_levels(self):
        self._write("Export levels as emoji text", self.studio.project.name + "_levels.txt", asciiart.export_levels(self.studio.project))

    def export_all(self):
        d = QtWidgets.QFileDialog.getExistingDirectory(self, "Export everything to a folder")
        if d:
            try:
                n = fileio.export_folder(self.studio.project, d)
            except OSError as e:
                error_box(self, "Export", e)
                return
            self.statusBar().showMessage("wrote %d files to %s" % (n, d))

    def closeEvent(self, ev):
        if not self.maybe_save():
            ev.ignore()
            return
        self.viewport.close()
        for w in self.windows.values():
            w.close()
        ev.accept()
        QtWidgets.QApplication.quit()


# ---------------------------------------------------------------------------------------------------------------------------------------------------

# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the effects panel

class EffectsWindow(Floating):
    """The picture effects over the current level: a stack (first to last, each can be switched off), and the controls of the selected one. The viewport applies the
    stack to what it draws, live. Every change goes through the Studio, so it is saved with the project and can be undone."""

    def __init__(self, studio):
        super().__init__(studio, "Effects", (780, 460))
        from .fxdefs import EFFECTS
        lay = QtWidgets.QHBoxLayout(self)
        left = QtWidgets.QVBoxLayout()
        self.title = QtWidgets.QLabel()
        left.addWidget(self.title)
        self.stack = QtWidgets.QListWidget()
        self.stack.currentRowChanged.connect(self._row)
        self.stack.itemChanged.connect(self._checked)
        left.addWidget(self.stack, 1)
        self.add_button = QtWidgets.QToolButton()
        self.add_button.setText("Add effect")
        self.add_button.setPopupMode(QtWidgets.QToolButton.InstantPopup)
        menu = QtWidgets.QMenu(self.add_button)
        groups = {}
        for name, e in sorted(EFFECTS.items(), key=lambda kv: (kv[1]["group"], kv[1]["id"])):
            if e.get("hidden"):
                continue                  # (an effect another window drives: scene_lights is the Lights window's)
            groups.setdefault(e["group"] or "Other", menu.addMenu(e["group"] or "Other")).addAction(
                e["title"], lambda _=False, n=name: self._add(n))
        self.add_button.setMenu(menu)
        row = QtWidgets.QGridLayout()
        row.addWidget(self.add_button, 0, 0)
        self.buttons = {}
        for key, text, fn in (("up", "Up", lambda: self._move(-1)), ("down", "Down", lambda: self._move(1)), ("dup", "Duplicate", self._dup),
                              ("del", "Remove", self._remove), ("reset", "Reset", self._reset)):
            b = QtWidgets.QPushButton(text)
            b.clicked.connect(lambda _=False, f=fn: f())
            row.addWidget(b, *divmod(len(self.buttons) + 1, 3))
            self.buttons[key] = b
        left.addLayout(row)
        lay.addLayout(left, 2)
        scroll = QtWidgets.QScrollArea()
        scroll.setWidgetResizable(True)
        self.form = ParamForm()
        self.form.edited.connect(self._edited)
        scroll.setWidget(self.form)
        lay.addWidget(scroll, 3)
        self._busy = False
        self.refresh()

    def on_changed(self, kind):
        if kind in ("all", "selection", "level", "structure"):
            self.refresh()

    def _index(self):
        return self.stack.currentRow()

    def refresh(self, keep=None):
        from .fxdefs import EFFECTS
        lv = self.studio.level
        row = self._index() if keep is None else keep
        self._busy = True
        self.stack.clear()
        effects = lv.effects if lv is not None else []
        self.title.setText("Level: %s" % lv.name if lv is not None else "No level")
        for e in effects:
            it = QtWidgets.QListWidgetItem(EFFECTS[e["effect"]]["title"])
            it.setFlags(it.flags() | Qt.ItemIsUserCheckable)
            it.setCheckState(Qt.Checked if e["enabled"] else Qt.Unchecked)
            self.stack.addItem(it)
        if effects:
            self.stack.setCurrentRow(min(max(row, 0), len(effects) - 1))
        self._busy = False
        self._row(self.stack.currentRow())

    def _row(self, i):
        if self._busy:
            return
        lv = self.studio.level
        entry = lv.effects[i] if lv is not None and 0 <= i < len(lv.effects) else None
        self.form.set_entry(entry)
        self.form.setEnabled(entry is not None)
        n = len(lv.effects) if lv is not None else 0
        for k in self.buttons:
            self.buttons[k].setEnabled(entry is not None)
        self.buttons["up"].setEnabled(entry is not None and i > 0)
        self.buttons["down"].setEnabled(entry is not None and i < n - 1)
        self.add_button.setEnabled(lv is not None)

    def _checked(self, item):
        if not self._busy:
            self.studio.set_effect_enabled(self.stack.row(item), item.checkState() == Qt.Checked)

    def _edited(self, name, value):
        try:
            self.studio.set_effect_value(self._index(), name, value)
        except (KeyError, ValueError) as e:
            self.studio.message.emit("effect: %s" % e)

    def _add(self, name):
        i = self.studio.add_effect(name)
        if i >= 0:
            self.refresh(keep=i)

    def _move(self, d):
        i = self.studio.move_effect(self._index(), d)
        self.refresh(keep=i)

    def _dup(self):
        i = self.studio.duplicate_effect(self._index())
        if i >= 0:
            self.refresh(keep=i)

    def _remove(self):
        i = self._index()
        self.studio.remove_effect(i)
        self.refresh(keep=i)

    def _reset(self):
        self.studio.reset_effect(self._index())


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the lights panel

class LightsWindow(Floating):
    """The lights of the current level: what they light over (ambient color, falloff, glow, exposure) and each light (point or spot) with its place, radius, color and
    so on. In the level editor the Lights tool places, selects and drags them on the level itself. The viewport lights its picture with them, live (up to 8)."""

    def __init__(self, studio):
        from . import lights as L
        super().__init__(studio, "Lights", (780, 560))
        self.L = L
        lay = QtWidgets.QVBoxLayout(self)
        top = QtWidgets.QHBoxLayout()
        self.title = QtWidgets.QLabel()
        top.addWidget(self.title, 1)
        self.enabled = QtWidgets.QCheckBox("Lighting on")
        self.enabled.toggled.connect(lambda on: None if self._busy else studio.set_lighting(enabled=bool(on)))
        top.addWidget(self.enabled)
        lay.addLayout(top)
        self.lighting = ParamForm()
        self.lighting.edited.connect(lambda n, v: studio.set_lighting(**{n: v}))
        lay.addWidget(self.lighting)
        mid = QtWidgets.QHBoxLayout()
        left = QtWidgets.QVBoxLayout()
        self.list = QtWidgets.QListWidget()
        self.list.currentRowChanged.connect(lambda i: None if self._busy else studio.select_light(i))
        self.list.itemChanged.connect(self._checked)
        left.addWidget(self.list, 1)
        row = QtWidgets.QGridLayout()
        self.buttons = {}
        for n, (key, text, fn) in enumerate((("point", "Add point", lambda: studio.add_light("point")), ("spot", "Add spot", lambda: studio.add_light("spot")),
                                             ("del", "Remove", lambda: studio.remove_light(studio.light)))):
            b = QtWidgets.QPushButton(text)
            b.clicked.connect(lambda _=False, f=fn: f())
            row.addWidget(b, n // 2, n % 2)
            self.buttons[key] = b
        left.addLayout(row)
        mid.addLayout(left, 2)
        scroll = QtWidgets.QScrollArea()
        scroll.setWidgetResizable(True)
        self.form = ParamForm()
        self.form.edited.connect(self._edited)
        scroll.setWidget(self.form)
        mid.addWidget(scroll, 3)
        lay.addLayout(mid, 1)
        self.hint = QtWidgets.QLabel("Level editor, Lights tool: click to add a light, drag one to move it, right-click to remove it.")
        self.hint.setWordWrap(True)
        lay.addWidget(self.hint)
        self._busy = False
        self._editing = False
        self.refresh()

    def on_changed(self, kind):
        if kind in ("all", "selection", "level", "structure"):
            self.refresh()
        elif kind == "lightsel" or (kind == "lights" and not self._editing):     # (a drag on the level moved the light: show its new place)
            self._select_row()

    def _specs(self):
        lv = self.studio.level
        specs = [dict(p) for p in self.L.LIGHT_SPECS]
        for p in specs:
            if p["name"] == "x":
                p["max"] = float(lv.width)
            elif p["name"] == "y":
                p["max"] = float(lv.height)
        return specs

    def refresh(self):
        lv = self.studio.level
        self._busy = True
        self.title.setText("Level: %s" % lv.name if lv is not None else "No level")
        self.list.clear()
        for n, l in enumerate(lv.lights if lv is not None else []):
            it = QtWidgets.QListWidgetItem("%s light %d" % (l["kind"].capitalize(), n + 1))
            it.setFlags(it.flags() | Qt.ItemIsUserCheckable)
            it.setCheckState(Qt.Checked if l["enabled"] else Qt.Unchecked)
            self.list.addItem(it)
        if lv is not None:
            self.enabled.setChecked(lv.lighting["enabled"])
            self.lighting.set_entry({"effect": "scene_lights", "values": lv.lighting}, only=self.L.LIGHTING_PARAMS)
        self._busy = False
        self._select_row()

    def _select_row(self):
        lv = self.studio.level
        i = self.studio.light
        self._busy = True
        self.list.setCurrentRow(i if i >= 0 else -1)
        self._busy = False
        light = lv.lights[i] if lv is not None and 0 <= i < len(lv.lights) else None
        if light is None:
            self.form.set_specs([], {})
        else:
            self.form.set_specs(self._specs(), dict(light, kind=self.L.KINDS.index(light["kind"])))
            self._spot_only(light["kind"] == "spot")
        self.form.setEnabled(light is not None)
        self.buttons["del"].setEnabled(light is not None)
        self.buttons["point"].setEnabled(lv is not None and len(lv.lights) < self.L.MAX_LIGHTS)
        self.buttons["spot"].setEnabled(self.buttons["point"].isEnabled())

    def _spot_only(self, on):
        for name in ("angle", "cone", "softness"):
            w = self.form.widgets.get(name)
            if w is not None:
                w.setEnabled(on)
                if w.parentWidget() is not None and w.parentWidget() is not self.form:
                    w.parentWidget().setEnabled(on)

    def _checked(self, item):
        if not self._busy:
            self.studio.set_light(self.list.row(item), enabled=item.checkState() == Qt.Checked)

    def _edited(self, name, value):
        i = self.studio.light
        self._editing = True
        try:
            if name == "kind":
                self.studio.set_light(i, kind=self.L.KINDS[int(value)])
                QtCore.QTimer.singleShot(0, self.refresh)        # (not now: the combo box that sent this would be deleted while it is still running)
            else:
                self.studio.set_light(i, **{name: value})
        except ValueError as e:
            self.studio.message.emit("light: %s" % e)
        finally:
            self._editing = False

    def sync_values(self):
        """Re-reads the selected light's values into the controls (after a drag on the level)."""
        self._select_row()


class SandWindow(Floating):
    """Falling sand and water in the viewport, simulated by the GPU (compute shaders, Native/Gfx2D/gfx2d_sand.inc): switch it on, pick what the left mouse button
    paints in the viewport, and watch it fall. The level's solid tiles are stone to it. It is a picture: balls and bodies do not touch it."""

    def __init__(self, studio):
        super().__init__(studio, "Sand", (330, 420))
        self.controller = None            # the ViewportController, set by create_windows: only for the grain counts
        st = studio.sand
        lay = QtWidgets.QVBoxLayout(self)
        self.enabled = QtWidgets.QCheckBox("Sand on (S in the viewport)")
        self.enabled.toggled.connect(lambda on: self._set("enabled", bool(on)))
        lay.addWidget(self.enabled)
        box = QtWidgets.QGroupBox("Left mouse button paints")
        row = QtWidgets.QHBoxLayout(box)
        self.group = QtWidgets.QButtonGroup(self)
        self.element_buttons = {}
        for el, name in ((2, "Sand"), (3, "Water"), (1, "Stone"), (0, "Erase")):
            b = QtWidgets.QRadioButton(name)
            b.toggled.connect(lambda on, el=el: self._set("element", el) if on else None)
            self.group.addButton(b)
            row.addWidget(b)
            self.element_buttons[el] = b
        lay.addWidget(box)
        form = QtWidgets.QFormLayout()
        self.radius = self._spin(1, 40, "radius", form, "Brush radius (cells)  [ ]")
        self.speed = self._spin(1, 16, "speed", form, "Steps a frame")
        self.scale = self._spin(1, 32, "scale", form, "Cells to a tile")
        lay.addLayout(form)
        self.solid = QtWidgets.QCheckBox("The level's solid tiles are stone")
        self.solid.toggled.connect(lambda on: (self._set("solid_stone", bool(on)), self._restart()))
        lay.addWidget(self.solid)
        row2 = QtWidgets.QHBoxLayout()
        self.pause = QtWidgets.QPushButton("Pause")
        self.pause.setCheckable(True)
        self.pause.toggled.connect(lambda on: self._set("paused", bool(on)))
        row2.addWidget(self.pause)
        clear = QtWidgets.QPushButton("Clear")
        clear.clicked.connect(self._restart)
        row2.addWidget(clear)
        lay.addLayout(row2)
        self.counts = QtWidgets.QLabel()
        self.counts.setTextFormat(Qt.PlainText)
        lay.addWidget(self.counts)
        self.note = QtWidgets.QLabel("Open the viewport, then paint with the left button. 1 stone, 2 sand, 3 water, 0 erase, Space pauses, C clears.")
        self.note.setWordWrap(True)
        lay.addWidget(self.note)
        lay.addStretch(1)
        self._busy = False
        self.timer = QtCore.QTimer(self)
        self.timer.timeout.connect(self.refresh)
        self.timer.start(400)
        studio.sand_changed.connect(self.refresh)
        self.refresh()

    def _spin(self, lo, hi, attr, form, label):
        sp = QtWidgets.QSpinBox()
        sp.setRange(lo, hi)
        sp.valueChanged.connect(lambda v: self._set(attr, int(v), restart=(attr == "scale")))
        form.addRow(label, sp)
        return sp

    def _set(self, attr, value, restart=False):
        if self._busy:
            return
        setattr(self.studio.sand, attr, value)
        if restart:
            self._restart()
        self.studio.sand_changed.emit()

    def _restart(self):
        self.studio.sand.generation += 1

    def refresh(self):
        st = self.studio.sand
        self._busy = True
        try:
            self.enabled.setChecked(st.enabled)
            self.element_buttons[st.element].setChecked(True)
            for sp, v in ((self.radius, st.radius), (self.speed, st.speed), (self.scale, st.scale)):
                sp.setValue(v)
            self.solid.setChecked(st.solid_stone)
            self.pause.setChecked(st.paused)
        finally:
            self._busy = False
        vp = self.controller.vp if self.controller is not None else None
        if st.error:
            self.counts.setText(st.error)
        elif vp is not None and vp.is_open and st.enabled and not st.paused:
            sand, water, stone = vp.sand_counts()
            self.counts.setText("grains: %d sand, %d water, %d stone" % (sand, water, stone))
        elif vp is None or not vp.is_open:
            self.counts.setText("the viewport is not open")


class ScriptsWindow(Floating):
    """The code editor: the project's C# scripts, which sprites and the game attach by class name. Build (F5) translates the engine and the scripts to C, links a new engine
    library and swaps it in: the graphics window closes and opens again on the new one. Errors come back with their line (the problems list; double-click to go there)."""

    def __init__(self, studio):
        super().__init__(studio, "Scripts", (980, 640))
        self.controller = None                 # the ViewportController, set by create_windows
        self.builder = scriptbuild.Builder()
        self.process = None
        self.last_result = None
        self._classes = []
        self.built = None                      # the scripts' text as of the last good build: [(name, text)]
        self._busy = False
        self._open = None                      # the name of the file in the editor
        lay = QtWidgets.QVBoxLayout(self)
        bar = QtWidgets.QHBoxLayout()
        for text, fn in (("New script", self.new_script), ("Delete", self.delete_script)):
            b = QtWidgets.QPushButton(text)
            b.clicked.connect(lambda _=False, f=fn: f())
            bar.addWidget(b)
        bar.addStretch(1)
        self.status = QtWidgets.QLabel()
        bar.addWidget(self.status)
        self.build_button = QtWidgets.QPushButton("Build && reload   F5")
        self.build_button.clicked.connect(lambda: self.build())
        bar.addWidget(self.build_button)
        lay.addLayout(bar)
        QtWidgets.QShortcut(QtGui.QKeySequence("F5"), self).activated.connect(lambda: self.build())

        split = QtWidgets.QSplitter(Qt.Horizontal)
        left = QtWidgets.QWidget()
        ll = QtWidgets.QVBoxLayout(left)
        ll.setContentsMargins(0, 0, 0, 0)
        ll.addWidget(QtWidgets.QLabel("Files"))
        self.files = QtWidgets.QListWidget()
        self.files.currentRowChanged.connect(self._pick_file)
        ll.addWidget(self.files, 2)
        ll.addWidget(QtWidgets.QLabel("Runs on the sprite:"))
        self.sprite_box = QtWidgets.QComboBox()
        self.sprite_box.currentIndexChanged.connect(self._fill_attachments)
        ll.addWidget(self.sprite_box)
        self.sprite_list = QtWidgets.QListWidget()
        self.sprite_list.itemChanged.connect(self._sprite_checked)
        ll.addWidget(self.sprite_list, 2)
        ll.addWidget(QtWidgets.QLabel("Runs in the game (once, whenever a level plays):"))
        self.game_list = QtWidgets.QListWidget()
        self.game_list.itemChanged.connect(self._game_checked)
        ll.addWidget(self.game_list, 2)
        split.addWidget(left)
        right = QtWidgets.QSplitter(Qt.Vertical)
        self.editor = CodeEditor()
        self.editor.textChanged.connect(self._text_changed)
        right.addWidget(self.editor)
        self.problems = QtWidgets.QListWidget()
        self.problems.itemActivated.connect(self._goto_problem)
        self.problems.itemClicked.connect(self._goto_problem)
        right.addWidget(self.problems)
        right.setSizes([480, 120])
        split.addWidget(right)
        split.setSizes([240, 740])
        lay.addWidget(split, 1)
        self.refresh()

    # ---- the files
    def _names(self):
        return [f.name for f in self.studio.project.scripts]

    def refresh(self, keep=None):
        proj = self.studio.project
        self._busy = True
        try:
            want = keep or self._open
            self.files.clear()
            for f in proj.scripts:
                self.files.addItem(f.name + ".cs")
            names = self._names()
            if names:
                self.files.setCurrentRow(names.index(want) if want in names else 0)
            self._load(names[self.files.currentRow()] if names else None)
            self.sprite_box.clear()
            for sp in proj.sprites:
                self.sprite_box.addItem(sp.name)
            if self.studio.sprite is not None and self.studio.sprite in proj.sprites:
                self.sprite_box.setCurrentIndex(proj.sprites.index(self.studio.sprite))
        finally:
            self._busy = False
        self._fill_attachments()
        self._show_status()

    def _load(self, name):
        self._open = name
        f = self.studio.project.script_file(name) if name else None
        self.editor.setEnabled(f is not None)
        self.editor.setPlainText(f.text if f is not None else "")
        self.editor.mark_errors({})

    def on_changed(self, kind):
        if kind in ("all", "scripts"):
            self.refresh()
        elif kind == "scripttext":
            classes = sorted(self.studio.project.script_classes())
            if classes != self._classes:                 # (a class was added or renamed while typing: the attach lists follow)
                self._fill_attachments()
        elif kind == "selection" and self.studio.sprite is not None and self.studio.sprite in self.studio.project.sprites:
            self.sprite_box.setCurrentIndex(self.studio.project.sprites.index(self.studio.sprite))

    def _pick_file(self, row):
        if self._busy:
            return
        names = self._names()
        if 0 <= row < len(names):
            self._load(names[row])

    def _text_changed(self):
        if self._busy or self._open is None:
            return
        self.studio.set_script_text(self._open, self.editor.toPlainText())
        self._show_status()

    def new_script(self, name=None):
        if name is None:
            name = ask_text(self, "New script", "Class name:", "MyScript")
            if not name:
                return None
        f = self.studio.add_script(name)
        self.refresh(keep=f.name)
        return f

    def delete_script(self):
        if self._open is not None:
            self.studio.delete_script(self._open)
            self._open = None
            self.refresh()

    # ---- what each sprite and the game run
    def _fill_attachments(self, *_):
        proj = self.studio.project
        classes = sorted(proj.script_classes())
        self._classes = classes
        sp = proj.sprites[self.sprite_box.currentIndex()] if 0 <= self.sprite_box.currentIndex() < len(proj.sprites) else None
        for widget, chosen in ((self.sprite_list, sp.scripts if sp else []), (self.game_list, proj.game_scripts)):
            widget.blockSignals(True)
            widget.clear()
            for c in classes:
                item = QtWidgets.QListWidgetItem(c)
                item.setFlags(item.flags() | Qt.ItemIsUserCheckable)
                item.setCheckState(Qt.Checked if c in chosen else Qt.Unchecked)
                widget.addItem(item)
            widget.blockSignals(False)
        self.sprite_list.setEnabled(sp is not None)

    def _sprite_checked(self, item):
        proj = self.studio.project
        i = self.sprite_box.currentIndex()
        if 0 <= i < len(proj.sprites):
            self.studio.set_sprite_script(proj.sprites[i], item.text(), item.checkState() == Qt.Checked)

    def _game_checked(self, item):
        self.studio.set_game_script(item.text(), item.checkState() == Qt.Checked)

    # ---- building
    def signature(self):
        return [(f.name, f.text) for f in self.studio.project.scripts]

    def _show_status(self):
        if self._busy_build():
            return
        if not self.studio.project.scripts:
            self.status.setText("no scripts yet")
        elif self.built == self.signature():
            self.status.setText("built")
        else:
            self.status.setText("changed since the last build" if self.built is not None else "not built yet")

    def _busy_build(self):
        return self.process is not None

    def build(self, sync=False):
        """Builds the project's scripts into a new engine library and swaps it in. Asynchronous (the translator takes a while) unless `sync`; returns the result if sync."""
        if self.process is not None:
            return None
        proj = self.studio.project
        self.problems.clear()
        self.editor.mark_errors({})
        sig = self.signature()
        cmd, lib = self.builder.prepare(proj)
        if sync:
            import subprocess
            r = subprocess.run(cmd, cwd=scriptbuild.ROOT, capture_output=True, text=True)
            return self._finished(lib, r.returncode, r.stdout + r.stderr, sig)
        self.process = QtCore.QProcess(self)
        self.process.setProcessChannelMode(QtCore.QProcess.MergedChannels)
        self.process.setWorkingDirectory(scriptbuild.ROOT)
        self.process.finished.connect(lambda code, _st, lib=lib, sig=sig: self._async_done(lib, code, sig))
        self.build_button.setEnabled(False)
        self.status.setText("building: translating the engine and the scripts, then linking (about a minute)...")
        self.studio.message.emit("building the scripts...")
        self.process.start(cmd[0], cmd[1:])
        return None

    def _async_done(self, lib, code, sig):
        proc, self.process = self.process, None
        out = bytes(proc.readAll()).decode("utf-8", "replace")
        proc.deleteLater()
        self.build_button.setEnabled(True)
        self._finished(lib, code, out, sig)

    def _finished(self, lib, code, output, sig):
        result = self.builder.finish(lib, code, output)
        self.last_result = result
        errors = {}
        for d in result.diagnostics:
            item = QtWidgets.QListWidgetItem(str(d))
            item.setData(Qt.UserRole, (d.script, d.line, d.col))
            item.setForeground(QtGui.QColor("#f48771" if d.severity == "error" else "#cca700"))
            self.problems.addItem(item)
            if d.script == self._open and d.line:
                errors.setdefault(d.line, d.message)
        self.editor.mark_errors(errors)
        if not result.ok:
            self.status.setText("build failed: %d problem%s" % (len(result.diagnostics), "" if len(result.diagnostics) == 1 else "s"))
            self.studio.message.emit("build failed")
            return result
        ok = self.controller.reload(lib) if self.controller is not None else True
        if ok:
            self.built = sig
            self.builder.forget_old(lib)
            self.status.setText("built: %d script class%s" % (len(result.scripts), "" if len(result.scripts) == 1 else "es"))
            self.studio.message.emit("built; the engine was reloaded")
            if self.problems.count() == 0:
                self.problems.addItem("no problems")
        return result

    def _goto_problem(self, item):
        data = item.data(Qt.UserRole)
        if not data or not data[0]:
            return
        name, line, col = data
        names = self._names()
        if name in names:
            self.files.setCurrentRow(names.index(name))
            self.editor.goto_line(line, col)

    def closeEvent(self, ev):
        super().closeEvent(ev)


def create_windows(studio):
    windows = {"palette": PaletteWindow(studio), "sprites": SpriteEditorWindow(studio), "levels": LevelEditorWindow(studio), "effects": EffectsWindow(studio), "lights": LightsWindow(studio),
               "sand": SandWindow(studio), "scripts": ScriptsWindow(studio)}
    main = ProjectWindow(studio, windows)
    windows["sand"].controller = main.viewport
    windows["scripts"].controller = main.viewport
    return main, windows


def tile_windows(main, windows):
    """A first arrangement of the free windows (move them as you like). On a big screen (2500 px wide or more) nothing overlaps: the project and palette windows and the
    engine's window along the top, the sprite and level editors below; on a smaller one they overlap, as windows do."""
    g = QtWidgets.QApplication.primaryScreen().availableGeometry()
    x0, y0 = g.x(), g.y()
    if g.width() >= 2500 and g.height() >= 1120:
        main.resize(420, 470)
        main.move(x0 + 10, y0 + 30)
        windows["palette"].resize(382, 470)
        windows["palette"].move(x0 + 440, y0 + 30)
        main.viewport.window_pos = (x0 + 832, y0 + 30)
        windows["sprites"].resize(1097, 560)
        windows["sprites"].move(x0 + 10, y0 + 560)
        windows["levels"].resize(1425, 560)
        windows["levels"].move(x0 + 1120, y0 + 560)
        windows["effects"].move(x0 + 1530, y0 + 30)
        windows["lights"].move(x0 + 1530, y0 + 520)
        windows["sand"].move(x0 + 1960, y0 + 30)
        windows["scripts"].move(x0 + 1960, y0 + 520)
        return
    main.move(20, 40)
    windows["palette"].move(460, 40)
    windows["sprites"].move(20, 300)
    windows["levels"].move(340, 120)
    windows["effects"].move(640, 200)
    windows["lights"].move(700, 260)
    windows["sand"].move(760, 320)
    windows["scripts"].move(120, 80)
