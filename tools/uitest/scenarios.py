"""UI scenarios for tools/uitest/pkf.py. Each test_* gets a freshly launched instance (own HOME,
own preferences) and a Ctx: ctx.save(name) copies a fixture save into that HOME, ctx.metric()
records a timing. Everything is read back as text: no screenshots.

Fixture: the Emerald save from test-fixtures/ (party Blaziken, Gardevoir, Marill, Nincada,
Poochyena, Trapinch; Castform in box 1 holds Mystic Water = Gen 3 item 209).
"""
from __future__ import annotations

import contextlib
import hashlib
import io
import importlib.util
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

EMERALD = "Pokemon - Emerald Version (USA, Europe).srm"
PARTY = [257, 282, 183, 290, 261, 328]          # national dex
BOX1 = {0: 304, 1: 296, 2: 309, 3: 285, 4: 129, 6: 276, 7: 351}
GEN3_LEFTOVERS, GEN3_MYSTIC_WATER = 200, 209   # Gen 3 internal ids (the decoder prints internal species too: Trapinch = 332)

# The project's independent Gen 3 checker/decoder (outside the repo, next to the test saves).
TOOLS = Path(os.environ.get("PKF_GEN3_TOOLS") or Path(__file__).resolve().parents[3] / "tools")


def _gen3():
    spec = importlib.util.spec_from_file_location("dump_gen3_box", TOOLS / "dump_gen3_box.py")
    if spec is None or not (TOOLS / "dump_gen3_box.py").exists():
        return None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def md5(path: Path) -> str:
    return hashlib.md5(path.read_bytes()).hexdigest()


# ---- helpers ------------------------------------------------------------------------------

def pad(app) -> str:
    return app.state()["pad"]


def finish_onboarding(app, timeout=20.0):
    """A fresh HOME is a first launch: Home appears, then a typewriter welcome, then the menu of
    what to link. Wait for that menu (a Home that stays bare for 4 s has no onboarding)."""
    started = time.monotonic()
    while time.monotonic() - started < timeout:
        top = pad(app)
        if top == "PadMenu":
            return
        if top in ("DialogueBox", "DialogueChoiceBox"):
            app.press("A")
            app.cmd("sleep 60")
            continue
        if top == "HomePage" and time.monotonic() - started > 4:
            return
        app.cmd("sleep 50")
    raise TimeoutError(f"onboarding did not finish (pad {pad(app)})")


def open_save(app, ctx, name=EMERALD) -> Path:
    """Opens a private copy of a fixture save the way a player does: File ▸ pick ▸ the box opens."""
    save = ctx.save(name)
    finish_onboarding(app)
    app.pick(save)
    if pad(app) == "PadMenu" and "Open a single save file" in app.texts():
        app.tap("Open a single save file")
    else:
        app.press("X")                       # Home's File shortcut
    ctx.metric("open: tap → box page", app.waitfor("page", "BoxBrowserPage", 20000))
    ctx.metric("open: → settled", app.settle())
    return save


def wait_status(app, text: str, timeout=10.0):
    """The box status strip is drawn, not a label: read it from the view model."""
    deadline = time.monotonic() + timeout
    while text not in (status := app.state().get("box.status", "")):
        if time.monotonic() > deadline:
            raise TimeoutError(f"status never said '{text}' (last: {status})")
        app.cmd("sleep 20")


def species_at(app) -> dict[int, int]:
    out = {}
    for row in app.slots().splitlines()[1:]:
        m = re.match(r"\s*(\d+) #(\d+)", row)
        if m:
            out[int(m.group(1))] = int(m.group(2))
    return out


def perf_took(app, name: str) -> list[float]:
    return [took for _, n, took in app.perf() if n == name and took is not None]


# ---- scenarios ----------------------------------------------------------------------------

def test_launch_to_home(app, ctx):
    finish_onboarding(app)
    marks = {name: at for at, name, _ in app.perf()}
    ctx.metric("launch: FinishedLaunching", marks["FinishedLaunching"])
    ctx.metric("launch: Home appearing", marks["HomePage appearing"])
    texts = app.texts()
    for label in ("Bank", "Settings", "Open a single save file"):
        assert label in texts, f"'{label}' missing on first launch"
    cpu = ctx.inst.cpu_percent(2)
    ctx.metric("idle CPU % (home menu)", cpu)
    assert cpu < 5, f"home idles at {cpu:.0f}% CPU"


def test_open_emerald(app, ctx):
    save = open_save(app, ctx)
    before = md5(save)
    st = app.state()
    assert "connected=True" in st["box"] and "gen=3" in st["box"], st["box"]
    got = species_at(app)
    assert got == BOX1, f"box 1 = {got}"
    assert "item=209" in app.slots(), "Castform's Mystic Water (Gen 3 id 209) not reported"
    ctx.metric("open: session", perf_took(app, "open: session")[0])
    ctx.metric("open: BoxBrowserPage ctor", perf_took(app, "BoxBrowserPage ctor")[0])
    ctx.metric("open: first box paint", perf_took(app, "paint box")[0])
    assert md5(save) == before, "opening wrote to the save"
    mem = app.mem()
    ctx.metric("memory: managed MB after open", mem["managed"])


def test_click_every_mon(app, ctx):
    """Issue #1 regression (clicking a mon crashed on macOS): every party and box-1 mon opens in the editor."""
    open_save(app, ctx)
    times = []
    for slot, species in enumerate(PARTY):
        app.cmd(f"select party {slot}")
        times.append(app.last_ms)
        app.settle()
        edit = app.state()["edit"]
        assert f"species={species} " in edit, f"party {slot}: {edit}"
    for slot, species in BOX1.items():
        app.cmd(f"select 1 {slot}")
        times.append(app.last_ms)
        app.settle()
        assert f"species={species} " in app.state()["edit"], f"box 1 slot {slot}"
    ctx.metric("select mon (median)", sorted(times)[len(times) // 2])
    ctx.metric("select mon (max)", max(times))


def test_gen3_held_item_names(app, ctx):
    """Gen 3 item ids are named from Gen 3's table (209 = Mystic Water, not Micle Berry)."""
    open_save(app, ctx)
    app.cmd("select 1 7")
    app.settle()
    held = app.cmd('texts "Mystic Water"')
    assert "Mystic Water" in held and "Micle" not in app.texts(), held
    app.cmd("select party 1")
    app.settle()
    assert "Exp. Share" in app.texts(), "Gardevoir's Exp. Share"


def test_party_idles_without_repainting(app, ctx):
    """A selected party card is still: no repaint loop (was ~22 fps, ~30% CPU)."""
    open_save(app, ctx)
    app.press("L")                           # box 1 → party
    app.cmd("select party 0")
    app.settle()
    app.perf(clear=True)
    cpu = ctx.inst.cpu_percent(2)
    paints = len(perf_took(app, "paint party"))
    ctx.metric("idle CPU % (party, mon selected)", cpu)
    ctx.metric("idle party paints in 2 s", paints)
    assert paints <= 2, f"party repainted {paints}× while idle"
    # Carrying a party mon does breathe, and stops when put back.
    app.press("A")
    app.settle()
    assert "CARRYING" in app.state()["box.status"], "A on a selected mon should grab it"
    app.perf(clear=True)
    app.cmd("sleep 500")
    breathing = len(perf_took(app, "paint party"))
    assert breathing >= 5, f"carried card did not breathe ({breathing} paints in 0.5 s)"
    app.press("B")
    app.settle()
    app.perf(clear=True)
    app.cmd("sleep 500")
    assert len(perf_took(app, "paint party")) <= 1, "repaint loop kept running after the carry was cancelled"


def test_box_paging(app, ctx):
    open_save(app, ctx)
    times = []
    for _ in range(13):
        app.press("R")
        times.append(app.settle())
    assert "index=13" in app.state()["box.label"], app.state()["box.label"]
    paints = perf_took(app, "paint box")
    ctx.metric("box page: settle (median)", sorted(times)[len(times) // 2])
    ctx.metric("box page: paint (median)", sorted(paints)[len(paints) // 2])
    ctx.metric("box page: paint (max)", max(paints))
    ctx.metric("memory: managed MB after 14 boxes", app.mem(gc=True)["managed"])


def test_held_item_write(app, ctx):
    """Leftovers on Trapinch through the real picker → Save changes → the file says Gen 3 item 200, still valid."""
    save = open_save(app, ctx)
    before = save.read_bytes()
    app.cmd("select party 5")
    app.settle()
    started = time.monotonic()
    app.tap("Held item")
    app.waitfor("text", "Search…", 5000)
    ctx.metric("held-item picker: open", (time.monotonic() - started) * 1000)
    app.type("Leftovers", "Search")
    started = time.monotonic()
    app.waitfor("text", "Return picks: Leftovers", 5000)
    ctx.metric("held-item picker: filter", (time.monotonic() - started) * 1000)
    app.press("A")
    app.settle()
    assert "Leftovers" in app.cmd('texts "Leftovers"'), "pick did not reach the editor"
    assert "nick=TRAPINCH" in app.state()["edit"], "nickname changed: " + app.state()["edit"]
    app.tap("Save changes")
    started = time.monotonic()
    wait_status(app, "Saved.")
    ctx.metric("save changes: → saved", (time.monotonic() - started) * 1000)
    after = save.read_bytes()
    assert after != before, "nothing written"
    gen3 = _gen3()
    if gen3 is None:
        ctx.note("Gen 3 decoder not found: file content unchecked")
        return
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        gen3.main(str(save), 1)
    assert f"slot 6: species 332, item {GEN3_LEFTOVERS}" in out.getvalue(), out.getvalue()
    check = subprocess.run([sys.executable, str(TOOLS / "verify_gen3_save.py"), str(save)], capture_output=True, text=True)
    assert check.stdout.startswith("OK"), check.stdout + check.stderr
    backups = list((ctx.inst.home / "Library" / "Application Support" / "PKForge" / "backups").glob("*.bin"))
    assert backups and backups[0].read_bytes() == before, "restore point missing or not the original"


def _idle(app, ctx, label: str):
    app.settle()
    app.cmd("sleep 400")                      # entry animations finish
    cpu = ctx.inst.cpu_percent(2)
    ctx.metric(f"idle CPU % ({label})", cpu)
    return cpu


def test_idle_cpu_sweep(app, ctx):
    """Every main screen must sit still when nothing moves (a Mac on battery)."""
    finish_onboarding(app)
    if pad(app) == "PadMenu":
        app.press("B")                        # dismiss the link menu: Home itself
    hot = {}
    for entry, page in (("Bank", None), ("Events", None), ("Dex Autopilot", None), ("Settings", None), ("Poképark", None)):
        before = app.state()
        try:
            app.tap(entry)
        except Exception as error:  # noqa: BLE001
            ctx.note(f"{entry}: {error}")
            continue
        app.cmd("sleep 600")
        after = app.state()
        label = f"{entry} → {after['page']}/{after['pad']}"
        # Poképark is an animated scene (30 fps); menus marquee their selected row. Everything else is still.
        limit = 25 if entry == "Poképark" else 8
        if (_cpu := _idle(app, ctx, label)) > limit:
            hot[label] = _cpu
        # Back to Home whatever opened (a page or an overlay).
        for _ in range(4):
            if app.state()["pad"] == before["pad"] and app.state()["page"] == before["page"]:
                break
            app.press("B")
            app.cmd("sleep 300")
    assert not hot, f"screens burning CPU while idle: {hot}"


KEYBOARD_KEYS = {"Return", "Esc", "X", "Y", "L", "R", "L R", "+", "-", "↑↓"}


def _hints(app) -> list[tuple[str, str, str, bool]]:
    rows = []
    for row in app.cmd("hints").splitlines():
        if not row.strip():
            continue
        glyph, shown = row[:3].strip(), row[4:11].strip()
        label, clickable = row[12:36].strip(), row.rstrip().endswith(" click")
        rows.append((glyph, shown, label, clickable))
    return rows


def test_controls_without_a_controller(app, ctx):
    """No controller: every hint names its keyboard key and works with a click; a controller brings the buttons back."""
    app.cmd("glyphs keyboard")
    save = ctx.save(EMERALD)
    finish_onboarding(app)
    problems, seen = [], 0

    def audit(where: str):
        nonlocal seen
        for glyph, shown, label, clickable in _hints(app):
            seen += 1
            if not clickable and glyph != "↑↓":
                problems.append(f"{where}: '{label}' ({glyph}) cannot be clicked")
            if shown not in KEYBOARD_KEYS:
                problems.append(f"{where}: '{label}' shows '{shown}', not a keyboard key")

    audit("onboarding menu")
    app.press("B")
    app.cmd("sleep 250")
    audit("home")
    app.pick(save)
    app.press("X")
    app.waitfor("page", "BoxBrowserPage")
    app.settle()
    audit("box")
    app.cmd("select 1 0")
    app.settle()
    for button, where in (("X", "tools menu"), ("Y", "save data menu"), ("Start", "mon menu")):
        app.press(button)
        app.cmd("sleep 350")
        audit(where)
        app.press("B")
        app.cmd("sleep 300")
    app.press("A")                        # grab: the footer turns to Place / Cancel
    app.settle()
    audit("carrying")
    app.press("B")
    app.settle()
    # A controller connects: hints show its buttons again, without reopening the screen.
    app.cmd("glyphs pad")
    shown = {s for _, s, _, _ in _hints(app)}
    assert shown <= {"(art)", "↑↓", "-"}, f"controller connected but hints still show {shown}"
    app.cmd("glyphs keyboard")
    assert any(s == "Return" for _, s, _, _ in _hints(app)), "hints did not switch back to keys"
    ctx.metric("hints checked", seen)
    assert not problems, "\n".join(problems)


def test_keyboard_matches_the_hints(app, ctx):
    """Press what the hint shows: X/Y/L/R/+/- are those keys (by character, so QWERTZ's Y is Y),
    Return/Esc confirm and go back, Page Up/Down page; the old emulator keys (S, Q, W, Z) are gone."""
    app.cmd("glyphs keyboard")
    open_save(app, ctx)

    def box_index():
        return int(re.search(r"index=(-?\d+)", app.state()["box.label"]).group(1))

    for key, expect in (("r", 1), ("pagedown", 2), ("l", 1), ("pageup", 0), ("]", 1), ("[", 0)):
        app.cmd(f'key "{key}"')
        app.settle()
        assert box_index() == expect, f"key {key}: box index {box_index()}, expected {expect}"
    app.cmd("select 1 0")
    app.settle()
    for key, opens in (("x", "Tools"), ("y", "Save data"), ("+", "mon menu"), ("tab", "mon menu")):
        assert "consumed" in app.cmd(f'key "{key}"'), key
        app.cmd("sleep 300")
        assert pad(app) == "PadMenu", f"key {key} should open the {opens} menu (pad {pad(app)})"
        app.cmd('key "esc"')
        app.cmd("sleep 300")
        assert pad(app) == "BoxBrowserPage", f"Esc did not close the {opens} menu"
    app.cmd('key "-"')
    app.settle()
    assert "Move mode" in app.cmd("hints"), "- should enter multi-select"
    app.cmd('key "-"')
    app.settle()
    for old in ("s", "q", "w", "z"):
        assert app.cmd(f'key "{old}"') == "no button", f"old emulator key {old} still mapped"


def test_mouse_right_click_and_cmd_s(app, ctx):
    """Right-click a Pokémon: it is selected and its menu opens. ⌘S saves the edited Pokémon, only from the editor."""
    save = open_save(app, ctx)
    app.cmd("rightclick 7")                      # Castform
    app.cmd("sleep 300")
    assert pad(app) == "PadMenu", f"right-click opened nothing (pad {pad(app)})"
    assert "species=351 " in app.state()["edit"], "right-click did not select Castform"
    app.press("B")
    app.cmd("sleep 300")
    app.cmd("rightclick 5")                      # an empty slot: no menu
    app.cmd("sleep 200")
    assert pad(app) == "BoxBrowserPage", "right-click on an empty slot opened a menu"
    before = save.read_bytes()
    app.cmd("press X")                           # a menu in front: ⌘S stays off
    app.cmd("sleep 300")
    assert app.cmd("menu-save") == "disabled", "⌘S worked under an open menu"
    app.press("B")
    app.cmd("sleep 300")
    assert app.cmd("menu-save") == "saving"
    wait_status(app, "Nothing changed")             # no edit: no write, no restore point
    assert save.read_bytes() == before
    app.type("MISTY", "CASTFORM")                    # rename Castform in the editor
    app.settle()
    assert app.cmd("menu-save") == "saving"
    wait_status(app, "Saved.")
    assert "nick=MISTY" in app.state()["edit"], app.state()["edit"]
    check = subprocess.run([sys.executable, str(TOOLS / "verify_gen3_save.py"), str(save)], capture_output=True, text=True)
    assert check.stdout.startswith("OK"), check.stdout + check.stderr
    ctx.note(f"⌘S wrote {sum(a != b for a, b in zip(before, save.read_bytes()))} byte(s)")


def _decoded(save: Path, box: int) -> dict[int, int]:
    """Box slots (1-based) → Gen 3 internal species, straight from the file."""
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        _gen3().main(str(save), box)
    section = out.getvalue().split(f"box {box}:")[1]
    return {int(m.group(1)): int(m.group(2)) for m in re.finditer(r"slot (\d+): species (\d+)", section)}


def test_drag_and_drop(app, ctx):
    """Click-and-drag: move to an empty slot, swap, carry to the next box over the edge, let go off the grid = no change.
    Every step checked in the file itself (Gen 3 internal species: Aron 382, Makuhita 335, Electrike 337, Magikarp 129)."""
    save = open_save(app, ctx)

    def drag(*args):
        reply = app.cmd("drag " + " ".join(map(str, args)))
        app.settle()
        assert "carrying=False" in reply, reply
        return reply

    started = time.monotonic()
    drag(0, 5)                                      # Aron → empty slot 6
    ctx.metric("drag: move (incl. write)", (time.monotonic() - started) * 1000)
    got = species_at(app)
    assert got.get(5) == 304 and 0 not in got, f"box 1 after move: {got}"
    file = _decoded(save, 1)
    assert file.get(6) == 382 and 1 not in file, f"file box 1: {file}"

    drag(1, 2)                                      # Makuhita onto Electrike: swap
    got = species_at(app)
    assert (got.get(1), got.get(2)) == (309, 296), f"swap: {got}"
    file = _decoded(save, 1)
    assert (file.get(2), file.get(3)) == (337, 335), f"file swap: {file}"

    started = time.monotonic()
    reply = drag(4, 0, 1)                           # Magikarp over the right edge into box 2, slot 1
    ctx.metric("drag: to next box (incl. edge hold)", (time.monotonic() - started) * 1000)
    assert "02 / 14" in reply, reply
    assert species_at(app).get(0) == 129, app.slots()
    assert 1 not in _decoded(save, 1) or _decoded(save, 1).get(5) != 129
    assert _decoded(save, 2).get(1) == 129, _decoded(save, 2)

    app.press("L")                                  # back to box 1
    app.settle()
    before = save.read_bytes()
    drag(6, -1)                                     # Taillow let go above the grid: back where it was
    assert species_at(app).get(6) == 276, app.slots()
    assert save.read_bytes() == before, "letting go off the grid wrote the save"

    check = subprocess.run([sys.executable, str(TOOLS / "verify_gen3_save.py"), str(save)], capture_output=True, text=True)
    assert check.stdout.startswith("OK"), check.stdout + check.stderr


def test_drag_into_and_within_the_party(app, ctx):
    """Party drags, read back from the file (Gen 3 internal: Aron 382, Trapinch 332, Blaziken 282, Gardevoir 394).
    A full party refuses a box Pokémon (the games' rule) and says why; with room, a drag over the left edge of
    box 1 lands it in the party (appended); and two party Pokémon swap by drag."""
    save = open_save(app, ctx)

    def party_file():
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            _gen3().main(str(save), 1)
        section = out.getvalue().split("box 1:")[0]
        return {int(m.group(1)): int(m.group(2)) for m in re.finditer(r"slot (\d+): species (\d+)", section)}

    before = save.read_bytes()
    reply = app.cmd("drag 0 5 -1")                 # Aron over the left edge onto the full party: refused, and why
    app.settle()
    assert "PARTY" in reply and "carrying=False" in reply, reply
    assert "party is full" in app.state()["box.status"], app.state()["box.status"]
    assert save.read_bytes() == before, "a refused move wrote the save"

    reply = app.cmd("drag 5 7 1")                  # Trapinch from the full party onto Castform: no room to swap
    app.settle()
    assert "can't swap" in app.state()["box.status"], app.state()["box.status"]
    assert save.read_bytes() == before
    app.press("L")
    app.settle()
    reply = app.cmd("drag 5 8 1")                  # Trapinch over the right edge into box 1's empty slot 9
    app.settle()
    assert "01 / 14" in reply and species_at(app).get(8) == 328, reply + app.slots()
    reply = app.cmd("drag 0 5 -1")                 # Aron over the left edge: the party has room now
    app.settle()
    assert "PARTY" in reply, reply
    file = party_file()
    assert file.get(6) == 382 and 332 not in file.values(), file
    assert _decoded(save, 1).get(9) == 332, _decoded(save, 1)

    reply = app.cmd("drag 0 1")                    # Blaziken onto Gardevoir inside the party
    app.settle()
    assert "carrying=False" in reply, reply
    file = party_file()
    assert (file.get(1), file.get(2)) == (394, 282), file
    check = subprocess.run([sys.executable, str(TOOLS / "verify_gen3_save.py"), str(save)], capture_output=True, text=True)
    assert check.stdout.startswith("OK"), check.stdout + check.stderr


def _footprint(ctx) -> float:
    out = subprocess.run(["footprint", str(ctx.inst.proc_pid)], capture_output=True, text=True).stdout
    m = re.search(r"Footprint: ([\d.]+) MB", out)
    return float(m.group(1)) if m else -1


def _canvas_mb(app) -> float:
    return float(re.search(r"total ([\d.]+) MB", app.cmd("canvases")).group(1))


def test_mac_window_memory_and_resize(app, ctx):
    """Mac-only costs an Android handheld never has: a big Retina window kept in drawn bitmaps,
    and live window resizing (every drawn view re-rasterizes at the new size)."""
    open_save(app, ctx)
    app.cmd("select 1 0")
    app.settle()
    ctx.metric("drawn bitmaps MB (home + box)", _canvas_mb(app))
    ctx.metric("footprint MB after open", _footprint(ctx))
    for _ in range(14):
        app.press("R")
        app.settle()
    ctx.metric("footprint MB after all 14 boxes", _footprint(ctx))
    paints, times = [], []
    for w, h in ((1100, 680), (1500, 900), (1280, 760), (1700, 1000), (1280, 760)):
        app.perf(clear=True)
        reply = app.cmd(f"resize {w} {h}")
        times.append(float(re.search(r"after ([\d.]+) ms", reply).group(1)))
        paints.append(sum(t for _, n, t in app.perf() if t is not None and n.startswith("paint")))
    ctx.metric("resize → settled (median)", sorted(times)[len(times) // 2])
    ctx.metric("resize: box paint ms (median)", sorted(paints)[len(paints) // 2])
    app.mem(gc=True)
    rss = app.mem()["rss"]
    ctx.metric("process RSS MB after resizes", rss)
    footprint = subprocess.run(["footprint", str(ctx.inst.proc_pid)], capture_output=True, text=True).stdout
    if m := re.search(r"Footprint: ([\d.]+) MB", footprint):
        ctx.metric("process footprint MB", float(m.group(1)))
    if m := re.search(r"([\d.]+) MB\s+\S+\s+\S+\s+\d+\s+CoreAnimation", footprint):
        ctx.metric("CoreAnimation dirty MB", float(m.group(1)))


def test_covered_window_pauses_animation(app, ctx):
    """A Mac keeps a covered window (or one on another desktop) 'active', unlike Android: animations must pause
    on the window's visibility, resume after, and close nothing. Minimizing pauses through the lifecycle (OnSleep)."""
    finish_onboarding(app)
    if pad(app) == "PadMenu":
        app.press("B")
        app.cmd("sleep 250")
    app.tap("Poképark")
    app.cmd("sleep 1200")
    assert pad(app) == "PokeparkPage"

    def park_paints(ms):
        app.perf(clear=True)
        app.cmd(f"sleep {ms}")
        return len(perf_took(app, "paint park"))

    assert park_paints(500) >= 10, "the park should animate while visible"
    app.cmd("visibility hidden")
    assert park_paints(1000) == 0, "the park kept painting behind other windows"
    ctx.metric("idle CPU % (Poképark, covered)", ctx.inst.cpu_percent(2))
    app.cmd("visibility visible")
    assert park_paints(500) >= 10, "the park did not resume when uncovered"
    assert pad(app) == "PokeparkPage", "uncovering should find the park as it was"
    app.cmd("visibility real")                     # from here on, what macOS reports
    app.cmd("window minimize")
    app.cmd("sleep 800")
    assert "visible=False" in app.cmd("visibility"), "a minimized window should count as unseen"
    assert park_paints(800) == 0, "the park painted while minimized"
    app.cmd("window restore")
    app.cmd("sleep 800")
    assert park_paints(500) >= 10, "the park did not resume after restore"


def test_undo_last_change(app, ctx):
    """Edit ▸ Undo (⌘Z) puts the save back one change at a time, newest first, from each change's restore point;
    with nothing left it is off; a change made outside PKForge since blocks it (the restore points keep everything)."""
    save = open_save(app, ctx)
    original = save.read_bytes()
    assert app.cmd("menu-undo") == "disabled", "Undo offered before any change"

    def rename_castform(name: str):
        app.cmd("select 1 7")                        # Castform
        app.settle()
        app.type(name, "CASTFORM")
        app.settle()
        assert app.cmd("menu-save") == "saving"
        wait_status(app, "Saved.")

    rename_castform("MISTY")
    assert app.cmd("menu-undo") == 'undoing "Edit CASTFORM"'   # named as it was before the edit
    wait_status(app, "Undone: Edit CASTFORM")
    assert save.read_bytes() == original, "undoing the rename did not restore the file"

    rename_castform("MISTY")                         # again, then a move on top of it
    renamed = save.read_bytes()
    reply = app.cmd("drag 0 5")                      # Aron → empty slot 6
    assert "carrying=False" in reply, reply
    wait_status(app, "MOVED")
    assert _decoded(save, 1).get(6) == 382

    reply = app.cmd("menu-undo")
    assert reply == 'undoing "Move ARON"', reply
    wait_status(app, "Undone: Move ARON")
    assert save.read_bytes() == renamed, "first undo did not put the move back"
    assert species_at(app).get(0) == 304, app.slots()           # the grid shows Aron home again

    reply = app.cmd("menu-undo")
    assert reply == 'undoing "Edit CASTFORM"', reply
    wait_status(app, "Undone: Edit CASTFORM")
    assert save.read_bytes() == original, "second undo did not restore the original file"
    assert app.cmd("menu-undo") == "disabled", "Undo still offered with nothing left"

    # A change the undo list doesn't know (an emulator writing the file) blocks the next undo.
    rename_castform("RAIN")
    outside = bytearray(save.read_bytes())
    outside[-1] ^= 0xFF                              # past the save data: still a valid file, but not the one we wrote
    save.write_bytes(bytes(outside))
    assert app.cmd("menu-undo").startswith("undoing")
    wait_status(app, "can't be undone")
    assert save.read_bytes() == bytes(outside), "undo overwrote a change made outside PKForge"
    assert app.cmd("menu-undo") == "disabled"


def test_open_recent_and_finder(app, ctx):
    """File ▸ Open Recent and Finder's Open With: from any screen, back to Home and into that save's boxes;
    the save opened lately leads the Open Recent list."""
    first = open_save(app, ctx)

    def recent() -> list[str]:                       # the app keeps /var/…, the harness /private/var/…
        return [os.path.realpath(p) for p in app.cmd("recent").splitlines()]

    assert recent()[0] == os.path.realpath(first), recent()

    def wait_opened(path: Path, how: str):
        # The box page may already be in front (another save's): wait for this file to lead the list.
        deadline = time.monotonic() + 15
        while recent()[0] != os.path.realpath(path):
            assert time.monotonic() < deadline, f"{how} never opened {path.name}: {recent()}"
            app.cmd("sleep 200")
        app.waitfor("page", "BoxBrowserPage")
        app.settle()
    second = first.with_name("second.srm")
    shutil.copyfile(first, second)
    app.press("X")                                   # a menu in front: Open Recent still goes through
    app.cmd("sleep 300")
    app.cmd(f"open-path {second}")
    wait_opened(second, "Open Recent")
    state = app.state()
    assert state["stack"].count("BoxBrowserPage") == 1, f"boxes stacked twice: {state['stack']}"
    assert recent()[:2] == [os.path.realpath(second), os.path.realpath(first)], recent()
    # Finder: LaunchServices hands the file to the running app (the scene's URL contexts).
    third = first.with_name("third.srm")
    shutil.copyfile(first, third)
    subprocess.run(["open", "-g", "-a", str(ctx.inst.app), str(third)], check=True)
    wait_opened(third, "Finder's open")
    assert app.state()["stack"].count("BoxBrowserPage") == 1, app.state()["stack"]
    gone = first.with_name("gone.srm")
    app.cmd(f"open-path {gone}")                     # a recent file that was moved: a clear message, dropped from the list
    app.cmd("sleep 800")
    assert os.path.realpath(gone) not in recent()


def test_keyboard_prose_names_the_keys(app, ctx):
    """Sentences that name a button follow the hints: on a keyboard 'Return adds', not 'A adds';
    the Bank footer says what A does on the slot under the cursor."""
    app.cmd("glyphs keyboard")
    finish_onboarding(app)
    if pad(app) == "PadMenu":
        app.press("B")
    app.tap("Bank")
    app.cmd("sleep 700")
    status = app.state().get("box.status", "")
    assert "Return adds" in status and "A adds" not in status, status
    footer = {glyph: label for glyph, _, label, _ in _hints(app)}
    assert footer.get("A") == "Add" and footer.get("B") == "Back", footer
    app.cmd("glyphs pad")
    app.press("Right")
    app.press("Left")                                # the status is rebuilt on a cursor move
    app.cmd("sleep 200")
    assert "A adds" in app.state().get("box.status", ""), app.state().get("box.status")
    app.press("B")
    app.waitfor("page", "HomePage")
    app.settle()
    app.cmd("glyphs keyboard")
    app.tap("Dex Autopilot")
    app.cmd("sleep 700")
    texts = app.texts()
    assert "Press Return" in texts and "Press A" not in texts, texts
