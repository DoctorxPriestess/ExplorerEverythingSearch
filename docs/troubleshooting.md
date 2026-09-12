# Troubleshooting

[English](troubleshooting.md) | [简体中文](troubleshooting.zh-CN.md) · [Back to README](../README.md)

Symptom → cause → what to do. Every entry ends with the log lines to look for, so you can tell which code path you are in. Log lines are quoted from `logs\app.log` (set `logLevel` to `Debug` to get all of them).

Fast triage:

1. `logs\app.log` — does the tool see Explorer (`Explorer detected hwnd=…`, `SearchBox detected hwnd=…`)?
2. Tray tooltip / menu — does it say `Everything: Connected (…)` or `Unavailable`?
3. `config.json` — `enabled`, `loggingEnabled`, `logLevel`, `autoSearchDelay`, `everythingPath`.

---

## Everything is unavailable

### Symptom: "Everything is not available — Everything not found" message box / balloon; every search fails

**Cause.** No `Everything.exe` could be located: the configured path is wrong, the running instance could not be inspected, and nothing was found in the usual install locations or on `PATH`.

**Fix.**
1. Install Everything from <https://www.voidtools.com/>, or
2. Tray → **Settings...** → *Everything.exe path* → **Browse...**, or set it directly:

```json
{ "everythingPath": "C:\\Program Files\\Everything\\Everything.exe" }
```

3. Click **Test / reconnect**.

**Look for.**

```text
[WARN] configured everythingPath does not exist: <path>
[WARN] Everything.exe could not be located
[ERROR] Everything not found: no Everything.exe configured or detected
```

### Symptom: the tray says `Everything: Unavailable` but the EXE is there

**Cause.** One of three: Everything is not running (the tool can still start it, but the *status* comes from the IPC version query), or the path is not resolvable, or another user's/session's instance is running and its module path is not readable.

**Fix.** Start Everything, then Tray → **Reconnect Everything** (this also drops the cached detection).

**Look for.** `[INFO] Everything connection: Connected (1.5.0.1423) path=…` after the reconnect.

### Symptom: Everything is running but results are empty or obviously incomplete

**Cause.** Everything has not finished loading its database, or the drive holding the folder is not indexed.

**Fix.**
1. Wait for Everything to finish indexing (its own status bar).
2. In Everything, check *Tools → Options → Indexes* (NTFS volumes / folder indexes) and add the failing drive or folder.
3. Confirm from outside the tool that both the database and the drive report as ready:

```powershell
Add-Type -Namespace T -Name I -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
'@
$h = [T.I]::FindWindow('EVERYTHING_TASKBAR_NOTIFICATION', $null)
"ipc window: $h"                                    # 0 = Everything is not running
"db loaded : $([T.I]::SendMessage($h,0x400,[IntPtr]401,[IntPtr]::Zero))"   # 1 = loaded
"NTFS C:   : $([T.I]::SendMessage($h,0x400,[IntPtr]400,[IntPtr]3))"        # 1 = indexed
```

On the machine used to verify this project both returned `1` — the tool queries the same values with `EverythingIpc.IsDatabaseLoaded` / `IsDriveIndexed`, but **never uses them on the search path**, so a "database not loaded" state is not reported to you; it simply shows up as empty results.

---

## Search does nothing, or the scope is wrong

### Symptom: typing in the Explorer search box never opens anything

**Cause (most likely).** Monitoring is off, or the search box was never detected.

**Fix.**
1. Tray → **Enable monitoring** (or `"enabled": true`).
2. Check that the log shows the window and its search box:

```text
[INFO] Explorer detected hwnd=1904286
[INFO] SearchBox detected hwnd=1904286 name=" 在 数据目录 空格 中搜索"
```

3. If the window is found but the search box never is, the log shows `search box not found yet in hwnd=… (attempt N)`. Explorer's search box is created lazily; open the window, click into the search box, and wait for the periodic rescan (`explorerRescanSeconds`, default 60 s) — or restart the tool.
4. If there is no `Explorer detected` line at all, the WinEvent hooks could not be installed or the window class is not `CabinetWClass` (a third-party file manager): `[WARN] could not install the WinEvent hook for 0x…`.

### Symptom: the results are scoped to the wrong folder

**Cause.** The scope is captured from the Shell location of the *triggering* window. If the window was already showing a search result view when the tool started (or when the window was attached), the origin folder may have been recorded from a different view — or not recorded at all.

**Fix.**
1. Navigate the Explorer window to the folder you want, then type the query.
2. Check the resolved scope in the log:

```text
[INFO] ResolvedPath="C:\Users\…\ees-smoke\数据目录 空格"
[INFO] SearchScope=CurrentDirectoryAndSubdirectories (carried over from the window's folder)
```

`(carried over from the window's folder)` means the folder was taken from the remembered live location (normal for the second and later searches in the same window, because Explorer has already turned the window into a search result view). If the path is not what you expect, clear the search box, navigate again, and repeat.

### Symptom: `SearchScope=AllVolumes` (or no scope at all) when searching a normal folder

**Cause.** The window is showing **This PC** ("这台电脑"), which is deliberately mapped to "every volume" — there is no single folder to scope to — or **Home** (主文件夹), which maps to the union of your known folders. Since "This PC" and "Home" are remembered per window, a search started there keeps that meaning even after Explorer turned the window into a result view.

**Fix.** This is by design; see the scope table in the [README](../README.md#scope-semantics-of-this-pc--home--search-result-views). To search one folder, open that folder.

### Symptom: searching inside `…\inside` also matches `…\inside2`

**Cause.** Not the tool: this happens when a folder path is used as *search text*. Everything matches a path as a substring there.

**Fix.** Nothing to do — the tool never puts the path into the query text; it uses `-path <dir>` (folder match) or `<ancestor:…>` (union). If you *want* a substring match, type it yourself in the Everything window, which is a normal Everything search box. For reference, `-path "a;b"` is a **single token**, not a union.

### Symptom: "搜索结果视图作用域未知" / "the folder the search was started from is unknown"

```text
[WARN] search not redirected: SearchScopeUnknown detail="Explorer is showing a search result view and the folder it was started from is unknown" raw="“此电脑”中的搜索结果&thistpc"
```

**Cause.** Explorer had already committed a search in that window, so its Shell location was the synthetic search-view name, and no earlier live location had been recorded for that window — typically because the window was already in a search result view when the tool started or when it was attached. (Searching from **This PC** or **Home** no longer lands here: those locations are remembered per window and resolve to all volumes / the known-folder union.)

**Fix (any one).**
1. Type a new query into that window's search box: the first keystroke of a query records the live folder (`_onQueryStarted`), so the *next* submit in that window resolves.
2. Click the breadcrumb (leave the search view) and search again.
3. Close the search view with the ✕ and reopen the window.

The refusal is intentional: the alternative would be searching an unknown location, which is worse. The message box appears once per session for this kind; later occurrences are balloons.

### Symptom: `UnsupportedShellNamespace` for Recycle Bin / Network / Control Panel

```text
[WARN] search not redirected: UnsupportedShellNamespace detail="回收站" raw="::{645FF040-5081-101B-9F08-00AA002F954E}"
```

**Cause.** The location is a virtual folder with no file-system search scope.

**Fix.** None — this is by design. Navigate to a real folder to have the search redirected.

### Symptom: `DirectoryUnavailable` — "the resolved directory no longer exists"

**Cause.** The folder was moved, renamed, deleted, or the drive/share went offline between navigation and the search. The remembered scope for that window is dropped.

**Fix.** Navigate to the new location and search again.

### Symptom: a perfectly good query does not open Everything, nothing is logged

**Cause (possible).** The tool deliberately ignores an **empty** search box (`Cancel` on whitespace-only text) and suppresses duplicate submissions for the same window/scope/text.

**Fix.** Open the **Open log folder** and check for `identical search already performed for this window; skipped`, `search text is empty; nothing submitted`, or `search superseded hwnd=… text=…` (fast typing: only the last query in a burst is executed).

---

## Enter and auto-search behaviour

### Symptom: Enter does not submit immediately, but the search appears after a short pause

**Cause.** The low-level keyboard hook is not active (it is the only exact Enter signal) and the focus heuristic did not fire. Reasons: `detectEnterByKeyboardHook` is `false`, security software blocked `SetWindowsHookEx`, or the search box never actually held the keyboard focus.

**Fix.**
1. Check the log — the hook announces itself:

```text
[DEBUG] Enter detection: keyboard hook installed (an Explorer search box has the focus)
[WARN] could not install the keyboard hook for Enter detection; falling back to the focus and commit heuristics
```

2. Add the tool to your security software's allow-list and restart it, or
3. Accept the fallbacks and set:

```json
{ "detectEnterByKeyboardHook": false, "detectEnterByFocusChange": true, "detectEnterByCommitTiming": true }
```

With the hook off, Enter is recognised only when the keyboard focus leaves the search box into the result area of the same window, or when Explorer commits within `enterCommitWindowMs` (default 400 ms) of the last keystroke with the title carrying the search box text. **This fallback path was not verified by hand — see [verification.md](verification.md#11-not-verified--unverified).**

4. If Enter must work but nothing helps, lower `autoSearchDelay` (e.g. `300`) so the idle trigger fires sooner.

**Look for.** `[DEBUG] Enter detected (<reason>)` with a reason of `the Enter key was pressed in the search box` (hook), `focus left the search box -> …` (focus), or `Explorer commit after N ms (…)` (timing).

### Symptom: a search was submitted while I was still typing

**Cause.** `autoSearchDelay` elapsed (default 1000 ms). Any pause longer than one second submits the partial text.

**Fix.** Raise `autoSearchDelay` (up to 60000). A pause of that length restarts on the next keystroke, so nothing is lost, but the earlier partial query will have been sent to Everything.

### Symptom: pressing Enter twice submitted only once, or Enter right after an auto-search did nothing

**Cause.** Deliberate de-duplication: the keyboard hook and the focus fallback both observe the same key press, so a second report within 400 ms is ignored; identical window/scope/text is skipped entirely.

**Look for.** `[DEBUG] Enter was already submitted for this window; ignoring the second report (…)` / `[DEBUG] identical search already performed for this window; skipped`.

---

## Everything window and focus

### Symptom: the Everything window steals the keyboard focus and my typing goes there

**Cause / design.** The window is raised with `ShowWindow(SW_SHOWNOACTIVATE)` + `SetWindowPos(..., SWP_NOACTIVATE | SWP_SHOWWINDOW)` — **on top, never activated**. If Everything takes the foreground anyway, the tool hands the focus back by calling `SetFocus()` on the Explorer search box, but *only* if you have produced no input of your own since the search was issued (`GetLastInputInfo`). If you have, the tool deliberately leaves the focus alone so it does not yank you out of whatever you started doing.

**Fix.**
1. Check which branch ran:

```text
[DEBUG] returning the keyboard focus to the Explorer search box (hwnd=1904286)
[DEBUG] Everything did not take the keyboard focus; the Explorer search box keeps it
[DEBUG] the user produced input after the search; leaving the keyboard focus alone
```

2. If the third line is what you see, that is the intended behaviour. Click back into the Explorer search box and continue.
3. If the focus is never returned at all, the search box may not be focusable in the current view — the log then shows `could not focus the search box (hwnd=…): …` at `Debug`. Nothing is broken; the window and results are unaffected.
4. If you do not want the Everything window raised over Explorer at all, close/reuse it manually (`reuseEverythingWindow: false` creates a fresh window per search, which does not change the raising behaviour — the raising is not configurable today).

### Symptom: the Everything window is not visible / stays behind Explorer

**Cause (possible).** The window was found but could not be raised (it may be minimised, on another virtual desktop, or another window is topmost).

**Fix.** Bring it forward manually; the query is already in it. Check for `[WARN] Everything window title does not reflect the query yet; using the located window` (the fallback picked a window whose title was not yet updated) and `Everything window reused` / `Everything window created`.

### Symptom: the Everything window was closed and searches never open a new one

**Cause.** Reuse is expected (`reuseEverythingWindow: true`), and the tool did not find a window to reuse after the command line was issued. Possible reasons: Everything is not running at all (the "not available" path), or the window did not appear within the 15 s wait.

**Fix.**
1. Tray → **Reconnect Everything**.
2. Start Everything manually and search again.
3. Look for `[ERROR] Everything search window did not appear within 15000 ms` and `[DEBUG] Everything launcher still running: Everything was started by this call` (the tool started Everything itself; the launcher process *is* Everything and is deliberately not waited for or killed).

---

## Logs

### Symptom: `logs\app.log` is empty or missing

**Cause(s).**
1. `"loggingEnabled": false`, or `"logLevel": "None"` — nothing is produced at all (by design; the handle is even closed).
2. The file is elsewhere: check `<root>`, i.e. the EXE's folder or the `--root` directory.
3. The root is not writable — the tool then runs with in-memory settings and tells you once (`Configuration is read-only`).

**Fix.** Set `"loggingEnabled": true, "logLevel": "Debug"`, restart, reproduce, then Tray → **Open log folder**. The first lines should be:

```text
[INFO] configuration loaded from <root>\config.json
[INFO] Explorer Everything Search 1.0.0.0 starting
[INFO] application directory: <root>
[INFO] configuration: <root>\config.json
[INFO] OS: Microsoft Windows NT 10.0.26200.0 (X64)
[INFO] Everything: Connected (…) path=…
```

### Symptom: the log file grows quickly and I want smaller files

**Cause.** `logLevel: Debug`/`Trace` is verbose (every keystroke produces a `SearchBox text changed:` line).

**Fix.** Use `"logLevel": "Information"`, and/or lower `maxLogFileSizeMb` (default 5) and `maxLogFiles` (default 5). Rotation names are `app.log`, `app.1.log` … `app.<maxLogFiles-1>.log`; the oldest is deleted. **Rotation has not been exercised by hand** — see [verification.md](verification.md#11-not-verified--unverified).

### Symptom: "Clear logs" reports failure, or the files come back immediately

**Cause.** A file was locked by another reader (the tool retries 5 times with 50 ms), or logging is on and new lines are written right after the deletion — which is expected: clearing logs does not stop logging.

**Fix.** Close other readers, then retry. A failure message names the files that could not be removed: `log files could not be cleared: could not remove: app.1.log`.

---

## Startup entry, instances, display, privileges

### Symptom: the tool does not start with Windows (or starts from an old path after I moved it)

**Cause.** The `Run` value stores an absolute path: `"<path to EXE>" --startup`. After moving the folder, the stored path is stale.

**Fix.**
1. The entry is repaired automatically at the next start **when `startWithWindows` is true**; the log then says:

```text
[INFO] start with Windows entry repaired: "<new path>" --startup
```

2. Manually: Tray → **Settings...** → the warning "The start-up entry points to another location" → **Repair start-up entry**.
3. Verify the registry value yourself:

```powershell
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ExplorerEverythingSearch |
  Select-Object -ExpandProperty ExplorerEverythingSearch
```

4. If the value cannot be written, the log says `[WARN] could not register start with Windows: could not write HKCU\...\Run` — usually a policy or a locked-down HKCU.

### Symptom: nothing seems to happen when I launch the EXE a second time

**Cause.** By design: a second launch does not create a second tray icon. With `--settings` it asks the running instance to open its settings window; with `--exit` it asks the running instance to quit; with no arguments it silently exits.

**Fix.** Use the running instance's tray icon. This is the intended single-instance behaviour rather than a fault. Check for the log lines `[INFO] configuration loaded from …` appearing only once per root.

### Symptom: two instances are running / my test instance interfered with the normal one

**Cause.** `--root <dir>` deliberately creates an **independent instance** (mutex and named-event names get a SHA-256-derived suffix of the root path), so a test root never talks to the real instance — and cannot be controlled by it either.

**Fix.** Start the test instance with the same `--root`, or omit `--root` to use the portable default (EXE folder). Do not expect `--exit` without `--root` to stop a `--root` instance.

### Symptom: the tray icon is missing, or the settings window is on the wrong monitor / looks blurry

**Cause.** Tray icons can be hidden by Windows' overflow area; the settings window is opened with `WindowStartupLocation=CenterScreen`. The manifest declares `PerMonitorV2` DPI awareness, so layout follows the monitor's scale.

**Fix.** Show hidden tray icons (taskbar settings), or run `--settings` again. Mixed-DPI/multi-monitor behaviour was **not hand-verified** — see [verification.md](verification.md#11-not-verified--unverified).

### Symptom: does this need administrator rights? Will it work if the app is elevated?

**Cause / design.** No: the manifest requests `asInvoker`, and everything used (WinEvent hooks, UI Automation, `Shell.Application`, HKCU `Run`, folder creation next to the EXE) works for a normal user. No `uiAccess` is requested, which is why the low-level keyboard hook can still be blocked while a UAC prompt (secure desktop) is on screen.

**Fix / notes.**
- If you install the EXE under `C:\Program Files`, a normal user cannot create `config.json`/`logs\` there. Put it in a user-writable folder (or pass `--root`). The tool then reports `Configuration is read-only` once and keeps working with in-memory settings.

**Bitness / packaging caveat.** The Enter-detection evidence for this project comes from the **framework-dependent 64-bit** build (`bin\Debug\net8.0-windows`, x64) on a 64-bit Windows. Whether the low-level keyboard hook behaves identically in the **single-file self-contained** publish, and whether a 64-bit process's hook reaches a 32-bit consumer, were **not tested — UNKNOWN**. If Enter detection is unreliable for you: switch to the framework-dependent publish, and/or set `detectEnterByKeyboardHook: false` to run on the focus/commit fallbacks.
