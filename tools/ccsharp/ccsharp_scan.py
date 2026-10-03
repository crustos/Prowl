#!/usr/bin/env python3
"""ccsharp_scan.py -- how much of Prowl's 2D engine can CCSharp translate to C?

CCSharp (https://github.com/crustos/CCSharp) is a whole-program compiler: C# -> Crust C++ subset -> C. It stops at the first
Roslyn error, and only reports what is outside the Crust subset once the program binds. Prowl's 2D code names types that
CCSharp's corelib has never heard of (Float2, MonoBehaviour ...), so pointing the compiler straight at it
reports nothing useful. This script builds an OVERLAY that makes it bind, then runs the compiler and sorts what it says.

    python3 tools/ccsharp/ccsharp_scan.py overlay                 write the overlay to /tmp/prowl-ccs-overlay (and stop)
    python3 tools/ccsharp/ccsharp_scan.py scan [--tier NAME] [--json FILE] [-v]
    python3 tools/ccsharp/ccsharp_scan.py conformance             translate -> C -> gcc -> run, and diff with real .NET

The overlay is a COPY of CCSharp's corelib (never edited in place) plus two kinds of declaration:

  * corelib gaps   .NET members the program uses and CCSharp has not implemented (Stack<T>, Array.Resize, Span<T> ...). Declared
                   without [Cpp], so using one is refused BY NAME: that is the "corelib gap" row of the report. Each is added
                   only if the corelib does not already have it, so the overlay shrinks as CCSharp grows.
  * context        subset C# with trivial bodies that stands in for what the C core would provide: the math structs, the engine
                   types (MonoBehaviour, GameObject, Transform ...); the native Box2D calls are the generated C-flavor bindings, in the corelib copy.
                   Compiled with the program and never reported on, so what is reported is the 2D code's OWN constructs: how it
                   uses those types still counts (a generic method, a null, a lambda), what they are does not.

Nothing here is a claim that the code RUNS in C. The `conformance` command is the claim: it translates programs, builds them with gcc
and compares their output with the same C# on real .NET.

CCSharp, crust and coost are cloned beside this repository (see `python3 build.py deps`):  ../CCSharp  ../crust  ../coost
"""
import argparse
import collections
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
PROWL = os.path.dirname(os.path.dirname(HERE))
RUNTIME = os.path.join(PROWL, "Prowl.Runtime")
DEFAULT_OVERLAY = os.path.join(tempfile.gettempdir(), "prowl-ccs-overlay")


def sibling(name):
    return os.path.join(os.path.dirname(PROWL), name)


def ccsharp_home():
    return os.environ.get("CCSHARP_HOME") or sibling("CCSharp")


# ---------------------------------------------------------------------------------------------------------------------------------
#  What is scanned
# ---------------------------------------------------------------------------------------------------------------------------------

def rt(*parts):
    return os.path.join(RUNTIME, *parts)


def CORE2D_FILES():
    """Everything the 2D runtime is made of, in build order, read from Prowl.Core2D/Prowl.Core2D.csproj: the one list both the .NET build and the C
    build use. Items marked CBuild="false" (the .NET-only native bindings) are left out: the C build uses the generated C flavor."""
    proj = os.path.join(PROWL, "Prowl.Core2D", "Prowl.Core2D.csproj")
    with open(proj, encoding="utf-8") as f:
        text = f.read()
    files = []
    for m in re.finditer(r'<Compile\s+Include="([^"]+)"([^>]*)/>', text):
        if 'CBuild="false"' in m.group(2):
            continue
        files.append(os.path.normpath(os.path.join(os.path.dirname(proj), m.group(1).replace("\\", os.sep))))
    return files


def c2(*parts):
    """A file of Prowl.Core2D, the translatable 2D runtime (nodes, scene, components, physics components)."""
    return os.path.join(PROWL, "Prowl.Core2D", *parts)


# A tier is a set of files scanned TOGETHER (the compiler is whole-program). Later tiers include the earlier ones as context
# so that their types bind, but only the tier's own files are reported on.
TIERS = [
    ("math", "engine-independent geometry, outlines and pose maths",
     [rt("Physics2D", f) for f in ("Pose2D.cs", "Collider2DGeometry.cs", "Collider2DOutline.cs")]),
    ("tables", "the integer core: handle table and active-trigger set (only indices)",
     [rt("Physics2D", f) for f in ("HandleTable.cs", "TriggerSet.cs")]),
    ("registry", "the arena registry: bodies and joints as small records that point at each other",
     [rt("Physics2D", f) for f in ("MaxInstancesAttribute.cs", "Arena.cs", "BodyRecord.cs", "JointRecord.cs", "Registry2D.cs")]),
    ("core", "the simulation core: native world, step, pose writes, the event stream, queries (through the generated bindings)",
     [rt("Physics2D", "SimCore2D.cs")]),
    ("engine", "the engine layer: host objects, event dispatch, scene ownership, the world and the components",
     [rt("Physics2D", "HostTable.cs"), rt("Physics2D", "PhysicsSimulation2D.cs"), rt("Physics2D", "Engine", "PhysicsWorld2D.cs"), rt("Physics2D", "Engine", "Physics2DTypes.cs")]
     + [rt("Components", "Physics2D", f) for f in ("Rigidbody2D.cs", "Collider2D.cs", "CircleCollider2D.cs", "BoxCollider2D.cs",
                                                  "CapsuleCollider2D.cs", "PolygonCollider2D.cs", "EdgeCollider2D.cs")]),
]
# Plain, dependency-free files from the real engine that the 2D code uses; compiled as context, never reported on.
REAL_CONTEXT = [rt("PhysicsCommon", "ForceMode.cs"), rt("PhysicsCommon", "RigidbodyInterpolation.cs"),
                rt("GameObject", "Attributes", "InspectorAttributes.cs"), rt("ComponentIconAttribute.cs")]


# ---------------------------------------------------------------------------------------------------------------------------------
#  The overlay
# ---------------------------------------------------------------------------------------------------------------------------------

# (name, regex that is true when the corelib ALREADY has it, declaration text). Declared without [Cpp]: refused by name when used.
CORELIB_GAPS = [
    ("Stack<T>", r"class\s+Stack\s*<", """
namespace System.Collections.Generic {
  public sealed class Stack<T> {
    public Stack() {}
    public extern int Count { get; }
    public extern void Push(T item);
    public extern T Pop();
    public extern T Peek();
    public extern void Clear();
  }
}"""),
    ("HashSet<T>", r"class\s+HashSet\s*<", """
namespace System.Collections.Generic {
  public sealed class HashSet<T> : IEnumerable<T> {
    public HashSet() {}
    public HashSet(object comparer) {}
    public extern int Count { get; }
    public extern bool Add(T item);
    public extern bool Remove(T item);
    public extern bool Contains(T item);
    public extern void Clear();
    public extern int RemoveWhere(Func<T, bool> match);
    public extern Enumerator GetEnumerator();
    extern IEnumerator<T> IEnumerable<T>.GetEnumerator();
    extern System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator();
    public struct Enumerator : IEnumerator<T> {
      public extern T Current { get; }
      extern object System.Collections.IEnumerator.Current { get; }
      public extern bool MoveNext();
      public extern void Reset();
      public extern void Dispose();
    }
  }
}"""),
    ("ReferenceEquals", r"ReferenceEquals", None),   # patched into Object below
    ("Span<T>", r"struct\s+Span\s*<", """
namespace System {
  public ref struct Span<T> {
    public extern int Length { get; }
    public extern T this[int index] { get; set; }
    public extern Span<T> Slice(int start);
    public extern void CopyTo(Span<T> destination);
    public static extern implicit operator Span<T>(T[] array);
    public extern Enumerator GetEnumerator();
    public ref struct Enumerator { public extern ref T Current { get; } public extern bool MoveNext(); }
  }
  public ref struct ReadOnlySpan<T> {
    public extern int Length { get; }
    public extern T this[int index] { get; }
    public extern ReadOnlySpan<T> Slice(int start);
    public extern void CopyTo(Span<T> destination);
    public static extern implicit operator ReadOnlySpan<T>(T[] array);
    public static extern implicit operator ReadOnlySpan<T>(Span<T> span);
    public extern Enumerator GetEnumerator();
    public ref struct Enumerator { public extern ref readonly T Current { get; } public extern bool MoveNext(); }
  }
}"""),
    ("IEqualityComparer<T>", r"IEqualityComparer\s*<", """
namespace System.Collections.Generic {
  public interface IEqualityComparer<in T> { bool Equals(T x, T y); int GetHashCode(T obj); }
  public sealed class ReferenceEqualityComparer {
    public static extern ReferenceEqualityComparer Instance { get; }
  }
}"""),
]

# Members added to a type the corelib already has (it cannot be re-declared), spliced into its body.
CORELIB_MEMBER_PATCHES = [
    ("System.Array statics", "public abstract class Array", r"\bResize\b", """
    public static extern void Resize<T>(ref T[] array, int newSize);
    public static extern void Clear(System.Array array, int index, int length);
    public static extern void Fill<T>(T[] array, T value);
    public static extern void Copy(System.Array source, System.Array destination, int length);"""),
    ("object.ReferenceEquals", "public class Object", r"\bReferenceEquals\b", """
    public static extern bool ReferenceEquals(object a, object b);"""),
    ("Array.Empty", "public abstract class Array", r"\bEmpty\s*<", """
    public static extern T[] Empty<T>();"""),
    ("List<T>.AddRange", "public sealed class List<T>", r"\bAddRange\b", """
    public extern void AddRange(IEnumerable<T> collection);
    public List(IEnumerable<T> collection) {}"""),
]

# Context: subset C# standing in for the engine and the math types. Trivial bodies; refusals in these files are never reported.
CONTEXT_BASE = """
using System;
using System.Collections.Generic;
namespace Prowl.Echo {
  [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)] public sealed class SerializeFieldAttribute : Attribute {}
}
namespace System.Diagnostics.CodeAnalysis {
  // Nullable-analysis annotations: no runtime meaning, so they bind and are otherwise ignored.
  [AttributeUsage(AttributeTargets.All)] public sealed class MemberNotNullWhenAttribute : Attribute { public MemberNotNullWhenAttribute(bool returnValue, string member) {} }
}
namespace Prowl.Vector {
  public struct Float2 { public float X; public float Y; public Float2(float x, float y) { X = x; Y = y; } public static Float2 Zero; }
  public struct Float3 {
    public float X; public float Y; public float Z;
    public Float3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static Float3 One; public static Float3 Zero;
    public bool Equals(Float3 o) { return X == o.X && Y == o.Y && Z == o.Z; }
  }
  public struct Quaternion { public float X; public float Y; public float Z; public float W; public Quaternion(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; } }
  public struct Color { public float R; public float G; public float B; public float A; public static Color Green; public static Color Red; public static Color Yellow; public static Color Cyan; public static Color White; }
  public static class Maths { public const float Deg2Rad = 0.017453292f; public const float Rad2Deg = 57.29578f; }
  public sealed class Transform {
    public uint Version { get { return 0u; } }
    public Float3 Position { get { return Float3.Zero; } set { } }
    public Quaternion Rotation { get { return new Quaternion(0f, 0f, 0f, 1f); } set { } }
    public Float3 LossyScale { get { return Float3.One; } }
    public Float3 LocalScale { get { return Float3.One; } }
    public Transform Parent { get { return new Transform(); } }
    public Float3 TransformPoint(Float3 p) { return p; }
  }
}
namespace Prowl.Runtime {
  public struct LayerMask { public uint Mask; public static LayerMask Everything; public static LayerMask FromMask(uint mask) { LayerMask m = new LayerMask(); m.Mask = mask; return m; } }
  public class EngineObject { public string Name { get { return ""; } set { } } public bool IsDisposed { get { return false; } } }
  public static class EngineObjectExtensions {
    public static bool IsValid(this EngineObject obj) { return true; }
    public static bool IsNotValid(this EngineObject obj) { return false; }
  }
  public class GameObject : EngineObject {
    public int LayerIndex { get { return 0; } set { } }
    public Prowl.Vector.Transform Transform { get { return new Prowl.Vector.Transform(); } }
    public Prowl.Runtime.Resources.Scene Scene { get { return new Prowl.Runtime.Resources.Scene(); } }
  }
  public class MonoBehaviour : EngineObject {
    public GameObject GameObject { get { return new GameObject(); } }
    public Prowl.Vector.Transform Transform { get { return new Prowl.Vector.Transform(); } }
    public bool EnabledInHierarchy { get { return true; } }
    public bool Enabled { get { return true; } set { } }
    protected void AssertOwner() {}
    public virtual void OnEnable() {}
    public virtual void OnDisable() {}
    public virtual void OnValidate() {}
    public virtual void Update() {}
    public virtual void DrawGizmos() {}
    public virtual void DrawGizmosSelected() {}
    public List<T> GetComponentsInChildren<T>() { return new List<T>(); }
    public List<T> GetComponentsInParent<T>() { return new List<T>(); }
  }
  public static class Debug {
    public static void LogError(string message) {}
    public static void LogErrorOnce(string key, string message) {}
    public static void DrawLine(Prowl.Vector.Float3 a, Prowl.Vector.Float3 b, Prowl.Vector.Color c) {}
    public static void DrawDashedLine(Prowl.Vector.Float3 a, Prowl.Vector.Float3 b, Prowl.Vector.Color c) {}
  }
  public static class Time { public static float FixedDeltaTime { get { return 0.02f; } } public static float FixedAlpha { get { return 0f; } } }
  public static class CollisionMatrix { public static int Version { get { return 0; } } public static uint[] GetRows() { return new uint[32]; } }
}
"""

# Needs program types (Collider2D, Collision2D, PhysicsWorld2D), so only the tier that contains them can carry it.
CONTEXT_ENGINE = """
namespace Prowl.Runtime.Resources {
  public class Scene : Prowl.Runtime.EngineObject { public Prowl.Runtime.PhysicsWorld2D Physics2D { get { return new Prowl.Runtime.PhysicsWorld2D(); } } }
}
namespace Prowl.Runtime {
  public static class SceneDispatcher {
    public static void CollisionBegin2D(GameObject go, in Collision2D collision) {}
    public static void CollisionEnd2D(GameObject go, in Collision2D collision) {}
    public static void TriggerEnter2D(GameObject go, Collider2D other) {}
    public static void TriggerStay2D(GameObject go, Collider2D other) {}
    public static void TriggerExit2D(GameObject go, Collider2D other) {}
  }
}
"""

# Same, for the tiers that do not contain PhysicsWorld2D.
CONTEXT_SCENE_STUB = """
namespace Prowl.Runtime.Resources { public class Scene : Prowl.Runtime.EngineObject {} }
"""

def corelib_text(corelib_dir):
    out = []
    for dp, _, fn in os.walk(corelib_dir):
        for f in fn:
            if f.endswith(".cs"):
                with open(os.path.join(dp, f), encoding="utf-8") as fh:
                    out.append(fh.read())
    return "\n".join(out)


def splice_members(text, class_header, members):
    """Insert `members` just before the closing brace of the class that starts with `class_header`."""
    i = text.find(class_header)
    if i < 0:
        return None
    j = text.index("{", i)
    depth = 0
    for k in range(j, len(text)):
        if text[k] == "{":
            depth += 1
        elif text[k] == "}":
            depth -= 1
            if depth == 0:
                return text[:k] + members + "\n  " + text[k:]
    return None


def make_overlay(out=DEFAULT_OVERLAY, verbose=False):
    """Write the overlay: <out>/corelib (a corelib copy + declarations), <out>/context/*.cs. Returns (corelib_dir, context_files, notes)."""
    home = ccsharp_home()
    src = os.path.join(home, "corelib", "src")
    if not os.path.isdir(src):
        sys.exit("ccsharp_scan: no CCSharp corelib at %s\n  clone it beside Prowl:  git clone https://github.com/crustos/CCSharp %s\n"
                 "  (or run `python3 build.py deps`; or set CCSHARP_HOME)" % (src, home))
    if os.path.exists(out):
        shutil.rmtree(out)
    corelib = os.path.join(out, "corelib")
    shutil.copytree(src, corelib)
    existing = corelib_text(corelib)
    notes = []

    for name, present, text in CORELIB_GAPS:
        if re.search(present, existing):
            notes.append("corelib already has %s" % name)
        elif text:
            with open(os.path.join(corelib, "prowl_gap_%s.cs" % re.sub(r"\W", "_", name)), "w", encoding="utf-8") as f:
                f.write(text)
            notes.append("declared (unimplemented): %s" % name)

    for name, header, present, members in CORELIB_MEMBER_PATCHES:
        if re.search(present, existing):
            notes.append("corelib already has %s" % name)
            continue
        done = False
        for dp, _, fn in os.walk(corelib):
            for f in fn:
                p = os.path.join(dp, f)
                if not f.endswith(".cs") or f.startswith("prowl_"):
                    continue
                with open(p, encoding="utf-8") as fh:
                    t = fh.read()
                patched = splice_members(t, header, members)
                if patched is not None:
                    with open(p, "w", encoding="utf-8") as fh:
                        fh.write(patched)
                    done = True
                    break
            if done:
                break
        notes.append(("declared (unimplemented): " if done else "COULD NOT PATCH: ") + name)

    ctx = os.path.join(out, "context")
    os.makedirs(ctx)
    # The native API is the generated C flavor, in the corelib copy: the only place `extern` and [Cpp] are legal, and exactly the file the C build
    # compiles against, so what the scan binds is what will run.
    import gen_pb2
    model = gen_pb2.parse_header()
    with open(os.path.join(corelib, "prowl_pb2_bindings.cs"), "w", encoding="utf-8") as f:
        f.write(gen_pb2.emit_c(model))
    files = {"base": CONTEXT_BASE, "engine": CONTEXT_ENGINE, "scene_stub": CONTEXT_SCENE_STUB}
    paths = {}
    for k, text in files.items():
        paths[k] = os.path.join(ctx, "prowl_%s.cs" % k)
        with open(paths[k], "w", encoding="utf-8") as f:
            f.write(text)
    notes.append("PB2 bindings: the generated C flavor, %d native functions" % len(model.functions))
    if verbose:
        print("overlay written to %s" % out)
        for n in notes:
            print("   " + n)
    return corelib, paths, notes


# ---------------------------------------------------------------------------------------------------------------------------------
#  Running the compiler and reading what it says
# ---------------------------------------------------------------------------------------------------------------------------------

def compiler_cmd():
    ccs = os.path.join(ccsharp_home(), "build", "compiler", "ccs.dll")
    if not os.path.exists(ccs):
        sys.exit("ccsharp_scan: the CCSharp compiler is not built (%s)\n  run:  python3 %s compiler" % (ccs, os.path.join(ccsharp_home(), "build.py")))
    return ["dotnet", ccs]


ROSLYN = re.compile(r"^(?P<file>.+?): \((?P<line>\d+),(?P<col>\d+)\): error (?P<code>CS\d+): (?P<msg>.*)$")
REFUSAL = re.compile(r"^(?P<file>/.+?\.cs):(?P<line>\d+): (?P<msg>.+)$")


class ScanError(Exception):
    """The overlay or the compiler itself is broken. Never reported as a result: a scanner that swallows this says CLEAN for code it
    never looked at."""


def run_compiler(files, corelib, workdir):
    lst = os.path.join(workdir, "sources.txt")
    with open(lst, "w") as f:
        f.write("\n".join(files) + "\n")
    cmd = compiler_cmd() + [files[0], "Scan", "--crust", "--library", "--home=" + ccsharp_home(), "--corelib=" + corelib, "--srclist=" + lst]
    p = subprocess.run(cmd, cwd=workdir, capture_output=True, text=True)
    text = p.stdout + p.stderr
    roslyn, refusals, corelib_errors = [], [], []
    for line in text.splitlines():
        if line.startswith("corelib "):
            corelib_errors.append(line[len("corelib "):])
            continue
        m = ROSLYN.match(line)
        if m:
            roslyn.append((m.group("file"), int(m.group("line")), m.group("code"), m.group("msg")))
            continue
        m = REFUSAL.match(line)
        if m:
            refusals.append((m.group("file"), int(m.group("line")), m.group("msg")))
    if corelib_errors:
        raise ScanError("the overlay's corelib does not compile (%d errors); first:\n   %s" % (len(corelib_errors), "\n   ".join(corelib_errors[:5])))
    if p.returncode != 0 and not roslyn and not refusals:
        raise ScanError("the compiler failed (exit %d) and said nothing the scan understands:\n%s" % (p.returncode, text[-1500:]))
    if p.returncode == 0 and (roslyn or refusals):
        raise ScanError("the compiler reported diagnostics but exited 0:\n%s" % text[-800:])
    return p.returncode, roslyn, refusals


# What each refusal is about. Order matters: first match wins.
CATEGORIES = [
    ("null",            re.compile(r"`null`")),
    ("exceptions",      re.compile(r"`throw`|`try`")),
    ("lambda/delegate", re.compile(r"lambda|delegate|anonymous", re.I)),
    ("type tests",      re.compile(r"`is`|`as`|runtime type information", re.I)),
    ("class identity",  re.compile(r"`==` on class references|identity", re.I)),
    ("aliasing",        re.compile(r"alias|single-owner|single owner", re.I)),
    ("arrays of structs", re.compile(r"`new T\[n\]`")),
    ("generic methods", re.compile(r"generic method", re.I)),
    ("optional/named args", re.compile(r"named argument|optional", re.I)),
    ("nested types",    re.compile(r"nested", re.I)),
    ("pointers/unsafe", re.compile(r"pointer|unsafe|stackalloc|fixed", re.I)),
    ("properties",      re.compile(r"propert", re.I)),
    ("char",            re.compile(r"`char`")),
    ("string",          re.compile(r"\bstring\b", re.I)),
    ("evaluation order", re.compile(r"evaluat|hoist|side effect|temporary|conflict", re.I)),
    ("inheritance",     re.compile(r"base|interface|virtual|override", re.I)),
]
NAMED = re.compile(r"`(?P<sym>[^`]+)` is declared in the CC# corelib")


def classify(msg):
    """-> (kind, detail). kind is one of: corelib gap, engine boundary, native wrapper, or a language-subset category."""
    m = NAMED.search(msg)
    if m:
        sym = m.group("sym")
        if sym.startswith("Prowl.Runtime.Physics2D.Native") or "Box2DWorld" in sym or "StepEvents" in sym or ".PB2" in sym:
            return "native wrapper", sym
        if sym.startswith("Prowl.") or sym.startswith("Prowl"):
            return "engine boundary", sym
        return "corelib gap", sym
    for name, rx in CATEGORIES:
        if rx.search(msg):
            return "subset: " + name, msg[:90]
    return "subset: other", msg[:90]


def loc(path):
    n = 0
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            t = line.strip()
            if t and not t.startswith("//") and not t.startswith("///") and not t.startswith("/*") and not t.startswith("*"):
                n += 1
    return n


def type_defined_in(name, paths):
    """Whether some file in `paths` declares a type called `name` (the last segment of a dotted name)."""
    short = re.escape(name.split(".")[-1])
    for p in paths:
        try:
            with open(p, encoding="utf-8-sig") as f:
                if re.search(r"\b(class|struct|interface|enum|record)\s+%s\b" % short, f.read()):
                    return True
        except OSError:
            pass
    return False


def scan_tier(name, files, corelib, context, extra_context=(), unavailable=()):
    # `context` is a list of files; `extra_context` the earlier tiers' program files
    """Compile the tier's files with its context. Files whose Roslyn errors block binding are dropped (and what they needed is
    reported) until the rest binds; the survivors then yield the compiler's own refusals."""
    work = tempfile.mkdtemp(prefix="ccs-scan-")
    try:
        live = list(files)
        blocked = {}
        for _ in range(len(files) + 1):
            rc, roslyn, refusals = run_compiler(list(context) + list(extra_context) + live, corelib, work)
            if not roslyn:
                break
            bad = {}
            for f, ln, code, msg in roslyn:
                bad.setdefault(f, []).append((ln, code, msg))
            drop = [f for f in live if f in bad]
            if not drop:
                # The errors are all in context files (the overlay, the generated PB2, the engine stand-ins): nothing of the tier's
                # was even looked at. That is a broken scanner, NOT a clean tier -- an earlier version marked every file of the tier
                # CLEAN here, which is how a context that stopped compiling turned into a report of 83% translatable.
                #
                # One legitimate case: the stand-ins name a type that a file of the program defines, and that file is itself blocked and so
                # was left out (the engine stand-ins mention PhysicsWorld2D, which lives in a file that needs the simulation). Then the
                # tier is blocked BY that type, and says so. Anything else is still a broken scanner.
                gone = list(unavailable) + [f for f in blocked]
                unexplained, explained = [], []
                for f, es in bad.items():
                    where = os.path.relpath(f, PROWL) if f.startswith(PROWL) else f
                    for ln, code, msg in es:
                        m = re.search(r"'([^']+)'", msg)
                        if code in ("CS0234", "CS0246") and m and type_defined_in(m.group(1), gone):
                            explained.append((ln, code, msg))
                        else:
                            unexplained.append("%s:%d: %s %s" % (where, ln, code, msg[:150]))
                if unexplained or not explained:
                    raise ScanError("the %s tier's context does not compile, so none of its files were checked:\n   %s"
                                    % (name, "\n   ".join(unexplained[:8])))
                for f in live:
                    blocked[f] = explained
                live = []
                break
            for f in drop:
                blocked[f] = bad[f]
                live.remove(f)
            if not live:
                break
        else:
            raise ScanError("could not get the %s tier to bind after dropping every blocked file" % name)

        by_file = collections.defaultdict(list)
        if live and not roslyn:
            if rc == 0 and refusals:
                raise ScanError("exit 0 with refusals")
            for f, ln, msg in refusals:
                if f in live:
                    by_file[f].append((ln, msg))
        rows = []
        for f in files:
            rel = os.path.relpath(f, PROWL)
            if f in blocked:
                names = collections.Counter()
                for ln, code, msg in blocked[f]:
                    t = re.search(r"'([^']+)'", msg)
                    names[t.group(1) if t else code] += 1
                rows.append({"file": rel, "loc": loc(f), "status": "blocked", "blocked_by": names.most_common(12), "refusals": [],
                             "errors": ["%d: %s %s" % (ln, code, msg[:160]) for ln, code, msg in blocked[f][:40]]})
            else:
                rs = [{"line": ln, "kind": classify(m)[0], "detail": classify(m)[1], "message": m} for ln, m in by_file.get(f, [])]
                rows.append({"file": rel, "loc": loc(f), "status": "clean" if not rs else "refused", "refusals": rs})
        return rows
    finally:
        shutil.rmtree(work, ignore_errors=True)


def tier_context(name, paths):
    """The context a tier needs: always the base + generated PB2; the managed Box2D wrapper for the simulation and engine tiers; and the
    Scene/SceneDispatcher stand-ins that name PhysicsWorld2D / Collider2D only where those are part of the program."""
    ctx = [paths["base"]] + REAL_CONTEXT
    ctx.append(paths["engine"] if name == "engine" else paths["scene_stub"])
    return ctx


def cmd_scan(args):
    corelib, paths, notes = make_overlay(args.overlay, verbose=args.verbose)
    report = []
    done_files = []
    never_bound = []   # files of earlier tiers that did not bind: left out of every later tier's context
    try:
        for name, desc, files in TIERS:
            if args.tier and name not in args.tier:
                done_files += files
                continue
            # earlier tiers' files are context for later ones, so the types they define bind
            prior = [f for f in done_files if f not in files]
            rows = scan_tier(name, files, corelib, tier_context(name, paths), extra_context=prior, unavailable=never_bound)
            report.append({"tier": name, "description": desc, "files": rows})
            # A file that did not bind cannot be context for the next tier: leaving it in made every later tier fail on ITS errors. Without
            # it, whatever needs its types is reported as blocked by them, which is the true state of affairs.
            blocked_here = {os.path.join(PROWL, r["file"]) for r in rows if r["status"] == "blocked"}
            never_bound += [f for f in files if f in blocked_here]
            done_files += [f for f in files if f not in blocked_here]
    except ScanError as e:
        print("ccsharp_scan: SCAN FAILED (not a result): %s" % e)
        return 2
    print_report(report, args)
    if args.json:
        with open(args.json, "w") as f:
            json.dump(report, f, indent=2)
        print("\nwrote %s" % args.json)
    return 0


def print_report(report, args):
    total_loc = clean_loc = total_files = clean_files = blocked_files = 0
    kinds = collections.Counter()
    symbols = collections.defaultdict(collections.Counter)
    for t in report:
        print("\n== %s: %s" % (t["tier"], t["description"]))
        for r in t["files"]:
            total_files += 1
            total_loc += r["loc"]
            if r["status"] == "clean":
                clean_files += 1
                clean_loc += r["loc"]
                print("   CLEAN    %-62s %4d lines" % (r["file"], r["loc"]))
            elif r["status"] == "blocked":
                blocked_files += 1
                print("   BLOCKED  %-62s %4d lines   needs: %s" % (r["file"], r["loc"], ", ".join("%s x%d" % x for x in r["blocked_by"][:6])))
                if args.verbose:
                    for e in r.get("errors", []):
                        print("             " + e)
            else:
                c = collections.Counter(x["kind"] for x in r["refusals"])
                print("   REFUSED  %-62s %4d lines   %d refusal(s): %s" % (r["file"], r["loc"], len(r["refusals"]), ", ".join("%s x%d" % x for x in c.most_common(5))))
                for x in r["refusals"]:
                    kinds[x["kind"]] += 1
                    symbols[x["kind"]][x["detail"]] += 1
                    if args.verbose:
                        print("             %s:%d  [%s] %s" % (os.path.basename(r["file"]), x["line"], x["kind"], x["message"][:150]))
    print("\n== summary   (refusal counts are a LOWER BOUND: a refusal at a declaration, such as a base list or an `in` parameter, can stop the")
    print("                compiler before it reaches the bodies. Fix the cheap ones and rescan to see the next layer.)")
    print("   files that translate unchanged : %d of %d   (%d of %d lines, %d%%)" % (clean_files, total_files, clean_loc, total_loc, 100 * clean_loc // max(total_loc, 1)))
    print("   files blocked before refusals   : %d  (cannot say more until the declarations they need exist)" % blocked_files)
    if kinds:
        print("   refusals by kind:")
        for k, n in kinds.most_common():
            top = ", ".join("%s x%d" % (s[:48], c) for s, c in symbols[k].most_common(3))
            print("      %-26s %4d   %s" % (k, n, top))


# ---------------------------------------------------------------------------------------------------------------------------------
#  Conformance: translate -> C -> gcc -> run, and compare with real .NET
# ---------------------------------------------------------------------------------------------------------------------------------

CONFORMANCE = [
    ("math", "MathConformance", [rt("Physics2D", f) for f in ("Pose2D.cs", "Collider2DGeometry.cs", "Collider2DOutline.cs")],
     os.path.join(HERE, "conformance", "MathConformance.cs")),
    ("handles", "HandleConformance", [rt("Physics2D", f) for f in ("HandleTable.cs", "TriggerSet.cs")],
     os.path.join(HERE, "conformance", "HandleConformance.cs")),
    ("registry", "RegistryConformance", [rt("Physics2D", f) for f in ("HandleTable.cs", "MaxInstancesAttribute.cs", "Arena.cs", "BodyRecord.cs", "JointRecord.cs", "Registry2D.cs", "Pose2D.cs")],
     os.path.join(HERE, "conformance", "RegistryConformance.cs")),
    # tier 1 of the 2D runtime: the node hierarchy and its transforms, against a naive model that recomputes every world from the root
    ("node", "NodeConformance", [rt("Physics2D", "MaxInstancesAttribute.cs"), rt("Physics2D", "Arena.cs"), rt("Physics2D", "Pose2D.cs"), c2("CoreLimits.cs"), c2("Node.cs")],
     os.path.join(HERE, "conformance", "NodeConformance.cs")),
    # tier 2: the scene, components and the order of the game's callbacks; scripts mutate the scene in the middle of dispatch
    ("lifecycle", "LifecycleConformance", CORE2D_FILES(), os.path.join(HERE, "conformance", "LifecycleConformance.cs"), True, True),
    # tier 3: physics components and the collision and trigger messages, against hand-worked numbers, on the real library
    ("physics-scene", "PhysicsSceneConformance", CORE2D_FILES(), os.path.join(HERE, "conformance", "PhysicsSceneConformance.cs"), True, True),
    # the draw batch a renderer is handed: layer order, a child of a rotated and scaled parent, a sprite on a falling body, recycling
    ("render-batch", "RenderBatchConformance", CORE2D_FILES(), os.path.join(HERE, "conformance", "RenderBatchConformance.cs"), True, False),
    # the simulation core, whole: native world, step, pose writes into the body arena, and the event stream, against the real library
    ("sim", "SimConformance", [rt("Physics2D", f) for f in ("HandleTable.cs", "TriggerSet.cs", "MaxInstancesAttribute.cs", "Arena.cs", "BodyRecord.cs", "JointRecord.cs", "Registry2D.cs", "Pose2D.cs", "SimCore2D.cs")],
     os.path.join(HERE, "conformance", "SimConformance.cs"), True),
    # `native` cases call the real Box2D shim through bindings generated from prowl_box2d.h (gen_pb2.py): on .NET through [LibraryImport], as
    # translated C linked against the same library. No program files: everything it needs is the generated bindings.
    ("native", "NativeConformance", [], os.path.join(HERE, "conformance", "NativeConformance.cs"), True),
]


def run_dotnet_reference(files, workdir, extra_env=None):
    proj = os.path.join(workdir, "ref.csproj")
    items = "\n".join('    <Compile Include="%s" />' % f for f in files)
    with open(proj, "w") as f:
        f.write('<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
                '<ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable><NuGetAudit>false</NuGetAudit>'
                '<AllowUnsafeBlocks>true</AllowUnsafeBlocks><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>\n'
                '  <ItemGroup>\n%s\n  </ItemGroup>\n</Project>\n' % items)
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", **(extra_env or {}))
    b = subprocess.run(["dotnet", "build", "-c", "Release", "-o", os.path.join(workdir, "bin"), proj], capture_output=True, text=True, env=env)
    if b.returncode != 0:
        return None, "the reference did not build under .NET:\n" + "\n".join(l for l in b.stdout.splitlines() if "error" in l)[:1500]
    r = subprocess.run(["dotnet", os.path.join(workdir, "bin", "ref.dll")], capture_output=True, text=True, env=env)
    return r.stdout, None


def native_library_dir():
    """The directory holding the built libprowl_box2d (python3 build.py native installs it under Libraries/<rid>/native), or None."""
    for rid_dir in sorted(glob.glob(os.path.join(PROWL, "Libraries", "*", "native"))):
        if any(os.path.exists(os.path.join(rid_dir, n)) for n in ("libprowl_box2d.so", "libprowl_box2d.dylib", "prowl_box2d.dll")):
            return rid_dir
    return None


def cmd_conformance(args):
    home = ccsharp_home()
    ccs2c = os.path.join(home, "crust", "ccs2c.py")
    if not os.path.exists(ccs2c):
        sys.exit("ccsharp_scan: no CCSharp at %s (run `python3 build.py deps`)" % home)
    cc = os.environ.get("CC") or shutil.which("cc") or shutil.which("gcc")
    failures = 0

    # The engine compiles ONE copy of the native API, generated from the header and committed; it must be what the header generates today.
    import gen_pb2
    try:
        committed = open(gen_pb2.RUNTIME_FILE, encoding="utf-8", newline="").read() if os.path.exists(gen_pb2.RUNTIME_FILE) else ""
        if committed != gen_pb2.runtime_text():
            print("FAIL bindings the committed PB2.Generated.cs is not what prowl_box2d.h generates (python3 tools/ccsharp/gen_pb2.py runtime)")
            failures += 1
        else:
            print("ok   bindings PB2.Generated.cs is current: %d native functions from prowl_box2d.h" % len(gen_pb2.parse_header().functions))
    except gen_pb2.GenError as e:
        print("FAIL bindings gen_pb2: %s" % e)
        failures += 1

    for case in CONFORMANCE:
        name, main, files, program = case[:4]
        native = len(case) > 4 and case[4]
        scripts = len(case) > 5 and case[5]
        work = tempfile.mkdtemp(prefix="ccs-conf-")
        try:
            allfiles = files + [program]
            if scripts:
                # the program's [Script] classes get their call sink, generated the way a game's build does it
                import gen_scripts
                sink = os.path.join(work, "Scripts.g.cs")
                try:
                    gen_scripts.generate(sink, [program])
                except gen_scripts.GenError as e:
                    print("FAIL %-8s gen_scripts: %s" % (name, e))
                    failures += 1
                    continue
                allfiles = allfiles + [sink]
            out = os.path.join(work, "out")
            flags, link, ref_files, ref_env = [], [], list(allfiles), None
            if native:
                libdir = native_library_dir()
                if libdir is None:
                    print("FAIL %-8s the native library is not built (python3 build.py native)" % name)
                    failures += 1
                    continue
                gen_pb2.generate(os.path.join(work, "gen"))
                nat_inc = os.path.join(PROWL, "Native", "Box2D")
                flags = ["--bindings=" + os.path.join(work, "gen", "c"), "--include=" + nat_inc]
                link = ["-I" + nat_inc, "-L" + libdir, "-lprowl_box2d", "-Wl,-rpath," + libdir]
                ref_files = [os.path.join(work, "gen", "net", "PB2.net.cs")] + allfiles
                ref_env = {"LD_LIBRARY_PATH": libdir + os.pathsep + os.environ.get("LD_LIBRARY_PATH", "")}
            t = subprocess.run([sys.executable, ccs2c] + allfiles + flags + ["--main=" + main, "--name=Conf", "--convert=" + out, "--c"], capture_output=True, text=True)
            if t.returncode != 0:
                print("FAIL %-8s translation refused:\n%s" % (name, (t.stdout + t.stderr)[-1500:]))
                failures += 1
                continue
            exe = os.path.join(work, "conf")
            g = subprocess.run([cc, "-w", "-O1", "-o", exe, os.path.join(out, "Conf.c")] + link + ["-lm"], capture_output=True, text=True)
            if g.returncode != 0:
                errs = [l for l in g.stderr.splitlines() if "error" in l]
                print("FAIL %-8s gcc rejected the generated C (%d errors):\n   %s" % (name, len(errs), "\n   ".join(e[:170] for e in errs[:6])))
                failures += 1
                continue
            c_out = subprocess.run([exe], capture_output=True, text=True).stdout
            ref, err = run_dotnet_reference(ref_files, work, ref_env)
            if ref is None:
                print("FAIL %-8s %s" % (name, err))
                failures += 1
            elif ref == c_out:
                print("ok   %-8s translated C and real .NET print identical output (%d lines)" % (name, len(c_out.splitlines())))
            else:
                a, b2 = ref.splitlines(), c_out.splitlines()
                first = next((i for i in range(max(len(a), len(b2))) if i >= len(a) or i >= len(b2) or a[i] != b2[i]), 0)
                print("FAIL %-8s outputs differ at line %d:\n   .NET: %s\n   C   : %s" % (name, first + 1, a[first] if first < len(a) else "(none)", b2[first] if first < len(b2) else "(none)"))
                failures += 1
        finally:
            shutil.rmtree(work, ignore_errors=True)
    return 1 if failures else 0


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[1], formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    o = sub.add_parser("overlay", help="write the overlay and stop")
    o.add_argument("--out", dest="overlay", default=DEFAULT_OVERLAY)
    s = sub.add_parser("scan", help="scan the 2D engine and report what translates")
    s.add_argument("--overlay", default=DEFAULT_OVERLAY, help="where to write the overlay (default %s)" % DEFAULT_OVERLAY)
    s.add_argument("--tier", action="append", help="only this tier (repeatable): " + ", ".join(t[0] for t in TIERS))
    s.add_argument("--json", help="also write the report as JSON")
    s.add_argument("-v", "--verbose", action="store_true", help="list every refusal with its line")
    sub.add_parser("conformance", help="translate to C, build with gcc, run, and diff with real .NET")
    a = ap.parse_args()
    if a.cmd == "overlay":
        make_overlay(a.overlay, verbose=True)
        return 0
    if a.cmd == "scan":
        return cmd_scan(a)
    return cmd_conformance(a)


if __name__ == "__main__":
    sys.exit(main())
