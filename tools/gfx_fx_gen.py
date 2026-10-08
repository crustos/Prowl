#!/usr/bin/env python3
"""
gfx_fx_gen.py - the effect registry of gfx2d: Native/Gfx2D/fx/*.fx  ->  the code every backend runs, and the table the editor builds its controls from.

  python3 tools/gfx_fx_gen.py             write the generated files (only those whose text changes)
  python3 tools/gfx_fx_gen.py --check     exit 1 if a generated file is not what the registry makes (nothing is written)
  python3 tools/gfx_fx_gen.py --selftest  check the parser, the parameter layout and the refusals

An EFFECT is one pass over the picture (gfx_effect in gfx2d.h). It is written ONCE, here, in the three languages the backends need, so the three cannot drift
apart unnoticed (the tests draw each effect on every backend and compare):

  fx/NAME.fx
      id: 3                          the number gfx_effect takes. Written down, never renumbered: a project may keep it. 1..255, unique.
      name: my_effect                the name a project file keeps. lower case, digits and _, unique.
      title: My effect               what the editor shows.
      group: Color                   (optional) the editor's menu.
      param: TYPE NAME [key=value ...]
          TYPE   float | int | bool | color (4 floats r, g, b, a) | enum (options=A,B,C: the value is the index)
          keys   min= max= default= (a color's default is r,g,b,a with commas) label="Shown name" options=A,B,C
      --- glsl                       the BODY of `vec4 c` -> `vec4 c`, GLSL ES 3.00. `c` is the pixel (straight alpha, 0..1; leave c.a alone: the
      --- wgsl                       picture is opaque), `uv` is its position over the picture (0..1, y from the TOP). Each parameter is a local of its
      --- c                          own name. The C body is the CPU reference (the CPU reference: tools/gfx_fx_ref.c): `float* c` (4 floats), `u`, `v`, the same locals.

The pixel is whatever the picture holds when the effect runs (inside the current clip); the result is clamped to 0..1 when it is stored, so a body need not.
A parameter's floats are packed in order into GFX_FX_PARAMS (16) floats, a color on a multiple of 4 (so a GPU reads it as one vec4). That packing is what
gfx_effect takes; this tool prints it into the editor's table (prowl_editor/fxdefs.py) so nothing else has to know it.

What it writes:
  Native/Gfx2D/gfx2d_fx_gen.h        the CPU reference and the GLSL for the C backends, the defaults for the core
  Native/Gfx2D/gfx2d.h               the id constants, between its <fx-ids> markers (so the C# bindings carry them)
  Native/Gfx2D/web/prowl_web.js   the GLSL and WGSL for the page, between its <fx-generated> markers
  prowl_editor/fxdefs.py              the schema, as JSON, for the PyQt editor
"""
import json
import os
import re
import shlex
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(ROOT, "Native", "Gfx2D")
FX_DIR = os.path.join(SRC, "fx")
OUT_HEADER = os.path.join(SRC, "gfx2d_fx_gen.h")
OUT_API = os.path.join(SRC, "gfx2d.h")
OUT_JS = os.path.join(SRC, "web", "prowl_web.js")
OUT_PY = os.path.join(ROOT, "prowl_editor", "fxdefs.py")

PARAM_FLOATS = 128
TYPES = ("float", "int", "bool", "color", "enum")
COMPONENTS = "xyzw"
# names a parameter may not take: it becomes a local of the same name in three languages, so it must not shadow a function or a word of any of them
RESERVED = set("""c uv u v px py pw ph p fx fxu u_p u_src u_size frag main
if else for while do return break continue switch case default discard true false void const var let fn struct in out inout uniform
float int uint bool vec2 vec3 vec4 mat2 mat3 mat4 vec2f vec3f vec4f f32 i32 u32 array
min max mix clamp abs sin cos tan asin acos atan atan2 pow exp exp2 log log2 sqrt floor ceil fract step smoothstep length dot cross normalize sign mod select
sinf cosf powf floorf fabsf sqrtf""".split())
NAME_RE = re.compile(r"^[a-z][a-z0-9_]*$")
PARAM_RE = re.compile(r"^[A-Za-z][A-Za-z0-9]*$")


class FxError(Exception):
    pass


# ---- parsing -------------------------------------------------------------------------------------------------------------------------------

def _num(text, where):
    try:
        return float(text)
    except ValueError:
        raise FxError("%s: %r is not a number" % (where, text))


def parse_fx(text, where="fx"):
    """One .fx file's text -> {"id", "name", "title", "group", "params": [...], "glsl", "wgsl", "c"}; raises FxError."""
    head, bodies, section = [], {}, None
    for line in text.splitlines():
        m = re.match(r"^---\s*(\w+)\s*$", line)
        if m:
            section = m.group(1)
            if section not in ("glsl", "wgsl", "c", "glsl_lib", "wgsl_lib", "c_lib"):
                raise FxError("%s: unknown section '--- %s' (glsl, wgsl, c, and optionally glsl_lib, wgsl_lib, c_lib: helper functions before the effect)" % (where, section))
            if section in bodies:
                raise FxError("%s: section '--- %s' twice" % (where, section))
            bodies[section] = []
        elif section is None:
            head.append(line)
        else:
            bodies[section].append(line)
    for s in ("glsl", "wgsl", "c"):
        if s not in bodies:
            raise FxError("%s: missing section '--- %s'" % (where, s))
    fx = {"group": "", "params": []}
    for line in head:
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        key, _, value = line.partition(":")
        key, value = key.strip(), value.strip()
        if key == "id":
            try:
                fx["id"] = int(value)
            except ValueError:
                raise FxError("%s: id %r is not a whole number" % (where, value))
        elif key in ("name", "title", "group"):
            fx[key] = value
        elif key == "hidden":                    # an effect an editor panel of its own drives: the generic effect list does not offer it
            fx["hidden"] = value.lower() in ("true", "yes", "1")
        elif key == "param":
            fx["params"].append(_parse_param(value, where))
        else:
            raise FxError("%s: unknown key %r" % (where, key))
    for need in ("id", "name", "title"):
        if need not in fx:
            raise FxError("%s: no '%s:' line" % (where, need))
    if not 1 <= fx["id"] <= 255:
        raise FxError("%s: id %d is outside 1..255" % (where, fx["id"]))
    if not NAME_RE.match(fx["name"]):
        raise FxError("%s: name %r must be lower case letters, digits and _ (starting with a letter)" % (where, fx["name"]))
    for s, lines in bodies.items():
        body = "\n".join(lines).strip("\n")
        if not body.strip():
            raise FxError("%s: section '--- %s' is empty" % (where, s))
        fx[s] = body
    for s in ("glsl_lib", "wgsl_lib", "c_lib"):
        fx.setdefault(s, "")
    _layout(fx, where)
    return fx


def _parse_param(value, where):
    try:
        tok = shlex.split(value)
    except ValueError as e:
        raise FxError("%s: param %r: %s" % (where, value, e))
    if len(tok) < 2:
        raise FxError("%s: param needs a type and a name: %r" % (where, value))
    typ, name, opts = tok[0], tok[1], {}
    where = "%s: param %s" % (where, name)
    if typ not in TYPES:
        raise FxError("%s: type %r is not one of %s" % (where, typ, ", ".join(TYPES)))
    if not PARAM_RE.match(name) or name in RESERVED:
        raise FxError("%s: %r is not a usable name (letters and digits, and not a word of GLSL, WGSL or C)" % (where, name))
    for t in tok[2:]:
        k, eq, v = t.partition("=")
        if not eq or k not in ("min", "max", "default", "label", "options"):
            raise FxError("%s: %r is not key=value with key min, max, default, label or options" % (where, t))
        opts[k] = v
    p = {"name": name, "type": typ, "label": opts.get("label", name)}
    if typ == "enum":
        p["options"] = [o for o in opts.get("options", "").split(",") if o]
        if len(p["options"]) < 2:
            raise FxError("%s: an enum needs options=A,B,... (two or more)" % where)
    elif "options" in opts:
        raise FxError("%s: only an enum has options" % where)
    if typ == "color":
        d = [_num(x, where) for x in opts["default"].split(",")] if "default" in opts else [0.0, 0.0, 0.0, 1.0]
        if len(d) != 4:
            raise FxError("%s: a color's default is r,g,b,a (four numbers)" % where)
        p.update(size=4, min=0.0, max=1.0, default=d)
        if "min" in opts or "max" in opts:
            raise FxError("%s: a color has no min or max (each channel is 0..1)" % where)
    else:
        lo, hi = {"float": (0.0, 1.0), "int": (0.0, 100.0), "bool": (0.0, 1.0), "enum": (0.0, float(len(p.get("options", [])) - 1))}[typ]
        lo, hi = _num(opts["min"], where) if "min" in opts else lo, _num(opts["max"], where) if "max" in opts else hi
        if typ == "bool":
            d = {"true": 1.0, "false": 0.0}.get(opts.get("default", "false"))
            if d is None:
                d = _num(opts["default"], where)
        else:
            d = _num(opts["default"], where) if "default" in opts else min(max(0.0, lo), hi)
        if lo > hi:
            raise FxError("%s: min %g is above max %g" % (where, lo, hi))
        if not lo <= d <= hi:
            raise FxError("%s: default %g is outside %g..%g" % (where, d, lo, hi))
        if typ in ("int", "enum", "bool") and (d != int(d) or lo != int(lo) or hi != int(hi)):
            raise FxError("%s: an %s takes whole numbers" % (where, typ))
        p.update(size=1, min=lo, max=hi, default=[d])
    return p


def _layout(fx, where):
    """Packs the parameters into the float block (a color on a multiple of 4) and records each one's offset."""
    at, seen = 0, set()
    for p in fx["params"]:
        if p["name"] in seen:
            raise FxError("%s: parameter %s twice" % (where, p["name"]))
        seen.add(p["name"])
        if p["type"] == "color":
            at = (at + 3) // 4 * 4
        p["offset"] = at
        at += p["size"]
    if at > PARAM_FLOATS:
        raise FxError("%s: the parameters need %d floats, a gfx_effect holds %d" % (where, at, PARAM_FLOATS))
    fx["nparams"] = at


def load_registry(directory=FX_DIR):
    """Every fx/*.fx, in id order. Raises FxError for a bad file, or two with one id or name."""
    fxs, ids, names = [], {}, {}
    if not os.path.isdir(directory):
        raise FxError("no registry folder: " + directory)
    for f in sorted(os.listdir(directory)):
        if not f.endswith(".fx"):
            continue
        with open(os.path.join(directory, f), encoding="utf-8") as h:
            fx = parse_fx(h.read(), f)
        fx["file"] = f
        if fx["name"] != f[:-3]:
            raise FxError("%s: name %r must match the file name" % (f, fx["name"]))
        if fx["id"] in ids:
            raise FxError("%s: id %d is also used by %s" % (f, fx["id"], ids[fx["id"]]))
        ids[fx["id"]] = f
        names[fx["name"]] = f
        fxs.append(fx)
    return sorted(fxs, key=lambda x: x["id"])


# ---- the three languages ---------------------------------------------------------------------------------------------------------------------

def _slot(p, n):
    return "%d" % (p["offset"] // 4), COMPONENTS[p["offset"] % 4]


def _glsl_locals(fx):
    out = []
    for p in fx["params"]:
        s, k = _slot(p, p["offset"])
        if p["type"] == "float":
            out.append("  float %s = u_p[%s].%s;" % (p["name"], s, k))
        elif p["type"] in ("int", "enum"):
            out.append("  int %s = int(floor(u_p[%s].%s + 0.5));" % (p["name"], s, k))
        elif p["type"] == "bool":
            out.append("  bool %s = u_p[%s].%s > 0.5;" % (p["name"], s, k))
        else:
            out.append("  vec4 %s = u_p[%s];" % (p["name"], s))
    return out


def _wgsl_locals(fx):
    out = []
    for p in fx["params"]:
        s, k = _slot(p, p["offset"])
        if p["type"] == "float":
            out.append("  let %s = fxu.p[%s].%s;" % (p["name"], s, k))
        elif p["type"] in ("int", "enum"):
            out.append("  let %s = i32(floor(fxu.p[%s].%s + 0.5));" % (p["name"], s, k))
        elif p["type"] == "bool":
            out.append("  let %s = fxu.p[%s].%s > 0.5;" % (p["name"], s, k))
        else:
            out.append("  let %s = fxu.p[%s];" % (p["name"], s))
    return out


def _c_locals(fx):
    out = []
    for p in fx["params"]:
        o = p["offset"]
        if p["type"] == "float":
            out.append("\tfloat %s = p[%d];" % (p["name"], o))
        elif p["type"] in ("int", "enum"):
            out.append("\tint %s = (int)floorf( p[%d] + 0.5f );" % (p["name"], o))
        elif p["type"] == "bool":
            out.append("\tint %s = p[%d] > 0.5f;" % (p["name"], o))
        else:
            out.append("\tconst float* %s = &p[%d];" % (p["name"], o))
        out.append("\t(void)%s;" % p["name"])
    return out


def _indent(body, pad):
    return "\n".join((pad + l) if l.strip() else "" for l in body.splitlines())


def glsl_source(fx):
    """The whole fragment shader (GLSL ES 3.00) of an effect. Its vertex shader is VS (one big triangle)."""
    return "\n".join([
        "#version 300 es",
        "precision highp float;",
        "precision highp int;",
        "uniform sampler2D u_src;",
        "uniform vec4 u_p[%d];" % (PARAM_FLOATS // 4),
        "uniform vec4 u_size;",
        "layout(location = 0) out vec4 frag;",
        GLSL_PRELUDE,
        fx["glsl_lib"],
        "vec4 fx(vec4 c, vec2 uv, float px, float py, float pw, float ph) {",
    ] + _glsl_locals(fx) + [_indent(fx["glsl"], "  "), "  return c;", "}",
        "void main() {",
        "  vec4 c = texelFetch(u_src, ivec2(gl_FragCoord.xy), 0);",
        "  float py = u_size.y - gl_FragCoord.y;",
        "  frag = fx(c, vec2(gl_FragCoord.x, py) / u_size.xy, gl_FragCoord.x, py, u_size.x, u_size.y);",
        "}", ""])


# helpers every effect's shader may use (the C side of them is in gfx2d_soft.c): the OpenToonz gradient curves, and "a (premultiplied) color over the picture"
GLSL_PRELUDE = """float fxc_curve(int ty, float t) {
  if (ty == 1) return t;
  if (ty == 2) return t * t;
  if (ty == 3) return 1.0 - (1.0 - t) * (1.0 - t);
  return (-2.0 * t + 3.0) * (t * t);
}
vec4 fxc_over_pm(vec4 c, vec3 pm, float a, float op) { return vec4(pm * op + c.rgb * (1.0 - a * op), c.a); }
vec4 fxc_over(vec4 c, vec4 c1, vec4 c2, float f, float op) {
  return fxc_over_pm(c, c1.rgb * c1.a * (1.0 - f) + c2.rgb * c2.a * f, c1.a * (1.0 - f) + c2.a * f, op);
}"""

WGSL_PRELUDE = """fn fxc_curve(ty: i32, t: f32) -> f32 {
  if (ty == 1) { return t; }
  if (ty == 2) { return t * t; }
  if (ty == 3) { return 1.0 - (1.0 - t) * (1.0 - t); }
  return (-2.0 * t + 3.0) * (t * t);
}
fn fxc_over_pm(c: vec4f, pm: vec3f, a: f32, op: f32) -> vec4f { return vec4f(pm * op + c.rgb * (1.0 - a * op), c.a); }
fn fxc_over(c: vec4f, c1: vec4f, c2: vec4f, f: f32, op: f32) -> vec4f {
  return fxc_over_pm(c, c1.rgb * c1.a * (1.0 - f) + c2.rgb * c2.a * f, c1.a * (1.0 - f) + c2.a * f, op);
}"""

GLSL_VS = "\n".join(["#version 300 es", "void main() {",
                     "  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));",
                     "  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);", "}", ""])


def wgsl_source(fx):
    return "\n".join([
        "struct FxU { p: array<vec4f, %d>, size: vec4f };" % (PARAM_FLOATS // 4),
        "@group(0) @binding(0) var<uniform> fxu: FxU;",
        "@group(0) @binding(1) var src: texture_2d<f32>;",
        WGSL_PRELUDE,
        fx["wgsl_lib"],
        "fn fx(c_in: vec4f, uv: vec2f, px: f32, py: f32, pw: f32, ph: f32) -> vec4f {",
        "  var c = c_in;",
    ] + _wgsl_locals(fx) + [_indent(fx["wgsl"], "  "), "  return c;", "}",
        "@vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4f {",
        "  let p = vec2f(f32((i << 1u) & 2u), f32(i & 2u));",
        "  return vec4f(p * 2.0 - 1.0, 0.0, 1.0);",
        "}",
        "@fragment fn fs(@builtin(position) pos: vec4f) -> @location(0) vec4f {",
        "  let c = textureLoad(src, vec2i(pos.xy), 0);",
        "  return fx(c, pos.xy / fxu.size.xy, pos.x, pos.y, fxu.size.x, fxu.size.y);",
        "}", ""])


def c_function(fx):
    return "\n".join([
        "// %s" % fx["file"] if "file" in fx else "// %s" % fx["name"],
        fx["c_lib"],
        "static void gfx_fx_soft_%s( const float* p, float* c, float u, float v, float px, float py, float pw, float ph )" % fx["name"], "{"] + _c_locals(fx) + [
        "\t(void)p;", "\t(void)u;", "\t(void)v;", "\t(void)px;", "\t(void)py;", "\t(void)pw;", "\t(void)ph;", _indent(fx["c"], "\t"), "}", ""])


# ---- the outputs ---------------------------------------------------------------------------------------------------------------------------------

def _cf(x):
    s = "%.9g" % x
    if not any(ch in s for ch in ".enif"):
        s += ".0"
    return s + "f"


def _cstr(text):
    return "\n".join('\t"%s\\n"' % l.replace("\\", "\\\\").replace('"', '\\"') for l in text.rstrip("\n").split("\n"))


def render_header(fxs):
    o = ["// gfx2d_fx_gen.h: GENERATED by tools/gfx_fx_gen.py from Native/Gfx2D/fx/*.fx. Do not edit: change the .fx and run the tool.",
         "//",
         "// Include it with the parts you want defined first: GFX_FX_WANT_INFO (the core: names and defaults), GFX_FX_WANT_SOFT (the CPU reference),",
         "// GFX_FX_WANT_GLSL (the shaders of the GL backend).", "#ifndef PROWL2D_GFX2D_FX_GEN_H", "#define PROWL2D_GFX2D_FX_GEN_H", "",
         '#include "gfx2d.h"', ""]
    top = max(f["id"] for f in fxs) if fxs else 0
    o += ["#if GFX_FX_PARAMS != %d" % PARAM_FLOATS, '#error "GFX_FX_PARAMS in gfx2d.h is not PARAM_FLOATS of tools/gfx_fx_gen.py"', "#endif", ""]
    o += ["#if GFX_FX_ID_MAX != %d" % top, '#error "gfx2d.h and gfx2d_fx_gen.h are from different registries: run tools/gfx_fx_gen.py"', "#endif", ""]
    o += ["#ifdef GFX_FX_WANT_INFO", "typedef struct GfxFxInfo", "{", "\tconst char* name; // NULL: no effect has this id", "\tint nparams;",
          "\tfloat defaults[GFX_FX_PARAMS];", "} GfxFxInfo;", "static const GfxFxInfo gfx_fx_info[GFX_FX_ID_MAX + 1] = {", "\t{ NULL, 0, { 0 } },"]
    by_id = {f["id"]: f for f in fxs}
    for i in range(1, top + 1):
        f = by_id.get(i)
        if f is None:
            o.append("\t{ NULL, 0, { 0 } },")
            continue
        d = [0.0] * PARAM_FLOATS
        for p in f["params"]:
            for k, v in enumerate(p["default"]):
                d[p["offset"] + k] = v
        o.append('\t{ "%s", %d, { %s } }, // %d' % (f["name"], f["nparams"], ", ".join(_cf(v) for v in d), i))
    o += ["};", "#endif", ""]
    o += ["#ifdef GFX_FX_WANT_SOFT", "#include <math.h>", "#include <stddef.h>", "", "// the CPU reference: `c` is the pixel (r, g, b, a in 0..1), u and v its place over the picture (v from the TOP)", ""]
    for f in fxs:
        o.append(c_function(f))
    o += ["typedef void ( *GfxFxSoftFn )( const float* p, float* c, float u, float v, float px, float py, float pw, float ph );",
          "static const GfxFxSoftFn gfx_fx_soft[GFX_FX_ID_MAX + 1] = {", "\tNULL,"]
    for i in range(1, top + 1):
        o.append("\t%s," % ("gfx_fx_soft_" + by_id[i]["name"] if i in by_id else "NULL"))
    o += ["};", "#endif", ""]
    o += ["#ifdef GFX_FX_WANT_GLSL", "static const char* const GFX_FX_VS =", _cstr(GLSL_VS) + ";", "static const char* const gfx_fx_glsl[GFX_FX_ID_MAX + 1] = {", "\tNULL,"]
    for i in range(1, top + 1):
        if i in by_id:
            o += ["\t// " + by_id[i]["name"], _cstr(glsl_source(by_id[i])) + ","]
        else:
            o.append("\tNULL,")
    o += ["};", "#endif", "", "#endif", ""]
    return "\n".join(o)


def render_ids(fxs):
    o = ["// <fx-ids> GENERATED by tools/gfx_fx_gen.py from fx/*.fx (the id of each effect for gfx_effect): do not edit"]
    for f in fxs:
        o.append("#define GFX_FX_%s %d // %s" % (f["name"].upper(), f["id"], f["title"]))
    o.append("#define GFX_FX_ID_MAX %d" % (max(f["id"] for f in fxs) if fxs else 0))
    o.append("// </fx-ids>")
    return "\n".join(o)


def _js_tpl(s):
    return "`" + s.rstrip("\n").replace("\\", "\\\\").replace("`", "\\`").replace("${", "\\${") + "`"


def render_js(fxs):
    o = ["// <fx-generated> GENERATED by tools/gfx_fx_gen.py from Native/Gfx2D/fx/*.fx: do not edit (change the .fx and run the tool)",
         "const FX_PARAM_FLOATS = %d;" % PARAM_FLOATS, "const FX_VS = %s;" % _js_tpl(GLSL_VS), "const FX = {"]
    for f in fxs:
        o += ["  %d: { name: \"%s\"," % (f["id"], f["name"]), "    glsl: %s," % _js_tpl(glsl_source(f)), "    wgsl: %s }," % _js_tpl(wgsl_source(f))]
    o += ["};", "// </fx-generated>"]
    return "\n".join(o)


def render_py(fxs):
    reg = {}
    for f in fxs:
        reg[f["name"]] = {"id": f["id"], "title": f["title"], "group": f["group"], "hidden": bool(f.get("hidden")), "nparams": f["nparams"],
                          "params": [{k: p[k] for k in ("name", "type", "label", "offset", "size", "min", "max", "default", "options") if k in p} for p in f["params"]]}
    return ('"""fxdefs.py: GENERATED by tools/gfx_fx_gen.py from Native/Gfx2D/fx/*.fx. Do not edit: change the .fx and run the tool.\n\n'
            'EFFECTS maps an effect\'s name to its id (what gfx_effect takes), its title, its menu group and its parameters; each parameter says its type, its place in\n'
            'the float block (offset, size), its range, its default (a list: four numbers for a color) and, for an enum, its options."""\n'
            "import json\n\nPARAM_FLOATS = %d\n\nEFFECTS = json.loads(r'''\n%s\n''')\n\nBY_ID = {e[\"id\"]: n for n, e in EFFECTS.items()}\n"
            % (PARAM_FLOATS, json.dumps(reg, indent=1, sort_keys=True)))


def splice(text, begin, end, block, where):
    """Replaces the lines from the one holding `begin` to the one holding `end` (both included) by `block`."""
    a, b = text.find(begin), text.find(end)
    if a < 0 or b < a:
        raise FxError("%s has no %s ... %s markers" % (where, begin, end))
    a = text.rfind("\n", 0, a) + 1
    b = text.find("\n", b)
    b = len(text) if b < 0 else b
    return text[:a] + block + text[b:]


def outputs(fxs):
    """{path: new text} for every generated file."""
    res = {OUT_HEADER: render_header(fxs), OUT_PY: render_py(fxs)}
    for path, begin, end, block in ((OUT_API, "<fx-ids>", "</fx-ids>", render_ids(fxs)), (OUT_JS, "<fx-generated>", "</fx-generated>", render_js(fxs))):
        with open(path, encoding="utf-8") as h:
            res[path] = splice(h.read(), begin, end, block, os.path.relpath(path, ROOT))
    return res


def generate(write=True):
    """Writes (or, with write=False, only compares) the generated files. Returns the paths that differ from the registry."""
    changed = []
    for path, text in outputs(load_registry()).items():
        old = None
        if os.path.exists(path):
            with open(path, encoding="utf-8", newline="") as h:
                old = h.read()
        if old != text:
            changed.append(path)
            if write:
                with open(path, "w", encoding="utf-8", newline="") as h:
                    h.write(text)
    return changed


# ---- self test -----------------------------------------------------------------------------------------------------------------------------------

_OK = """id: 9
name: demo
title: Demo
param: float a min=0 max=2 default=1
param: color tone default=0.1,0.2,0.3,1
param: int steps min=1 max=8 default=4
param: bool flip
param: enum mode options=Add,Mul,Screen default=2
--- glsl
c.rgb = c.rgb * a;
--- wgsl
c = vec4f(c.rgb * a, c.a);
--- c
c[0] *= a;
"""


def selftest():
    bad = []

    def expect(cond, what):
        if not cond:
            bad.append(what)

    fx = parse_fx(_OK, "ok.fx")
    offs = [(p["name"], p["offset"]) for p in fx["params"]]
    expect(offs == [("a", 0), ("tone", 4), ("steps", 8), ("flip", 9), ("mode", 10)], "layout: a color starts on a multiple of 4, the rest pack: %s" % offs)
    expect(fx["nparams"] == 11, "nparams %d" % fx["nparams"])
    expect(fx["params"][1]["default"] == [0.1, 0.2, 0.3, 1.0] and fx["params"][4]["default"] == [2.0], "defaults")
    g, w, c = glsl_source(fx), wgsl_source(fx), c_function(dict(fx, file="ok.fx"))
    expect("vec4 tone = u_p[1];" in g and "int mode = int(floor(u_p[2].z + 0.5));" in g and "bool flip = u_p[2].y > 0.5;" in g, "glsl locals")
    expect("let tone = fxu.p[1];" in w and "let mode = i32(floor(fxu.p[2].z + 0.5));" in w, "wgsl locals")
    expect("const float* tone = &p[4];" in c and "int mode = (int)floorf( p[10] + 0.5f );" in c, "c locals")
    refused = {
        "a missing section": _OK.replace("--- wgsl\nc = vec4f(c.rgb * a, c.a);\n", ""),
        "a reserved name": _OK.replace("param: float a ", "param: float mix "),
        "a default outside its range": _OK.replace("default=1\n", "default=3\n"),
        "an unknown type": _OK.replace("param: bool flip", "param: matrix flip"),
        "a bad id": _OK.replace("id: 9", "id: 300"),
        "too many parameters": _OK.replace("param: bool flip", "".join("param: float x%d\n" % i for i in range(PARAM_FLOATS))),
        "a name with capitals": _OK.replace("name: demo", "name: Demo"),
        "a fractional int": _OK.replace("param: int steps min=1 max=8 default=4", "param: int steps min=1 max=8 default=2.5"),
    }
    for what, text in refused.items():
        try:
            parse_fx(text, "bad.fx")
            bad.append("accepted " + what)
        except FxError:
            pass
    try:
        reg = load_registry()
        expect(len({f["id"] for f in reg}) == len(reg) and reg, "the real registry loads")
    except FxError as e:
        bad.append("the real registry: %s" % e)
    for b in bad:
        print("selftest FAIL: " + b)
    if not bad:
        print("selftest ok")
    return not bad


def main(argv):
    try:
        if "--selftest" in argv:
            return 0 if selftest() else 1
        changed = generate(write="--check" not in argv)
    except FxError as e:
        print("gfx_fx_gen: %s" % e, file=sys.stderr)
        return 1
    rel = [os.path.relpath(p, ROOT) for p in changed]
    if "--check" in argv:
        for r in rel:
            print("out of date: " + r)
        return 1 if rel else 0
    for r in rel:
        print("wrote " + r)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
