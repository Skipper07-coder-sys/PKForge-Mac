# UI tests for the Mac app

Drives real app instances and reads the screen back as text, so tests need no screenshots.

```sh
tools/build-mac-test.sh                         # test build → dist/test.noindex/PKForge.app (~35 s)
python3 tools/uitest/pkf.py run -j 4            # every scenario, 4 instances side by side
python3 tools/uitest/pkf.py run -k write --repeat 3
python3 tools/uitest/pkf.py compare a.json b.json   # timing medians before/after a change
```

**How it works.** The test build defines `PKF_AUTOMATION`, which compiles in
`Platforms/MacCatalyst/Automation.cs`: a remote control listening on `127.0.0.1` only, and only when
`PKFORGE_AUTOMATION_PORT` is set. The normal build (`tools/build-mac.sh`) contains none of it, and
`Perf.Mark`/`Perf.Took` compile away.

Each instance is an APFS clone of the test build with its own bundle id (own preferences), its own
`HOME` (own Application Support, caches, backups, logs) and its own port, so instances run in
parallel and never touch the real app's data. Fixture saves are copied into that `HOME`; the
originals are never opened.

**Commands** (`pkf.py send N "<command>"` against an instance started with `pkf.py up N`):

| command | does |
|---|---|
| `texts [filter]` | visible text in reading order; `[bracketed]` = clickable |
| `tree [depth]` | the visual tree with types, frames and texts |
| `state` | top page, pad owner, nav stack, view-model state, selected mon |
| `slots` | the current box: species, items, legality |
| `hints` | each footer hint: button, key shown, label, clickable or not |
| `press <A\|B\|X\|Y\|L\|R\|Up…\|Start\|Select> [n]` | the same path as keyboard and controllers |
| `tap "<text>" [n]` | clicks the n-th visible element containing the text |
| `type "<text>" ["<field>"]` | types into a field (by placeholder), else the focused or topmost one |
| `select <party\|box#> <slot>` | a click on a grid slot |
| `key <char\|return\|esc\|tab\|pageup…>` | a keyboard key through the app's own key map (by character, like a real layout) |
| `drag <from> <to> [pages]` | press, move, rest on an edge until the boxes paged, let go (`to` -1 = off the grid) |
| `rightclick <slot>` | a right-click on a storage slot |
| `menu-save` | File ▸ Save Changes (⌘S): `saving` or `disabled` |
| `pick <path>` | the next file pick answers with this path, no open panel |
| `glyphs keyboard\|pad` | simulates the last controller leaving, or one connecting |
| `waitfor page\|pad\|text\|notext\|idle <value> [ms]`, `settle` | waits without sleeps |
| `perf [clear]`, `mem [gc]`, `log [n]` | timing marks, memory, the app log |

Scenarios live in `scenarios.py` (`test_*` functions, each on a freshly launched instance).
Failures print the screen as text (`state`, `texts`, the log tail). For pixel-level questions
(a GPU view, clipping) screenshot just the instance's window with `screencapture -l <window id>`.

Env: `PKF_WORK` (clones and homes, default `$TMPDIR/pkf-uitest`), `PKF_SAVES` (fixture saves,
default `../test-saves/originals` beside the repo), `PKF_GEN3_TOOLS` (independent Gen 3 checker).
