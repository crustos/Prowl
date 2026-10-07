"""gui.py: the Prowl2D editor, as a set of FLOATING windows (none is docked in another; arrange them as you like):

    Project        the File menu (open / save JSON, import and export ASCII art), the lists of sprites and levels
    Palette        the indexed colours: a letter for each, one colour each
    Sprite Editor  pixels and animation frames
    Level Editor   the tile-map grid, with the level also shown as emoji text
    Viewport       NOT a Qt window: the engine's own SDL2 window (libprowl2d.so through ctypes), animated, with a physics play mode

Everything edits one Project (model.py) held by the Studio, which also owns the selection and the undo stack and tells the windows what changed.
"""
import os
import re

from PyQt5 import QtCore, QtGui, QtWidgets
from PyQt5.QtCore import Qt

from . import asciiart, fileio
from .engine import Engine, EngineError, Viewport
from .gui_widgets import (LevelCanvas, MappingDialog, PixelCanvas, TileDialog, emoji_font, error_box, sprite_pixmap, swatch_icon)
from .model import Level, Project, ProjectError, Sprite, TileDef, color_hex

JSON_FILTER = "Prowl2D project (*.json);;All files (*)"
TEXT_FILTER = "ASCII art (*.txt);;All files (*)"


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the shared state

class Studio(QtCore.QObject):
    """The project being edited, what is selected in it, and the undo stack. `changed(kind)` tells the windows what to refresh:
    'all' (another project), 'structure' (sprites, levels or tiles added, removed, renamed), 'selection', 'pixels', 'level', 'palette'."""
    changed = QtCore.pyqtSignal(str)
    viewport_requested = QtCore.pyqtSignal()
    message = QtCore.pyqtSignal(str)

    UNDO_LIMIT = 100

    def __init__(self, project):
        super().__init__()
        self.set_project(project, emit=False)

    def set_project(self, project, emit=True):
        self.project = project
        self.sprite = project.sprites[0] if project.sprites else None
        self.level = project.levels[0] if project.levels else None
        self.tile = next(iter(project.tiles), None)
        self.color = 1 if len(project.palette) > 1 else 0
        self.undo, self.redo = [], []
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
        return (obj.width, obj.height, list(obj.cells))

    @staticmethod
    def _restore(obj, snap):
        obj.width, obj.height = snap[0], snap[1]
        if isinstance(obj, Sprite):
            obj.frames = [bytearray(f) for f in snap[2]]
        else:
            obj.cells = list(snap[2])

    def checkpoint(self, obj):
        """Call BEFORE changing a sprite or level: one call is one undo step."""
        self.undo.append((obj, self._snap(obj)))
        del self.undo[:-self.UNDO_LIMIT]
        self.redo.clear()

    def _step(self, src, dst):
        if not src:
            return
        obj, snap = src.pop()
        dst.append((obj, self._snap(obj)))
        self._restore(obj, snap)
        self.edited("pixels" if isinstance(obj, Sprite) else "level")

    def do_undo(self):
        self._step(self.undo, self.redo)

    def do_redo(self):
        self._step(self.redo, self.undo)

    # ---- selection
    def select_sprite(self, sprite):
        if sprite is not self.sprite:
            self.sprite = sprite
            self.changed.emit("selection")

    def select_level(self, level):
        if level is not self.level:
            self.level = level
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
    def add_tile(self, emoji, name, sprite, solid):
        self.project.tiles[emoji] = TileDef(emoji, name, sprite, solid)
        self.tile = emoji
        self.edited("structure")

    def edit_tile(self, old, emoji, name, sprite, solid):
        tiles = {}
        for k, t in self.project.tiles.items():          # keep the tile's place in the tile list
            tiles[emoji if k == old else k] = TileDef(emoji, name, sprite, solid) if k == old else t
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
        self.zoom.setRange(12, 72)
        self.zoom.setValue(32)
        self.zoom.setFixedWidth(90)
        self.zoom.valueChanged.connect(self._zoom)
        top.addWidget(self.zoom)
        top.addStretch(1)
        vb = QtWidgets.QPushButton("Open viewport (SDL)")
        vb.clicked.connect(studio.viewport_requested.emit)
        top.addWidget(vb)
        root.addLayout(top)

        mid = QtWidgets.QHBoxLayout()
        left = QtWidgets.QVBoxLayout()
        self.tool_group = QtWidgets.QButtonGroup(self)
        for i, (key, label) in enumerate((("paint", "Paint"), ("erase", "Erase"), ("fill", "Fill"), ("pick", "Pick tile"))):
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

    def refresh_tiles(self):
        proj = self.studio.project
        self._busy = True
        self.tiles.clear()
        for e, t in proj.tiles.items():
            spr = proj.sprite_for_tile(t)
            it = QtWidgets.QListWidgetItem("%s  %s%s" % (e, t.name, "  (solid)" if t.solid else ""))
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

    def open(self):
        try:
            if self.engine is None:
                self.engine = Engine()
            if self.vp is None:
                self.vp = Viewport(self.engine, self.studio.project, 800, 480)
            if self.vp.is_open:
                return True
            self.vp.project = self.studio.project
            self.vp.show_level(self.studio.level)
            self.vp.open()
        except EngineError as e:
            QtWidgets.QMessageBox.warning(self.parent_widget, "Viewport", str(e))
            return False
        self.timer.start(16)
        self.studio.message.emit("viewport: P play/edit, click drops a ball, wheel zooms, right-drag pans, Home refits, Esc closes")
        return True

    def close(self):
        self.timer.stop()
        if self.vp is not None and self.engine is not None and self.vp.is_open:
            self.vp.close()

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
        for text, key in (("Palette", "palette"), ("Sprite Editor", "sprites"), ("Level Editor", "levels")):
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

def create_windows(studio):
    windows = {"palette": PaletteWindow(studio), "sprites": SpriteEditorWindow(studio), "levels": LevelEditorWindow(studio)}
    main = ProjectWindow(studio, windows)
    return main, windows


def tile_windows(main, windows):
    """A first arrangement: the project window at the top left, the editors where they do not cover each other. They are free windows: move them."""
    main.move(20, 40)
    windows["palette"].move(460, 40)
    windows["sprites"].move(20, 300)
    windows["levels"].move(340, 120)
