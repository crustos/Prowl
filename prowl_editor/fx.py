"""Effects from Python: turns an effect's name and the values of its parameters into what gfx_effect takes (an id and the float block).

The effects, their parameters, ranges and defaults are written once in Native/Gfx2D/fx/*.fx and generated into fxdefs.py (tools/gfx_fx_gen.py); this module only
packs values into that table's places, so the editor can build its controls from fxdefs.EFFECTS and pass what they hold to Engine.effect.
"""
import math

from .fxdefs import EFFECTS, PARAM_FLOATS


def _clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v


def _floats(p, value):
    """The floats one parameter's value is stored as: a number for a float, int, bool or enum (an enum may also be given by the label of its option), four
    numbers r, g, b, a for a color (three is fine: the alpha is then 1). Numbers outside the parameter's range are brought into it; a NaN is refused."""
    t, lo, hi = p["type"], p["min"], p["max"]
    if t == "color":
        v = [float(x) for x in value]
        if len(v) == 3:
            v.append(1.0)
        if len(v) != 4:
            raise ValueError("%s: a color is r, g, b (and a): %r" % (p["name"], value))
        if any(math.isnan(x) for x in v):
            raise ValueError("%s: not a number: %r" % (p["name"], value))
        return [_clamp(x, 0.0, 1.0) for x in v]
    if t == "enum" and isinstance(value, str):
        if value not in p["options"]:
            raise ValueError("%s: %r is not one of %s" % (p["name"], value, ", ".join(p["options"])))
        value = p["options"].index(value)
    v = float(value)
    if math.isnan(v):
        raise ValueError("%s: not a number" % p["name"])
    if t in ("int", "enum"):
        v = float(round(v))
    elif t == "bool":
        v = 1.0 if v else 0.0
    return [_clamp(v, lo, hi)]


def pack(name, values=None):
    """(effect id, the PARAM_FLOATS floats) for the effect `name` and a dict {parameter: value}; a parameter that is not given has its default.
    Raises KeyError for an effect or a parameter the registry does not have, ValueError for a value that cannot be stored."""
    if name not in EFFECTS:
        raise KeyError("no effect %r (the registry has: %s)" % (name, ", ".join(sorted(EFFECTS))))
    e = EFFECTS[name]
    values = dict(values or {})
    out = [0.0] * PARAM_FLOATS
    for p in e["params"]:
        floats = _floats(p, values.pop(p["name"])) if p["name"] in values else list(p["default"])
        out[p["offset"]:p["offset"] + p["size"]] = floats
    if values:
        raise KeyError("effect %r has no parameter %s (it has: %s)" % (name, ", ".join(sorted(values)), ", ".join(p["name"] for p in e["params"])))
    return e["id"], out


def defaults(name):
    """The default value of each parameter of the effect `name`, as pack() takes them."""
    return {p["name"]: (p["default"] if p["type"] == "color" else p["default"][0]) for p in EFFECTS[name]["params"]}
