#!/usr/bin/env python3
"""ccsharp_scan.py -- how much of Prowl's 2D engine can CCSharp translate to C?

CCSharp (https://github.com/crustos/CCSharp) is a whole-program compiler: C# -> Crust C++ subset -> C. It stops at the first
Roslyn error, and only reports what is outside the Crust subset once the program binds. Prowl's 2D code names types that
CCSharp's corelib has never heard of (Float2, MonoBehaviour, the Box2D wrapper), so pointing the compiler straight at it
reports nothing useful. This script builds an OVERLAY that makes it bind, then runs the compiler and sorts what it says.

    python3 tools/ccsharp/ccsharp_scan.py overlay                 write the overlay to /tmp/prowl-ccs-overlay (and stop)
    python3 tools/ccsharp/ccsharp_scan.py scan [--tier NAME] [--json FILE] [-v]
    python3 tools/ccsharp/ccsharp_scan.py conformance             translate -> C -> gcc -> run, and diff with real .NET

The overlay is a COPY of CCSharp's corelib (never edited in place) plus two kinds of declaration:

  * corelib gaps   .NET members the program uses and CCSharp has not implemented (Stack<T>, Array.Resize, Span<T> ...). Declared
                   without [Cpp], so using one is refused BY NAME: that is the "corelib gap" row of the report. Each is added
                   only if the corelib does not already have it, so the overlay shrinks as CCSharp grows.
  * context        subset C# with trivial bodies that stands in for what the C core would provide: the math structs, the engine
                   types (MonoBehaviour, GameObject, Transform ...) and the native Box2D calls (generated from the real PB2.cs).
                   Compiled with the program and never reported on, so what is reported is the 2D code's OWN constructs: how it
                   uses those types still counts (a generic method, a null, a lambda), what they are does not.

Nothing here is a claim that the code RUNS in C. The `conformance` command is the claim: it translates programs, builds them with gcc
and compares their output with the same C# on real .NET.

CCSharp, crust and coost are cloned beside this repository (see `python3 build.py deps`):  ../CCSharp  ../crust  ../coost
"""
import argparse
import collections
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
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


# A tier is a set of files scanned TOGETHER (the compiler is whole-program). Later tiers include the earlier ones as context
# so that their types bind, but only the tier's own files are reported on.
TIERS = [
    ("math", "engine-independent geometry, outlines and pose maths",
     [rt("Physics2D", f) for f in ("Pose2D.cs", "Collider2DGeometry.cs", "Collider2DOutline.cs")]),
    ("registry", "the body / collider slot registry",
     [rt("Physics2D", "SlotRegistry.cs")]),
    ("simulation", "the simulation core: stepping, ownership, events, queries",
     [rt("Physics2D", "PhysicsSimulation2D.cs")]),
    ("engine", "the engine-facing world and components",
     [rt("Physics2D", "Engine", "PhysicsWorld2D.cs"), rt("Physics2D", "Engine", "Physics2DTypes.cs")]
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

# What the simulation tier calls on the managed Box2D wrapper. Signatures only; the C build calls the shim (pb2_*) directly.
CONTEXT_NATIVE_WRAPPER = """
using System;
namespace Prowl.Runtime.Physics2D.Native {
  public ref struct StepEvents {
    public ReadOnlySpan<PB2BodyMove> Moves;
    public ReadOnlySpan<PB2ContactEvent> Contacts;
    public ReadOnlySpan<PB2SensorEvent> Sensors;
    public int BeginCount;
    public int AwakeBodyCount;
    public ReadOnlySpan<PB2ContactEvent> ContactBegins { get { return Contacts; } }
    public ReadOnlySpan<PB2ContactEvent> ContactEnds { get { return Contacts; } }
  }
  public sealed class Box2DWorld : IDisposable {
    public static bool Exists { get { return false; } }
    public static Box2DWorld Create(float gx, float gy, int workers) { return new Box2DWorld(); }
    public void SetGravity(float x, float y) {}
    public void SetLayerMatrix(ReadOnlySpan<uint> rows) {}
    public void QueueTransform(uint body, float x, float y, float angle, bool kinematicTarget) {}
    public StepEvents Step(float dt, int subSteps) { return new StepEvents(); }
    public bool Raycast(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, bool hitSensors, out PB2RayHit hit) { hit = new PB2RayHit(); return false; }
    public int RaycastAll(float ox, float oy, float dx, float dy, float maxDistance, uint layerMask, bool hitSensors, Span<PB2RayHit> results) { return 0; }
    public int OverlapPoint(float x, float y, uint layerMask, bool hitSensors, Span<int> colliders) { return 0; }
    public int OverlapCircle(float cx, float cy, float radius, uint layerMask, bool hitSensors, Span<int> colliders) { return 0; }
    public int OverlapBox(float cx, float cy, float halfW, float halfH, float angle, uint layerMask, bool hitSensors, Span<int> colliders) { return 0; }
    public void Dispose() {}
  }
}
"""


def gen_pb2_context():
    """The PB2 structs, enums and static methods, generated from the real PB2.cs so they cannot drift from it. Every
    [LibraryImport] method becomes a method with a trivial body; the structs are copied as they are."""
    with open(rt("Physics2D", "Native", "PB2.cs"), encoding="utf-8-sig") as f:
        text = f.read()
    head, _, tail = text.partition("internal static unsafe partial class PB2")
    head = re.sub(r"^using System\.Runtime\.[\w.]+;\s*$", "", head, flags=re.M)
    head = re.sub(r"\[StructLayout\([^\]]*\)\]\s*", "", head)
    head = head.replace("internal ", "public ")
    methods = []
    for m in re.finditer(r"\[LibraryImport\([^\]]*\)\]\s*public static partial (?P<ret>[\w*]+) (?P<name>\w+)\((?P<params>[^)]*)\);", tail):
        ret = m.group("ret")
        body = "" if ret == "void" else ("return 0;" if ret in ("int", "uint") else "return default(%s);" % ret)
        methods.append("    public static %s %s(%s) { %s }" % (ret, m.group("name"), m.group("params"), body))
    consts = "\n".join("    " + l.strip() for l in tail.splitlines() if re.match(r"\s*public const ", l))
    return head + "\npublic static unsafe class PB2 {\n" + consts + "\n" + "\n".join(methods) + "\n}\n", len(methods)


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
    pb2, n_pb2 = gen_pb2_context()
    files = {"base": CONTEXT_BASE, "pb2": pb2, "native_wrapper": CONTEXT_NATIVE_WRAPPER, "engine": CONTEXT_ENGINE, "scene_stub": CONTEXT_SCENE_STUB}
    paths = {}
    for k, text in files.items():
        paths[k] = os.path.join(ctx, "prowl_%s.cs" % k)
        with open(paths[k], "w", encoding="utf-8") as f:
            f.write(text)
    notes.append("PB2 context generated from PB2.cs (%d native methods)" % n_pb2)
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


def scan_tier(name, files, corelib, context, extra_context=()):
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
                # the errors are in context files or the facade: nothing of the tier's to drop
                for f, es in bad.items():
                    blocked.setdefault("(context) " + os.path.relpath(f, PROWL), es)
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
    ctx = [paths["base"], paths["pb2"]] + REAL_CONTEXT
    if name in ("simulation", "engine"):
        ctx.append(paths["native_wrapper"])
    ctx.append(paths["engine"] if name == "engine" else paths["scene_stub"])
    return ctx


def cmd_scan(args):
    corelib, paths, notes = make_overlay(args.overlay, verbose=args.verbose)
    report = []
    done_files = []
    try:
        for name, desc, files in TIERS:
            if args.tier and name not in args.tier:
                done_files += files
                continue
            # earlier tiers' files are context for later ones, so the types they define bind
            prior = [f for f in done_files if f not in files]
            rows = scan_tier(name, files, corelib, tier_context(name, paths), extra_context=prior)
            report.append({"tier": name, "description": desc, "files": rows})
            done_files += files
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
]


def run_dotnet_reference(files, workdir):
    proj = os.path.join(workdir, "ref.csproj")
    items = "\n".join('    <Compile Include="%s" />' % f for f in files)
    with open(proj, "w") as f:
        f.write('<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
                '<ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup>\n'
                '  <ItemGroup>\n%s\n  </ItemGroup>\n</Project>\n' % items)
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    b = subprocess.run(["dotnet", "build", "-c", "Release", "-o", os.path.join(workdir, "bin"), proj], capture_output=True, text=True, env=env)
    if b.returncode != 0:
        return None, "the reference did not build under .NET:\n" + "\n".join(l for l in b.stdout.splitlines() if "error" in l)[:1500]
    r = subprocess.run(["dotnet", os.path.join(workdir, "bin", "ref.dll")], capture_output=True, text=True, env=env)
    return r.stdout, None


def cmd_conformance(args):
    home = ccsharp_home()
    ccs2c = os.path.join(home, "crust", "ccs2c.py")
    if not os.path.exists(ccs2c):
        sys.exit("ccsharp_scan: no CCSharp at %s (run `python3 build.py deps`)" % home)
    cc = os.environ.get("CC") or shutil.which("cc") or shutil.which("gcc")
    failures = 0
    for name, main, files, program in CONFORMANCE:
        work = tempfile.mkdtemp(prefix="ccs-conf-")
        try:
            allfiles = files + [program]
            out = os.path.join(work, "out")
            t = subprocess.run([sys.executable, ccs2c] + allfiles + ["--main=" + main, "--name=Conf", "--convert=" + out, "--c"], capture_output=True, text=True)
            if t.returncode != 0:
                print("FAIL %-8s translation refused:\n%s" % (name, (t.stdout + t.stderr)[-1500:]))
                failures += 1
                continue
            exe = os.path.join(work, "conf")
            g = subprocess.run([cc, "-w", "-O1", "-o", exe, os.path.join(out, "Conf.c"), "-lm"], capture_output=True, text=True)
            if g.returncode != 0:
                errs = [l for l in g.stderr.splitlines() if "error" in l]
                print("FAIL %-8s gcc rejected the generated C (%d errors):\n   %s" % (name, len(errs), "\n   ".join(e[:170] for e in errs[:6])))
                failures += 1
                continue
            c_out = subprocess.run([exe], capture_output=True, text=True).stdout
            ref, err = run_dotnet_reference(allfiles, work)
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
