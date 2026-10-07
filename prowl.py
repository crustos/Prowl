#!/usr/bin/env python3
"""prowl.py -- the Prowl2D 2D editor (PyQt5). No .NET: the engine is libprowl2d.so (the C# runtime translated to C), called through ctypes.

    python3 prowl.py [project.json]        open the editor (a demo project if none is given)
    python3 prowl.py --viewport [...]      also open the engine's SDL2 window on the first level
    python3 prowl.py --export-ascii DIR project.json     no GUI: write the project's sprites and levels as ASCII art / emoji text into DIR
    python3 prowl.py --import-ascii OUT.json [--sprites A.txt ...] [--levels B.txt ...]    no GUI: build a project from ASCII art and emoji levels
    python3 prowl.py --selftest            run the tests (the model and text formats, then the GUI windows if PyQt5 is installed)
    python3 prowl.py --screenshots DIR     draw every window to a PNG in DIR (works without a display) and exit
    python3 prowl.py --quit-after SECONDS  close the editor by itself after a while, as if the user quit (for smoke tests)

The editor is several floating windows (project / palette / sprite editor / level editor); the viewport is the engine's own window. Build the engine once:

    python3 tools/prowl2d_so.py            -> /tmp/libprowl2d.so   (set PROWL2D_LIB to use another path)

Without it everything but the viewport works. Sprites are indexed-palette pixel art (a letter per colour); levels are grids of emoji tiles, which an
exported level shows as text. See prowl_editor/asciiart.py for both formats.
"""
import argparse
import os
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, ROOT)


def load_project(path):
    from prowl_editor.model import Project
    from prowl_editor.demo import make_demo_project
    return Project.load(path) if path else make_demo_project()


def cmd_export_ascii(out_dir, path):
    from prowl_editor.fileio import export_folder
    p = load_project(path)
    n = export_folder(p, out_dir)
    print("wrote %d file(s) to %s (%d sprites, %d levels)" % (n, out_dir, len(p.sprites), len(p.levels)))
    return 0


def cmd_import_ascii(out_json, sprites, levels):
    from prowl_editor import asciiart, fileio
    from prowl_editor.model import Project
    p = Project(fileio.stem(out_json))
    for path in sprites:
        text = fileio.name_first_sprite(fileio.read_text(path), fileio.stem(path))
        made, added = asciiart.import_sprites(text, p)
        print("sprites %s from %s%s" % (", ".join(s.name for s in made), path, "  (new palette keys: %s)" % " ".join(added) if added else ""))
    for path in levels:
        made, new = asciiart.import_levels(fileio.read_text(path), p, fileio.stem(path))
        print("levels %s from %s%s" % (", ".join(l.name for l in made), path, "  (new tiles: %s)" % " ".join(t.emoji for t in new) if new else ""))
    p.save(out_json)
    print("wrote", out_json)
    return 0


def cmd_selftest():
    import unittest
    names = ["prowl_editor.test_core"]
    try:
        import PyQt5  # noqa: F401
        os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
        names.append("prowl_editor.test_gui")
        names.append("prowl_editor.test_engine")
    except ImportError:
        print("(PyQt5 is not installed: only the model and text-format tests run)")
    suite = unittest.defaultTestLoader.loadTestsFromNames(names)
    return 0 if unittest.TextTestRunner(verbosity=1).run(suite).wasSuccessful() else 1


def make_app(argv):
    from PyQt5 import QtWidgets
    from prowl_editor import gui
    app = QtWidgets.QApplication(argv)
    return app, gui


def cmd_screenshots(out_dir, project_path):
    if not os.environ.get("DISPLAY") and not os.environ.get("WAYLAND_DISPLAY"):
        os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
    app, gui = make_app(sys.argv[:1])
    studio = gui.Studio(load_project(project_path))
    main, windows = gui.create_windows(studio)
    os.makedirs(out_dir, exist_ok=True)
    shots = [("project", main)] + [(k, w) for k, w in windows.items()]
    for _, w in shots:
        w.show()
    studio.select_tile(next(iter(studio.project.tiles)))
    for _ in range(5):
        app.processEvents()
    for name, w in shots:
        path = os.path.join(out_dir, "%s.png" % name)
        w.grab().save(path)
        print("wrote", path)
    # the level editor again, as emoji
    windows["levels"].canvas.view = "emoji"
    windows["levels"].canvas.update()
    app.processEvents()
    path = os.path.join(out_dir, "levels_emoji.png")
    windows["levels"].grab().save(path)
    print("wrote", path)
    return 0


def cmd_gui(project_path, open_viewport, quit_after=None):
    app, gui = make_app(sys.argv[:1])
    studio = gui.Studio(load_project(project_path))
    main, windows = gui.create_windows(studio)
    gui.tile_windows(main, windows)
    main.show()
    for w in windows.values():
        w.show()
    from PyQt5 import QtCore
    if open_viewport:
        QtCore.QTimer.singleShot(200, main.viewport.open)
    if quit_after:
        QtCore.QTimer.singleShot(int(quit_after * 1000), main.close)
    return app.exec_()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("project", nargs="?", help="a project .json (default: the demo project)")
    ap.add_argument("--viewport", action="store_true", help="also open the engine's SDL2 window")
    ap.add_argument("--export-ascii", metavar="DIR", help="no GUI: write sprites/ and levels/ text files of the project into DIR")
    ap.add_argument("--import-ascii", metavar="OUT.json", help="no GUI: build a project from --sprites and --levels text files")
    ap.add_argument("--sprites", nargs="*", default=[], metavar="FILE.txt")
    ap.add_argument("--levels", nargs="*", default=[], metavar="FILE.txt")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--screenshots", metavar="DIR")
    ap.add_argument("--quit-after", type=float, metavar="SECONDS", help=argparse.SUPPRESS)
    a = ap.parse_args()
    try:
        if a.selftest:
            return cmd_selftest()
        if a.export_ascii:
            return cmd_export_ascii(a.export_ascii, a.project)
        if a.import_ascii:
            return cmd_import_ascii(a.import_ascii, a.sprites, a.levels)
        if a.screenshots:
            return cmd_screenshots(a.screenshots, a.project)
        return cmd_gui(a.project, a.viewport, a.quit_after)
    except Exception as e:                        # a bad file is a message, not a traceback
        from prowl_editor.model import ProjectError
        if isinstance(e, (ProjectError, OSError)):
            print("prowl.py: %s" % e, file=sys.stderr)
            return 1
        raise


if __name__ == "__main__":
    sys.exit(main())
