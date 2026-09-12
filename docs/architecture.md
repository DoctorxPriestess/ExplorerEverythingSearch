# Architecture and key mechanisms

[English](architecture.md) | [简体中文](architecture.zh-CN.md) · [Back to README](../README.md)

This document explains how Explorer Everything Search works internally: the layering, the thread model, the event-driven monitoring design, the three Enter-detection mechanisms, the idle auto-commit, how the search context is extracted from Explorer, how the Everything integration is performed, and how logging, configuration and errors are handled.

Every claim below is grounded in the source files named next to it. Statements that were measured on a real machine are marked **measured**; statements that follow from the code but were not exercised by hand are marked **not hand-verified**.

## 1. Layering

```
ExplorerEverythingSearch.sln
├── src\ExplorerEverythingSearch.Core      (net8.0-windows, no UI, no entry point)
│   ├── Configuration\  AppConfig, ConfigStore
│   ├── Diagnostics\    AppLogger, LogLevel/LogLevels
│   ├── Everything\     EverythingLocator, EverythingIpc, EverythingBridge, EsCliClient
│   ├── Explorer\       ExplorerWindowMonitor, SearchBoxWatcher, ExplorerSearchSession
│   ├── Interop\        NativeMethods (all Win32 P/Invoke in one place)
│   ├── Search\         SearchModel, SearchDebouncer, SearchCoordinator, EverythingQueryBuilder
│   ├── Shell\          ExplorerLocationResolver, ShellLocationClassifier, SearchScopeResolver,
│   │                   WindowScopeTracker, KnownFolderResolver
│   ├── Startup\        StartupRegistration (+ IStartupRegistry / HkcuRunRegistry)
│   └── Threading\      StaDispatcher
├── src\ExplorerEverythingSearch.App       (WinExe; assembly name ExplorerEverythingSearch)
│   ├── App.xaml.cs     entry point, command line, single-instance decision
│   ├── AppRoot.cs      composition root / application controller
│   ├── AppPaths.cs     root resolution (EXE directory or --root)
│   ├── SingleInstanceGuard.cs
│   ├── CommandLineOptions.cs
│   ├── Tray\           TrayIconController, TrayIconFactory
│   ├── Views\          SettingsWindow (WPF, plain code-behind)
│   ├── Notifications\  NotificationService
│   └── Localization\   Strings (en-US + zh-CN, built in)
├── tests\ExplorerEverythingSearch.Tests   unit tests (xUnit); E2E project still missing
└── tools\probes\ExplorerProbe             developer/diagnostic probe, not shipped
```

- `Core` references WPF **only** for `System.Windows.Automation`; it has no UI and no entry point (`ExplorerEverythingSearch.Core.csproj`).
- `Core` exposes internals to the test assemblies: `InternalsVisibleTo("ExplorerEverythingSearch.Tests")` and `...("ExplorerEverythingSearch.E2E")`. The unit test project exists (`tests\ExplorerEverythingSearch.Tests`, xUnit); the E2E project **does not exist yet**.
- `App` is a tray-only WPF application that also uses WinForms for `NotifyIcon`; the implicit `System.Windows.Forms` using is removed so that `Application`/`MessageBox` keep resolving to WPF.
- Composition happens in exactly one place, `AppRoot` (`AppRoot.cs`): config store → logger → STA dispatcher → location resolver → scope resolver → Everything locator/bridge → coordinator → notification service → monitor → startup registration → tray.

## 2. Thread model

| Thread | Created in | Work | Idle behaviour |
|---|---|---|---|
| WPF UI thread | `App` | tray icon, settings window, balloons/message boxes, application lifetime | normal WPF message loop |
| `EES-STA` | `StaDispatcher` | UI Automation calls and events, Shell (`Shell.Application`) automation objects, WinEvent callbacks, the low-level keyboard hook callback | **blocks in `MsgWaitForMultipleObjectsEx`** until work is queued *or* a window message arrives |
| `EES-SearchWorker` | `SearchCoordinator` | consumes submitted searches, resolves the scope on the STA thread, builds and executes the Everything query | blocks on `BlockingCollection.GetConsumingEnumerable()` |
| `EES-LogWriter` | `AppLogger` | batches queued log lines and flushes them | blocks on a `SemaphoreSlim` |
| `EES-SingleInstance` | `SingleInstanceGuard` | waits for the `--settings` / `--exit` signals | blocks on `WaitHandle.WaitAny` |

### Why one dedicated STA thread (`StaDispatcher.cs`)

Both subsystems the tool depends on require it:

- **UI Automation delivers events only to a thread that pumps messages.** `Automation.AddAutomationPropertyChangedEventHandler` / `AddAutomationEventHandler` / `AddAutomationFocusChangedEventHandler` subscribers therefore live on `EES-STA`.
- **The Shell automation object model (`Shell.Application`, `ShellWindows`) is apartment-threaded**: the objects must be created and used on one STA thread.

`StaDispatcher` is strictly event driven — no polling and no busy wait:

```csharp
waitResult = NativeMethods.MsgWaitForMultipleObjectsEx(
    1, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
```

- `WAIT_OBJECT_0` → work was queued: drain the queue (after the loop head already drained it) and drop the surplus semaphore signal.
- `WAIT_OBJECT_0 + 1` or `WAIT_TIMEOUT` → window messages are pending: `PeekMessage`/`TranslateMessage`/`DispatchMessage` until the queue is empty.
- anything else (`WAIT_FAILED`, abandoned) → `Thread.Sleep(1)` so the thread can never spin.

`Invoke<T>` executes work on the thread and waits (default 30 s; the coordinator passes 15 s for scope resolution); `Post` is fire-and-forget, which is what the WinEvent and UI Automation callbacks use so that an Explorer callback is never blocked. `Dispose` posts `WM_QUIT`, releases the semaphore and joins with a 2 s timeout.

**Idle cost:** with no Explorer activity, no queued work and no window messages, all four worker threads are blocked in kernel waits. The timer in the monitor is armed with `Timeout.Infinite` whenever there is nothing due (`ScheduleNextTick`).

## 3. Event-driven monitoring

`ExplorerWindowMonitor` (`Core\Explorer\ExplorerWindowMonitor.cs`) owns one `SearchBoxWatcher` + one `ExplorerSearchSession` per Explorer window.

**Discovery — WinEvent hooks (out-of-context, `WINEVENT_SKIPOWNPROCESS`):**

```csharp
(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE)                 // windows appearing/disappearing
(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE)       // titles changing (search commit signal)
```

The callbacks run on `EES-STA`. `EVENT_OBJECT_CREATE`/`SHOW` with class name `CabinetWClass` adds a window; `DESTROY`/`HIDE` removes it; `NAMECHANGE` for a tracked window feeds the commit heuristic and refreshes the remembered folder when the search box is empty.

**Search box — UI Automation events** (`SearchBoxWatcher.Attach`):
`ValuePattern.ValueProperty` and `TextPattern.TextChangedEvent` on the search box itself (this is the per-keystroke trigger), plus `AutomationElement.NameProperty` on the window (the commit/title signal). Failures to subscribe to the optional text-pattern or title handler are logged at `Debug` and are not fatal.

**Low-frequency self-healing rescan:** a single self-scheduling `System.Threading.Timer` (`Tick`) performs an `EnumWindows`-style sweep (`NativeMethods.FindTopLevelWindows("CabinetWClass")`), re-attaches windows whose search box was not ready, and drops dead handles. Interval `explorerRescanSeconds` (default **60 s**, clamped 0–3600, `0` = never). The same timer doubles as the attach-retry scheduler (exponential back-off 300 ms × 2^attempts, capped at 30 s). While nothing is due the timer is set to `Timeout.Infinite`, so no wakeups happen at all.

**Explorer restarts** are handled implicitly: when the last tracked window disappears `_allClosedSince` is set, and the next discovered window logs `Explorer restarted: monitoring re-established` after the previous `Explorer detected hwnd=...` line.

**Search box discovery** (`SearchBoxWatcher.FindSearchBox`) is deliberately conservative:

1. the element with `AutomationId = FileExplorerSearchBox`, then its descendant `Edit`;
2. otherwise the `Edit` inside any `AutoSuggestBox` whose `AutomationId` is **not** `PART_AutoSuggestBox` (the address bar);
3. never "the first `Edit` in the window" — the details view contains `Edit` controls for column values.

A shape check (`IsSearchBoxElementShape`) verifies that the found element really is the `Edit` of an `AutoSuggestBox` host that is not the address bar, so column editors can never be subscribed to. A subscription that goes stale (element disappeared) is marked and re-attached on the next rescan.

## 4. Enter detection — three mechanisms

Explorer gives no direct "the user pressed Enter" notification, and it performs its own automatic search when the user stops typing. Three independent mechanisms are used, in this priority order (`AppConfig.DetectEnterByKeyboardHook`, `DetectEnterByFocusChange`, `DetectEnterByCommitTiming`).

### 4.1 Primary: low-level keyboard hook, installed only while the search box has focus

`ExplorerWindowMonitor.InstallKeyboardHook` installs `WH_KEYBOARD_LL` **on demand**:

- `Automation.AddAutomationFocusChangedEventHandler` reports the focus moving into the search box (`SearchBoxWatcher.IsSearchBoxElement`) → the hook is installed.
- The focus leaving the search box, or the subscription going away, → `UninstallKeyboardHook`.

The callback only inspects `WM_KEYDOWN`/`WM_SYSKEYDOWN` with `vkCode == VK_RETURN`, reads the remembered focused search-box handle (`_focusedSearchBoxHwnd`) and calls `ExplorerSearchSession.OnEnterDetected`. It is delivered on `EES-STA` and does the minimum possible work (one `Interlocked.Read`, one dictionary lookup); it always returns `CallNextHookEx`, so it never swallows the key from Explorer.

Consequences of this design, all intentional:

- the tool observes **no keyboard input at all** while no Explorer search box is focused,
- Enter works **even when the search finds nothing** and even when the window title never changes — it does not depend on Explorer's result view,
- measured: pressing Enter in a focused search box produced the Explorer search-result view 10–20 ms later, and the submitted text contained the character typed immediately before Enter. The full path to the Everything window (keypress → query visible) was reported as ≈100–200 ms but was **not measured in the same run** — see [verification.md](verification.md#11-not-verified--unverified).

If `SetWindowsHookEx` fails (security software, a policy, an unusual desktop), a warning is logged and the two fallbacks below carry the feature.

### 4.2 Secondary: focus leaves the search box into the result area

`OnFocusChanged` treats "the keyboard focus left the search box and moved to another element **of the same window**" as Enter, with guards:

- a move into Explorer's own search-view input site (`ControlType.Pane` + `ClassName = InputSiteWindowClass`) is explicitly **not** Enter — Explorer moves the focus there by itself while typing (measured as little as ~140 ms after a keystroke) and typing still reaches the search box from there;
- a move to a **different window** is not Enter;
- a focus change while a mouse button is down (`GetAsyncKeyState`) is not Enter;
- a focus change more than `EnterInputFreshnessMs` (**500 ms**) after the last system-wide input (`GetLastInputInfo`) is not Enter, because Explorer's own view updates move the focus out of the box without a keypress.

### 4.3 Fallback: commit timing

`ExplorerSearchSession.OnExplorerCommitDetected` runs for every window-title / UIA-name change and requires **all** of:

1. `detectEnterByCommitTiming` is on;
2. the search box is not empty;
3. the new title carries the search box text as a prefix (a re-shown previous query does not);
4. the change happened within `enterCommitWindowMs` (default **400 ms**) of the last keystroke — Explorer's own automatic commit happens ≈800 ms after the last keystroke, which is why 400 ms separates them;
5. the window really is showing **search results**: the location is re-resolved through the Shell and classified (`ShellLocationKind.SearchResults`). A plain title preview (Explorer shows `query - folder` while the user types) fails this check.

Duplicate reports are suppressed: the same commit detail within 400 ms is ignored, and `OnEnterDetected` ignores a second Enter report within 400 ms. Mechanism 4.1 and 4.2 both see the same key press, so this de-duplication is required.

## 5. Idle auto-commit (`autoSearchDelay`)

Every keystroke reaches `ExplorerSearchSession.OnTextChanged` (via `SearchBoxWatcher.ReportTextChange`) and restarts a one-shot `System.Threading.Timer` in `SearchDebouncer` with `autoSearchDelay` (default **1000 ms**).

- The value is re-read from the configuration on every text change, so a settings change applies to the next keystroke.
- An empty or whitespace-only box cancels the pending timer (`Cancel`).
- Enter (`SubmitNow`) cancels the pending timer and submits immediately.
- `SubmitNow` always submits, even when no timer was pending (pressing Enter twice), and a cancelled timer can never fire for the old text: `OnTimer` clears `_pending` under the same lock before calling back.
- **Measured:** with `autoSearchDelay = 1000`, the interval from the last keystroke to the `Search submitted trigger=IdleTimeout` log line was 997–1005 ms.

Two safety nets around submit time:

- **Live re-read.** `CurrentTextAtSubmit` re-reads the real search box value (ValuePattern, else TextPattern) at submit time. Explorer clears the box itself when a search view is left and the notification may not arrive, so the tracked text is only a fallback; a changed live value is logged (`search box text changed without a notification`).
- **Per-window supersession.** `SearchCoordinator` stamps every submit with a monotonically increasing sequence per window and drops a queued item when a newer one exists for the same window (`search superseded hwnd=... text=...`). Identical repeats (same window, scope and text) are skipped.

## 6. Search context extraction (and why `IShellBrowser`/`IFolderView` cannot be used)

`ShellAutomationLocationResolver` (`Core\Shell\ExplorerLocationResolver.cs`) reads, for one `HWND`:

```
Shell.Application → .Windows() → item with .HWND == hwnd
                  → .LocationName, .LocationURL, .Document.Folder.Self.Path
```

Everything goes through **dual (IDispatch-marshalled)** interfaces, because that is the only kind of interface that can be used *across a process boundary*: `IShellBrowser` / `IFolderView` are raw vtable interfaces of the Explorer process and are **not marshalled** to an external client. Probing them from another process fails with `E_NOINTERFACE` (no proxy/stub is registered). `Document.Folder.Self.Path` is the same value Explorer's own address bar is built from, so it is the real, possibly relocated, file-system path. This is documented in the class comment of `ExplorerLocationResolver.cs` and was established during the earlier probing work; the `E_NOINTERFACE` result itself was **not re-measured while writing this document**.

`ShellLocationClassifier.Classify(selfPath, displayName)` turns the raw parsing name into a `ShellLocationKind`:

| Raw value | Kind |
|---|---|
| `\\server\share\...` (UNC) | `FileSystemPath` |
| `C:` / `C:\...` | `FileSystemPath` |
| `::{20d04fe0-3aea-1069-a2d8-08002b30309d}` (CLSID_MyComputer) | `ThisPc` |
| `::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}` (Windows 11 Home) | `Home` |
| `::{...}` (anything else) | `OtherVirtualFolder` |
| anything else, e.g. `“<folder>”中的搜索结果&<query>` / `Search results in <folder>&<query>` | `SearchResults` (a scope hint is extracted from the typographic/plain/CJK quotes) |
| empty | `Unknown` |

`SearchScopeResolver.Resolve(hwnd)` maps the classified location to a `ResolvedScope`:

- `FileSystemPath` → `CurrentDirectoryAndSubdirectories` with that one path (UNC paths skip the local existence check);
- `ThisPc` → `AllVolumes`;
- `Home` → `KnownFoldersUnion` = the existing known folders from `KnownFolderResolver` (`SHGetKnownFolderPath` for Desktop/Documents/Downloads/Pictures/Videos/Music, deduplicated, non-existent ones skipped — so redirected/relocated folders resolve to their true location);
- `SearchResults` → the **remembered** folder of that window, or `SearchScopeUnknown`;
- `OtherVirtualFolder` → `UnsupportedShellNamespace` (notification, no redirect);
- missing directory → `DirectoryUnavailable`; window gone / no Shell window → `ExplorerWindowNotFound`.

### The search-result-view scope fallback (`WindowScopeBeforeSearch`)

As soon as Explorer commits a search — by Enter, or by its own automatic search ≈800 ms after the last keystroke — it replaces the window's Shell location with a synthetic name such as `“数据目录 空格”中的搜索结果&alpha`; the location the search must be scoped to is then no longer readable. `WindowScopeTracker` therefore remembers the last live location per window as `{ path, displayName, observedAt, origin, kind }` (`kind` defaults to `FileSystemPath`), captured at every moment when the window is still in its live view:

1. right after the search box was found (window attach),
2. on every live Shell resolution that lands on a real file-system path (i.e. on every submit of a live folder),
3. when the search box becomes non-empty (first keystroke of a query, `_onQueryStarted`),
4. on a window-title change while the search box is empty (navigation / leaving a search view).

`TryRecordLiveScope` records **This PC** and **Home** as well (with `path` empty and the matching `kind`), because a search started there must stay a whole-volume or known-folder search; a search-result view itself is skipped so it cannot overwrite the location the search was started from:

```text
[DEBUG] window scope recorded hwnd=1904286 path="C:\...\ees-smoke\数据目录 空格"
[DEBUG] window scope recorded hwnd=1709020 kind=ThisPc
```

A search submitted from a search-result view uses that record and logs it explicitly:

```text
[INFO] ResolvedPath="C:\...\ees-smoke\数据目录 空格"
[INFO] SearchScope=CurrentDirectoryAndSubdirectories (carried over from the window's folder)
```

The class comment states that this means "on the first keystroke of a query". The implementation performs the capture **on the transition from empty to non-empty text** (`startedQuery = _text.Length == 0 && text.Length > 0`), which is the same first keystroke; the log line above shows the carried-over path was correct in the measured run.

A search result view whose remembered location is **This PC** resolves to `AllVolumes`, and one whose remembered location is **Home** resolves to the known-folder union — both with `Source = KnownFolders`/`AllVolumes` and `IsSearchResultView = true`. That is what makes a search started from "This PC" or "Home" keep its meaning after Explorer has already turned the window into a result view. Only a remembered `FileSystemPath` uses `ScopeResolutionSource.WindowScopeBeforeSearch`.

If the window shows a search result view and no location was ever recorded, the search is **not** redirected and the user gets:

```text
[WARN] search not redirected: SearchScopeUnknown detail="Explorer is showing a search result view and the folder it was started from is unknown" raw="“此电脑”中的搜索结果&thistpc"
```

This is a deliberate "refuse rather than guess" policy.

## 7. Everything integration

### 7.1 Locating Everything (`EverythingLocator`)

Resolution order (nothing is hard-coded to a developer machine):

1. configured `everythingPath` (a warning is logged if it does not exist),
2. the executable of a **running** Everything process,
3. `Everything.exe` under `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%ProgramW6432%`, the application directory, `%LocalAppData%`, each `\Everything\`,
4. directories from uninstall registry entries (`DisplayName` containing "Everything", `InstallLocation`, `DisplayIcon`) in HKCU+HKLM, 64- and 32-bit views,
5. `PATH`.

`es.exe` is resolved the same way, preferring the Everything directory. The result is cached until `Invalidate()` (called on settings save and by the tray's "Reconnect Everything").

### 7.2 Query building (`EverythingQueryBuilder`)

| Scope | Command line |
|---|---|
| one directory | `-no-new-window -path "<dir>" -s* <text>` |
| multi-folder union (Home) | `-no-new-window -s* <text> <ancestor:"a"\|ancestor:"b"\|...>` |
| all volumes (This PC) | `-no-new-window -s* <text>` |

- `-no-new-window` when `reuseEverythingWindow` is true, otherwise `-new-window`.
- `-s*` means "the rest of the command line is the literal search text" (Everything 1.5+). Older builds get `-s "<text>"` with a literal `"` written as `"""`. The capability is chosen from the IPC version query; an unknown version falls back to the safe 1.4 form.
- The user text is passed through **verbatim** as Everything search syntax; it is never concatenated into a synthesised query string that could change its meaning. The scope is the only thing appended, and only as `-path` or an `ancestor:` union.
- A quoted path is only used when the path contains a space, `&`, `(`, or `)`.
- Multi-folder unions require Everything 1.5; if the version is unknown, building fails with `a multi-folder search scope requires Everything 1.5 or later` and the search is reported as unavailable instead of silently searching everywhere.

**Why `-path` and not a path inside the query text:** Everything matches a folder path *inside the query text* as a **path substring** — searching inside `D:\a\inside` also matched `D:\a\inside2`. `-path <dir>` matches the folder, not a substring. Measured; note also that `-path "a;b"` is a single token, not a union.

### 7.3 Execution and window handling (`EverythingBridge`)

`Execute(invocation, explorerHwnd)` — serialised by a lock so two Explorer windows cannot interleave their Everything updates:

1. Snapshot the existing Everything search windows (`FindTopLevelWindows("EVERYTHING")`) to know whether reuse is expected.
2. Start `Everything.exe` with the command line (`UseShellExecute = false`, `CreateNoWindow = true`, working directory = the Everything folder).
3. Wait up to 2 s for the launcher to exit. When Everything is already running, the launcher forwards the command line via IPC and exits after ~100 ms; when Everything was **not** running, this process *is* the new Everything instance, so it is neither waited for indefinitely nor killed.
4. `WaitForSearchWindow` polls the `EVERYTHING` windows (20 ms, doubling to a 200 ms cap) for a title containing a prefix of the query (at most the first 24 characters, because long queries are elided in the title). A newly created — or, failing that, the first existing — window is used as a fallback after `windowWaitMs` (default 15 000 ms), and a warning is logged that the title does not reflect the query yet.
5. Raise the window **without** activating it (`NativeMethods.BringToFrontWithoutActivating`), then restore the Explorer search box focus (below).
6. Return `WindowCreated` / `WindowReused`, the wall-clock latency and `TitleReflectsQuery` as a verification signal.

### 7.4 IPC usage

Everything's documented message-based IPC (`everything_ipc.h`) is used only for **status and capability information**, through `FindWindow("EVERYTHING_TASKBAR_NOTIFICATION")` + `WM_USER` messages: major/minor/revision/build version, whether the database is loaded, and whether an NTFS drive is indexed. The search itself is **not** issued through IPC: per the class comment of `EverythingIpc.cs`, the documented `EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8` message is accepted by Everything 1.5.0.1423b but **has no effect**, so the officially supported command line is used instead. (Measured during the earlier probing work; not re-measured while writing this document. `EverythingIpc.IsDatabaseLoaded` and `IsDriveIndexed` exist for diagnostics and are not called on the search path.)

### 7.5 Focus strategy

Requirement: the results must be visible in front of Explorer, but the user's typing must keep going into the Explorer search box.

```csharp
ShowWindow(hWnd, SW_SHOWNOACTIVATE);
SetWindowPos(hWnd, HWND_TOP, 0,0,0,0, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_SHOWWINDOW);
```

`SWP_NOACTIVATE` raises the window in the z-order without giving it the keyboard focus. If Everything nevertheless ended up as the foreground window, `EverythingBridge.RestoreSearchBoxFocus` hands the keyboard focus back by calling `SearchBoxWatcher.FocusSearchBoxThreadSafe` (`AutomationElement.SetFocus()` on the search box) — **but only if all of**:

- the current foreground window is the Everything window (otherwise Everything never took the focus and the message `Everything did not take the keyboard focus; the Explorer search box keeps it` is logged),
- the system-wide last-input tick has not changed since before the command was sent (`GetLastInputInfo`) — i.e. the user has not started doing something else in the meantime (`the user produced input after the search; leaving the keyboard focus alone`).

A refusal by Explorer (the box may not be focusable in the current view) is logged at `Debug` and is not an error; the window and its results are unaffected.

**Measured:** after an automatic (idle) search the log shows `returning the keyboard focus to the Explorer search box (hwnd=1904286)` and continued typing was still received by the same search box and produced a further submit.

**Not hand-verified:** the behaviour on a system where the Explorer search box uses the UIA `Edit` rather than a WinUI `AutoSuggestBox`-hosted control, and the "Everything took the foreground on purpose" branch.

## 8. Logging and configuration

- **`AppLogger`** (`Core\Diagnostics\AppLogger.cs`): `<root>\logs\app.log`, `[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] message`, UTF-8 without BOM, `FileShare.ReadWrite | FileShare.Delete`. `Create(root, config, fileLogging)` is called by `AppRoot` with `fileLogging: false` while the configuration is being loaded and is only enabled through `SetFileLogging(config.LoggingEnabled)` afterwards — a configuration that disables logging therefore never creates `logs\` or `app.log`, not even a truncated one. Writing is a `ConcurrentQueue` enqueue plus a semaphore release; the `EES-LogWriter` thread drains the queue, flushes once per batch and rotates when the file exceeds `maxLogFileSizeMb` (`app.log` → `app.1.log` → … → `app.<maxLogFiles-1>.log`, oldest deleted). `SetFileLogging(false)` (or level `None`) closes the handle and produces nothing further. `ClearLogs` asks the writer to close (with a 5 s timeout), deletes every `*.log` (5 attempts with 50 ms backoff), and lets the writer reopen lazily. Every path swallows exceptions: logging can never break the application.
- **`ConfigStore`** (`Core\Configuration\ConfigStore.cs`): `config.json` in the root directory, never `%AppData%`. Atomic save (write `config.json.tmp`, then `File.Move(..., overwrite: true)`). A missing file is created with defaults; an unreadable/corrupt file falls back to in-memory defaults and logs an error; an unwritable file sets `IsPersistDisabled` + `LastError` and triggers a notification, and the tool keeps working with in-memory settings. `AppRoot` reports the outcome after the logger has been configured, so the `configuration loaded from …` line (with `(defaults are used in memory: …)` appended when reading failed) is itself written to the log file.
- **`AppConfig.Normalize`** clamps every numeric field (`autoSearchDelay` 100–60000, `enterCommitWindowMs` 50–5000, `explorerRescanSeconds` 0–3600, `maxLogFileSizeMb` 1–1024, `maxLogFiles` 1–100), trims and unquotes the two paths, canonicalises `logLevel` and normalises `language` (`auto` / `en-US` / `zh-CN`, unknown → `auto`). A hand-edited file can therefore never produce an out-of-range runtime value.
- **Structured search log lines** (emitted by `SearchCoordinator.Process`, in this order):

```text
[INFO] Search submitted trigger=Enter|IdleTimeout
[INFO] SourceExplorerHwnd=<hwnd>
[INFO] SearchText="<text>"
[INFO] ResolvedPath="<path>"            (or  <all volumes> )
[INFO] SearchScope=<kind> [ (carried over from the window's folder) ]
[INFO] Query="<query>"
[INFO] Everything window created|reused
[INFO] Search completed in <n> ms
```

## 9. Error handling and degradation

| Situation | Behaviour |
|---|---|
| `config.json` missing / corrupt / unwritable | created / defaults in memory + error log / in-memory settings + one notification; the tool keeps running |
| Everything not found | search fails, `EverythingUnavailable` → message box the first time, balloon afterwards, with the hint to set `everythingPath` |
| Everything found but not running | `Probe` reports `Available` (executable known) and the search starts it |
| Everything window does not appear within 15 s | failure logged, notification; the Explorer search box is untouched |
| Everything window title does not reflect the query yet | warning, the located window is used anyway |
| Multi-folder scope without a known Everything 1.5 | query building fails with a clear error; no silent fallback to "search everywhere" |
| Unsupported virtual folder (Recycle Bin, Network, …) | `UnsupportedShellNamespace`, notification, no redirect |
| Search result view with unknown origin folder | `SearchScopeUnknown`, notification, no redirect |
| Resolved directory no longer exists | `DirectoryUnavailable`, remembered scope dropped, notification |
| Explorer window closed while typing | `ExplorerWindowNotFound`, notification |
| Shell resolution throws | `LocationUnavailable` with the exception message; error logged with the stack trace |
| `SetWindowsHookEx` for the keyboard hook fails | warning, the two fallback Enter mechanisms keep working |
| UIA focus/title/text handler cannot be subscribed | `Debug` log; the remaining signals keep working |
| Any logger failure | swallowed; no log line, no crash |
| Settings changed at runtime | applied immediately where possible: logger level/flag, language, Everything path cache, startup entry, monitoring start/stop, tray labels |
| Tray/menu failures | caught and logged; the application never shows an unhandled exception dialog (startup failures are the one exception: they show a message box and exit with code 1) |

Notifications follow one policy (`NotificationService`): **errors are never silent** — the first occurrence of a given error kind is a modal message box, later ones are tray balloons; purely informational messages (logs cleared, read-only config, startup entry repaired) obey `showNotifications`.
