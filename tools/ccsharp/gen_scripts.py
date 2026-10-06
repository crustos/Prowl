#!/usr/bin/env python3
"""gen_scripts.py -- the call sink for a game's scripts.

    python3 tools/ccsharp/gen_scripts.py OUT.cs  SCRIPT_FILE.cs [SCRIPT_FILE.cs ...]

The 2D runtime's scene (Prowl.Core2D/Scene2D.cs) cannot call game code: the C# subset it is written in has no virtual dispatch, interface or delegate.
It produces a stream of calls instead (`while (scene.NextCall()) ...`), and this writes the one piece that performs them: for each class marked
[Script], a pool of its instances (slots are recycled when a component is freed), a factory that makes a component of it on a node, and
`Scripts.Invoke(scene)`, which calls the right method of the right object for the call in `scene.CallKind` / `scene.CallComponent`, and `Scripts.Tick(scene, dt)`,
the engine's fixed-step game loop over it (so a game's Main is `Scripts.Init(); ... Scripts.Tick(scene, dt)` once a frame).

What it reads from a script class (and refuses, with the file and line, when it is wrong):
  * `[Script]` or `[Script(Order = n)]`           the execution order, lower first
  * `[MaxInstances(N)]`                            how many can exist at once; N must be an integer literal here
  * `public Component Self;`                       set by the factory (Scripts.AddName(node) returns the script; Scripts.AttachName(node) returns nothing)
  * the callbacks, by name and signature:
        Start() Update() FixedUpdate() LateUpdate() OnEnable() OnDisable()
        OnCollisionBegin2D(Collision2D c)  OnCollisionEnd2D(Collision2D c)
        OnTriggerEnter2D(Collider2D other) OnTriggerStay2D(Collider2D other) OnTriggerExit2D(Collider2D other)
  * `public void Reset()`                           optional: puts a recycled instance back to its starting state. Without it a recycled
                                                   instance keeps the fields it had (field initialisers run only for a new object).

This is the whole of what the .NET engine does by reflection (SceneDispatcher.Compute), done once, when the program is built.
"""
import os
import re
import sys

CALLBACKS = [
    # name, bit, parameter type (None: no parameters), expression passed
    ("OnEnable", "OnEnable", None, None),
    ("OnDisable", "OnDisable", None, None),
    ("Start", "Start", None, None),
    ("FixedUpdate", "FixedUpdate", None, None),
    ("Update", "Update", None, None),
    ("LateUpdate", "LateUpdate", None, None),
    ("OnCollisionBegin2D", "CollisionBegin2D", "Collision2D", "scene.Collision"),
    ("OnCollisionEnd2D", "CollisionEnd2D", "Collision2D", "scene.Collision"),
    ("OnTriggerEnter2D", "TriggerEnter2D", "Collider2D", "scene.Collision.Other"),
    ("OnTriggerStay2D", "TriggerStay2D", "Collider2D", "scene.Collision.Other"),
    ("OnTriggerExit2D", "TriggerExit2D", "Collider2D", "scene.Collision.Other"),
]
FIRST_KIND = 100


class GenError(Exception):
    pass


def line_of(text, pos):
    return text.count("\n", 0, pos) + 1


def strip_comments(text):
    """Blank out comments and string contents, keeping every offset and newline, so a regex never matches inside them."""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(re.sub(r"[^\n]", " ", text[i:j]))
            i = j
        elif c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            out.append('"' + re.sub(r"[^\n]", " ", text[i + 1:j]) + '"')
            i = j + 1
        else:
            out.append(c)
            i += 1
    return "".join(out)


def body_of(text, open_brace):
    depth = 0
    for i in range(open_brace, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[open_brace + 1:i], i
    raise GenError("unbalanced braces")


def scan_file(path):
    with open(path, encoding="utf-8-sig") as f:
        raw = f.read()
    text = strip_comments(raw)
    namespaces = re.findall(r"^\s*namespace\s+([\w.]+)", text, re.M)
    scripts = []
    cls = re.compile(r"((?:\[[^\]]*\]\s*)+)(?:(?:public|internal)\s+)?(?:sealed\s+)?class\s+(\w+)\s*(?::[^{]*)?\{")
    for m in cls.finditer(text):
        attrs = m.group(1)
        if not re.search(r"\bScript\b", attrs):
            continue
        name = m.group(2)
        where = "%s:%d" % (path, line_of(text, m.start()))
        order = 0
        om = re.search(r"\bScript\s*\(\s*(?:Order\s*=\s*)?(-?\d+)\s*\)", attrs)
        if om:
            order = int(om.group(1))
        cm = re.search(r"\bMaxInstances\s*\(\s*([^)]*?)\s*\)", attrs)
        if not cm or not re.fullmatch(r"\d+", cm.group(1)):
            raise GenError("%s: script %s needs [MaxInstances(N)] with N an integer literal" % (where, name))
        capacity = int(cm.group(1))
        if capacity < 1:
            raise GenError("%s: script %s: MaxInstances must be at least 1" % (where, name))
        body, _ = body_of(text, m.end() - 1)
        if not re.search(r"\bpublic\s+Component\s+Self\s*;", body):
            raise GenError("%s: script %s needs `public Component Self;` (the factory sets it)" % (where, name))
        has = {}
        for cb_name, bit, ptype, _arg in CALLBACKS:
            for mm in re.finditer(r"\bpublic\s+void\s+%s\s*\(([^)]*)\)" % cb_name, body):
                params = mm.group(1).strip()
                ok = (params == "") if ptype is None else bool(re.fullmatch(r"%s\s+\w+" % ptype, params))
                if not ok:
                    raise GenError("%s: script %s: %s must be `public void %s(%s)`" % (where, name, cb_name, cb_name, (ptype + " x") if ptype else ""))
                has[cb_name] = True
        # a callback that is almost right is a callback that silently never runs: refuse the lookalikes
        for mm in re.finditer(r"\bvoid\s+(\w+)\s*\(", body):
            nm = mm.group(1)
            if nm in [c[0] for c in CALLBACKS] and nm not in has:
                raise GenError("%s: script %s: %s is declared but is not `public void` with the expected parameters, so it would never be called" % (where, name, nm))
        has_reset = bool(re.search(r"\bpublic\s+void\s+Reset\s*\(\s*\)", body))
        scripts.append(dict(name=name, order=order, capacity=capacity, callbacks=[c for c in CALLBACKS if c[0] in has], reset=has_reset, where=where))
    return namespaces, scripts


def generate(out_path, sources):
    namespaces, scripts = [], []
    for p in sources:
        ns, sc = scan_file(p)
        namespaces += ns
        scripts += sc
    seen = {}
    for s in scripts:
        if s["name"] in seen:
            raise GenError("two scripts are called %s (%s and %s)" % (s["name"], seen[s["name"]], s["where"]))
        seen[s["name"]] = s["where"]
    scripts.sort(key=lambda s: s["name"])

    L = []
    L.append("// <auto-generated>")
    L.append("// Generated by tools/ccsharp/gen_scripts.py from: " + ", ".join(os.path.basename(p) for p in sources))
    L.append("// Do not edit: change the scripts and regenerate. This is the call sink for Scene2D.NextCall(); see Prowl.Core2D/ScriptAttribute.cs.")
    L.append("// </auto-generated>")
    L.append("")
    L.append("using System.Collections.Generic;")
    for ns in sorted(set(namespaces) - {"Prowl.Core2D"}):
        L.append("using %s;" % ns)
    L.append("")
    L.append("namespace Prowl.Core2D;")
    L.append("")
    L.append("internal static class ScriptKind")
    L.append("{")
    for i, s in enumerate(scripts):
        L.append("    public const int %s = %d;" % (s["name"], FIRST_KIND + i))
    L.append("}")
    L.append("")
    L.append("internal static class Scripts")
    L.append("{")
    for s in scripts:
        n = s["name"]
        L.append("    private static %s[] _%s;" % (n, n))
        L.append("    private static List<int> _%sFree;" % n)
        L.append("    private static int _%sHigh;" % n)
    L.append("")
    L.append("    /// <summary>Makes the pools. Call once, before any script is added.</summary>")
    L.append("    public static void Init()")
    L.append("    {")
    for s in scripts:
        n = s["name"]
        L.append("        _%s = new %s[%d];" % (n, n, s["capacity"]))
        L.append("        _%sFree = new List<int>();" % n)
        L.append("        _%sHigh = 0;" % n)
    L.append("    }")
    L.append("")
    for s in scripts:
        n, cap = s["name"], s["capacity"]
        mask = " | ".join("Callbacks.%s" % c[1] for c in s["callbacks"]) or "0"
        L.append("    /// <summary>A new %s on a node, enabled if the node is active; null if all %d are in use or the node is not live.</summary>" % (n, cap))
        L.append("    public static %s Add%s(Node node)" % (n, n))
        L.append("    {")
        L.append("        int slot;")
        L.append("        if (_%sFree.Count > 0)" % n)
        L.append("        {")
        L.append("            slot = _%sFree[_%sFree.Count - 1];" % (n, n))
        L.append("            _%sFree.RemoveAt(_%sFree.Count - 1);" % (n, n))
        L.append("        }")
        L.append("        else")
        L.append("        {")
        L.append("            if (_%sHigh >= %d) return null;" % (n, cap))
        L.append("            slot = _%sHigh;" % n)
        L.append("            _%sHigh++;" % n)
        L.append("        }")
        L.append("        %s s = _%s[slot];" % (n, n))
        L.append("        if (s == null)")
        L.append("        {")
        L.append("            s = new %s();" % n)
        L.append("            _%s[slot] = s;" % n)
        L.append("        }")
        if s["reset"]:
            L.append("        else s.Reset();")
        L.append("        Component c = Scene2D.Current.Create(node, ScriptKind.%s, slot, %s, %d);" % (n, mask, s["order"]))
        L.append("        if (c == null)")
        L.append("        {")
        L.append("            _%sFree.Add(slot);" % n)
        L.append("            return null;")
        L.append("        }")
        L.append("        s.Self = c;")
        L.append("        Scene2D.Current.Finish(c);                   // enabled now, so the script is set up before its OnEnable can be delivered")
        L.append("        return s;")
        L.append("    }")
        L.append("")
        L.append("    /// <summary>The same, for a caller that does not need the script: it returns nothing, which is what native code can call when the script is managed (--dna).</summary>")
        L.append("    public static void Attach%s(Node node) { Add%s(node); }" % (n, n))
        L.append("")
        L.append("    /// <summary>The %s a component is, or null.</summary>" % n)
        L.append("    public static %s As%s(Component c)" % (n, n))
        L.append("    {")
        L.append("        if (c == null || c.Kind != ScriptKind.%s) return null;" % n)
        L.append("        return _%s[c.Slot];" % n)
        L.append("    }")
        L.append("")
    L.append("    private static float _accumulator;")
    L.append("")
    L.append("    /// <summary>The most fixed steps one frame may catch up on, as in the .NET engine (Time.MaxFixedIterations).</summary>")
    L.append("    public const int MaxFixedIterations = 3;")
    L.append("")
    L.append("    /// <summary>Delivers every call of the step or frame that was just begun.</summary>")
    L.append("    public static void Pump(Scene2D scene)")
    L.append("    {")
    L.append("        while (scene.NextCall()) Invoke(scene);")
    L.append("    }")
    L.append("")
    L.append("    /// <summary>")
    L.append("    /// One frame of the game loop, the .NET engine's (Game.cs): the frame's time goes into an accumulator, which is paid out in fixed steps (at")
    L.append("    /// most <see cref=\"MaxFixedIterations\"/>, and a backlog beyond that is dropped rather than replayed), then the frame itself runs, told how far")
    L.append("    /// into the next fixed step it is.")
    L.append("    /// </summary>")
    L.append("    public static void Tick(Scene2D scene, float dt)")
    L.append("    {")
    L.append("        _accumulator += dt;")
    L.append("        int count = 0;")
    L.append("        while (_accumulator >= scene.FixedDeltaTime && count < MaxFixedIterations)")
    L.append("        {")
    L.append("            scene.BeginFixedStep(scene.FixedDeltaTime);")
    L.append("            Pump(scene);")
    L.append("            _accumulator -= scene.FixedDeltaTime;")
    L.append("            count++;")
    L.append("        }")
    L.append("        if (_accumulator >= scene.FixedDeltaTime) _accumulator = 0f;")
    L.append("        float alpha = _accumulator / scene.FixedDeltaTime;")
    L.append("        if (alpha > 1f) alpha = 1f;")
    L.append("        scene.BeginFrame(dt, alpha);")
    L.append("        Pump(scene);")
    L.append("    }")
    L.append("")
    L.append("    /// <summary>Performs the call the scene has just produced: <c>scene.CallKind</c> on <c>scene.CallComponent</c>.</summary>")
    L.append("    public static void Invoke(Scene2D scene)")
    L.append("    {")
    L.append("        Component c = scene.CallComponent;")
    L.append("        int kind = scene.CallKind;")
    L.append("        if (kind == Callbacks.Release)")
    L.append("        {")
    L.append("            Release(c);")
    L.append("            return;")
    L.append("        }")
    for s in scripts:
        L.append("        if (c.Kind == ScriptKind.%s)" % s["name"])
        L.append("        {")
        L.append("            Invoke%s(scene, kind, _%s[c.Slot]);" % (s["name"], s["name"]))
        L.append("            return;")
        L.append("        }")
    L.append("    }")
    L.append("")
    for s in scripts:
        n = s["name"]
        L.append("    private static void Invoke%s(Scene2D scene, int kind, %s o)" % (n, n))
        L.append("    {")
        for cb_name, bit, ptype, arg in s["callbacks"]:
            L.append("        if (kind == Callbacks.%s) { o.%s(%s); return; }" % (bit, cb_name, arg or ""))
        L.append("    }")
        L.append("")
    L.append("    // A component is being freed: its script's slot can be used again.")
    L.append("    private static void Release(Component c)")
    L.append("    {")
    for s in scripts:
        L.append("        if (c.Kind == ScriptKind.%s) { _%sFree.Add(c.Slot); return; }" % (s["name"], s["name"]))
    L.append("    }")
    L.append("}")
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(L) + "\n")
    return scripts


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    try:
        scripts = generate(argv[0], argv[1:])
    except GenError as e:
        print("gen_scripts: %s" % e)
        return 1
    print("gen_scripts: %d script(s) -> %s" % (len(scripts), argv[0]))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
