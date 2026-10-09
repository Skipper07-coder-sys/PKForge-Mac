#!/usr/bin/env python3
"""Drive PKForge test builds (tools/build-mac-test.sh) without screenshots.

Every instance is a clone of dist/test.noindex/PKForge.app with its own bundle id (own preferences),
its own HOME (own Application Support, caches, backups, logs) and its own loopback port, so several
run side by side. The app answers in text: visible labels/buttons, view-model state, timings.

  pkf.py run [-j 4] [-k name] [--repeat 3] [--out results.json]   run scenarios.py in parallel
  pkf.py compare before.json after.json                           timing medians side by side
  pkf.py up N                                                     start instance N and leave it running
  pkf.py send N "texts"                                           one command to a running instance
  pkf.py down N | pkf.py clean                                    stop one | stop all, drop clones + prefs

Env: PKF_WORK  scratch folder for clones and homes (default $TMPDIR/pkf-uitest)
     PKF_SAVES folder holding the fixture saves (default ../../../test-fixtures)
"""
from __future__ import annotations

import argparse
import concurrent.futures as futures
import importlib.util
import json
import os
import plistlib
import shutil
import socket
import statistics
import subprocess
import sys
import threading
import time
import traceback
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
BUILD = REPO / "dist" / "test.noindex" / "PKForge.app"
WORK = Path(os.environ.get("PKF_WORK") or Path(os.environ.get("TMPDIR", "/tmp")) / "pkf-uitest")
# Outside test-saves/ (the app's linked folder), so the fixtures never show on the real app's shelf.
SAVES = Path(os.environ.get("PKF_SAVES") or (REPO.parent / "test-fixtures"))
BASE_PORT = 47100
BUNDLE = "org.pkforge.app.uitest"
END = "<<END>>"


class CommandError(RuntimeError):
    pass


class App:
    """One connection to a running instance. Every call returns the reply body as text."""

    def __init__(self, port: int, timeout: float = 60):
        self.port = port
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.file = self.sock.makefile("rw", encoding="utf-8", newline="\n")
        self.last_ms = 0.0

    def close(self):
        try:
            self.file.close()
            self.sock.close()
        except OSError:
            pass

    def cmd(self, line: str) -> str:
        self.file.write(line + "\n")
        self.file.flush()
        head = self.file.readline().rstrip("\n")
        body = []
        while True:
            row = self.file.readline()
            if row == "":
                raise CommandError(f"connection closed during '{line}'")
            row = row.rstrip("\n")
            if row == END:
                break
            body.append(row)
        if head.startswith("err "):
            raise CommandError(f"{line!r}: {head[4:]}")
        self.last_ms = float(head.split()[1]) if head.startswith("ok ") else 0.0
        return "\n".join(body)

    # Conveniences used by scenarios.
    def press(self, button: str, times: int = 1) -> str:
        return self.cmd(f"press {button} {times}")

    def pick(self, path: Path | str) -> str:
        return self.cmd(f'pick "{path}"')

    def texts(self) -> str:
        return self.cmd("texts")

    def state(self) -> dict[str, str]:
        """page, pad, stack, home, box (connection), box.label, box.status, edit (selected mon)."""
        out = {}
        for row in self.cmd("state").splitlines():
            key, _, value = row.partition(" ")
            if key in out:
                key = f"{key}.{value.split('=', 1)[0]}"
            out[key] = value
        return out

    def slots(self) -> str:
        return self.cmd("slots")

    def tap(self, text: str, nth: int = 1) -> str:
        return self.cmd(f'tap "{text}" {nth}')

    def type(self, text: str, field: str = "") -> str:
        """Types into the field whose placeholder/text contains `field`, else the focused or topmost one."""
        return self.cmd(f'type "{text}" "{field}"' if field else f'type "{text}"')

    def waitfor(self, kind: str, value: str = "-", timeout_ms: int = 15000) -> float:
        reply = self.cmd(f'waitfor {kind} "{value}" {timeout_ms}')
        return float(reply.split()[1])  # "after 123.4 ms"

    def settle(self, timeout_ms: int = 10000) -> float:
        return float(self.cmd(f"settle {timeout_ms}").split()[1])

    def perf(self, clear: bool = False) -> list[tuple[float, str, float | None]]:
        rows = []
        for row in self.cmd("perf clear" if clear else "perf").splitlines():
            at, _, rest = row.strip().partition("  ")
            name, _, took = rest.partition("  took ")
            rows.append((float(at), name, float(took) if took else None))
        return rows

    def mem(self, gc: bool = False) -> dict[str, float]:
        out = {}
        for row in self.cmd("mem gc" if gc else "mem").splitlines():
            parts = row.split()
            if parts[0] in ("managed", "rss"):
                out[parts[0]] = float(parts[1])
        return out


@dataclass
class Instance:
    n: int
    proc_pid: int | None = None

    @property
    def root(self) -> Path:
        return WORK / f"i{self.n}"

    @property
    def home(self) -> Path:
        return self.root / "home"

    @property
    def app(self) -> Path:
        return self.root / "PKForge.app"

    @property
    def port(self) -> int:
        return BASE_PORT + self.n

    @property
    def bundle(self) -> str:
        return f"{BUNDLE}{self.n}"

    def prepare(self, fresh_home: bool = True):
        """Clone the test build (APFS clone: instant, no extra disk) under this instance's bundle id."""
        if not BUILD.exists():
            sys.exit(f"no test build at {BUILD}: run tools/build-mac-test.sh")
        stamp = self.root / "build.stamp"
        built = str(BUILD.joinpath("Contents", "MacOS", "PKForge.App").stat().st_mtime)
        if not self.app.exists() or not stamp.exists() or stamp.read_text() != built:
            shutil.rmtree(self.app, ignore_errors=True)
            self.root.mkdir(parents=True, exist_ok=True)
            subprocess.run(["cp", "-cR", str(BUILD), str(self.app)], check=True)
            info = self.app / "Contents" / "Info.plist"
            with info.open("rb") as f:
                plist = plistlib.load(f)
            plist["CFBundleIdentifier"] = self.bundle
            plist["CFBundleName"] = f"PKForge T{self.n}"
            # Clones must not join Finder's Open With menu for save files (open -a still hands them files).
            plist.pop("CFBundleDocumentTypes", None)
            plist.pop("UTImportedTypeDeclarations", None)
            with info.open("wb") as f:
                plistlib.dump(plist, f)
            subprocess.run(["codesign", "--force", "--sign", "-", "--preserve-metadata=entitlements,flags", str(self.app)],
                           check=True, capture_output=True)
            stamp.write_text(built)
        if fresh_home:
            shutil.rmtree(self.home, ignore_errors=True)
            subprocess.run(["defaults", "delete", self.bundle], capture_output=True)
        self.home.mkdir(parents=True, exist_ok=True)
        # Background instances must not be App-Napped: throttled timers would ruin every timing.
        subprocess.run(["defaults", "write", self.bundle, "NSAppSleepDisabled", "-bool", "YES"], check=True)

    def launch(self, timeout: float = 30) -> App:
        self.stop()
        # -n: a new instance even if one runs; -g: stay in the background (no focus stealing).
        subprocess.run(["open", "-n", "-g", "--env", f"HOME={self.home}", "--env", f"PKFORGE_AUTOMATION_PORT={self.port}",
                        "--env", "PKFORGE_UITEST=1", str(self.app)], check=True)
        deadline = time.monotonic() + timeout
        while True:
            try:
                app = App(self.port)
                app.cmd("ping")
                self.proc_pid = int(app.cmd("info").splitlines()[0].split()[1])
                return app
            except (OSError, CommandError):
                if time.monotonic() > deadline:
                    raise TimeoutError(f"instance {self.n} did not answer on port {self.port} in {timeout:.0f} s")
                time.sleep(0.1)

    def stop(self):
        try:
            app = App(self.port, timeout=3)
            try:
                # Ask the running process for its pid: it may be from an earlier run of this tool.
                self.proc_pid = int(app.cmd("info").splitlines()[0].split()[1])
                app.cmd("quit")
            finally:
                app.close()
        except (OSError, CommandError):
            pass
        deadline = time.monotonic() + 5
        while self.proc_pid and time.monotonic() < deadline and _alive(self.proc_pid):
            time.sleep(0.05)
        if self.proc_pid and _alive(self.proc_pid):
            os.kill(self.proc_pid, 9)
        self.proc_pid = None

    def cpu_percent(self, seconds: float = 2.0) -> float:
        """Average CPU over a window, from the kernel's tick counters (no sampling of the app)."""
        def ticks():
            out = subprocess.run(["ps", "-o", "time=", "-p", str(self.proc_pid)], capture_output=True, text=True).stdout.strip()
            m, _, s = out.rpartition(":")
            h, _, m = m.rpartition(":")
            return (int(h or 0) * 3600) + int(m or 0) * 60 + float(s)
        a = ticks()
        time.sleep(seconds)
        return (ticks() - a) / seconds * 100


def _alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


# ---- scenarios ------------------------------------------------------------------------------

@dataclass
class Ctx:
    """What a scenario gets besides the app: fixture saves, a place for files, and metrics."""
    inst: Instance
    metrics: dict[str, float] = field(default_factory=dict)
    notes: list[str] = field(default_factory=list)

    def save(self, name: str) -> Path:
        """A private copy of a fixture save inside this instance's HOME (originals are never touched)."""
        src = SAVES / name
        dst = self.inst.home / "saves" / name
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, dst)
        dst.chmod(0o644)
        return dst

    def metric(self, name: str, ms: float):
        self.metrics[name] = round(ms, 1)

    def note(self, text: str):
        self.notes.append(text)


def load_scenarios(pattern: str | None):
    spec = importlib.util.spec_from_file_location("scenarios", HERE / "scenarios.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    tests = [(name, fn) for name, fn in vars(module).items() if name.startswith("test_") and callable(fn)]
    tests.sort(key=lambda t: t[1].__code__.co_firstlineno)
    if pattern:
        tests = [t for t in tests if pattern in t[0]]
    return tests


def run_one(inst: Instance, name: str, fn) -> dict:
    ctx = Ctx(inst)
    started = time.monotonic()
    result = {"test": name, "instance": inst.n}
    app = None
    try:
        inst.prepare(fresh_home=True)
        t0 = time.monotonic()
        app = inst.launch()
        ctx.metric("launch→ping", (time.monotonic() - t0) * 1000)
        fn(app, ctx)
        result["ok"] = True
    except Exception as error:  # noqa: BLE001 - a scenario failure of any kind is reported, not raised
        result["ok"] = False
        result["error"] = f"{type(error).__name__}: {error}"
        result["trace"] = traceback.format_exc(limit=4)
        if app is not None:
            dump = []
            for probe in ("state", "texts", "log 15"):
                try:
                    dump.append(f"--- {probe}\n{app.cmd(probe)}")
                except Exception as probe_error:  # noqa: BLE001
                    dump.append(f"--- {probe} failed: {probe_error}")
            result["screen"] = "\n".join(dump)
    finally:
        if app is not None:
            app.close()
        inst.stop()
    result["seconds"] = round(time.monotonic() - started, 2)
    result["metrics"] = ctx.metrics
    result["notes"] = ctx.notes
    return result


def cmd_run(args):
    tests = load_scenarios(args.k)
    if not tests:
        sys.exit("no scenarios matched")
    jobs = [(name, fn) for _ in range(args.repeat) for name, fn in tests]
    pool_size = max(1, min(args.j, len(jobs)))
    free = list(range(1, pool_size + 1))
    lock = threading.Lock()
    results = []
    print(f"{len(jobs)} run(s) of {len(tests)} scenario(s) on {pool_size} instance(s)", flush=True)

    def work(job):
        with lock:
            n = free.pop()
        try:
            r = run_one(Instance(n), *job)
        finally:
            with lock:
                free.append(n)
        status = "PASS" if r["ok"] else "FAIL"
        print(f"  {status} {r['test']:<34} i{r['instance']} {r['seconds']:6.1f}s"
              + ("" if r["ok"] else f"  {r['error']}"), flush=True)
        return r

    started = time.monotonic()
    with futures.ThreadPoolExecutor(pool_size) as pool:
        results = list(pool.map(work, jobs))
    wall = time.monotonic() - started

    failed = [r for r in results if not r["ok"]]
    for r in failed:
        print(f"\n=== {r['test']} (i{r['instance']}) {r['error']}\n{r.get('trace', '')}{r.get('screen', '')}")
    print(f"\n{len(results) - len(failed)}/{len(results)} passed in {wall:.1f}s wall")
    summarize(results)
    out = Path(args.out) if args.out else WORK / f"results-{time.strftime('%Y%m%d-%H%M%S')}.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps({"wall": wall, "results": results}, indent=1))
    print(f"results → {out}")
    sys.exit(1 if failed else 0)


def metric_table(results) -> dict[str, list[float]]:
    table: dict[str, list[float]] = {}
    for r in results:
        if not r["ok"]:
            continue
        for k, v in r["metrics"].items():
            table.setdefault(f"{r['test']}: {k}", []).append(v)
    return table


def summarize(results):
    table = metric_table(results)
    if not table:
        return
    print("\nmetric (ms)                                                     median      min      max   n")
    for key in sorted(table):
        vals = table[key]
        print(f"  {key:<60} {statistics.median(vals):8.1f} {min(vals):8.1f} {max(vals):8.1f} {len(vals):3d}")


def cmd_compare(args):
    a = metric_table(json.loads(Path(args.before).read_text())["results"])
    b = metric_table(json.loads(Path(args.after).read_text())["results"])
    print("metric (median ms)                                              before    after   change")
    for key in sorted(set(a) | set(b)):
        ma = statistics.median(a[key]) if key in a else None
        mb = statistics.median(b[key]) if key in b else None
        change = f"{(mb - ma) / ma * 100:+6.0f}%" if ma and mb is not None else "     -"
        fa = f"{ma:8.1f}" if ma is not None else "       -"
        fb = f"{mb:8.1f}" if mb is not None else "       -"
        print(f"  {key:<60} {fa} {fb}  {change}")


def cmd_up(args):
    inst = Instance(args.n)
    inst.prepare(fresh_home=not args.keep)
    app = inst.launch()
    print(app.cmd("info"))
    app.close()


def cmd_send(args):
    try:
        app = App(BASE_PORT + args.n)
    except OSError as error:
        sys.exit(f"instance {args.n} is not running ({error.strerror})")
    try:
        for line in args.lines:
            try:
                reply = app.cmd(line)
            except CommandError as error:
                sys.exit(f"error: {error}")
            print(f"[{app.last_ms:.0f} ms] {reply}" if reply else f"[{app.last_ms:.0f} ms]")
    finally:
        app.close()


def cmd_down(args):
    Instance(args.n).stop()


LSREGISTER = ("/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework"
              "/Support/lsregister")


def cmd_clean(_args):
    for d in sorted(WORK.glob("i*")):
        if d.is_dir() and d.name[1:].isdigit():
            inst = Instance(int(d.name[1:]))
            inst.stop()
            subprocess.run([LSREGISTER, "-u", str(inst.app)], capture_output=True)
            subprocess.run(["defaults", "delete", inst.bundle], capture_output=True)
            # cfprefsd can leave the emptied file behind.
            (Path.home() / "Library" / "Preferences" / f"{inst.bundle}.plist").unlink(missing_ok=True)
            shutil.rmtree(d, ignore_errors=True)
    print(f"cleaned {WORK}")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("-j", type=int, default=4)
    r.add_argument("-k")
    r.add_argument("--repeat", type=int, default=1)
    r.add_argument("--out")
    r.set_defaults(func=cmd_run)
    c = sub.add_parser("compare")
    c.add_argument("before")
    c.add_argument("after")
    c.set_defaults(func=cmd_compare)
    u = sub.add_parser("up")
    u.add_argument("n", type=int)
    u.add_argument("--keep", action="store_true", help="keep the instance's HOME from last time")
    u.set_defaults(func=cmd_up)
    s = sub.add_parser("send")
    s.add_argument("n", type=int)
    s.add_argument("lines", nargs="+")
    s.set_defaults(func=cmd_send)
    d = sub.add_parser("down")
    d.add_argument("n", type=int)
    d.set_defaults(func=cmd_down)
    sub.add_parser("clean").set_defaults(func=cmd_clean)
    args = p.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
