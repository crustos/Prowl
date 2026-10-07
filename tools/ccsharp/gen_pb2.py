#!/usr/bin/env python3
"""gen_pb2.py -- turn Native/Box2D/prowl_box2d.h into the managed bindings for it, so the header is the only place the native API is written down.

    python3 tools/ccsharp/gen_pb2.py [--lib gfx2d] generate OUT_DIR   write OUT_DIR/net/PB2.net.cs and OUT_DIR/c/PB2.c.cs  (GFX.* for gfx2d)
    python3 tools/ccsharp/gen_pb2.py runtime                write Prowl.Runtime/Physics2D/Native/PB2.Generated.cs (the .NET flavor, internal)
    python3 tools/ccsharp/gen_pb2.py check                  is that committed file what the header generates?

Two flavors of ONE API, with the same names and the same signatures, so one piece of C# runs on both:

  net   .NET: [LibraryImport] declarations over the real pointers, each pointer-taking function wrapped so its public signature has no
        pointers. For code that runs in the editor.
  c     the C# -> C build (CCSharp): `extern` members carrying [Cpp] templates that spell the call in C, and structs that name the C structs
        of the header. Compiled as CCSharp "bindings" (--bindings=DIR); it contains no code, only how a use is written.

The subset has no pointers, so a pointer parameter becomes what the C# subset can say, as the PB2_* markers in the header direct:

      PB2_IN / PB2_OUT      a record      ->  `ref PB2Record`          (the C template passes its address)
      PB2_IN_ARR / _OUT_ARR  scalars       ->  `T[]`                    (the template passes `.data()`)
      PB2_IN_ARR / _OUT_ARR  records       ->  `List<PB2Record>`        (likewise)

and a record's members that C# cannot hold become accessors: `PB2_VIEW(T, n) intptr_t moves` -> `PB2.StepInfoMoves(ref info, i)`, and an array member
`float p[12]` -> `PB2.JointDefGetP(ref def, i)` / `JointDefSetP(ref def, i, v)`. Records use the C field names, because in the C flavor a field IS
the C field (`[Cpp("{this}.moveCount")]`).

The parser below is deliberately small: this header is regular. It is the one place a different front end could be dropped in (Crust's
`--emit-decls` digests C++ classes, not C headers, which is why it is not used). It refuses what it cannot classify rather than guessing.
"""
import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROWL = os.path.dirname(os.path.dirname(HERE))


class Lib:
    """One native library's binding conventions: its header, the C# namespace and class, and the prefixes its header uses (`pb2_name`, `PB2_API`,
    `PB2_IN_ARR`, `PB2Record`, `PB2_CONSTANT`)."""

    def __init__(self, header, namespace, library, include, prefix, runtime_file=None):
        self.header, self.namespace, self.library, self.include = header, namespace, library, include
        self.lc, self.uc, self.runtime_file = prefix.lower(), prefix.upper(), runtime_file


LIBS = {
    "box2d": Lib(os.path.join(PROWL, "Native", "Box2D", "prowl_box2d.h"), "Prowl.Native.Box2D", "prowl_box2d", '"prowl_box2d.h"', "PB2",
                 os.path.join(PROWL, "Prowl.Runtime", "Physics2D", "Native", "PB2.Generated.cs")),
    # the 2D renderer (Native/Gfx2D): generated at game-build time, so there is no committed copy
    "gfx2d": Lib(os.path.join(PROWL, "Native", "Gfx2D", "gfx2d.h"), "Prowl.Native.Gfx2D", "gfx2d", '"gfx2d.h"', "GFX"),
}

# The library being generated for. Box2D unless use() says otherwise, so the tools that import this module keep working unchanged.
HEADER = NAMESPACE = LIBRARY = HEADER_INCLUDE = RUNTIME_FILE = PREFIX_LC = PREFIX_UC = None


def use(name):
    """Makes `name` ("box2d" or "gfx2d") the library the rest of this module generates for."""
    global HEADER, NAMESPACE, LIBRARY, HEADER_INCLUDE, RUNTIME_FILE, PREFIX_LC, PREFIX_UC
    lib = LIBS[name]
    HEADER, NAMESPACE, LIBRARY, HEADER_INCLUDE = lib.header, lib.namespace, lib.library, lib.include
    RUNTIME_FILE, PREFIX_LC, PREFIX_UC = lib.runtime_file, lib.lc, lib.uc


use("box2d")

# C type -> (C# type in the C flavor, C# type in the .NET flavor)
SCALARS = {
    "int": ("int", "int"), "int32_t": ("int", "int"), "uint32_t": ("uint", "uint"), "float": ("float", "float"),
    "intptr_t": ("long", "nint"), "void": ("void", "void"),
    "uint8_t": ("byte", "byte"),     # pixels: gfx2d textures (a byte[] in C#)
}
KEYWORDS = {"params", "object", "string", "base", "ref", "out", "in", "event", "lock", "checked", "fixed", "default", "operator", "namespace",
            "char", "byte", "decimal", "delegate", "is", "as", "new", "this", "class", "struct"}


class GenError(Exception):
    pass


# ---------------------------------------------------------------------------------------------------------------------------------
#  The model
# ---------------------------------------------------------------------------------------------------------------------------------

class Field:
    def __init__(self, ctype, name, array=None, view=None):
        self.ctype, self.name, self.array, self.view = ctype, name, array, view   # view = (elementType, countField) or None


class Struct:
    def __init__(self, name):
        self.name, self.fields = name, []


class Param:
    def __init__(self, ctype, name, pointer, direction):
        self.ctype, self.name, self.pointer, self.direction = ctype, name, pointer, direction   # direction: IN OUT IN_ARR OUT_ARR or None


class Function:
    def __init__(self, cname, ret, params):
        self.cname, self.ret, self.params = cname, ret, params


class Model:
    def __init__(self):
        self.consts, self.structs, self.functions = [], [], []   # consts: (name, value-text, csharp type)
        self.abi_slots = []                                      # (slot, what): what is "version", "sizeof(void*)" or a record name

    def struct(self, name):
        return next((s for s in self.structs if s.name == name), None)


# ---------------------------------------------------------------------------------------------------------------------------------
#  The parser
# ---------------------------------------------------------------------------------------------------------------------------------

def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def parse_abi_slots(raw):
    """The slots pb2_abi() fills, from the comment that documents it: `// out: [0]=version [1]=BodyMove ...` and its continuation lines."""
    i = raw.find("// Fills out[0..")
    if i < 0:
        return []
    block = raw[i:raw.find(PREFIX_UC + "_API", i)]
    return [(int(n), w) for n, w in re.findall(r"\[(\d+)\]=([\w()*]+)", block)]


def parse_header(path=None):
    path = path or HEADER
    with open(path, encoding="utf-8") as f:
        raw = f.read()
    text = strip_comments(raw)
    m = Model()
    m.abi_slots = parse_abi_slots(raw)

    for dm in re.finditer(r"^[ \t]*#[ \t]*define[ \t]+(%s_\w+)[ \t]+(\S+)[ \t]*$" % PREFIX_UC, text, flags=re.M):
        name, value = dm.group(1), dm.group(2)
        if re.fullmatch(r"\d+", value):
            m.consts.append((name, value, "int"))
        elif re.fullmatch(r"\d+[uU]", value):
            m.consts.append((name, value, "uint"))
        # anything else (an expression, a macro) is not a constant the bindings can use

    for sm in re.finditer(r"typedef\s+struct\s+(\w+)\s*\{(.*?)\}\s*(\w+)\s*;", text, flags=re.S):
        if sm.group(1) != sm.group(3):
            raise GenError("typedef struct %s ... %s: the tag and the name differ" % (sm.group(1), sm.group(3)))
        st = Struct(sm.group(1))
        for decl in [d.strip() for d in sm.group(2).split(";") if d.strip()]:
            view = None
            vm = re.match(PREFIX_UC + r"_VIEW\s*\(\s*(\w+)\s*,\s*(\w+)\s*\)\s*(.*)", decl, flags=re.S)
            if vm:
                view, decl = (vm.group(1), vm.group(2)), vm.group(3).strip()
            tm = re.match(r"(\w+)\s+(.*)", decl, flags=re.S)
            if not tm:
                raise GenError("cannot read the field `%s` of %s" % (decl, st.name))
            ctype = tm.group(1)
            for declarator in [x.strip() for x in tm.group(2).split(",")]:
                am = re.fullmatch(r"(\w+)\s*\[\s*(\w+)\s*\]", declarator)
                if am:
                    st.fields.append(Field(ctype, am.group(1), array=am.group(2)))
                elif re.fullmatch(r"\w+", declarator):
                    st.fields.append(Field(ctype, declarator, view=view))
                else:
                    raise GenError("cannot read the declarator `%s` in %s" % (declarator, st.name))
        m.structs.append(st)

    for fm in re.finditer(PREFIX_UC + r"_API\s+(\w+)\s+(" + PREFIX_LC + r"_\w+)\s*\(([^)]*)\)\s*;", text, flags=re.S):
        ret, cname, raw = fm.group(1), fm.group(2), fm.group(3)
        params = []
        for p in [x.strip() for x in raw.split(",") if x.strip() and x.strip() != "void"]:
            direction = None
            dm = re.match(r"(%s_IN_ARR|%s_OUT_ARR|%s_IN|%s_OUT)\s+(.*)" % ((PREFIX_UC,) * 4), p, flags=re.S)
            if dm:
                direction, p = dm.group(1)[4:], dm.group(2).strip()
            pm = re.fullmatch(r"(?:const\s+)?(\w+)\s*(\*?)\s*(\w+)", p)
            if not pm:
                raise GenError("%s: cannot read the parameter `%s`" % (cname, p))
            ctype, star, name = pm.group(1), pm.group(2), pm.group(3)
            if star and direction is None:
                raise GenError("%s: the pointer parameter `%s` has no direction marker. Say what it means in the header: "
                               "%s_IN, %s_OUT, %s_IN_ARR or %s_OUT_ARR." % ((cname, name) + (PREFIX_UC,) * 4))
            if not star and direction is not None:
                raise GenError("%s: `%s` is marked %s but is not a pointer." % (cname, name, direction))
            params.append(Param(ctype, name, bool(star), direction))
        m.functions.append(Function(cname, ret, params))

    if not m.functions:
        raise GenError("no %s_API functions found in %s" % (PREFIX_UC, path))
    check_types(m)
    return m


def check_types(m):
    names = {s.name for s in m.structs}
    for s in m.structs:
        for f in s.fields:
            if f.ctype not in SCALARS and f.ctype not in names:
                raise GenError("%s.%s: unknown type %s" % (s.name, f.name, f.ctype))
            if f.view and f.view[0] not in SCALARS and f.view[0] not in names:
                raise GenError("%s.%s: %s_VIEW of unknown type %s" % (s.name, f.name, PREFIX_UC, f.view[0]))
            if f.view and f.ctype != "intptr_t":
                raise GenError("%s.%s: %s_VIEW is for an intptr_t field" % (s.name, f.name, PREFIX_UC))
            if f.view and not any(g.name == f.view[1] for g in s.fields):
                raise GenError("%s.%s: %s_VIEW names a count field %s that the record does not have" % (s.name, f.name, PREFIX_UC, f.view[1]))
    for fn in m.functions:
        if fn.ret not in SCALARS:
            raise GenError("%s: unknown return type %s" % (fn.cname, fn.ret))
        for p in fn.params:
            if p.ctype not in SCALARS and p.ctype not in names:
                raise GenError("%s(%s): unknown type %s" % (fn.cname, p.name, p.ctype))
            record = p.ctype in names
            if p.pointer and p.direction in ("IN", "OUT") and not record:
                raise GenError("%s(%s): %s marks a single scalar behind a pointer, which has no managed form here. Use an array marker." % (fn.cname, p.name, p.direction))
            if p.pointer and p.direction in ("IN_ARR", "OUT_ARR") and p.ctype == "void":
                raise GenError("%s(%s): an array of void" % (fn.cname, p.name))
            if not p.pointer and record:
                raise GenError("%s(%s): a record passed by value is not supported" % (fn.cname, p.name))


# ---------------------------------------------------------------------------------------------------------------------------------
#  Names and types shared by both emitters
# ---------------------------------------------------------------------------------------------------------------------------------

def pascal(snake):
    return "".join(w[:1].upper() + w[1:] for w in snake.split("_") if w)


def method_name(cname):
    return pascal(cname[len(PREFIX_LC + "_"):])


def const_name(cname):
    return pascal(cname[len(PREFIX_UC + "_"):].lower())


def ident(name):
    return "@" + name if name in KEYWORDS else name


def record_stem(struct_name):
    return struct_name[len(PREFIX_UC):] if struct_name.startswith(PREFIX_UC) else struct_name


def accessor_name(struct_name, field_name, verb=""):
    """StepInfoMoves (a view), JointDefGetP / JointDefSetP (an array member): the record, then the verb, then the field."""
    return record_stem(struct_name) + verb + field_name[:1].upper() + field_name[1:]


def cs_param(p, model, flavor):
    """The managed parameter for a C parameter, as (type text, is_record, is_list)."""
    names = {s.name for s in model.structs}
    scalar = SCALARS[p.ctype][0 if flavor == "c" else 1] if p.ctype in SCALARS else p.ctype
    if not p.pointer:
        return scalar
    if p.direction in ("IN", "OUT"):
        return "ref " + p.ctype
    if p.ctype in names:
        return "List<%s>" % p.ctype
    return scalar + "[]"


def c_template_arg(p, model, index):
    names = {s.name for s in model.structs}
    if not p.pointer:
        return "{%d}" % index
    if p.direction in ("IN", "OUT"):
        return "&{%d}" % index
    return "{%d}.data()" % index


# ---------------------------------------------------------------------------------------------------------------------------------
#  The C flavor
# ---------------------------------------------------------------------------------------------------------------------------------

BANNER = """// <auto-generated>
// Generated from {header} by tools/ccsharp/gen_pb2.py. Do not edit: change the header and regenerate.
// {what}
// </auto-generated>
"""


def HEADER_REL():
    """The header's path as the banner says it: relative to the repository, with forward slashes."""
    return os.path.relpath(HEADER, PROWL).replace(os.sep, "/")


def emit_c(m):
    out = [BANNER.format(header=HEADER_REL(), what="C-build flavor: how C# code in the CC# subset names the C of the shim. Compiled as --bindings; it holds no code.")]
    out.append("using System.Collections.Generic;\n")
    out.append("namespace %s\n{" % NAMESPACE)

    for s in m.structs:
        out.append('  [Crust.Cpp("%s"), Crust.CppInclude("\\"%s\\"")]' % (s.name, HEADER_INCLUDE.strip('"')))
        out.append("  public struct %s" % s.name)
        out.append("  {")
        for f in s.fields:
            if f.array or f.view:
                continue   # reached through accessors below; the subset cannot hold an array or a raw pointer
            cs = SCALARS[f.ctype][0] if f.ctype in SCALARS else f.ctype
            out.append('    [Crust.Cpp("{this}.%s")] public %s %s;' % (f.name, cs, ident(f.name)))
        out.append("  }")
        out.append("")

    out.append('  [Crust.CppInclude("\\"%s\\"")]' % HEADER_INCLUDE.strip('"'))
    out.append("  public static class %s" % PREFIX_UC)
    out.append("  {")
    for name, value, ty in m.consts:
        out.append("    public const %s %s = %s;" % (ty, const_name(name), value))
    out.append("")
    for fn in m.functions:
        args = ", ".join(c_template_arg(p, m, i) for i, p in enumerate(fn.params))
        sig = ", ".join("%s %s" % (cs_param(p, m, "c"), ident(p.name)) for p in fn.params)
        out.append('    [Crust.Cpp("%s(%s)")] public static extern %s %s(%s);' % (fn.cname, args, SCALARS[fn.ret][0], method_name(fn.cname), sig))
    out.append("")
    for s in m.structs:
        for f in s.fields:
            if f.view:
                et, count = f.view
                ret = SCALARS[et][0] if et in SCALARS else et
                out.append("    // %s.%s: %s element %s of the array it points at (%s of them)" % (s.name, f.name, "reads", "i", count))
                out.append('    [Crust.Cpp("(((%s*)({0}).%s)[{1}])")] public static extern %s %s(ref %s record, int index);'
                           % (et, f.name, ret, accessor_name(s.name, f.name), s.name))
            elif f.array:
                et = SCALARS[f.ctype][0]
                out.append('    [Crust.Cpp("({0}).%s[{1}]")] public static extern %s %s(ref %s record, int index);'
                           % (f.name, et, accessor_name(s.name, f.name, "Get"), s.name))
                out.append('    [Crust.Cpp("({0}).%s[{1}] = {2}")] public static extern void %s(ref %s record, int index, %s value);'
                           % (f.name, accessor_name(s.name, f.name, "Set"), s.name, et))
    out.append("  }")
    out.append("}")
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------------------------------------------------------------------------
#  The .NET flavor
# ---------------------------------------------------------------------------------------------------------------------------------

def net_raw_param(p, m):
    scalar = SCALARS[p.ctype][1] if p.ctype in SCALARS else p.ctype
    return ("%s* %s" % (scalar, ident(p.name))) if p.pointer else ("%s %s" % (scalar, ident(p.name)))


def emit_net(m, visibility="public"):
    names = {s.name for s in m.structs}
    vis = visibility
    out = [BANNER.format(header=HEADER_REL(), what=".NET flavor: [LibraryImport] over the real pointers, wrapped so the public signatures match the C flavor exactly.")]
    out.append("using System;\nusing System.Collections.Generic;\nusing System.Runtime.InteropServices;\n")
    out.append("namespace %s\n{" % NAMESPACE)

    for s in m.structs:
        has_array = any(f.array for f in s.fields)
        out.append("  [StructLayout(LayoutKind.Sequential)]")
        out.append("  %s %sstruct %s" % (vis, "unsafe " if has_array else "", s.name))
        out.append("  {")
        for f in s.fields:
            cs = SCALARS[f.ctype][1] if f.ctype in SCALARS else f.ctype
            if f.array:
                out.append("    public fixed %s %s[%s.%s];" % (cs, f.name, PREFIX_UC, const_name(f.array)))
            else:
                out.append("    public %s %s;" % (cs, ident(f.name)))
        out.append("  }")
        out.append("")

    out.append("  %s static unsafe partial class %s" % (vis, PREFIX_UC))
    out.append("  {")
    out.append('    private const string Lib = "%s";' % LIBRARY)
    for name, value, ty in m.consts:
        out.append("    public const %s %s = %s;" % (ty, const_name(name), value))
    out.append("")

    for fn in m.functions:
        ret = SCALARS[fn.ret][1]
        pointered = any(p.pointer for p in fn.params)
        mname = method_name(fn.cname)
        if not pointered:
            sig = ", ".join("%s %s" % (cs_param(p, m, "net"), ident(p.name)) for p in fn.params)
            out.append('    [LibraryImport(Lib, EntryPoint = "%s")] public static partial %s %s(%s);' % (fn.cname, ret, mname, sig))
            continue
        raw = "Raw" + mname
        out.append('    [LibraryImport(Lib, EntryPoint = "%s")] private static partial %s %s(%s);'
                   % (fn.cname, ret, raw, ", ".join(net_raw_param(p, m) for p in fn.params)))
        sig = ", ".join("%s %s" % (cs_param(p, m, "net"), ident(p.name)) for p in fn.params)
        out.append("    public static %s %s(%s)" % (ret, mname, sig))
        out.append("    {")
        indent, closers, call_args = "        ", 0, []
        for p in fn.params:
            if not p.pointer:
                call_args.append(ident(p.name))
                continue
            scalar = SCALARS[p.ctype][1] if p.ctype in SCALARS else p.ctype
            pv = "p_" + p.name
            if p.direction in ("IN", "OUT"):
                out.append("%sfixed (%s* %s = &%s)" % (indent, p.ctype, pv, ident(p.name)))
            elif p.ctype in names:
                out.append("%sfixed (%s* %s = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(%s))" % (indent, p.ctype, pv, ident(p.name)))
            else:
                out.append("%sfixed (%s* %s = %s)" % (indent, scalar, pv, ident(p.name)))
            out.append("%s{" % indent)
            indent += "    "
            closers += 1
            call_args.append(pv)
        out.append("%s%s%s(%s);" % (indent, "" if ret == "void" else "return ", raw, ", ".join(call_args)))
        for _ in range(closers):
            indent = indent[:-4]
            out.append("%s}" % indent)
        out.append("    }")
        out.append("")

    if m.abi_slots:
        out.append("    /// <summary>Throws if the loaded library was built from a different header than these structs: every record size and the ABI version are compared.</summary>")
        out.append("    public static void VerifyAbi()")
        out.append("    {")
        out.append("        int[] abi = new int[16];")
        out.append("        Abi(abi);")
        out.append("        string bad = \"\";")
        for slot, what in m.abi_slots:
            if what == "version":
                expect, label = "AbiVersion", "ABI version"
            elif what == "sizeof(void*)":
                expect, label = "IntPtr.Size", "pointer size"
            else:
                st = PREFIX_UC + what
                if st not in names:
                    raise GenError("%s_abi documents slot [%d]=%s but the header has no record %s" % (PREFIX_LC, slot, what, st))
                expect, label = "System.Runtime.CompilerServices.Unsafe.SizeOf<%s>()" % st, st
            out.append("        if (abi[%d] != %s) bad += \"%s is \" + abi[%d] + \" natively and \" + %s + \" here; \";" % (slot, expect, label, slot, expect))
        out.append("        if (bad.Length > 0) throw new InvalidOperationException(\"prowl_box2d ABI mismatch: \" + bad + \"Rebuild Native/Box2D.\");")
        out.append("    }")
        out.append("")
    out.append("")
    for s in m.structs:
        for f in s.fields:
            if f.view:
                et, count = f.view
                ret = SCALARS[et][1] if et in SCALARS else et
                out.append("    public static %s %s(ref %s record, int index) => ((%s*)record.%s)[index];"
                           % (ret, accessor_name(s.name, f.name), s.name, SCALARS[et][1] if et in SCALARS else et, f.name))
            elif f.array:
                et = SCALARS[f.ctype][1]
                out.append("    public static %s %s(ref %s record, int index) { fixed (%s* r = &record) return r->%s[index]; }"
                           % (et, accessor_name(s.name, f.name, "Get"), s.name, s.name, f.name))
                out.append("    public static void %s(ref %s record, int index, %s value) { fixed (%s* r = &record) r->%s[index] = value; }"
                           % (accessor_name(s.name, f.name, "Set"), s.name, et, s.name, f.name))
    out.append("  }")
    out.append("}")
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------------------------------------------------------------------------
#  Commands
# ---------------------------------------------------------------------------------------------------------------------------------

def runtime_text(header=None):
    """The .NET flavor as the runtime compiles it: internal, so the engine does not publish the native API."""
    return emit_net(parse_header(header or HEADER), visibility="internal")


def generate(out_dir, header=None):
    m = parse_header(header or HEADER)
    for sub, name, text in (("net", PREFIX_UC + ".net.cs", emit_net(m)), ("c", PREFIX_UC + ".c.cs", emit_c(m))):
        d = os.path.join(out_dir, sub)
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, name), "w", encoding="utf-8") as f:
            f.write(text)
    return m


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lib", default="box2d", choices=sorted(LIBS), help="which native library to generate bindings for (default box2d)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    g = sub.add_parser("generate", help="write the two flavors")
    g.add_argument("out")
    g.add_argument("--header", default=None)
    r = sub.add_parser("runtime", help="write the .NET flavor the engine compiles (internal) to Prowl.Runtime/Physics2D/Native/PB2.Generated.cs")
    r.add_argument("--header", default=None)
    c = sub.add_parser("check", help="is the committed PB2.Generated.cs what the header generates?")
    c.add_argument("--header", default=None)
    a = ap.parse_args(argv)
    use(a.lib)
    a.header = a.header or HEADER
    if a.cmd in ("runtime", "check") and RUNTIME_FILE is None:
        print("gen_pb2: %s has no committed .NET copy (it is generated when a game is built): use `generate`" % a.lib)
        return 2
    try:
        if a.cmd == "generate":
            m = generate(a.out, a.header)
            print("generated %d functions, %d records, %d constants into %s" % (len(m.functions), len(m.structs), len(m.consts), a.out))
            return 0
        if a.cmd == "runtime":
            with open(RUNTIME_FILE, "w", encoding="utf-8", newline="\n") as f:
                f.write(runtime_text(a.header))
            print("wrote %s" % os.path.relpath(RUNTIME_FILE, PROWL))
            return 0
        rc = 0
        committed = ""
        if os.path.exists(RUNTIME_FILE):
            with open(RUNTIME_FILE, encoding="utf-8", newline="") as f:
                committed = f.read()
        if committed != runtime_text(a.header):
            print("the committed %s is not what the header generates: run  python3 tools/ccsharp/gen_pb2.py runtime" % os.path.relpath(RUNTIME_FILE, PROWL))
            rc = 1
        if rc == 0:
            print("PB2.Generated.cs is current: %d native functions, generated from prowl_box2d.h" % len(parse_header(a.header).functions))
        return rc
    except GenError as e:
        print("gen_pb2: %s" % e)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
