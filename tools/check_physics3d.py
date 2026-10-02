#!/usr/bin/env python3
"""check_physics3d.py -- does the 3D switch still hold?

Prowl builds without 3D physics by default (ProwlPhysics3D=false in Directory.Build.props). 3D is kept, but it is NOT maintained: it
is there for someone else to take over. The seam that makes that possible is small: the 3D folders are left out of the build, and
everything that touches them is wrapped in `#if PROWL_PHYSICS_3D`. This script checks that the seam has not been broken by a change
made without 3D in mind (a new file that mentions Rigidbody3D, a using of Jitter2 outside the 3D folders ...).

It needs no NuGet access and builds nothing: it compiles the sources with the SDK's own Roslyn (csc) against the reference
assemblies only. Packages such as Prowl.Echo are therefore unresolved in BOTH builds, which is the point: the same unresolved-package
noise is in each, so only the DIFFERENCE between them means anything.

    ON   every file, PROWL_PHYSICS_3D defined          (what the 3D-enabled build sees)
    OFF  3D folders removed, nothing defined           (what the default build sees)

The check passes when every diagnostic in OFF also appears in ON, ignoring line numbers. A diagnostic that is only in OFF means code
outside the 3D folders now needs a 3D type (or Jitter) and is not guarded.

    python3 tools/check_physics3d.py [--project Prowl.Runtime ...] [-v]
"""
import argparse
import collections
import glob
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_PROJECTS = ["Prowl.Runtime", "Prowl.Editor", "Prowl.Runtime.Test", "Prowl.Editor.Test", "Players"]

# What the Prowl.Runtime csproj removes when ProwlPhysics3D is not 'true'.
THREE_D_FOLDERS = ("Prowl.Runtime/Physics/", "Prowl.Runtime/Components/Physics/")
# Test files the Prowl.Runtime.Test csproj removes under the same condition.
THREE_D_TESTS = ("AnimatorRagdollTests.cs", "CharacterControllerTests.cs", "PhysicsTests.cs", "WheelTests.cs")

DIAGNOSTIC = re.compile(r"^(?P<file>.+?)\((?P<line>\d+),\d+\): error (?P<code>CS\d+): (?P<msg>.*?)(?: \[.*)?$")


def find_csc():
    hits = sorted(glob.glob("/usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll") + glob.glob("/usr/share/dotnet/sdk/*/Roslyn/bincore/csc.dll")
                  + glob.glob("/usr/local/share/dotnet/sdk/*/Roslyn/bincore/csc.dll") + glob.glob(r"C:\Program Files\dotnet\sdk\*\Roslyn\bincore\csc.dll"))
    return hits[-1] if hits else None


def find_refs():
    hits = sorted(glob.glob("/usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net*") + glob.glob("/usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net*")
                  + glob.glob("/usr/local/share/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net*") + glob.glob(r"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\*\ref\net*"))
    return hits[-1] if hits else None


def sources(projects, three_d):
    out = []
    for proj in projects:
        base = os.path.join(ROOT, proj)
        for dp, dn, fn in os.walk(base):
            dn[:] = [d for d in dn if d not in ("bin", "obj")]
            for f in fn:
                if not f.endswith(".cs"):
                    continue
                path = os.path.join(dp, f)
                rel = os.path.relpath(path, ROOT).replace("\\", "/")
                if not three_d:
                    if any(rel.startswith(p) for p in THREE_D_FOLDERS):
                        continue
                    if proj == "Prowl.Runtime.Test" and f in THREE_D_TESTS:
                        continue
                out.append(path)
    return sorted(out)


def compile_diagnostics(csc, refs, files, defines):
    with tempfile.TemporaryDirectory() as tmp:
        rsp = ["/nologo", "/target:library", "/nostdlib", "/langversion:preview", "/nullable:enable", "/unsafe", "/out:" + os.path.join(tmp, "x.dll")]
        rsp += ["/define:" + d for d in defines] + ["/reference:" + r for r in glob.glob(os.path.join(refs, "*.dll"))] + files
        path = os.path.join(tmp, "args.rsp")
        with open(path, "w") as f:
            f.write("\n".join('"%s"' % a if " " in a else a for a in rsp))
        p = subprocess.run(["dotnet", csc, "@" + path], capture_output=True, text=True)
    found = collections.Counter()
    where = {}
    for line in (p.stdout + p.stderr).splitlines():
        m = DIAGNOSTIC.match(line)
        if m:
            key = (os.path.basename(m.group("file")), m.group("code"), m.group("msg"))
            found[key] += 1
            where.setdefault(key, (os.path.relpath(m.group("file"), ROOT), int(m.group("line"))))
    return found, where


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[1])
    ap.add_argument("--project", action="append", help="a project directory to include (default: %s)" % ", ".join(DEFAULT_PROJECTS))
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args()
    projects = a.project or DEFAULT_PROJECTS

    csc, refs = find_csc(), find_refs()
    if not csc or not refs:
        print("check_physics3d: needs the .NET SDK (csc) and its reference pack. Not found.")
        return 2

    on, _ = compile_diagnostics(csc, refs, sources(projects, True), ["PROWL_PHYSICS_3D"])
    off, where = compile_diagnostics(csc, refs, sources(projects, False), [])
    if not on:
        print("check_physics3d: the 3D-on compile reported no diagnostics at all, so the check proves nothing (is the SDK usable?).")
        return 2
    new = off - on
    print("3D on : %d diagnostics (the same unresolved packages appear in both builds)" % sum(on.values()))
    print("3D off: %d diagnostics" % sum(off.values()))
    if not new:
        print("ok: nothing outside the 3D folders needs a 3D type. The seam holds.")
        return 0
    print("FAIL: %d diagnostic(s) exist only in the default (3D off) build. Code outside the 3D folders needs 3D physics and is not guarded"
          " with #if PROWL_PHYSICS_3D:" % sum(new.values()))
    for key in sorted(new, key=lambda k: where[k]):
        f, ln = where[key]
        print("   %s:%d  %s %s" % (f, ln, key[1], key[2][:140]))
    return 1


if __name__ == "__main__":
    sys.exit(main())
