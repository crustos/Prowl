"""The controls of an effect, built from the registry (fxdefs.EFFECTS): a slider and a number box for a float, a number box for an int, a check box for a bool, a
combo box for an enum, a color button (with alpha) for a color. Nothing here knows any effect by name: a new .fx file shows up with its controls."""
from PyQt5 import QtCore, QtGui, QtWidgets
from PyQt5.QtCore import Qt

from .fxdefs import EFFECTS

SLIDER_STEPS = 1000


def decimals_for(lo, hi):
    span = hi - lo
    return 4 if span <= 2 else 3 if span <= 20 else 2 if span <= 400 else 1


class ColorButton(QtWidgets.QPushButton):
    """A button that shows a color (r, g, b, a in 0..1) and opens the color dialog; `changed(list)` says the new one."""
    changed = QtCore.pyqtSignal(list)

    def __init__(self, rgba, parent=None):
        super().__init__(parent)
        self.rgba = list(rgba)
        self.setFixedHeight(24)
        self.clicked.connect(self._pick)
        self._paint()

    def set_color(self, rgba):
        self.rgba = [float(x) for x in rgba]
        self._paint()

    def _qcolor(self):
        return QtGui.QColor.fromRgbF(*[min(1.0, max(0.0, x)) for x in self.rgba])

    def _paint(self):
        c = self._qcolor()
        pm = QtGui.QPixmap(60, 16)
        pm.fill(QtGui.QColor(255, 255, 255))
        p = QtGui.QPainter(pm)
        p.fillRect(0, 0, 30, 8, QtGui.QColor(190, 190, 190))
        p.fillRect(30, 8, 30, 8, QtGui.QColor(190, 190, 190))
        p.fillRect(0, 0, 60, 16, c)                  # over a checkerboard, so the alpha shows
        p.fillRect(0, 0, 30, 16, QtGui.QColor.fromRgbF(c.redF(), c.greenF(), c.blueF()))
        p.end()
        self.setIcon(QtGui.QIcon(pm))
        self.setIconSize(pm.size())
        self.setText("#%02x%02x%02x  a %.2f" % (c.red(), c.green(), c.blue(), c.alphaF()))

    def _pick(self):
        dlg = QtWidgets.QColorDialog(self._qcolor(), self)
        dlg.setOption(QtWidgets.QColorDialog.ShowAlphaChannel, True)
        dlg.setWindowTitle("Color")
        if dlg.exec_():
            c = dlg.currentColor()
            self.set_color([c.redF(), c.greenF(), c.blueF(), c.alphaF()])
            self.changed.emit(list(self.rgba))


class ParamForm(QtWidgets.QWidget):
    """The controls of one effect entry ({"effect", "values", "enabled"}). `edited(name, value)` says which parameter the user changed, and to what.
    set_entry() shows another entry; refresh() re-reads the values (after an undo)."""
    edited = QtCore.pyqtSignal(str, object)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.entry = None
        self.only = None
        self.specs = None
        self.widgets = {}
        self._quiet = False
        self.layout_ = QtWidgets.QFormLayout(self)
        self.layout_.setLabelAlignment(Qt.AlignRight)

    def set_entry(self, entry, only=None):
        """Shows the controls of an effect entry ({"effect", "values"}); `only` limits them to those parameters (by name)."""
        self.entry = entry
        self.only = only
        self.specs = None
        self._build()

    def set_specs(self, specs, values):
        """Shows controls for any list of parameter specs (the shape of fxdefs' params: name, type, label, min, max, options, default) over the dict `values`."""
        self.entry = {"values": values}
        self.only = None
        self.specs = specs
        self._build()

    def _build(self):
        self.widgets = {}
        while self.layout_.rowCount():
            self.layout_.removeRow(0)
        entry = self.entry
        if entry is None:
            return
        specs = self.specs if self.specs is not None else EFFECTS[entry["effect"]]["params"]
        for p in specs:
            if self.only is not None and p["name"] not in self.only:
                continue
            w = self._make(p, entry["values"].get(p["name"], p["default"] if p["type"] == "color" else p["default"][0]))
            self.layout_.addRow(p["label"] + ":", w)

    def refresh(self):
        if self.entry is not None:
            self._build()

    def _emit(self, name, value):
        if not self._quiet:
            self.edited.emit(name, value)

    def _make(self, p, value):
        name, t = p["name"], p["type"]
        box = QtWidgets.QWidget()
        if t == "color":
            b = ColorButton(value)
            b.changed.connect(lambda rgba, n=name: self._emit(n, rgba))
            self.widgets[name] = b
            return b
        if t == "bool":
            c = QtWidgets.QCheckBox()
            c.setChecked(bool(value))
            c.toggled.connect(lambda on, n=name: self._emit(n, bool(on)))
            self.widgets[name] = c
            return c
        if t == "enum":
            c = QtWidgets.QComboBox()
            c.addItems(p["options"])
            c.setCurrentIndex(int(value))
            c.currentIndexChanged.connect(lambda i, n=name: self._emit(n, int(i)))
            self.widgets[name] = c
            return c
        lo, hi = p["min"], p["max"]
        if t == "int":
            sp = QtWidgets.QSpinBox()
            sp.setRange(int(lo), int(hi))
            sp.setValue(int(value))
            sp.valueChanged.connect(lambda v, n=name: self._emit(n, int(v)))
            self.widgets[name] = sp
            return sp
        lay = QtWidgets.QHBoxLayout(box)
        lay.setContentsMargins(0, 0, 0, 0)
        sl = QtWidgets.QSlider(Qt.Horizontal)
        sl.setRange(0, SLIDER_STEPS)
        sp = QtWidgets.QDoubleSpinBox()
        sp.setDecimals(decimals_for(lo, hi))
        sp.setRange(lo, hi)
        sp.setSingleStep((hi - lo) / 100.0)
        sp.setKeyboardTracking(False)
        sl.setValue(round((float(value) - lo) / (hi - lo) * SLIDER_STEPS) if hi > lo else 0)
        sp.setValue(float(value))

        def from_slider(i):
            v = lo + (hi - lo) * i / SLIDER_STEPS
            if abs(v - sp.value()) > 1e-9 and not self._quiet:
                sp.setValue(v)                       # (sp's valueChanged then sends the edit)
        def from_spin(v):
            self._quiet, was = True, self._quiet
            sl.setValue(round((v - lo) / (hi - lo) * SLIDER_STEPS) if hi > lo else 0)
            self._quiet = was
            self._emit(name, float(v))
        sl.valueChanged.connect(from_slider)
        sp.valueChanged.connect(from_spin)
        lay.addWidget(sl, 1)
        lay.addWidget(sp)
        self.widgets[name] = sp
        return box
