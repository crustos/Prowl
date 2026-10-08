"""The parts of the code editor: a plain-text editor with line numbers, a current-line highlight, auto-indent, Tab as spaces and C# colouring. No project knowledge here."""
import re

from PyQt5 import QtCore, QtGui, QtWidgets
from PyQt5.QtCore import Qt

KEYWORDS = ("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally "
            "fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public "
            "readonly ref return sbyte sealed short sizeof static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void "
            "volatile while").split()
TYPES = "Component Node Scene2D Rigidbody2D Collider2D Collision2D Input2D Script MaxInstances Console Math Mathf".split()


def _fmt(color, bold=False, italic=False):
    f = QtGui.QTextCharFormat()
    f.setForeground(QtGui.QColor(color))
    if bold:
        f.setFontWeight(QtGui.QFont.Bold)
    f.setFontItalic(italic)
    return f


class CSharpHighlighter(QtGui.QSyntaxHighlighter):
    """Keywords, the engine's types, numbers, strings and comments (// and a /* */ that runs over lines)."""

    def __init__(self, doc):
        super().__init__(doc)
        self.rules = [(re.compile(r"\b(%s)\b" % "|".join(KEYWORDS)), _fmt("#c586c0", True)),
                      (re.compile(r"\b(%s)\b" % "|".join(TYPES)), _fmt("#4ec9b0")),
                      (re.compile(r"\b\d+(\.\d+)?f?\b"), _fmt("#b5cea8")),
                      (re.compile(r"\[[A-Za-z_][^\]\n]*\]"), _fmt("#dcdcaa")),
                      (re.compile(r'"(\\.|[^"\\])*"'), _fmt("#ce9178")),
                      (re.compile(r"'(\\.|[^'\\])*'"), _fmt("#ce9178")),
                      (re.compile(r"//[^\n]*"), _fmt("#6a9955", italic=True))]
        self.comment = _fmt("#6a9955", italic=True)

    def highlightBlock(self, text):
        for rx, fmt in self.rules:
            for m in rx.finditer(text):
                self.setFormat(m.start(), m.end() - m.start(), fmt)
        # /* ... */ over lines: state 1 = still inside
        self.setCurrentBlockState(0)
        pos = 0
        if self.previousBlockState() != 1:
            m = re.search(r"/\*", text)
            pos = m.start() if m else -1
        while pos >= 0:
            end = text.find("*/", pos + (0 if self.previousBlockState() == 1 and pos == 0 else 2))
            if end < 0:
                self.setCurrentBlockState(1)
                self.setFormat(pos, len(text) - pos, self.comment)
                break
            self.setFormat(pos, end + 2 - pos, self.comment)
            m = re.search(r"/\*", text[end + 2:])
            pos = end + 2 + m.start() if m else -1


class _Gutter(QtWidgets.QWidget):
    def __init__(self, editor):
        super().__init__(editor)
        self.editor = editor

    def sizeHint(self):
        return QtCore.QSize(self.editor.gutter_width(), 0)

    def paintEvent(self, ev):
        self.editor.paint_gutter(ev)


class CodeEditor(QtWidgets.QPlainTextEdit):
    """QPlainTextEdit with line numbers, Tab = 4 spaces (Shift+Tab takes them back), auto-indent (and a `}` lines up with its `{`), and marks for lines with errors."""
    INDENT = "    "

    def __init__(self, parent=None):
        super().__init__(parent)
        font = QtGui.QFontDatabase.systemFont(QtGui.QFontDatabase.FixedFont)
        font.setPointSize(10)
        self.setFont(font)
        self.setLineWrapMode(QtWidgets.QPlainTextEdit.NoWrap)
        self.setTabStopDistance(4 * QtGui.QFontMetricsF(font).horizontalAdvance(" "))
        self.setStyleSheet("QPlainTextEdit { background: #1e1e1e; color: #d4d4d4; selection-background-color: #264f78; }")
        self.highlighter = CSharpHighlighter(self.document())
        self.gutter = _Gutter(self)
        self.error_lines = {}                       # line (1-based) -> message
        self.blockCountChanged.connect(self._update_width)
        self.updateRequest.connect(self._scroll_gutter)
        self.cursorPositionChanged.connect(self._highlight_line)
        self._update_width()
        self._highlight_line()

    # ---- the gutter
    def gutter_width(self):
        return 14 + self.fontMetrics().horizontalAdvance("9") * max(3, len(str(self.blockCount())))

    def _update_width(self, *_):
        self.setViewportMargins(self.gutter_width(), 0, 0, 0)

    def _scroll_gutter(self, rect, dy):
        if dy:
            self.gutter.scroll(0, dy)
        else:
            self.gutter.update(0, rect.y(), self.gutter.width(), rect.height())
        if rect.contains(self.viewport().rect()):
            self._update_width()

    def resizeEvent(self, ev):
        super().resizeEvent(ev)
        cr = self.contentsRect()
        self.gutter.setGeometry(QtCore.QRect(cr.left(), cr.top(), self.gutter_width(), cr.height()))

    def paint_gutter(self, ev):
        p = QtGui.QPainter(self.gutter)
        p.fillRect(ev.rect(), QtGui.QColor("#252526"))
        block = self.firstVisibleBlock()
        n = block.blockNumber()
        top = round(self.blockBoundingGeometry(block).translated(self.contentOffset()).top())
        bottom = top + round(self.blockBoundingRect(block).height())
        while block.isValid() and top <= ev.rect().bottom():
            if block.isVisible() and bottom >= ev.rect().top():
                bad = (n + 1) in self.error_lines
                p.setPen(QtGui.QColor("#f48771" if bad else "#858585"))
                p.drawText(0, top, self.gutter.width() - 6, self.fontMetrics().height(), Qt.AlignRight, ("● " if bad else "") + str(n + 1))
            block = block.next()
            top = bottom
            bottom = top + round(self.blockBoundingRect(block).height())
            n += 1

    def _highlight_line(self):
        sels = []
        sel = QtWidgets.QTextEdit.ExtraSelection()
        sel.format.setBackground(QtGui.QColor("#2a2d2e"))
        sel.format.setProperty(QtGui.QTextFormat.FullWidthSelection, True)
        sel.cursor = self.textCursor()
        sel.cursor.clearSelection()
        sels.append(sel)
        for line, msg in self.error_lines.items():
            blk = self.document().findBlockByNumber(line - 1)
            if blk.isValid():
                e = QtWidgets.QTextEdit.ExtraSelection()
                e.format.setUnderlineStyle(QtGui.QTextCharFormat.WaveUnderline)
                e.format.setUnderlineColor(QtGui.QColor("#f48771"))
                e.cursor = QtGui.QTextCursor(blk)
                e.cursor.select(QtGui.QTextCursor.LineUnderCursor)
                e.format.setToolTip(msg)
                sels.append(e)
        self.setExtraSelections(sels)

    def mark_errors(self, errors):
        """errors: {line: message}; an empty dict clears the marks."""
        self.error_lines = dict(errors)
        self._highlight_line()
        self.gutter.update()

    def goto_line(self, line, col=1):
        blk = self.document().findBlockByNumber(max(0, line - 1))
        if blk.isValid():
            c = QtGui.QTextCursor(blk)
            c.movePosition(QtGui.QTextCursor.Right, QtGui.QTextCursor.MoveAnchor, max(0, min(col - 1, blk.length() - 1)))
            self.setTextCursor(c)
            self.centerCursor()
        self.setFocus()

    # ---- typing
    def keyPressEvent(self, ev):
        cur = self.textCursor()
        key = ev.key()
        if key == Qt.Key_Tab and not (ev.modifiers() & Qt.ShiftModifier):
            if cur.hasSelection():
                self._shift_lines(True)
            else:
                cur.insertText(self.INDENT[:len(self.INDENT) - (cur.positionInBlock() % len(self.INDENT))])
            return
        if key == Qt.Key_Backtab or (key == Qt.Key_Tab and ev.modifiers() & Qt.ShiftModifier):
            self._shift_lines(False)
            return
        if key in (Qt.Key_Return, Qt.Key_Enter):
            line = cur.block().text()
            before = line[:cur.positionInBlock()]
            indent = re.match(r"\s*", line).group(0)
            if before.rstrip().endswith("{"):
                indent += self.INDENT
            cur.insertText("\n" + indent)
            self.ensureCursorVisible()
            return
        if key == Qt.Key_BraceRight and not cur.hasSelection():
            line = cur.block().text()
            if line.strip() == "" and len(line) >= len(self.INDENT) and line.endswith(self.INDENT):
                cur.movePosition(QtGui.QTextCursor.StartOfBlock)
                cur.movePosition(QtGui.QTextCursor.Right, QtGui.QTextCursor.KeepAnchor, len(self.INDENT))
                cur.removeSelectedText()
                cur = self.textCursor()
        super().keyPressEvent(ev)

    def _shift_lines(self, right):
        cur = self.textCursor()
        doc = self.document()
        first = doc.findBlock(cur.selectionStart()).blockNumber()
        last = doc.findBlock(max(cur.selectionStart(), cur.selectionEnd() - 1)).blockNumber() if cur.hasSelection() else first
        cur.beginEditBlock()
        for n in range(first, last + 1):
            c = QtGui.QTextCursor(doc.findBlockByNumber(n))
            if right:
                c.insertText(self.INDENT)
            else:
                text = c.block().text()
                k = len(text) - len(text.lstrip(" "))
                k = min(k, len(self.INDENT))
                if k:
                    c.movePosition(QtGui.QTextCursor.Right, QtGui.QTextCursor.KeepAnchor, k)
                    c.removeSelectedText()
        cur.endEditBlock()
