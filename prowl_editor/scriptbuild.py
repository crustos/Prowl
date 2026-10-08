"""scriptbuild.py: building the project's C# scripts into the engine (no Qt here, so it can be tested without a display).

The scripts are C# in the subset the translator accepts (tools/ccsharp/README.md): a build writes them to a folder, runs tools/prowl2d_so.py --scripts on it, which translates
engine + scripts to C and links a NEW libprowl2d (a new file each time: a library already loaded under a name is not loaded again), and reads what the translator said:
errors come back as `File.cs(line,col): error CODE: message`, which the code editor shows in its problems list. The library's numbers for the scripts are in OUT.scripts.json.
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

_HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(_HERE)

_DIAG = re.compile(r"^(?P<file>[^\s(][^()]*?)\((?P<line>\d+),(?P<col>\d+)\):\s*(?P<sev>error|warning)\s*(?P<code>\w+)?:?\s*(?P<msg>.*)$")


class Diagnostic:
    def __init__(self, script, line, col, severity, code, message):
        self.script, self.line, self.col, self.severity, self.code, self.message = script, line, col, severity, code, message

    def __str__(self):
        where = "%s:%d" % (self.script, self.line) if self.script else ""
        return ("%s: " % where if where else "") + "%s %s: %s" % (self.severity, self.code or "", self.message)


def file_name(name):
    """A script's file name (the name without .cs, made safe)."""
    return re.sub(r"[^A-Za-z0-9_]", "_", name) or "Script"


def parse_diagnostics(text):
    """The compiler-style lines in `text` as Diagnostics (script = the file name without .cs); any other `error ...` line has no place."""
    out = []
    for raw in text.splitlines():
        line = raw.strip()
        m = _DIAG.match(line)
        if m:
            base = os.path.basename(m.group("file"))
            out.append(Diagnostic(base[:-3] if base.endswith(".cs") else base, int(m.group("line")), int(m.group("col")), m.group("sev"), m.group("code") or "", m.group("msg")))
        elif re.search(r"\berror\b", line) and not line.startswith(("$", "==")):
            out.append(Diagnostic("", 0, 0, "error", "", line))
    return out


class BuildResult:
    def __init__(self, ok, lib=None, diagnostics=None, log="", scripts=None):
        self.ok, self.lib, self.diagnostics, self.log, self.scripts = ok, lib, diagnostics or [], log, scripts or {}


class Builder:
    """One per editor session: a work folder, and a count that makes each library's file name new."""

    def __init__(self, root=None):
        self.root = root or tempfile.mkdtemp(prefix="prowl_scripts_")
        self.count = 0
        self.libs = []

    def prepare(self, project):
        """Writes the project's scripts to src/ and returns (the command to run, the library it will write)."""
        src = os.path.join(self.root, "src")
        shutil.rmtree(src, ignore_errors=True)
        os.makedirs(src)
        for f in project.scripts:
            with open(os.path.join(src, file_name(f.name) + ".cs"), "w", encoding="utf-8", newline="\n") as fh:
                fh.write(f.text)
        self.count += 1
        lib = os.path.join(self.root, "libprowl2d_%d.so" % self.count)
        cmd = [sys.executable, os.path.join(ROOT, "tools", "prowl2d_so.py"), "--scripts", src, "--work", os.path.join(self.root, "work"), "-o", lib]
        return cmd, lib

    def finish(self, lib, returncode, output):
        """The result of the command `prepare` gave, once it has run."""
        diags = parse_diagnostics(output)
        if returncode == 0 and os.path.exists(lib):
            import json
            try:
                with open(lib + ".scripts.json", encoding="utf-8") as f:
                    scripts = {item["name"]: int(item["id"]) for item in json.load(f)}
            except (OSError, ValueError, KeyError):
                scripts = {}
            self.libs.append(lib)
            return BuildResult(True, lib, diags, output, scripts)
        if not diags:
            diags = [Diagnostic("", 0, 0, "error", "", "the build failed: " + (output.strip().splitlines() or ["no output"])[-1])]
        return BuildResult(False, None, diags, output)

    def build(self, project):
        """Synchronous: prepare, run, finish."""
        cmd, lib = self.prepare(project)
        r = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)
        return self.finish(lib, r.returncode, r.stdout + r.stderr)

    def forget_old(self, keep):
        """Deletes the libraries of earlier builds except `keep` (and the ones still loaded must not be passed here)."""
        for lib in list(self.libs):
            if lib != keep:
                for path in (lib, lib + ".scripts.json"):
                    try:
                        os.remove(path)
                    except OSError:
                        pass
                self.libs.remove(lib)
