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

At **02:49:45** that file no longer existed: the `ees-smoke-root\logs\` directory remained but was **empty**, and `ees-smoke-root\config.json` had been rewritten (mtime 02:18:37 → 02:48:32). The deletion was not performed by this document's author and no build was run. Two readings are therefore possible and neither could be confirmed from here: a parallel verification of the "clear logs" feature (which deletes `*.log` while the tool runs) or an external cleanup of the temp folder. The excerpts are reproduced verbatim from the 02:45 reading; **re-running the scenarios below is the only way to re-verify them.** Consequently the log excerpts in this document are **STALE** as artifacts, even though they were accurate when read.

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
| 3 | **The `...\smokedata` vs `...\smokedata\sub` two-window pair** | this session's log used other folders. Fix: re-run the two-window scenario and check both `ResolvedPath` lines. |
| 4 | **Log rotation** (`maxLogFileSizeMb` / `maxLogFiles`, `app.1.log` … `app.N.log`) | no rotated file has ever been produced in the captured runs. Fix: set `maxLogFileSizeMb: 1`, `logLevel: Debug`, exercise searches until rotation, inspect `logs\`. |
| 5 | **Clearing logs while running** (tray "Clear logs" / settings button) | `AppLogger.ClearLogs` (close → delete → reopen) was never run by this document's author. **Indirect evidence only:** between the 02:45 reading and 02:49:45 the `logs\` directory of the `--root` tree became empty and `config.json` was rewritten, which is consistent with a clear-logs run by a parallel process — attribution to that feature is an **INFERENCE**, not a measurement. Fix: click it with logging on, confirm `app.log` disappears (handle closed, so the deletion succeeds) and reappears with new lines. |
| 6 | **"Open log folder"** from tray and settings | code calls `Process.Start` on the folder; not exercised. |
| 7 | **Tray menu end to end** (all items, status texts, balloons, double-click) | only the icon creation (`notification area icon created`) appears in the log. |
| 8 | **Start with Windows**: writing the HKCU `Run` value, detecting a stale entry, automatic repair, and the settings "Repair start-up entry" button | the captured run had `startWithWindows: false`, so the code path never ran. Fix: enable it, check `HKCU\...\Run\ExplorerEverythingSearch`, then move the EXE and verify the repair log line. |
| 9 | **`--startup`, `--settings`, `--exit`, `--help`, `--version`** | the captured runs used `--root` only. Fix: run each switch and observe the documented effect. |
| 10 | **Second launch with `--settings` reaching the running instance** (mutex + named event) | not exercised. Fix: start the app, then `ExplorerEverythingSearch.exe --settings` and check that the first instance opens the dialog. |
| 11 | **Two independent instances via two different `--root` values** | reasoned from `SingleInstanceGuard`'s hashed suffix; not run. |
| 12 | **Idle CPU / no-wakeup claim** | reasoned from `MsgWaitForMultipleObjectsEx` + `Timeout.Infinite` timers; no measurement (e.g. Process Explorer CPU time over an idle hour). |
| 13 | **The 60 s self-healing rescan actually repairing a missed window event** | no `Explorer rescan (periodic)` line appears in the captured log (the run was shorter than 60 s of relevant activity or the rescan happened without a Repaint). Fix: raise `logLevel` to `Debug`, close an Explorer window without destroying it (e.g. via a missed hook), and wait for the periodic rescan line. |
| 14 | **Home (主文件夹) scope = union of known folders** | no `SearchScope=KnownFoldersUnion` line in the captured log. Fix: search from the Home view and check the log + the resulting `ancestor:` query. |
| 15 | **A known folder redirected to a non-system drive / UNC path** | implemented via `SHGetKnownFolderPath`; not tested with an actual redirection. |
| 16 | **Everything not installed / not running / database not loaded** notification paths | the test machine always had Everything running with a loaded database. Fix: stop Everything (and/or set a bogus `everythingPath`) and search. |
| 17 | **Security software blocking `SetWindowsHookEx`** → fallback to focus/commit heuristics | the hook installed successfully here. Fix: set `detectEnterByKeyboardHook: false` and confirm Enter still works through the fallbacks (that is also the workaround documented in troubleshooting). |
| 18 | **Everything window title not yet reflecting the query** (`Everything window title does not reflect the query yet`) | warning path never triggered in the captured run. |
| 19 | **A search result view whose origin folder is remembered successfully after leaving and re-entering** | only the carried-over-scope case (§4) was observed. |
| 20 | **High DPI / multi-monitor / per-monitor DPI changes** | manifest declares `PerMonitorV2`; behaviour was not observed on a multi-monitor or mixed-DPI setup. |
| 21 | **Running without administrator rights on a locked-down machine** | manifest is `asInvoker` and the test run was unelevated, but no ACL-restricted scenario (e.g. a read-only install directory, which triggers the "configuration is read-only" notification) was produced. |
| 22 | **The unit tests and E2E tests** | `tests\ExplorerEverythingSearch.Tests` **now exists** (xUnit, `AppConfigTests` + `ConfigStoreTests` + `TestSupport` fakes) but **has never been executed** — no test run result exists, so nothing is proven yet. `tests\ExplorerEverythingSearch.E2E` still **does not exist**. Fix: run `dotnet test tests\ExplorerEverythingSearch.Tests\ExplorerEverythingSearch.Tests.csproj -c Release` and record the result. |
| 23 | **`tools\package.ps1`** | the file **now exists** (PowerShell 7+, builds the portable and framework-dependent zips into `artifacts\`, runs the unit tests unless `-SkipTests`, copies README/LICENSE into both packages). It has **never been executed**. Fix: run `pwsh tools/package.ps1 -SkipTests` and inspect `artifacts\*.zip`. |
| 23b | **The GitHub workflows** `.github\workflows\build.yml` and `release.yml` | **never executed**; the referenced unit test project only just appeared, so the first CI run will be the real test. |
| 24 | **The `E_NOINTERFACE` result for `IShellBrowser`/`IFolderView`** | asserted in the `ExplorerLocationResolver` class comment from earlier probing; not re-measured while writing this document. |
| 25 | **`EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8` being accepted but ineffective** | asserted in the `EverythingIpc` class comment from earlier probing; not re-measured while writing this document. |
| 26 | **`EverythingIpc.IsDatabaseLoaded` / `IsDriveIndexed` being used on any application path** | they respond to IPC (verified), but the application never queries them — they exist for diagnostics only. |
| 27 | **Behaviour when the Explorer search box is exposed as a plain UIA `Edit`** (older Windows builds / a different shell layout) | only the Windows 11 build 26200 shape (`AutoSuggestBox` host) was tested. On Windows 10 the `FileExplorerSearchBox` AutomationId fallback may or may not match — **UNKNOWN**. |
| 28 | **The `--root` instance isolation during a concurrent "real" run** | the captured run did use `--root`, but no real instance was running at the same time. |
| 29 | **Persistence of the captioned `app.log` evidence** | the file was read at 02:45 (8 363 bytes / 82 lines) and was gone by 02:49:45, with `[root]\logs\` left empty. The deletion's cause is **UNKNOWN** (see the note at the top of this document); every log excerpt here is therefore a **STALE** artifact and needs a re-run to be re-verified. |
