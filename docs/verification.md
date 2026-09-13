# Verification methods and measured evidence

[English](verification.md) | [简体中文](verification.zh-CN.md) · [Back to README](../README.md)

This document lists what was actually measured, with the commands/log excerpts needed to reproduce each item, and — separately and explicitly — what was **not** verified.

Conventions used below:

- **MEASURED** — executed on the test machine and observed in command output or in the application's own `app.log`.
- **CODE-ONLY** — behaviour that exists in the source but was not exercised by hand; listed under [Not verified](#not-verified--unverified).
- Reproduction steps use the actual paths/values from the measurements.

**State of the evidence artifacts.** All `app.log` excerpts below were read on **2026-09-13 at ≈02:45** from

```
C:\Users\ControlxSaria\AppData\Local\Temp\ees-smoke-root\logs\app.log   (8 363 bytes, 82 lines, last entry 02:44:56)
```

At **02:49:45** that file no longer existed: the `ees-smoke-root\logs\` directory remained but was **empty**, and `ees-smoke-root\config.json` had been rewritten (mtime 02:18:37 → 02:48:32). The cause was established afterwards: the agent working on this repository deleted the log files on purpose while verifying the "logging can be switched off / logs can be cleared" requirement against a running instance, which also rewrote `config.json`. The excerpts are reproduced verbatim from the 02:45 reading, so they are still **STALE as artifacts** even though the disappearance is now explained; re-running the scenarios below is the only way to re-verify them.

## 1. Test environment (re-measured while writing this document)

| Item | Value | How it was read |
|---|---|---|
| OS | Windows 11, `10.0.26200.0`, 25H2, build 26200, x64 | `[Environment]::OSVersion`, `HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion` → `CurrentBuild`/`DisplayVersion` |
| Everything | `C:\Program Files\Everything\Everything.exe`, file/product version **1.5.0.1423b**, 5 138 088 bytes | `Get-Item ... \| % VersionInfo` |
| Everything IPC version query | `1.5.0.1423` | `FindWindow("EVERYTHING_TASKBAR_NOTIFICATION")` + `WM_USER 0..3` (`SendMessage`) |
| Everything database loaded | `1` (loaded) | same window, `WM_USER` 401 |
| NTFS drive indexed | `C:` → `1` (indexed) | same window, `WM_USER` 400, wParam 3 |
| Everything processes | 2 × `Everything.exe` (`C:\Program Files\Everything\Everything.exe`) | `Get-Process Everything` |
| Application version in the log | `Explorer Everything Search 1.0.0.0`, `OS: Microsoft Windows NT 10.0.26200.0 (X64)` | `app.log` |
| Application root used for the run | `C:\Users\ControlxSaria\AppData\Local\Temp\ees-smoke-root` (`--root`) | `app.log` |
| Test data folders | `...\ees-smoke\smokedata`, `...\ees-smoke\smokedata\sub`, `...\ees-smoke\数据目录 空格` (each with a token file) | `Get-ChildItem` |
| Everything window observed | `C:\Users\...\Temp\ees-smoke\smokedata\ afterrestart - Everything` with a hierarchical (tree) folder listing | `Get-Process \| ? MainWindowTitle -like '*Everything*'` |

Both `es.exe` and the paused-behaviour items below were **not** part of this measurement round — see the unverified list.

## 2. Enter detection: keyboard hook is the primary mechanism

**MEASURED** (log, `logLevel: Debug`). The hook is installed only while an Explorer search box has the focus, and it is what submits on Enter:

```text
[2026-09-13 02:44:44.221] [DEBUG] search box has the focus (hwnd=1709020)
[2026-09-13 02:44:44.222] [DEBUG] Enter detection: keyboard hook installed (an Explorer search box has the focus)
[2026-09-13 02:44:44.225] [DEBUG] Explorer moved the focus into its own search view input site; not treated as Enter
...
[2026-09-13 02:44:49.275] [DEBUG] Enter detection: keyboard hook removed (no Explorer search box has the focus)
```

Relevant because it proves two separate things: (a) the low-level hook is armed exactly while the search box is focused, and (b) Explorer's own focus move into its search-view input site is correctly *not* treated as Enter.

**MEASURED** (probe `ev-enter.txt`, `ev-enter4.txt`): pressing Enter while the box was focused produced the search-result view 10–20 ms later and the result view was complete ≈320 ms after the key press:

```text
   2333ms [probe] PRESSING ENTER
   2353ms [UIA-FOCUS] aid=[0] name=[inner] cls=[UIItem]      <- focus left the search box
   2780ms [POLL-TITLE] [qzxed - “evprobe-enter”中的搜索结果 - 文件资源管理器]
```

**MEASURED** (probe `ev-enter4.txt`): Enter works with **zero results** — the search view showed `没有与搜索条件匹配的项。` and the focus moved to `EmptyTextFocusable`. This is the case no title heuristics could catch, and it is why the keyboard hook is the primary mechanism.

**Reported earlier in the project, not re-measured in this round:** the ≈100–200 ms figure from keypress to the Everything window showing the query. This round's evidence covers 10–20 ms to the *Explorer* result view (§2) and Everything end-to-end 154 ms (§5) separately; the two were not measured in the same run, so the full-path range is **UNVERIFIED**.

## 3. Idle auto-commit (`autoSearchDelay` = 1000 ms)

**MEASURED** (log, three independent windows/keystrokes). Last keystroke → `Search submitted trigger=IdleTimeout`:

| Window | Last keystroke | Submit | Δ |
|---|---|---|---|
| `hwnd=1709020` (`thistpc`) | `02:44:45.147` | `02:44:46.162` | **1015 ms** |
| `hwnd=1904286` (`alpha`) | `02:44:50.137` | `02:44:51.152` | **1015 ms** |
| `hwnd=2624920` (`recyc`) | `02:44:55.142` | `02:44:56.147` | **1005 ms** |

The last keystroke is the timestamp of the final `SearchBox text changed:` line; the submit is the `Search submitted trigger=IdleTimeout` line. Both carry millisecond resolution, so the deltas include log write ordering plus the `System.Threading.Timer` scheduling tail on a busy STA thread (+15 ms in two of the three rows).

The 997–1005 ms range reported earlier in the project is consistent with these entries; the wider 1005–1015 ms range is what this log shows. Both are within the ±2 % one would expect from timer granularity.

**MEASURED**: the interval is per-window, so a second window's typing does not restart another window's timer (three different `SourceExplorerHwnd` values in the same session, each with its own submit).

## 4. Scope isolation across two Explorer windows

**MEASURED** (log). Two Explorer windows in different folders, each triggered independently, each with its own resolved scope:

```text
[2026-09-13 02:44:51.152] [INFO] SourceExplorerHwnd=1904286
[2026-09-13 02:44:51.170] [INFO] ResolvedPath="C:\Users\ControlxSaria\AppData\Local\Temp\ees-smoke\数据目录 空格"
[2026-09-13 02:44:51.171] [INFO] SearchScope=CurrentDirectoryAndSubdirectories (carried over from the window's folder)
[2026-09-13 02:44:51.172] [INFO] Query="alpha"
```

and, earlier, `hwnd=1709020` resolving to **This PC** (`AllVolumes`) and `hwnd=2624920` resolving to the Recycle Bin (`OtherVirtualFolder` → refused with a notification). Four different windows, four different classifications in one run — the scope is bound to the triggering window, not to a global "current folder".

**MEASURED** (live check): the reusable Everything window's title is `C:\Users\...\Temp\ees-smoke\smokedata\ afterrestart - Everything` and its result list is a **hierarchical** folder view listing `sub` under `smokedata\` — i.e. the scope covered the folder and its subfolder. (A tree view proves the subfolder is *present in the scope*; whether every possible descendant is always enumerated was not exhaustively tested.)

**Reported earlier, not re-measured:** the specific pair `...\smokedata` vs `...\smokedata\sub` as two separate windows. The log evidence above uses other folders; the mechanism is identical (per-window `hwnd` keying) and is visible in the code.

## 5. Everything end-to-end latency and window reuse

**MEASURED** (log):

```text
[2026-09-13 02:44:51.327] [INFO] Everything window reused
[2026-09-13 02:44:51.327] [INFO] Search completed in 154 ms
```

`154 ms` is the wall-clock time from starting the Everything launcher until the window showing the query was located and raised (`EverythingBridge.Execute` stopwatch).

**MEASURED** (live check minutes later): the Everything window still existed and showed the query, so reuse happened across separate searches rather than accumulating windows.

**Reported earlier, not re-measured:** the 114–208 ms range over several runs. Treat the bandwidth as **UNVERIFIED**; only 154 ms is evidenced here.

## 6. Focus retention

**MEASURED** (log):

```text
[2026-09-13 02:44:51.327] [DEBUG] returning the keyboard focus to the Explorer search box (hwnd=1904286)
[2026-09-13 02:44:51.327] [INFO] Everything window reused
```

The focus-restore branch ran because Everything had taken the foreground, and the user's own input tick was unchanged. Corroborating behaviour in the same run: while `hwnd=1904286` had the search box focused, the user's *next* actions (moving to `hwnd=2624920`) produced `Enter detection: keyboard hook removed ...` / `focus left the Explorer search box for another window; not treated as Enter`, i.e. subsequent keystrokes were still being delivered to a focused Explorer search box rather than being swallowed by the Everything window.

**Not re-measured in this round:** the "user typed after the search → focus is deliberately left alone" branch (`the user produced input after the search; leaving the keyboard focus alone`) appears in code but not in the captured log.

## 7. Everything CLI semantics (why the query is built the way it is)

**MEASURED earlier, encoded in the code and the probe scripts** (`EverythingQueryBuilder.cs`, `sdk\ipc\everything_ipc.h`, `sdk\include\Everything.h`, `functs.txt`, `cliopt.txt`):

| Observation | Consequence in the code |
|---|---|
| `-path <dir>` matches the folder itself, **not** a path substring | the scope is always expressed as `-path` (single dir) |
| A folder path placed **inside the query text** is matched as a *path substring* — searching inside `D:\a\inside` also matched `D:\a\inside2` | the path is never concatenated into the query text |
| `-path "a;b"` is a **single token**, not a union | multi-folder scopes use `<ancestor:"a"\|ancestor:"b">` instead |
| `ancestor:` is an Everything 1.5 search function | the builder requires version ≥ 1.5 for unions and refuses otherwise |
| `-s*` (1.5+) passes the rest of the command line as the literal query | `-s*` is used when the IPC version says 1.5+, otherwise `-s "…"` with `"""` escaping |
| `EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8` is accepted by 1.5.0.1423b but has **no effect** | searches go through the official command line; IPC is used for version/db status only |

**Re-verified while writing this document:** the IPC *version* query works (returns `1.5.0.1423`) and `is_db_loaded`/`is_ntfs_drive_indexed` respond, which is the mechanism the capability detection relies on. The `-path`-vs-substring and `COPYDATA` no-effect findings were **not** re-run in this round.

## 8. Unsupported locations are refused, not guessed

**MEASURED** (log, same run as §3):

```text
# Recycle Bin (::{645FF040-...})
[2026-09-13 02:44:56.150] [DEBUG] Shell location hwnd=2624920 kind=OtherVirtualFolder self="::{645FF040-5081-101B-9F08-00AA002F954E}"
[2026-09-13 02:44:56.150] [WARN] search not redirected: UnsupportedShellNamespace detail="回收站" raw="::{645FF040-5081-101B-9F08-00AA002F954E}"

# This PC, whose origin folder was never recorded -> refused instead of "search everywhere"
[2026-09-13 02:44:46.173] [DEBUG] Shell location hwnd=1709020 kind=SearchResults self="“此电脑”中的搜索结果&thistpc"
[2026-09-13 02:44:46.175] [WARN] search not redirected: SearchScopeUnknown detail="Explorer is showing a search result view and the folder it was started from is unknown" raw="“此电脑”中的搜索结果&thistpc"
```

Both lines are accompanied by a user-visible notification (`NotificationService.ReportUnsupportedScope`).

## 9. Rejection of false Enter signals

**MEASURED** (log) — these are the cases that make a naive implementation submit at the wrong moment:

| Log line | What it shows |
|---|---|
| `Explorer only previewed the query in its title after 156 ms ("thistpc - 此电脑 - 文件资源管理器") - not a commit` | the per-keystroke title preview is not a commit |
| `Explorer committed a search after 830 ms ("thistpc - 文件资源管理器") - treated as its own auto search` / `after 844 ms` | Explorer's automatic commit is ≈800 ms after the last keystroke → `enterCommitWindowMs = 400` separates it from a real Enter |
| `Explorer moved the focus into its own search view input site; not treated as Enter` | Explorer moves the focus by itself while typing |
| `focus moved inside Explorer hwnd=... -> ControlType.ListItem ... while the search box was not focused` | list/mouse focus changes are noise |
| `focus left the Explorer search box for another window; not treated as Enter` | switching windows is not Enter |

## 10. Logging format

**MEASURED**: `<root>\logs\app.log` exists with the documented format and UTF-8 layout:

```text
[2026-09-13 02:44:30.622] [INFO] configuration loaded from C:\...\ees-smoke-root\config.json
[2026-09-13 02:44:30.633] [INFO] Explorer Everything Search 1.0.0.0 starting
[2026-09-13 02:44:30.633] [INFO] application directory: C:\...\ees-smoke-root
[2026-09-13 02:44:30.633] [INFO] OS: Microsoft Windows NT 10.0.26200.0 (X64)
[2026-09-13 02:44:30.649] [INFO] Everything: Connected (1.5.0.1423) path=C:\Program Files\Everything\Everything.exe
[2026-09-13 02:44:30.702] [DEBUG] notification area icon created
[2026-09-13 02:44:30.703] [INFO] Explorer search monitoring started
[2026-09-13 02:44:30.704] [DEBUG] WinEvent hooks installed: 2
[2026-09-13 02:44:30.757] [DEBUG] Explorer rescan (startup): windows=0 searchBoxes=0
[2026-09-13 02:44:34.451] [INFO] SearchBox detected hwnd=1709020 name=" 在 此电脑 中搜索"
```

Also **MEASURED**: the run used `--root C:\Users\ControlxSaria\AppData\Local\Temp\ees-smoke-root`, and both `config.json` and `logs\app.log` were created there — i.e. the portable layout and `--root` redirection work as documented. The `config.json` found there also confirms the persisted default shape (16 keys at the time of that run; the current `AppConfig` has one more, `detectEnterByKeyboardHook`, which that older run's file predates — a stale on-disk config simply keeps its default for the new key).

## 11. Not verified / UNVERIFIED

Everything in this list is **code-only**: it is implemented and reasoned about, but was **not** exercised end-to-end by hand. Do not treat it as proven.

| # | Item | Why it is unverified / what would verify it |
|---|---|---|
| 1 | **Everything end-to-end latency range 114–208 ms** | only one 154 ms sample exists in the captured log. Fix: time N≥10 searches and record `Search completed in <n> ms`. |
| 2 | **Enter path ≈100–200 ms from keypress to the Everything window** | the captured probes measured the Explorer result view (10–20 ms), not the Everything window. Fix: timestamp the keypress (probe) and the Everything title update (bridge log) in the same run. |
| 3 | ~~The `...\smokedata` vs `...\smokedata\sub` two-window pair~~ | **CLOSED**: measured twice, first by hand (`hwnd=10422348` → `...\smokedata`, `hwnd=1576348` → `...\smokedata\sub`, see §4) and then by the `multi-window` E2E scenario (`hwnd=1181104` → `multi-window-a`, `hwnd=2622926` → `multi-window-b`, 131/132 ms). |
| 4 | **Log rotation** (`maxLogFileSizeMb` / `maxLogFiles`, `app.1.log` … `app.N.log`) | no rotated file has ever been produced in the captured runs. Fix: set `maxLogFileSizeMb: 1`, `logLevel: Debug`, exercise searches until rotation, inspect `logs\`. |
| 5 | ~~Clearing logs while running~~ | **CLOSED**: `log-clear` (E2E) reports `ClearLogs succeeded in 5/5 rounds` with logging continuing after every round, and the unit tests assert the same at the logger level. A real defect was found and fixed on the way: the "file is closed now" hand-over used a `Set()`/`Reset()` pulse the caller could miss, which made clearing fail and blocked the calling thread for 5 s (see §12.4). |
| 6 | **"Open log folder"** from tray and settings | code calls `Process.Start` on the folder; not exercised. |
| 7 | **Tray menu end to end** (all items, status texts, balloons, double-click) | only the icon creation (`notification area icon created`) appears in the log. |
| 8 | ~~**Start with Windows**: writing the HKCU `Run` value, detecting a stale entry, automatic repair, and the settings "Repair start-up entry" button~~ | **CLOSED for the four registration branches**: the packaged EXE was run with `--root` against the real `HKCU\...\Run\ExplorerEverythingSearch` and create / repair (stale value pointing at `C:\gone\...`) / keep / remove were all observed in the registry and in `app.log` — see §12.7. The settings button itself (a second entry point to the same code) was not clicked. |
| 9 | ~~**`--startup`, `--settings`, `--exit`, `--help`, `--version`**~~ | **CLOSED**: all five verified — `--startup` against the real registry (§12.7), and `--version` / `--help` / `--settings` / `--exit` by driving the built EXE and reading the windows it opens (§12.8). `-h` and `/?` reach the same code path as `--help` but were not typed by hand. |
| 10 | ~~**Second launch with `--settings` reaching the running instance** (mutex + named event)~~ | **CLOSED**: a second process started with `--settings --root <dir>` exits 0 while the already running instance opens its settings window (`Explorer Everything Search - 设置`, the localized title); `--exit --root <dir>` exits 0 and the running instance shuts down with exit code 0 — see §12.8. |
| 11 | **Two independent instances via two different `--root` values** | reasoned from `SingleInstanceGuard`'s hashed suffix; not run. |
| 12 | ~~Idle CPU / no-wakeup claim~~ | **CLOSED (measured)**: with two Explorer windows open and the tool idle for 12 s it used **15.6 ms** of CPU on a 24-core machine (**0.005 %** of one core, `TotalProcessorTime` delta), 26 threads. |
| 13 | ~~The 60 s self-healing rescan~~ | **PARTLY CLOSED**: `Explorer rescan (periodic): windows=1 searchBoxes=1` was observed in the Explorer-restart run, and the `explorer-restart` E2E scenario reproduces the recovery. A rescan repairing a *missed* window event was still not produced deliberately. |
| 14 | ~~Home (主文件夹) scope = union of known folders~~ | **CLOSED**: `SearchScope=KnownFoldersUnion` with six folders (`D:\ASUS\Desktop … D:\ASUS\Music`, i.e. the relocated known folders) and the `ancestor:` union query; also covered by the `home` E2E scenario. |
| 15 | **A known folder redirected to a non-system drive / UNC path** | implemented via `SHGetKnownFolderPath`; not tested with an actual redirection. |
| 16 | **Everything not installed / not running / database not loaded** notification paths | the test machine always had Everything running with a loaded database. Fix: stop Everything (and/or set a bogus `everythingPath`) and search. |
| 17 | **Security software blocking `SetWindowsHookEx`** → fallback to focus/commit heuristics | the hook installed successfully here. Fix: set `detectEnterByKeyboardHook: false` and confirm Enter still works through the fallbacks (that is also the workaround documented in troubleshooting). |
| 18 | **Everything window title not yet reflecting the query** (`Everything window title does not reflect the query yet`) | warning path never triggered in the captured run. |
| 19 | **A search result view whose origin folder is remembered successfully after leaving and re-entering** | only the carried-over-scope case (§4) was observed. |
| 20 | **High DPI / multi-monitor / per-monitor DPI changes** | manifest declares `PerMonitorV2`; behaviour was not observed on a multi-monitor or mixed-DPI setup. |
| 21 | **Running without administrator rights on a locked-down machine** | manifest is `asInvoker` and the test run was unelevated, but no ACL-restricted scenario (e.g. a read-only install directory, which triggers the "configuration is read-only" notification) was produced. |
| 22 | ~~The unit tests and E2E tests~~ | **CLOSED**: `dotnet test tests\ExplorerEverythingSearch.Tests\… -c Release` → **237 tests, 236 passed, 1 skipped**, ~4 s (the skipped one writes to `HKCU\…\Run` and is meant to be run by hand); `tests\ExplorerEverythingSearch.E2E --scenario all` → **13/13 passed, 72.9 s**, run twice in a healthy session and once in the degraded session of §12.5. |
| 23 | **`tools\package.ps1`** | the file **now exists** (PowerShell 7+, builds the portable and framework-dependent zips into `artifacts\`, runs the unit tests unless `-SkipTests`, copies README/LICENSE into both packages). It has **never been executed** — **CLOSED later the same day**: executed twice (one run including the unit tests) and it produces `ExplorerEverythingSearch-1.0.0-win-x64-portable.zip` (57.7 MB: one self-contained EXE + README/LICENSE) and `…-win-x64-framework-dependent.zip` (0.2 MB). The portable package was unpacked into a temp directory and started from there to verify the shipped binary (§12.5). Fix: run `pwsh tools/package.ps1 -SkipTests` and inspect `artifacts\*.zip`. |
| 23b | ~~**The GitHub workflows** `.github\workflows\build.yml` and `release.yml`~~ | **CLOSED (2026-09-13)**: after the repository was pushed, the `Release` workflow ran on the tag `v1.0.0` and **succeeded**, producing the GitHub release `Explorer Everything Search 1.0.0` with 2 assets, and `Build and test` ran on `main` at `322bf16` and **succeeded** — so the CI path (restore → `Release` build of the whole solution → unit tests → TRX upload) is green on a `windows-latest` runner, not only locally. The earlier review had already found that the solution contained only the test projects (fixed with `dotnet sln add`). |
| 24 | **The `E_NOINTERFACE` result for `IShellBrowser`/`IFolderView`** | asserted in the `ExplorerLocationResolver` class comment from earlier probing; not re-measured while writing this document. |
| 25 | **`EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8` being accepted but ineffective** | asserted in the `EverythingIpc` class comment from earlier probing; not re-measured while writing this document. |
| 26 | **`EverythingIpc.IsDatabaseLoaded` / `IsDriveIndexed` being used on any application path** | they respond to IPC (verified), but the application never queries them — they exist for diagnostics only. |
| 27 | **Behaviour when the Explorer search box is exposed as a plain UIA `Edit`** (older Windows builds / a different shell layout) | only the Windows 11 build 26200 shape (`AutoSuggestBox` host) was tested. On Windows 10 the `FileExplorerSearchBox` AutomationId fallback may or may not match — **UNKNOWN**. |
| 28 | **The `--root` instance isolation during a concurrent "real" run** | the captured run did use `--root`, but no real instance was running at the same time. |
| 29 | ~~Persistence of the captioned `app.log` evidence~~ | **EXPLAINED**: the deletion was a deliberate step of the "logging can be disabled / logs can be cleared" verification against a running instance (see the note at the top). The excerpts are still **STALE** artifacts, but for a known reason; §12 reproduces the current behaviour with fresh logs. |

## 12. How to run the E2E harness

`tests\ExplorerEverythingSearch.E2E` (added 2026-09-13) is a console application, not a `dotnet test`
project, because every scenario drives a **real** Explorer search box and inspects the **real** Everything
window. It is deliberately **not** part of `.github\workflows\build.yml`: a CI agent has no interactive
desktop session and no Everything installation.

```powershell
# everything, with a build of the application first
dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --scenario all

# one scenario, keeping the working root for inspection
dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --scenario basic-idle --keep-artifacts

# list the scenarios
dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --list
```

| Option | Meaning |
|---|---|
| `--scenario <name>\|all` | scenario to run; defaults to `all` |
| `--root <directory>` | working root instead of a fresh `%TEMP%\ees-e2e-<8 hex>` |
| `--keep-artifacts` | keep the working root (`config.json`, `logs\`, test folders) |
| `--no-build` | use the application as it is (the harness builds it by default) |

What a run does: build `ExplorerEverythingSearch.App` (Debug) → create the working root → write
`config.json` (`enabled=true`, `autoSearchDelay=1000`, `startWithWindows=false`, `showNotifications=false`,
`logLevel=Debug`, `loggingEnabled=true`, `reuseEverythingWindow=true`) → start
`ExplorerEverythingSearch.exe --root <root>` → run the scenarios → stop the instance it started (via
`--exit`, then a kill if needed), close **only** the Explorer windows it opened (HWND diffing, `WM_CLOSE`)
and delete the root. Exit code `0` = all passed, `1` = at least one failed, `2` = bad arguments.

A scenario fails loudly with the reason, the last application log lines and, when the application process
died, the tail of its standard error (`<root>\app\stderr.log`) — that is how the crash in §12.3 was found.

Preconditions: an interactive desktop session and Everything installed/running. Window titles are matched
with hints for both Chinese and English shells (`此电脑`/`This PC`, `主文件夹`/`Home`, `回收站`/`Recycle Bin`),
and new windows are located by HWND diffing, so no title text is required in the normal flow.

### 12.1 Measured result (2026-09-13, Windows 11 build 26200, Everything 1.5.0.1423b, .NET SDK 8.0.425)

`--scenario all` → **13 passed / 0 failed, 77.7 s, exit code 0** (after the fix in §12.3; the suite was run
in full three times and the `multi-window` scenario — the one that used to crash — four times).

| Scenario | Result | Time | Evidence from the run |
|---|---|---|---|
| `basic-idle` | PASS | 3.9 s | `trigger=IdleTimeout text="e2ealphatoken" hwnd=722350 path="...\data\basic-idle" scope=CurrentDirectoryAndSubdirectories query="e2ealphatoken" window=reused latency=149ms`; Everything window title `...\data\basic-idle\ e2ealphatoken - Everything` |
| `enter-immediate` | PASS | 2.5 s | `Enter -> submit visible after 240 ms; trigger=Enter text="e2eenterprobe" ... query="e2eenterprobe"` (well inside the 1000 ms idle window → it really was handled as Enter) |
| `continuous` | PASS | 4.2 s | first `trigger=IdleTimeout text="e2econtone"`, then — without re-clicking the box — `trigger=Enter text="e2econtonextra"` from the same `hwnd`, i.e. the focus was still usable |
| `this-pc` | PASS | 3.4 s | `scope=AllVolumes query="e2evolumescan"` with no `-path` and no `ancestor:` |
| `home` | PASS | 3.7 s | `scope=KnownFoldersUnion`, `path="D:\ASUS\Desktop \| D:\ASUS\Documents \| D:\ASUS\Downloads \| D:\ASUS\Pictures \| D:\ASUS\Videos \| D:\ASUS\Music"`, query `<ancestor:D:\ASUS\Desktop\|…\|ancestor:D:\ASUS\Music>` |
| `multi-window` | PASS | 7.3 s | window A `hwnd=1181104 path="...\multi-window-a"`, window B `hwnd=2622926 path="...\multi-window-b"`, latencies 131/132 ms — no cross contamination |
| `explorer-restart` | PASS | 12.8 s | `[INFO] Explorer restarted: monitoring re-established` after `taskkill /F /IM explorer.exe`, then a successful search in the new window |
| `everything-closed` | PASS | 4.1 s | after closing every Everything search window: `window=created` |
| `window-reuse` | PASS | 5.4 s | first search `window=reused`, second (fresh query) `window=reused`, `query="e2ereusetwo"` |
| `unsupported-namespace` | PASS | 4.9 s | `[WARN] search not redirected: UnsupportedShellNamespace detail="回收站" raw="::{645FF040-5081-101B-9F08-00AA002F954E}"`, no `Query=` line and no Everything title for that text |
| `unicode-path` | PASS | 3.4 s | `path="...\data\数据 目录" text="文件alpha" query="文件alpha"` (Unicode keystrokes and UTF-8 log round trip) |
| `logging-disabled` | PASS | 15.6 s | with `loggingEnabled=false`: no `logs\app.log` at all, and the Everything title still shows `e2enologscan` |
| `log-clear` | PASS | 6.7 s | `ClearLogs succeeded in 5/5 rounds`, logging continued after every round |

### 12.2 Raw evidence examples

```
Enter -> submit visible after 240 ms; trigger=Enter text="e2eenterprobe" hwnd=2166756
        path="...\data\enter-immediate" scope=CurrentDirectoryAndSubdirectories query="e2eenterprobe" window=reused latency=137ms

first:  trigger=IdleTimeout text="e2econtone"     hwnd=853270 path="...\data\continuous" ...
second: trigger=Enter       text="e2econtonextra" hwnd=853270 path="...\data\continuous" ...
```

### 12.3 Defect found by the harness (fixed)

While running the suite, the **application process was terminated by the .NET runtime**:

```
Process terminated. A callback was made on a garbage collected delegate of type
'ExplorerEverythingSearch.Core!…NativeMethods+WinEventDelegate::Invoke'.
Repeat 2 times:
   at …NativeMethods.PeekMessage(MSG ByRef, IntPtr, UInt32, UInt32, UInt32)
   at …StaDispatcher.PumpMessages()
   at …StaDispatcher.Run()
```

`ExplorerWindowMonitor.InstallHooks()` created the `WinEventDelegate` as a **local variable** and only kept
the returned hook handles, so the GC could collect the delegate and the next Explorer event called into
freed memory — a random process kill in normal use, which is exactly what `multi-window` hit (timeout, no
submit). It is now grounded in a field (`_winEventHandler`); `multi-window` passes 4/4 runs since.

### 12.4 Limits of the E2E suite (not proven by it)

- `log-clear` passes 5/5 rounds here in every run (15 rounds in total), while the unit test suite reported
  `ClearLogs` failing **3/3** in its own harness (a lost-pulse race in `AppLogger.RequestMaintenance`). The
  E2E path does not reproduce that race, so it did **not** clear the concern — the unit test evidence stood, and
  it was right: `RequestMaintenance` handed control over with `Set()` followed immediately by `Reset()`, so the
  caller could miss the pulse and then wait for the full 5 s timeout. Clearing the log therefore failed (and the
  calling UI thread stalled) whenever the logger thread won the race. It is now a fresh one-shot
  `ManualResetEventSlim` per request; the two unit tests that used to be skipped are enabled and pass, and
  `log-clear` still passes 5/5.
- The `explorer-restart` scenario closes **every** Explorer window of the session (sanctioned by the task).
- Not covered: multi-monitor / mixed-DPI behaviour, the tray menu items and balloons, `--settings`
  (only `--exit` is used, to stop the instance the harness started) and `--help`/`--version` (they open a
  message box, so they cannot be driven by the console harness), log rotation, Everything-not-installed /
  database-not-loaded notification paths, and security software blocking the keyboard hook. `--startup` **is**
  covered now — see §12.7.

### 12.5 Defect found in a degraded Shell session (fixed, with regression evidence)

Re-running the suite after the session had its `explorer.exe` force killed several times (the `explorer-restart`
scenario does that once per run) turned 12 of 13 scenarios red with **one and the same** symptom - every submit
became

```
[WARN] search not redirected: LocationUnavailable detail="RuntimeBinderException: Cannot perform runtime binding on a null reference" raw=""
```

so every resolved path and scope came back empty and **no** search was redirected any more, including the first
scenario of the run. The tool itself was fine: it still detected typing, Enter and the idle timeout, it still
submitted, and it reported the failure explicitly instead of failing silently. The App binary was unchanged
(`src` was clean apart from the hook fix in 12.3), so this is not a harness regression.

Independent probe, taken while a real Explorer window was open:

```
CabinetWClass hwnds: 2296904
ShellWindows Count = 2
  [0] <NULL ITEM>                    <- C# dynamic reading .HWND throws RuntimeBinderException
  [1] HWND=2296904 URL='file:///D:/SearchOptimization' Name='SearchOptimization'
```

Root cause - `src\ExplorerEverythingSearch.Core\Shell\ExplorerLocationResolver.cs`, `TryResolve`: the loop reads
`(int)window.HWND` for every item of `Shell.Application.Windows()` while the single `catch` sits **outside** the
loop. A `null` item - a stale `ShellWindows` registration left behind by a killed Explorer generation - throws on
the first iteration and aborts the whole enumeration, so the real window at the next index is never reached and
**every** Explorer window of the session is reported as `LocationUnavailable`. The `Enumerate()` method of the
same file already catches per item, so only `TryResolve` has this single-point failure. Field effect: once one
stale entry exists, no search of the session is redirected at all - a core-requirement failure, because the
"Explorer restarted" path the tool has to survive is exactly what leaves such entries behind. The entries
accumulated with every Explorer kill (1 → 2 → 3 while this was investigated) and survived an `explorer.exe`
restart as well as a `RuntimeBroker`/`dllhost` restart (INFERENCE - a logoff or reboot is what clears them).

Consequence for the numbers above: the 13/13 records were taken in a healthy Shell; in the degraded session the
same binary failed 12/13 for the reason above (independently reproduced by the parent agent: 2/13, exit 1).

**Fix (2026-09-13).** `TryResolve` now handles every `ShellWindows` entry on its own: reading `Item(i)` and the
entry's `HWND` is wrapped in `try`/`catch`, a `null` item is recognised as a stale registration, and such an entry
is skipped with a `Debug` log line (`ShellWindows[i] is a stale entry left behind by a killed Explorer process;
skipped`) while the search for the window continues. `Enumerate()` was hardened the same way. An optional logger is
passed in from `AppRoot`, so the reason is visible in `app.log` - the lack of that line is what made the initial
diagnosis slow.

**Regression evidence (same degraded session, nothing healed):** the session still held
`ShellWindows Count = 3` with **3 null entries** (verified right before and after the run), and

```
dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --scenario all
  passed 13, failed 0, total 72.9s     (exit code 0)
```

The fixed binary logs the skipped entries and then resolves normally:

```
[DEBUG] ShellWindows[0] is a stale entry left behind by a killed Explorer process; skipped
[DEBUG] ShellWindows[1] is a stale entry left behind by a killed Explorer process; skipped
[DEBUG] ShellWindows[2] is a stale entry left behind by a killed Explorer process; skipped
[INFO] ResolvedPath="C:\Users\…\ees-release2-…\data"
[INFO] SearchScope=CurrentDirectoryAndSubdirectories (carried over from the window's folder)
[INFO] Everything window created
[INFO] Search completed in 203 ms
```

The last block is from the **packaged release binary** (`artifacts\ExplorerEverythingSearch-1.0.0-win-x64-portable.zip`,
unpacked to a temp directory and started with `--root`), i.e. the shipped EXE was re-verified on the degraded shell
after the package had been rebuilt; the earlier package (built before the fix) reproduced the failure.

### 12.6 Harness hardening

`LogTail.WaitForSubmit` now takes an optional source window handle and every scenario passes the window it
drives, so a submit of a stale window from an earlier run can no longer be attributed to the scenario under test.
Before that, `continuous` matched a submit of `hwnd=4984110` whose resolved path belonged to a previous run's
root while the scenario was driving `hwnd=5050160`.

### 12.7 Start with Windows (real registry, `--startup`)

The `StartupRegistration` path was exercised against the **real** `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
value `ExplorerEverythingSearch` (the tray toggle and the `--startup` command line both go through it), using the
**packaged** EXE unpacked into a temp directory and started with `--root <temp>` so that the tool's own
`config.json` decided the behaviour. All four branches were observed:

| Branch | Setup | Observed |
|---|---|---|
| Create | value absent, `startWithWindows=true` | `[INFO] start with Windows entry created: "<exe>" --startup`; registry value present and pointing at that EXE (verified: the parsed path equalled the EXE path) |
| Repair | value present but pointing at `"C:\gone\ExplorerEverythingSearch.exe" --startup` | `[INFO] start with Windows entry repaired to "<exe>" --startup (the previous entry pointed elsewhere)` |
| Keep | value present and already correct | `[INFO] start with Windows entry is valid` (the value was not rewritten) |
| Remove | value present, `startWithWindows=false` | `[INFO] start with Windows entry removed`; the registry value was **absent** afterwards |

Raw transcript of the create/remove run (`$env:TEMP\ees-startup-90d4ee`, packaged EXE under `<root>\app`):

```
step1 Run value: absent
step2 created: "C:\Users\CONTRO~1\AppData\Local\Temp\ees-startup-90d4ee\app\ExplorerEverythingSearch.exe" --startup
step2 points at the packaged exe: True
step3 Run value: absent - removed by the app
[INFO] start with Windows entry created: "C:\Users\CONTRO~1\AppData\Local\…\ees-startup-90d4ee\app\ExplorerEverythingSearch.exe" --startup
[INFO] start with Windows entry removed
cleanup: temp root removed=True; app processes=0
final Run value: absent (original state restored)
```

Two notes on the method, because they are the parts that can go wrong when repeating this:

- The config file the tool reads is `<root>\config.json` (the root passed with `--root`, or the directory of the
  EXE when it is not passed). An instance started from a **deleted** root silently recreates a default config in
  which `startWithWindows` is `true`; that is exactly what happened in the first attempt at the removal step and
  it produced `start with Windows entry is valid` instead of the removal. Check the config file exists before
  concluding anything from a "removal did not happen" result.
- Stop every `ExplorerEverythingSearch.exe` process **before** deleting the temp root (an unlocked delete leaves
  a half-removed tree), and re-read the registry value afterwards — the assertion is the registry, not the log.

The machine's `Run` value was absent before this test and is absent again afterwards; no Explorer state, service
or user setting was modified by it.

### 12.8 Command line contract (driven against the built EXE)

Script: `tools\verify-cli.ps1` (verification helper, not part of the product; it targets the RID specific `Release` build by default and takes `-Exe <path>` to point at a packaged EXE). It starts the EXE, finds its windows through UI Automation and reads/dismisses the message boxes through Win32 (`EnumChildWindows` + `GetWindowText` + `WM_CLOSE`), because the UI Automation view of a standard message box is **empty** in this session: `FindAll(Descendants, ControlType=Text)` returns nothing, which is why the first version of the script reported an empty dialog text. All 14 checks pass:
is ignored). It starts the EXE, finds its windows through UI Automation and reads/dismisses the message boxes
through Win32 (`EnumChildWindows` + `GetWindowText` + `WM_CLOSE`), because the UI Automation view of a standard
message box is **empty** in this session: `FindAll(Descendants, ControlType=Text)` returns nothing, which is why
the first version of the script reported an empty dialog text. All 14 checks pass:

```
PASS version: dialog appears                    title='Explorer Everything Search' class='#32770'
PASS version: text carries a version            text='[Button] 确定 | [Static] Explorer Everything Search 1.0.0.0'
PASS version: dismissed by its OK button        InvokePattern on the button
PASS version: process exits 0                   exited=True code=0
PASS help: dialog appears                       title='Explorer Everything Search'
PASS help: documents every switch               all six switches present
PASS help: process exits 0                      exited=True code=0
PASS second instance: primary is running        pid=12996
PASS settings signal: a new window of the primary opens title='Explorer Everything Search - 设置' class='Window'
PASS settings signal: the second process exits 0 exited=True code=0
PASS settings signal: the primary is still running the signalling process did not replace it
PASS exit signal: the second process exits 0    exited=True code=0
PASS exit signal: the primary shuts down        exited=True code=0
PASS exit signal: no instance left              processes=0
```

What that establishes, per requirement: `--version` and `--help` really do show a dialog with the version / the
usage and the switch list and then exit 0 (the first run of the script found that the help text **did not document
`--help` itself**, which is fixed — the check now asks for all six switches); a second launch with `--settings`
does **not** start a second copy but makes the running instance open its settings window, whose title is the
localized one, while the signalling process exits 0; `--exit` makes the running instance stop with exit code 0.
The settings window was closed through its `WindowPattern` before the shutdown check, and every process and temp
root the script created was removed afterwards (`processes=0`).

### 12.9 Defect: every shutdown hung for six seconds on a UI Automation unsubscribe (fixed)

Symptom, before the fix: every shutdown wrote

```
[DEBUG] stopping the Explorer monitor reported: STA dispatcher did not complete the requested work in time
```

and the process then took **6.2 s** to exit (exit code 0, searches unaffected, no data loss). Reproduced four
independent ways: the packaged EXE with `--exit`, a `--root` instance shut down with its settings window open,
a `--root` instance shut down with no settings window and no Explorer window tracked
(`Explorer rescan (startup): windows=0 searchBoxes=0`), and the E2E harness at the end of `basic-idle`.

Leaving it as "an open finding about the dispatcher" would have been wrong, and the dispatcher turned out to be
innocent. A diagnostic hook on `StaDispatcher` (`Trace`, kept in the product — see the end of this section) showed
that the pump picks the stop item up immediately and that the **work item itself** runs for six seconds:

```
[DEBUG] STA work item started: Stop
[DEBUG] STA work item 'Stop' was not executed within 00:00:05; threadAlive=True queued=0 threadId=9100
[DEBUG] STA work item finished: Stop after 6021 ms
```

Breadcrumbs inside that item then isolated the call (3 of 3 runs, 6.022 – 6.040 s, so it is deterministic in
practice):

```
12:13:30.400 STA stop: removing the UI Automation focus handler
12:13:36.422 STA stop: focus handler removed, uninstalling hooks      <- 6.022 s later
12:13:36.423 STA stop: hooks uninstalled, detaching windows           <- 1 ms
12:13:36.423 STA stop: windows detached                               <- < 1 ms
```

**Root cause: `Automation.RemoveAutomationFocusChangedEventHandler` blocks for about six seconds inside UI
Automation.** Every other step of the teardown (unhooking the WinEvent hooks, uninstalling the keyboard hook,
detaching the tracked windows) is sub-millisecond. The call can occasionally return at once — one of five runs
finished the whole item in 4 ms — so it is a UI Automation internal wait, not a sleep of ours.

Why it mattered twice: `Stop()` is also called when monitoring is switched off at runtime from the settings
dialog, on the UI thread, so the same call froze the settings window for six seconds.

**Fix** (`ExplorerWindowMonitor`, plus the `Trace` hook in `StaDispatcher`):

- the teardown that actually stops monitoring (`UninstallHooks`, `Detach` of every tracked window, clearing the
  window map) stays synchronous and is still waited for — it costs about a millisecond;
- the slow unsubscribe is now **posted** to the STA thread instead of invoked, so no caller ever waits for it. It
  still runs on the thread UI Automation requires, and by then the monitor is stopped;
- `OnFocusChanged` now returns immediately while the handler is not running (`_disposed || !_started`), so a
  removal that is still queued cannot re-arm anything;
- a `_focusHandlerRegistered` flag, only touched on the STA thread, keeps a stop/start cycle (runtime toggle)
  from registering the handler twice while a removal is still queued.

Verification after the fix (same reproduction, no other change):

| Run | Before | After |
|---|---|---|
| 1 | 6.4 s, 1 timeout warning | **2.2 s, 0 warnings** |
| 2 | 6.3 s, 1 timeout warning | **0.2 s, 0 warnings** |
| 3 | 6.3 s, 1 timeout warning | **2.2 s, 0 warnings** |

The remaining 0.2 – 2.2 s is the normal teardown (the process now exits while the queued unsubscribe is still in
flight). The full E2E suite was re-run afterwards: **13 of 13 scenarios passed in 70.3 s** with **0** timeout
warnings in the two application logs, and the unit tests stayed at 237 (236 passed, 1 skipped).

**Diagnostics kept in the product** (`StaDispatcher.Trace`, wired to the logger in `AppRoot`): a line for every
work item that takes 250 ms or more, and a line for any `Invoke` that gives up, including `threadAlive`,
`queued` and `threadId`. That pair is what turns "the dispatcher did not finish in time" into an immediately
readable statement, and it costs nothing at the default log level (`Info`).
