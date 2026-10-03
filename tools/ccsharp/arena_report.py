#!/usr/bin/env python3
"""arena_report.py -- how much memory does each arena class of the 2D engine occupy, and does it fit in cache?

    python3 tools/ccsharp/arena_report.py [--l1 BYTES] [--l2 BYTES]       (or: python3 build.py arenas)

An arena class (`[MaxInstances(N)]`, see Prowl.Runtime/Physics2D/MaxInstancesAttribute.cs and Arena.cs) occupies at most N x sizeof(class) bytes, always: its slots are static.
So the capacities in PhysicsLimits are the engine's memory budget, and this prints it: it translates the arena classes with CCSharp, has the C
compiler say what sizeof really is (padding included), and tabulates bytes against the caches. Nothing here is estimated.

Two columns matter beyond the arena itself. The registry finds a record by index through a table of pointers, 8 bytes per slot, and the
integer table that says which slots are live costs 4. Those are counted as "tables". A table of what N would cost at other capacities follows,
because the useful question is usually "what if it were 2048?", not "what is it now?".
"""
import argparse
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ccsharp_scan as scan   # noqa: E402

CAPACITIES = [64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768]


def human(n):
    if n >= 1 << 20:
        return "%.2f MiB" % (n / (1 << 20))
    if n >= 1 << 10:
        return "%.1f KiB" % (n / (1 << 10))
    return "%d B" % n


def build_c(work):
    """Translate the whole simulation core (the 'sim' conformance program, which uses every arena class); returns the generated C and the
    flags the probe needs to compile it."""
    import gen_pb2
    case = next(c for c in scan.CONFORMANCE if c[0] == "sim")
    files = case[2] + [case[3]]
    ccs2c = os.path.join(scan.ccsharp_home(), "crust", "ccs2c.py")
    out = os.path.join(work, "out")
    gen_pb2.generate(os.path.join(work, "gen"))
    nat_inc = os.path.join(scan.PROWL, "Native", "Box2D")
    flags = ["--bindings=" + os.path.join(work, "gen", "c"), "--include=" + nat_inc]
    r = subprocess.run([sys.executable, ccs2c] + files + flags + ["--main=" + case[1], "--name=Arenas", "--convert=" + out, "--c"], capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit("arena_report: translation refused:\n" + (r.stdout + r.stderr)[-1500:])
    return os.path.join(out, "Arenas.c"), ["-I" + nat_inc]


def measure(c_file, work, cflags):
    """(class, capacity, sizeof) for every arena class in the generated C, with sizeof taken from the C compiler."""
    with open(c_file) as f:
        text = f.read()
    arenas = [(m.group(1), int(m.group(2))) for m in re.finditer(r"static const int (\w+)___max_instances = (\d+);", text)]
    if not arenas:
        sys.exit("arena_report: no arena class in the generated C")
    probe = os.path.join(work, "probe.c")
    with open(probe, "w") as f:
        f.write("#define main program_main\n#include \"%s\"\n#undef main\n#include <stdio.h>\nint main(void) {\n" % c_file)
        for name, _ in arenas:
            f.write("    printf(\"%s %%zu\\n\", sizeof(%s));\n" % (name, name))
        f.write("    return 0;\n}\n")
    exe = os.path.join(work, "probe")
    cc = os.environ.get("CC") or "cc"
    # nothing here is run except the probe, which only calls sizeof: the native functions the translated code mentions need not be linked
    g = subprocess.run([cc, "-w", "-o", exe, probe] + cflags + ["-Wl,--unresolved-symbols=ignore-all", "-lm"], capture_output=True, text=True)
    if g.returncode != 0:
        sys.exit("arena_report: could not measure:\n" + g.stderr[-800:])
    sizes = dict((l.split()[0], int(l.split()[1])) for l in subprocess.run([exe], capture_output=True, text=True).stdout.splitlines())
    return [(n.split("_")[-1], cap, sizes[n]) for n, cap in arenas]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--l1", type=int, default=32768, help="L1 data cache, bytes (default 32 KiB; many current cores have 48 KiB)")
    ap.add_argument("--l2", type=int, default=1 << 20, help="L2 cache, bytes (default 1 MiB)")
    a = ap.parse_args()

    work = tempfile.mkdtemp(prefix="arenas-")
    c_file, cflags = build_c(work)
    rows = measure(c_file, work, cflags)

    print("Arena classes of the 2D engine (capacity is PhysicsLimits; sizes are what the C compiler says)\n")
    print("  %-14s %9s %9s %12s %14s" % ("class", "capacity", "sizeof", "arena", "+ tables (12/slot)"))
    total_arena = total_tables = 0
    for name, cap, size in sorted(rows):
        tables = cap * 12 if cap > 1 else 0      # a singleton is found by name, not through a table
        total_arena += cap * size
        total_tables += tables
        print("  %-14s %9d %7d B %12s %14s" % (name, cap, size, human(cap * size), human(cap * size + tables)))
    total = total_arena + total_tables
    print("  %-14s %9s %9s %12s %14s" % ("total", "", "", human(total_arena), human(total)))
    verdict = "fits the L1 data cache" if total <= a.l1 else ("fits L2, not L1" if total <= a.l2 else "does NOT fit L2")
    print("\n  against L1 %s and L2 %s: %s (%.0f%% of L1)\n" % (human(a.l1), human(a.l2), verdict, 100.0 * total / a.l1))

    print("What a capacity costs, per class (arena + tables).   fits: 1 = L1, 2 = L2, - = neither\n")
    print("  %-14s" % "capacity" + "".join("%11d" % c for c in CAPACITIES))
    for name, cap, size in sorted(rows):
        cells = []
        for c in CAPACITIES:
            b = c * (size + 12)
            cells.append("%8s %s" % (human(b).replace(" ", ""), "1" if b <= a.l1 else "2" if b <= a.l2 else "-"))
        print("  %-14s" % name + "".join("%11s" % x for x in cells))
    print("\n  A class alone can fit L1 while the sum does not: the L1 is shared by everything the step touches (Box2D's own bodies, the solver, the stack).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
