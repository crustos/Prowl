"""gui_widgets.py: the pieces the editor's windows are made of: the pixel canvas, the tile-map canvas, small dialogs. PyQt5 only; no engine here."""
import math

from PyQt5 import QtCore, QtGui, QtWidgets
from PyQt5.QtCore import Qt

from .asciiart import emoji_for_name, natural_name, split_graphemes
from .engine import placeholder_color
from .model import ProjectError


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# images

def sprite_image(project, sprite, frame):
    """A frame as a QImage, one image pixel per sprite pixel (scale it with FastTransformation to keep it crisp)."""
    rgba = sprite.rgba(project.palette, frame)
    return QtGui.QImage(rgba, sprite.width, sprite.height, sprite.width * 4, QtGui.QImage.Format_RGBA8888).copy()


def sprite_pixmap(project, sprite, frame, size):
    img = sprite_image(project, sprite, frame)
    return QtGui.QPixmap.fromImage(img.scaled(size, size, Qt.KeepAspectRatio, Qt.FastTransformation))


def swatch_icon(rgb, size=16):
    pm = QtGui.QPixmap(size, size)
    if rgb is None:
        pm.fill(QtGui.QColor(70, 70, 76))
        p = QtGui.QPainter(pm)
        p.fillRect(0, 0, size // 2, size // 2, QtGui.QColor(110, 110, 118))
        p.fillRect(size // 2, size // 2, size // 2, size // 2, QtGui.QColor(110, 110, 118))
        p.end()
    else:
        pm.fill(QtGui.QColor(*rgb))
    return QtGui.QIcon(pm)


_CHECKER = None


def checker_brush():
    global _CHECKER
    if _CHECKER is None:
        pm = QtGui.QPixmap(16, 16)
        p = QtGui.QPainter(pm)
        p.fillRect(0, 0, 16, 16, QtGui.QColor(86, 86, 92))
        p.fillRect(0, 0, 8, 8, QtGui.QColor(110, 110, 118))
        p.fillRect(8, 8, 8, 8, QtGui.QColor(110, 110, 118))
        p.end()
        _CHECKER = QtGui.QBrush(pm)
    return _CHECKER


def emoji_font(pixel_size):
    f = QtGui.QFont()
    f.setFamilies(["Noto Color Emoji", "Apple Color Emoji", "Segoe UI Emoji", "Twemoji Mozilla", "DejaVu Sans"])
    f.setPixelSize(max(6, int(pixel_size)))
    return f


def font_can_draw(font, grapheme):
    return QtGui.QFontMetrics(font).inFontUcs4(ord(grapheme[0]))


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the pixel canvas

class PixelCanvas(QtWidgets.QWidget):
    """One frame of a sprite, zoomed. The tools paint palette indices (the studio's current colour); every stroke is one undo step."""
    edited = QtCore.pyqtSignal()
    picked = QtCore.pyqtSignal(int)         # the eyedropper took this palette index
    hovered = QtCore.pyqtSignal(str)

    def __init__(self, studio, parent=None):
        super().__init__(parent)
        self.studio = studio
        self.frame = 0
        self.tool = "pencil"
        self.onion = False
        self.grid = True
        self.setMouseTracking(True)
        self.setMinimumSize(256, 256)
        self.setSizePolicy(QtWidgets.QSizePolicy.Expanding, QtWidgets.QSizePolicy.Expanding)
        self._painting = None            # the button that is painting, during a stroke
        self._last = None

    def sprite(self):
        return self.studio.sprite

    def _geometry(self):
        s = self.sprite()
        if s is None:
            return None
        cell = max(1, min((self.width() - 8) // s.width, (self.height() - 8) // s.height))
        w, h = cell * s.width, cell * s.height
        return cell, (self.width() - w) // 2, (self.height() - h) // 2, w, h

    def paintEvent(self, _):
        p = QtGui.QPainter(self)
        p.fillRect(self.rect(), QtGui.QColor(36, 36, 42))
        g = self._geometry()
        s = self.sprite()
        if g is None or s is None:
            return
        cell, ox, oy, w, h = g
        p.fillRect(ox, oy, w, h, checker_brush())
        proj = self.studio.project
        frame = min(self.frame, len(s.frames) - 1)
        if self.onion and frame > 0:
            p.setOpacity(0.3)
            p.drawImage(QtCore.QRect(ox, oy, w, h), sprite_image(proj, s, frame - 1))
            p.setOpacity(1.0)
        p.drawImage(QtCore.QRect(ox, oy, w, h), sprite_image(proj, s, frame))
        if self.grid and cell >= 8:
            p.setPen(QtGui.QColor(0, 0, 0, 70))
            for x in range(s.width + 1):
                p.drawLine(ox + x * cell, oy, ox + x * cell, oy + h)
            for y in range(s.height + 1):
                p.drawLine(ox, oy + y * cell, ox + w, oy + y * cell)
        p.setPen(QtGui.QColor(160, 160, 170))
        p.drawRect(ox - 1, oy - 1, w + 1, h + 1)

    def cell_at(self, pos):
        g = self._geometry()
        s = self.sprite()
        if g is None or s is None:
            return None
        cell, ox, oy, w, h = g
        x, y = (pos.x() - ox) // cell, (pos.y() - oy) // cell
        return (x, y) if (0 <= x < s.width and 0 <= y < s.height) else None

    def _apply(self, x, y, button):
        s = self.sprite()
        index = 0 if (button == Qt.RightButton or self.tool == "eraser") else self.studio.color
        if self.tool == "picker" and button == Qt.LeftButton:
            self.picked.emit(s.get(self.frame, x, y))
            return False
        if self.tool == "fill" and button != Qt.NoButton:
            s.flood_fill(self.frame, x, y, index)
            return True
        if s.get(self.frame, x, y) == index:
            return False
        s.set(self.frame, x, y, index)
        return True

    def _line(self, a, b):
        """The cells between two mouse positions (a fast drag must not leave gaps)."""
        (x0, y0), (x1, y1) = a, b
        n = max(abs(x1 - x0), abs(y1 - y0), 1)
        return [(x0 + round((x1 - x0) * i / n), y0 + round((y1 - y0) * i / n)) for i in range(n + 1)]

    def mousePressEvent(self, e):
        c = self.cell_at(e.pos())
        if c is None or e.button() not in (Qt.LeftButton, Qt.RightButton):
            return
        if self.tool != "picker":
            self.studio.checkpoint(self.sprite())
        self._painting, self._last = e.button(), c
        if self._apply(c[0], c[1], e.button()):
            self.edited.emit()
        self.update()

    def mouseMoveEvent(self, e):
        c = self.cell_at(e.pos())
        s = self.sprite()
        if c is not None and s is not None:
            idx = s.get(self.frame, c[0], c[1])
            key = self.studio.project.palette.key_of(idx)
            self.hovered.emit("x %d  y %d   index %d '%s'" % (c[0], c[1], idx, key))
        if self._painting is not None and c is not None and self.tool in ("pencil", "eraser"):
            changed = False
            for x, y in self._line(self._last, c):
                changed |= self._apply(x, y, self._painting)
            self._last = c
            if changed:
                self.edited.emit()
            self.update()

    def mouseReleaseEvent(self, e):
        self._painting = None


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# the tile-map canvas

class LevelCanvas(QtWidgets.QWidget):
    """A level as a grid of cells; the tools place the studio's current tile. View is 'emoji' (the level as its text) or 'sprites' (as the game draws it)."""
    edited = QtCore.pyqtSignal()
    picked = QtCore.pyqtSignal(str)
    hovered = QtCore.pyqtSignal(str)

    def __init__(self, studio, parent=None):
        super().__init__(parent)
        self.studio = studio
        self.cell = 32
        self.tool = "paint"
        self.view = "sprites"
        self.setMouseTracking(True)
        self._painting = None
        self._last = None
        self._drag_light = -1
        self._icons = {}
        self._icon_rev = None
        self.fit_size()

    def level(self):
        return self.studio.level

    def fit_size(self):
        lv = self.level()
        if lv is not None:
            self.setFixedSize(lv.width * self.cell + 1, lv.height * self.cell + 1)
        self.update()

    def _tile_image(self, emoji):
        proj = self.studio.project
        rev = (proj.revision, proj.palette.version)
        if rev != self._icon_rev:
            self._icons = {}
            self._icon_rev = rev
        if emoji not in self._icons:
            tile = proj.tiles.get(emoji)
            spr = proj.sprite_for_tile(tile) if tile else None
            self._icons[emoji] = sprite_image(proj, spr, 0) if spr is not None else None
        return self._icons[emoji]

    def paintEvent(self, ev):
        lv = self.level()
        p = QtGui.QPainter(self)
        p.fillRect(self.rect(), QtGui.QColor(30, 31, 38))
        if lv is None:
            return
        c = self.cell
        font = emoji_font(c * 0.62)
        small = QtGui.QFont()
        small.setPixelSize(max(6, c // 4))
        proj = self.studio.project
        r = ev.rect()
        x0, x1 = max(0, r.left() // c), min(lv.width, r.right() // c + 1)
        y0, y1 = max(0, r.top() // c), min(lv.height, r.bottom() // c + 1)
        for y in range(y0, y1):
            for x in range(x0, x1):
                g = lv.cells[y * lv.width + x]
                rect = QtCore.QRect(x * c, y * c, c, c)
                if not g:
                    continue
                img = self._tile_image(g) if self.view == "sprites" else None
                if img is not None:
                    p.drawImage(rect, img)
                elif self.view == "emoji" and font_can_draw(font, g):
                    p.setFont(font)
                    p.setPen(QtGui.QColor(255, 255, 255))
                    p.drawText(rect, Qt.AlignCenter, g)
                else:                         # no sprite (or no emoji font): the tile's steady colour and its name
                    pr, pg, pb = placeholder_color(g)
                    p.fillRect(rect.adjusted(1, 1, 0, 0), QtGui.QColor(int(pr * 255), int(pg * 255), int(pb * 255)))
                    tile = proj.tiles.get(g)
                    p.setFont(small)
                    p.setPen(QtGui.QColor(20, 20, 24))
                    p.drawText(rect, Qt.AlignCenter | Qt.TextWordWrap, (tile.name if tile else natural_name(g))[:6])
        p.setPen(QtGui.QColor(255, 255, 255, 38))
        for x in range(x0, x1 + 1):
            p.drawLine(x * c, y0 * c, x * c, y1 * c)
        for y in range(y0, y1 + 1):
            p.drawLine(x0 * c, y * c, x1 * c, y * c)
        self._paint_lights(p, lv)

    LIGHT_MARK = 7                      # the radius in pixels of a light's marker

    def _light_pos(self, l):
        return QtCore.QPointF(l["x"] * self.cell, l["y"] * self.cell)

    def _paint_lights(self, p, lv):
        """Each light as a dot in its color with a ring of its radius (a spot also shows its cone); the selected one has a white outline."""
        p.setRenderHint(QtGui.QPainter.Antialiasing, True)
        for i, l in enumerate(lv.lights):
            ctr = self._light_pos(l)
            col = QtGui.QColor.fromRgbF(*l["color"][:3])
            ring = QtGui.QColor(col)
            ring.setAlpha(150 if l["enabled"] else 50)
            p.setBrush(Qt.NoBrush)
            p.setPen(QtGui.QPen(ring, 1, Qt.DashLine))
            rad = l["radius"] * self.cell
            if l["kind"] == "spot":
                a, cone = l["angle"], l["cone"]
                for edge in (a - cone, a + cone):
                    p.drawLine(ctr, ctr + QtCore.QPointF(rad * math.cos(math.radians(edge)), -rad * math.sin(math.radians(edge))))
                box = QtCore.QRectF(ctr.x() - rad, ctr.y() - rad, 2 * rad, 2 * rad)
                p.drawArc(box, int((a - cone) * 16), int(2 * cone * 16))
            else:
                p.drawEllipse(ctr, rad, rad)
            sel = i == self.studio.light
            p.setPen(QtGui.QPen(QtGui.QColor(255, 255, 255) if sel else QtGui.QColor(20, 20, 24), 2 if sel else 1))
            col.setAlpha(255 if l["enabled"] else 90)
            p.setBrush(col)
            p.drawEllipse(ctr, self.LIGHT_MARK, self.LIGHT_MARK)
            if l["kind"] == "spot":
                a = math.radians(l["angle"])
                p.drawLine(ctr, ctr + QtCore.QPointF(2.2 * self.LIGHT_MARK * math.cos(a), -2.2 * self.LIGHT_MARK * math.sin(a)))
        p.setRenderHint(QtGui.QPainter.Antialiasing, False)

    def light_at(self, pos):
        """The index of the light whose marker is under `pos` (the nearest, within a few pixels of it), or -1."""
        lv = self.level()
        best, best_d = -1, (self.LIGHT_MARK + 4) ** 2
        for i, l in enumerate(lv.lights if lv is not None else []):
            ctr = self._light_pos(l)
            d = (ctr.x() - pos.x()) ** 2 + (ctr.y() - pos.y()) ** 2
            if d <= best_d:
                best, best_d = i, d
        return best

    def _light_press(self, e):
        lv = self.level()
        i = self.light_at(e.pos())
        if e.button() == Qt.RightButton:
            if i >= 0:
                self.studio.remove_light(i)
            return
        if i < 0:                                        # an empty place: a new light there
            i = self.studio.add_light("point", e.pos().x() / self.cell, e.pos().y() / self.cell)
        else:
            self.studio.select_light(i)
        self._drag_light = i
        self._drag_off = (0.0, 0.0) if i < 0 else (lv.lights[i]["x"] - e.pos().x() / self.cell, lv.lights[i]["y"] - e.pos().y() / self.cell)

    def _light_move(self, e):
        lv = self.level()
        i = self._drag_light
        if lv is None or not 0 <= i < len(lv.lights):
            return
        x = min(max(e.pos().x() / self.cell + self._drag_off[0], 0.0), float(lv.width))
        y = min(max(e.pos().y() / self.cell + self._drag_off[1], 0.0), float(lv.height))
        self.studio.set_light(i, x=x, y=y)

    def cell_at(self, pos):
        lv = self.level()
        if lv is None:
            return None
        x, y = pos.x() // self.cell, pos.y() // self.cell
        return (x, y) if (0 <= x < lv.width and 0 <= y < lv.height) else None

    def _apply(self, x, y, button):
        lv = self.level()
        tile = "" if (button == Qt.RightButton or self.tool == "erase") else (self.studio.tile or "")
        if self.tool == "pick" and button == Qt.LeftButton:
            self.picked.emit(lv.get(x, y))
            return False
        if self.tool == "fill" or (self.tool == "pick" and button == Qt.RightButton):
            lv.flood_fill(x, y, tile)
            return True
        if lv.get(x, y) == tile:
            return False
        lv.set(x, y, tile)
        return True

    def mousePressEvent(self, e):
        if self.tool == "light":
            if e.button() in (Qt.LeftButton, Qt.RightButton):
                self._light_press(e)
            return
        c = self.cell_at(e.pos())
        if c is None or e.button() not in (Qt.LeftButton, Qt.RightButton):
            return
        if self.tool != "pick":
            self.studio.checkpoint(self.level())
        self._painting, self._last = e.button(), c
        if self._apply(c[0], c[1], e.button()):
            self.edited.emit()
        self.update()

    def mouseMoveEvent(self, e):
        if self.tool == "light":
            if self._drag_light >= 0:
                self._light_move(e)
            return
        c = self.cell_at(e.pos())
        lv = self.level()
        if c is not None and lv is not None:
            g = lv.get(c[0], c[1])
            tile = self.studio.project.tiles.get(g)
            self.hovered.emit("col %d  row %d   %s" % (c[0], c[1], ("%s %s" % (g, tile.name if tile else natural_name(g))) if g else "empty"))
        if self._painting is not None and c is not None and self.tool in ("paint", "erase"):
            changed = False
            n = max(abs(c[0] - self._last[0]), abs(c[1] - self._last[1]), 1)
            for i in range(n + 1):
                changed |= self._apply(self._last[0] + round((c[0] - self._last[0]) * i / n), self._last[1] + round((c[1] - self._last[1]) * i / n), self._painting)
            self._last = c
            if changed:
                self.edited.emit()
            self.update()

    def mouseReleaseEvent(self, e):
        self._painting = None
        self._drag_light = -1


# ---------------------------------------------------------------------------------------------------------------------------------------------------
# dialogs

class MappingDialog(QtWidgets.QDialog):
    """Asks which colour each character of some ASCII art means: the interface that turns letters into palette colours. `unknown` is {char: (r, g, b)}, the
    importer's guesses. result_mapping() is {char: (r, g, b) or None (transparent)}."""

    def __init__(self, unknown, sprite_names, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Import ASCII art: which colour is each character?")
        self.colors = dict(unknown)
        self.transparent = {c: False for c in unknown}
        lay = QtWidgets.QVBoxLayout(self)
        lay.addWidget(QtWidgets.QLabel("Importing %s.\nThese characters are not in the palette yet. Pick a colour for each one (or make it transparent); "
                                       "each becomes a palette entry keyed by the character." % ", ".join(sprite_names)))
        self.table = QtWidgets.QTableWidget(len(unknown), 3)
        self.table.setHorizontalHeaderLabels(["char", "colour", "transparent"])
        self.table.verticalHeader().setVisible(False)
        self.buttons = {}
        self.checks = {}
        for row, ch in enumerate(unknown):
            item = QtWidgets.QTableWidgetItem(ch)
            item.setFlags(Qt.ItemIsEnabled)
            item.setTextAlignment(Qt.AlignCenter)
            f = item.font()
            f.setFamily("DejaVu Sans Mono")
            f.setBold(True)
            item.setFont(f)
            self.table.setItem(row, 0, item)
            b = QtWidgets.QPushButton()
            b.clicked.connect(lambda _=False, c=ch: self._pick(c))
            self.table.setCellWidget(row, 1, b)
            self.buttons[ch] = b
            cb = QtWidgets.QCheckBox()
            cb.toggled.connect(lambda on, c=ch: self._set_transparent(c, on))
            w = QtWidgets.QWidget()
            hl = QtWidgets.QHBoxLayout(w)
            hl.addWidget(cb)
            hl.setAlignment(Qt.AlignCenter)
            hl.setContentsMargins(0, 0, 0, 0)
            self.table.setCellWidget(row, 2, w)
            self.checks[ch] = cb
            self._paint_button(ch)
        self.table.resizeColumnsToContents()
        self.table.setMinimumHeight(min(360, 40 + 32 * len(unknown)))
        lay.addWidget(self.table)
        bb = QtWidgets.QDialogButtonBox(QtWidgets.QDialogButtonBox.Ok | QtWidgets.QDialogButtonBox.Cancel)
        bb.accepted.connect(self.accept)
        bb.rejected.connect(self.reject)
        lay.addWidget(bb)

    def _paint_button(self, ch):
        b = self.buttons[ch]
        if self.transparent[ch]:
            b.setText("transparent")
            b.setStyleSheet("")
            b.setEnabled(False)
        else:
            r, g, bl = self.colors[ch]
            b.setText("#%02x%02x%02x" % (r, g, bl))
            b.setStyleSheet("background-color: rgb(%d,%d,%d); color: %s;" % (r, g, bl, "black" if (r * 299 + g * 587 + bl * 114) > 128000 else "white"))
            b.setEnabled(True)

    def _pick(self, ch):
        c = QtWidgets.QColorDialog.getColor(QtGui.QColor(*self.colors[ch]), self, "Colour for '%s'" % ch)
        if c.isValid():
            self.colors[ch] = (c.red(), c.green(), c.blue())
            self._paint_button(ch)

    def _set_transparent(self, ch, on):
        self.transparent[ch] = on
        self._paint_button(ch)

    def result_mapping(self):
        return {ch: (None if self.transparent[ch] else self.colors[ch]) for ch in self.colors}


class TileDialog(QtWidgets.QDialog):
    """Defines or edits a tile: its emoji, its natural name, which sprite draws it, whether it is solid."""

    def __init__(self, project, tile=None, parent=None):
        super().__init__(parent)
        self.project = project
        self.setWindowTitle("Edit tile" if tile else "New tile")
        form = QtWidgets.QFormLayout(self)
        self.emoji = QtWidgets.QLineEdit(tile.emoji if tile else "")
        self.emoji.setFont(emoji_font(22))
        self.emoji.setMaximumWidth(120)
        self.name = QtWidgets.QLineEdit(tile.name if tile else "")
        self.sprite = QtWidgets.QComboBox()
        self.sprite.addItem("(the sprite with the tile's name)", "")
        for s in project.sprites:
            self.sprite.addItem(s.name, s.name)
        if tile and tile.sprite:
            self.sprite.setCurrentIndex(max(0, self.sprite.findData(tile.sprite)))
        self.solid = QtWidgets.QCheckBox("solid (a collider in the viewport's play mode)")
        self.solid.setChecked(bool(tile and tile.solid))
        self.dynamic = QtWidgets.QCheckBox("movable (a crate: a dynamic body that blasts push)")
        self.dynamic.setChecked(bool(tile and tile.dynamic))
        self.diggable = QtWidgets.QCheckBox("diggable (a blast removes it, like dirt)")
        self.diggable.setChecked(bool(tile and tile.diggable))
        self.editing = tile
        self.emoji.textChanged.connect(self._emoji_changed)
        self.name.editingFinished.connect(self._name_finished)
        form.addRow("Emoji", self.emoji)
        form.addRow("Name", self.name)
        form.addRow("Drawn by", self.sprite)
        form.addRow("", self.solid)
        form.addRow("", self.dynamic)
        form.addRow("", self.diggable)
        hint = QtWidgets.QLabel("Type an emoji and its natural name fills in; or type a name such as 'brick' or 'deciduous tree' and leave the emoji empty.")
        hint.setWordWrap(True)
        form.addRow(hint)
        self.bb = QtWidgets.QDialogButtonBox(QtWidgets.QDialogButtonBox.Ok | QtWidgets.QDialogButtonBox.Cancel)
        self.bb.accepted.connect(self._accept)
        self.bb.rejected.connect(self.reject)
        form.addRow(self.bb)
        self._auto_name = not (tile and tile.name)

    def _emoji_changed(self, text):
        g = split_graphemes(text)
        if len(g) == 1 and (self._auto_name or not self.name.text()):
            self.name.setText(natural_name(g[0]))
            self._auto_name = True

    def _name_finished(self):
        if self.name.text():
            self._auto_name = False
        if not self.emoji.text().strip():
            e = emoji_for_name(self.name.text())
            if e:
                self.emoji.setText(e)

    def _accept(self):
        g = split_graphemes(self.emoji.text().strip())
        if len(g) != 1:
            QtWidgets.QMessageBox.warning(self, "Tile", "A tile is exactly one emoji (or character).")
            return
        if g[0] in (self.project.empty, " ") and not (self.editing and self.editing.emoji == g[0]):
            QtWidgets.QMessageBox.warning(self, "Tile", "%s is what an empty cell looks like in this project: pick another." % g[0])
            return
        if g[0] in self.project.tiles and not (self.editing and self.editing.emoji == g[0]):
            QtWidgets.QMessageBox.warning(self, "Tile", "%s is already a tile (%s)." % (g[0], self.project.tiles[g[0]].name))
            return
        self.result_values = (g[0], self.name.text().strip() or natural_name(g[0]), self.sprite.currentData() or "", self.solid.isChecked(),
                              self.dynamic.isChecked(), self.diggable.isChecked())
        self.accept()


def error_box(parent, title, err):
    QtWidgets.QMessageBox.critical(parent, title, str(err) if isinstance(err, ProjectError) else "%s: %s" % (type(err).__name__, err))
