"""demo.py: the project a new editor opens with: three sprites (one animated), three tiles and a level. It is made through the importers, so it is also a
worked example of the two text formats."""
from .asciiart import import_levels, import_sprites
from .model import Project, TileDef

SPRITES = """\
# sprite: brick
SSSSSSSS
RRRRSRRR
RRRRSRRR
SSSSSSSS
RSRRRRRR
RSRRRRRR
SSSSSSSS
RRRRSRRR

# sprite: grass
LGLGGLGL
GGGGGGGG
NNGNNNGN
NNNNNNNN
NTNNNNTN
NNNNNNNN
NNNTNNNN
NNNNNNNN

# sprite: coin
# fps: 6
..YYYY..
.YOOOOY.
YOOYYOOY
YOOYYOOY
YOOYYOOY
YOOYYOOY
.YOOOOY.
..YYYY..

...YY...
..YOOY..
.YOOYOY.
.YOOYOY.
.YOOYOY.
.YOOYOY.
..YOOY..
...YY...

...YY...
...YY...
...OY...
...OY...
...OY...
...OY...
...YY...
...YY...

...YY...
..YOOY..
.YOOYOY.
.YOOYOY.
.YOOYOY.
.YOOYOY.
..YOOY..
...YY...
"""

# the level, drawn as plain ASCII and imported with a legend (so it also shows that ASCII levels work)
LEVEL = """\
# level: first steps
# empty: .
# # = brick (solid)
# g = grass (solid)
# o = coin
........................
........................
........................
..........ooo...........
.........#####..........
........................
...oo...........ooo.....
..####.........#####....
........................
........................
gggggggggggggggggggggggg
gggggggggggggggggggggggg
"""


def make_demo_project():
    p = Project("demo")
    import_sprites(SPRITES, p)
    import_levels(LEVEL, p)
    # the level was drawn in ASCII; make its tiles the emoji a person would expect (the names, and so the sprites, stay)
    swap = {"#": "\U0001f9f1", "g": "\U0001f7e9", "o": "\U0001fa99"}       # brick, green square, coin
    lv = p.levels[0]
    lv.cells = [swap.get(c, c) for c in lv.cells]
    p.tiles = {swap.get(k, k): TileDef(swap.get(k, k), t.name, t.sprite, t.solid) for k, t in p.tiles.items()}
    p.empty = "⬛"
    p.dirty = False
    return p
