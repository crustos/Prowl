"""fileio.py: reading and writing the text formats as files (no Qt, so the command line can use it too)."""
import os
import re

from . import asciiart
from .model import slug

_SPRITE_HEADER = re.compile(r"^#\s*(sprite|name)\s*:", re.IGNORECASE | re.MULTILINE)


def stem(path):
    return os.path.splitext(os.path.basename(path))[0]


def name_first_sprite(text, base):
    """A file of plain art with no `# sprite:` line is named after the file."""
    if _SPRITE_HEADER.search(text):
        return text
    return "# sprite: %s\n%s" % (base, text)


def read_text(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def export_folder(project, folder):
    """Writes every sprite to sprites/<name>.txt and every level to levels/<name>.txt under `folder`. Returns how many files."""
    n = 0
    groups = (("sprites", project.sprites, lambda s: asciiart.export_sprite(s, project.palette)),
              ("levels", project.levels, lambda l: asciiart.export_level(l, project)))
    for sub, items, render in groups:
        if not items:
            continue
        os.makedirs(os.path.join(folder, sub), exist_ok=True)
        for it in items:
            with open(os.path.join(folder, sub, slug(it.name) + ".txt"), "w", encoding="utf-8", newline="\n") as f:
                f.write(render(it))
            n += 1
    return n
