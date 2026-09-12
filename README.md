# Explorer Everything Search

[English](README.md) | [简体中文](README.zh-CN.md)

**Type in the Windows Explorer search box — the results open in Everything, scoped to exactly the folder that Explorer window is showing.**

Explorer's own search box is slow and its scope is easy to misread. This tool watches the Explorer search box of every open window and forwards the search to [Everything](https://www.voidtools.com/) instead, restricted to the folder *that window* is in (plus its subfolders). The results appear in an Everything window that is raised in front of Explorer **without stealing the keyboard focus**, so you can keep typing in the Explorer search box and refine the query.

> Status: research prototype, verified by hand on one Windows 11 machine (see [docs/verification.md](docs/verification.md) for exactly what was measured and what was not). A unit test project now exists (`tests\ExplorerEverythingSearch.Tests`), but it has not been run yet and the end-to-end test project is still missing.

## Core path

```
Explorer search box
      │  (UI Automation: ValuePattern / TextPattern per keystroke)
      ▼
Search context extraction
      │  (Shell.Application IDispatch: the folder the window is really showing;
      │   remembered per window, because Explorer replaces it with a synthetic
      │   search-view location once a search is committed)
      ▼
Everything query bridge
      │  (official command line: -no-new-window -path <dir> -s* <text>,
      │   or  <ancestor:"a"|ancestor:"b">  for a multi-folder scope)
      ▼
Everything window  →  results scoped to that folder, window raised without focus theft
```

## Features

- **Enter commits immediately.** A low-level keyboard hook (`WH_KEYBOARD_LL`) is installed *only while an Explorer search box has the keyboard focus*, so pressing Enter submits right away. The full path (keypress → Everything window showing the query) was reported earlier as ≈100–200 ms but was **not re-measured in the same run** (evidenced separately: 10–20 ms to the Explorer result view, 154 ms Everything end-to-end). Two fallbacks cover the case where the hook is blocked.
- **Idle auto-search.** Stop typing for `autoSearchDelay` ms (default 1000 ms, measured 1005–1015 ms in the log) and the search is submitted automatically.
- **Exact scope, never guessed.** The scope comes from the Shell location of the *triggering* window, resolved live on every submit. The folder is also snapshotted when the window is attached, when it is navigated, and on the first keystroke of a query — because Explorer replaces the window location with a synthetic search view as soon as a search is committed.
- **Per-window isolation.** Two Explorer windows in different folders produce two independent searches with independent scopes, even when they are typed into at the same time.
- **Window reuse.** `-no-new-window` reuses the existing Everything search window instead of piling up windows (`reuseEverythingWindow`).
- **Focus never moves.** The Everything window is raised with `SetWindowPos` + `SWP_NOACTIVATE`; if Everything did take the foreground, the keyboard focus is handed back to the Explorer search box — but only if you have not produced input of your own in the meantime.
- **Event driven, near-zero idle cost.** Window discovery uses WinEvent hooks, the search box uses UI Automation events, and the STA message pump blocks in `MsgWaitForMultipleObjectsEx`. While nothing happens the process does not wake up. One low-frequency self-healing rescan (`explorerRescanSeconds`, default 60 s, `0` = off) repairs a missed window event.
- **Portable and silent.** No installer, no `%AppData%` writes, no admin rights (`asInvoker`). `config.json` and `logs\` live next to the executable.
- **Honest failure reporting.** A location that cannot be mapped to a file system scope produces a notification ("this search was not redirected to Everything") instead of a silently wrong search.
- **Tray UI, settings dialog, bilingual UI** (English / Simplified Chinese, `auto` follows the Windows UI language).

## System requirements

| | Requirement |
|---|---|
| OS | Windows 10 version 1809 (build 17763) or newer, or Windows 11. Verified on Windows 11 build 26200 only. |
| Runtime | .NET 8 desktop runtime — **only** for the framework-dependent package. The single-file self-contained package needs no runtime. |
| Everything | [Everything](https://www.voidtools.com/) 1.4 or 1.5 by voidtools. Everything 1.5 is recommended (`-s*` and the `ancestor:` function are used). Verified against 1.5.0.1423b x64 at `C:\Program Files\Everything`. |
| es.exe | Optional. Only used for diagnostics and cross-checking scope in manual verification; not needed to run. |
| Privileges | None. Runs as the current user. |

## Install and run

**Single-file, self-contained (recommended, no runtime needed):**

```powershell
dotnet publish src\ExplorerEverythingSearch.App\ExplorerEverythingSearch.App.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Copy `ExplorerEverythingSearch.exe` anywhere you like (for example `C:\Tools\ExplorerEverythingSearch\`) and run it. On first run it creates `config.json` and `logs\` **in the folder the EXE is in**.

**Framework-dependent (requires the .NET 8 desktop runtime):**

```powershell
dotnet publish src\ExplorerEverythingSearch.App\ExplorerEverythingSearch.App.csproj -c Release
```

`tools\package.ps1` (PowerShell 7+) builds both release archives into `artifacts\`:

```powershell
pwsh tools/package.ps1                       # builds, runs unit tests, packages both
pwsh tools/package.ps1 -Version 1.0.0 -SkipTests
```

- `ExplorerEverythingSearch-<version>-win-x64-portable.zip` — self-contained single EXE, no runtime needed
- `ExplorerEverythingSearch-<version>-win-x64-framework-dependent.zip` — small; needs the .NET 8 Desktop Runtime (x64)

Parameters: `-Configuration` (default `Release`), `-Version` (default from `Directory.Build.props`), `-OutputDirectory` (default `<repo>\artifacts`), `-SkipTests`. Both archives include the application, `README.md`, `README.zh-CN.md` and `LICENSE`; `config.json` is created next to the EXE on first start. If the unit test project is missing, packaging continues with a warning instead of failing. **The script has not been executed — UNVERIFIED** (see [docs/verification.md](docs/verification.md#11-not-verified--unverified)).

### Command line

| Argument | Effect |
|---|---|
| `--startup` | Start minimised in the notification area. This is what the HKCU `Run` entry uses. |
| `--settings` | Open the settings window. If an instance is already running, the running instance opens its settings window and this process exits. |
| `--exit` | Ask a running instance to exit, then exit. |
| `--root <directory>` | Store `config.json` and `logs\` in `<directory>` instead of next to the EXE. Also makes this an **independent instance** (its own mutex/signals), so a test run never disturbs the real instance. `--root=<directory>` is accepted too. |
| `--help`, `-h`, `/?` | Show usage in a message box. |
| `--version` | Show the version in a message box. |

Unknown arguments are ignored. Everything the tool does is silent (tray only); `--help`/`--version` are the only console-less interactions.

### First run

1. Start the EXE. A tray icon appears (no main window).
2. Open two Explorer windows in different folders, click into the search box of one of them, and type a file name fragment.
3. Press Enter, or just stop typing for a second. The Everything window comes to the front with results scoped to that folder.
4. Continue typing in the Explorer search box to refine — the focus stays there.

## Configuration (`config.json`)

The file is created with defaults on first run and is meant to be hand-editable. Every value is clamped on load, so a mistyped number can never break the runtime.

| Field | Default | Meaning | Values / range |
|---|---|---|---|
| `enabled` | `true` | Master switch for Explorer search monitoring. | `true` / `false` |
| `autoSearchDelay` | `1000` | Idle auto-search delay: submit after this many ms without a keystroke. | 100–60000 ms |
| `everythingPath` | `""` | Explicit path to `Everything.exe`. Empty = auto-detect (configured → running instance → uninstall registry → usual install locations → `PATH`). | path or `""` |
| `esPath` | `""` | Explicit path to `es.exe` (optional; diagnostics only). Empty = auto-detect. | path or `""` |
| `startWithWindows` | `true` | Register/refresh the HKCU `...\Run` entry. | `true` / `false` |
| `showNotifications` | `false` | Show informational tray balloons (logs cleared, read-only config, startup entry repaired). Errors are shown regardless. | `true` / `false` |
| `logLevel` | `"Information"` | Minimum severity written to the log file. | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `None` |
| `loggingEnabled` | `true` | Write log files at all. When `false`, no file is produced or written. | `true` / `false` |
| `reuseEverythingWindow` | `true` | Reuse the existing Everything search window (`-no-new-window`) instead of creating a new one per search (`-new-window`). | `true` / `false` |
| `detectEnterByKeyboardHook` | `true` | **Primary** Enter detection: low-level keyboard hook, installed only while an Explorer search box is focused. Not exposed in the settings window. | `true` / `false` |
| `detectEnterByFocusChange` | `true` | **Secondary**: the focus leaving the search box into the result area of the same window counts as Enter. | `true` / `false` |
| `detectEnterByCommitTiming` | `true` | **Fallback**: an Explorer commit within `enterCommitWindowMs` of the last keystroke, whose title carries the search box text, counts as Enter. | `true` / `false` |
| `enterCommitWindowMs` | `400` | Upper bound for the commit-timing heuristic. Explorer's own auto-commit happens ~800 ms after the last keystroke, hence 400 ms. | 50–5000 ms |
| `explorerRescanSeconds` | `60` | Low-frequency self-healing rescan of Explorer windows. `0` disables it (purely event driven). | 0–3600 s |
| `language` | `"auto"` | UI language. `auto` follows the Windows UI language. | `auto`, `en-US`, `zh-CN` |
| `maxLogFileSizeMb` | `5` | Rotate `app.log` once it reaches this size. | 1–1024 MB |
| `maxLogFiles` | `5` | Number of rotated files to keep (`app.1.log` … `app.N.log`). | 1–100 |

Example:

```json
{
  "enabled": true,
  "autoSearchDelay": 1000,
  "everythingPath": "",
  "esPath": "",
  "startWithWindows": false,
  "showNotifications": true,
  "logLevel": "Information",
  "loggingEnabled": true,
  "reuseEverythingWindow": true,
  "detectEnterByKeyboardHook": true,
  "detectEnterByFocusChange": true,
  "detectEnterByCommitTiming": true,
  "enterCommitWindowMs": 400,
  "explorerRescanSeconds": 60,
  "language": "auto",
  "maxLogFileSizeMb": 5,
  "maxLogFiles": 5
}
```

Saving is atomic (temp file + replace). If `config.json` cannot be read it is replaced by defaults in memory and an error is logged; if it cannot be written, a notification says the settings for this session are kept in memory only.

## Logging

- Location: `<root>\logs\app.log`, where `<root>` is the EXE folder, or the `--root` directory.
- Format: one line per entry, `[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] message`, UTF-8 **without** BOM, share mode `ReadWrite | Delete` (the file can be read while the tool runs).
- Level tags: `TRACE`, `DEBUG`, `INFO`, `WARN`, `ERROR`.
- Written asynchronously by a dedicated `EES-LogWriter` thread that batches and flushes; logging never blocks the Explorer/UIA thread and never throws into the application.
- Rotation: when `app.log` reaches `maxLogFileSizeMb`, `app.1.log` is shifted to `app.2.log`, …, and the oldest (`app.<maxLogFiles-1>.log`) is deleted; the current file becomes `app.1.log`.
- `loggingEnabled: false` stops writing entirely (the file handle is closed, so nothing keeps the file open). Setting `logLevel` to `None` has the same effect. The logger is created **without** a log file and only starts writing once the configuration has been loaded, so a configuration that disables logging never creates `logs\` or `app.log` at all.
- Tray menu **Open log folder** opens `logs\` in Explorer; **Clear logs** flushes, closes, deletes all `*.log` in that folder, and lets the writer reopen lazily. Clearing while running is supported by design — **UNVERIFIED by hand** (see [docs/verification.md](docs/verification.md)).

A real excerpt (level `Debug`):

```text
[2026-09-13 02:44:44.222] [DEBUG] Enter detection: keyboard hook installed (an Explorer search box has the focus)
[2026-09-13 02:44:45.147] [DEBUG] SearchBox text changed: "thistpc"
[2026-09-13 02:44:45.978] [DEBUG] Explorer committed a search after 830 ms ("thistpc - 文件资源管理器") - treated as its own auto search
[2026-09-13 02:44:46.162] [INFO] Search submitted trigger=IdleTimeout
[2026-09-13 02:44:46.162] [INFO] SourceExplorerHwnd=1709020
[2026-09-13 02:44:46.162] [INFO] SearchText="thistpc"
[2026-09-13 02:44:46.175] [WARN] search not redirected: SearchScopeUnknown detail="Explorer is showing a search result view and the folder it was started from is unknown" raw="“此电脑”中的搜索结果&thistpc"
```

and the corresponding successful case:

```text
[2026-09-13 02:44:51.152] [INFO] Search submitted trigger=IdleTimeout
[2026-09-13 02:44:51.170] [INFO] ResolvedPath="C:\Users\ControlxSaria\AppData\Local\Temp\ees-smoke\数据目录 空格"
[2026-09-13 02:44:51.171] [INFO] SearchScope=CurrentDirectoryAndSubdirectories (carried over from the window's folder)
[2026-09-13 02:44:51.172] [INFO] Query="alpha"
[2026-09-13 02:44:51.327] [DEBUG] returning the keyboard focus to the Explorer search box (hwnd=1904286)
[2026-09-13 02:44:51.327] [INFO] Everything window reused
[2026-09-13 02:44:51.327] [INFO] Search completed in 154 ms
```

## Tray menu

Left double-click opens the settings window.

| Item | Behaviour |
|---|---|
| `Explorer Search: Enabled / Disabled` | Status only (disabled item). |
| `Everything: Connected (x.y.z.b) / Unavailable` | Status only; the version is queried through Everything's IPC on each menu refresh. |
| `Pause monitoring` / `Enable monitoring` | Toggles `enabled` and persists it. |
| `Settings...` | Opens the settings window (monitoring, delay, Enter detection options, Everything paths, startup, notifications, logging, log level, log size, language; buttons for test/reconnect, open logs, clear logs, repair startup entry). |
| `Reconnect Everything` | Drops the cached Everything detection, probes again, and shows a balloon with the result. |
| `Open log folder` | Opens `<root>\logs`. |
| `Clear logs` | Deletes the log files while running (see Logging). |
| `Logging: on` / `Logging: off` | Toggles `loggingEnabled` and persists it. |
| `Exit` | Shuts the tool down. |

## Start with Windows

- The entry is `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value name `ExplorerEverythingSearch`, value `"<path to EXE>" --startup`. No administrator rights are involved.
- On startup **and** on every configuration save the expected value is compared with the stored one. If the EXE was moved, the stored path no longer matches: the entry is rewritten automatically and, if it had been stale, a notification reports the repair.
- The settings window also shows a warning plus a **Repair start-up entry** button when the entry is stale (exists but points to a path that no longer exists). Both the automatic repair log line and the manual button are **UNVERIFIED by hand**.

## Multi-window, language, and known folders

- **Multi-window**: every Explorer window (`CabinetWClass`) gets its own search box watcher and search session. Searches are serialised in the bridge so two windows cannot interleave their Everything updates, but each keeps its own scope and its own duplicate-suppression state.
- **Language**: `language` is `auto`, `en-US`, or `zh-CN`; `auto` picks Chinese when the Windows UI language is Chinese, English otherwise. Both string sets are built into the binary. Changing the language takes effect immediately after saving.
- **Known folders**: for the Windows 11 **Home** view the scope is the union of the user's known folders (Desktop, Documents, Downloads, Pictures, Videos, Music), resolved through `SHGetKnownFolderPath`. Because the paths come from the Shell, a **Download/Documents/Pictures/... folder redirected to a non-system drive (or to a UNC path) resolves to its real location** and is searched there. Folders that do not exist are skipped; if none resolve, the search is *not* redirected and a notification explains why.

## Scope semantics of "This PC" / "Home" / search result views

| Explorer shows | Everything scope | How it is expressed |
|---|---|---|
| A normal folder (including a redirected/relocated known folder) | That folder and all subfolders | `-no-new-window -path "<folder>" -s* <text>` |
| A search result view (`"<folder>"中的搜索结果&<query>`) | The folder the search was started from — remembered per window from the last live Shell resolution | same as above, logged with `(carried over from the window's folder)` |
| **Home** (Windows 11) | Union of the known folders listed above | `<ancestor:"a"\|ancestor:"b"\|...>` appended to the query |
| **This PC** | Every volume (no path restriction) | query only, no `-path`/`ancestor:` |
| A search result view started from **This PC** | Every volume — "This PC" is remembered per window, so a search started there keeps its meaning even after Explorer turns the window into a result view | query only |
| A search result view started from **Home** | Union of the known folders | `<ancestor:…>` union |
| Recycle Bin, Network, Control Panel, other virtual folders | **Not redirected** — an explicit notification is shown | — |
| Search result view whose origin location was never observed | **Not redirected** — notification: "the folder the search was started from is unknown" | — |

Important detail about scope and matching: Everything's `-path <dir>` matches folders, **not** a path substring, so `-path "D:\a\inside"` does not also match `D:\a\inside2`. In contrast, putting the folder path *inside the search text* is matched as a path substring and does match both — which is exactly why the scope is expressed with `-path` / `ancestor:` and never concatenated into the query text.

## Architecture

The layering, thread model, event-driven monitoring, the three Enter-detection mechanisms, the Shell-based scope resolution (and its per-window fallback) and the Everything integration are documented in **[docs/architecture.md](docs/architecture.md)**.

## Build

```powershell
dotnet build ExplorerEverythingSearch.sln -c Release
```

- Solution: `ExplorerEverythingSearch.sln` — `src\ExplorerEverythingSearch.Core` (net8.0-windows, no UI; WPF is referenced only for the managed UI Automation client) and `src\ExplorerEverythingSearch.App` (WinExe, WPF + WinForms for the tray icon; assembly name `ExplorerEverythingSearch`).
- Version/authors come from `Directory.Build.props` (`1.0.0`, "Explorer Everything Search contributors").
- With a `RuntimeIdentifier`, the App project publishes self-contained, single-file, compressed, **untrimmed** (WPF cannot be trimmed) and without a PDB.
- CI: `.github\workflows\build.yml` restores, builds in `Release` on `windows-latest`, runs the unit tests and uploads `test-results.trx`. `.github\workflows\release.yml` (on a `v*` tag or manually) resolves the version, runs `tools/package.ps1`, uploads both archives and creates/updates the GitHub release. **Neither workflow has been executed — UNVERIFIED.**

## Tests

- Unit tests: `tests\ExplorerEverythingSearch.Tests` (xUnit 2.9, `Microsoft.NET.Test.Sdk` 17.11, net8.0-windows, `InternalsVisibleTo` from Core). It currently covers `AppConfig.Normalize`/serialisation and `ConfigStore` (defaults, corruption, atomic save, unwritable root) via `AppConfigTests` and `ConfigStoreTests`, with fake `IExplorerLocationResolver`/`IStartupRegistry` helpers in `TestSupport`.

```powershell
dotnet test tests\ExplorerEverythingSearch.Tests\ExplorerEverythingSearch.Tests.csproj -c Release
```

  **This suite has not been executed — UNVERIFIED** (the repository's own build was still in progress while this document was written).
- End-to-end tests: `tests\ExplorerEverythingSearch.E2E` is referenced by `InternalsVisibleTo` and CI comments but **does not exist yet — UNVERIFIED**; `build.yml` deliberately excludes E2E because it drives real Explorer and Everything windows.
- The manual verification protocol that was actually executed (with timings and log excerpts) is in [docs/verification.md](docs/verification.md). The developer probe used for the low-level measurements is `tools\probes\ExplorerProbe` (`resolve` dumps Shell locations, `events` drives a real search box and logs every UI Automation signal).

## Troubleshooting

See [docs/troubleshooting.md](docs/troubleshooting.md) for symptom → cause → fix entries covering Everything not installed/running, empty or wrongly scoped results, the "search scope unknown" notification, security software blocking the keyboard hook, focus behaviour, empty logs, a broken start-with-Windows entry, everything closing, multiple instances, high DPI/multi-monitor, and privileges.

## License

MIT — see [LICENSE](LICENSE). Copyright (c) 2026 Explorer Everything Search contributors.

## Acknowledgements

- [Everything](https://www.voidtools.com/) by **voidtools** — the search engine this tool drives. This project is not affiliated with voidtools.
- The Everything IPC/SDK documentation (`everything_ipc.h`) is the reference for the version query used here.

## Disclaimer

This software is provided "as is", without warranty of any kind. It installs a low-level keyboard hook that is active *only* while an Explorer search box has the keyboard focus, it reads Explorer window titles and Shell locations, and it writes a `Run` entry for the current user. It reads no file contents and sends nothing over the network. Review the source before running it, and use it at your own risk.
