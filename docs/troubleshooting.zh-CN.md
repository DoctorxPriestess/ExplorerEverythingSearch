# 故障排查

[简体中文](troubleshooting.zh-CN.md) | [English](troubleshooting.md) · [返回 README](../README.zh-CN.md)

症状 → 原因 → 处理。每条末尾给出应当查找的日志行，便于判断当前落在哪条代码路径上。日志行均引自已生成的 `logs\app.log`（把 `logLevel` 设为 `Debug` 可获得全部日志）。

快速定位三步：

1. `logs\app.log` —— 工具是否看到了 Explorer（`Explorer detected hwnd=…`、`SearchBox detected hwnd=…`）？
2. 托盘提示/菜单 —— 显示 `Everything：已连接（…）` 还是 `不可用`？
3. `config.json` —— `enabled`、`loggingEnabled`、`logLevel`、`autoSearchDelay`、`everythingPath`。

---

## Everything 不可用

### 症状：弹出“Everything 不可用 —— 未找到 Everything”消息框/气泡；所有搜索都失败

**原因。** 找不到 `Everything.exe`：配置路径错误、无法读取正在运行的实例路径，且在常见安装位置与 `PATH` 中也没有找到。

**处理。**
1. 从 <https://www.voidtools.com/> 安装 Everything，或
2. 托盘 → **设置...** → *Everything.exe 路径* → **浏览...**，或直接写入配置：

```json
{ "everythingPath": "C:\\Program Files\\Everything\\Everything.exe" }
```

3. 点击 **测试 / 重新连接**。

**查找。**

```text
[WARN] configured everythingPath does not exist: <路径>
[WARN] Everything.exe could not be located
[ERROR] Everything not found: no Everything.exe configured or detected
```

### 症状：托盘显示 `Everything：不可用`，但 EXE 明明在

**原因。** 三种可能：Everything 没在运行（工具仍可自行启动它，但*状态*来自 IPC 版本查询）、路径无法解析、或者运行的是其它用户/会话的实例而无法读取其模块路径。

**处理。** 启动 Everything，然后托盘 → **重新连接 Everything**（同时会清除缓存的检测结果）。

**查找。** 重连后应出现 `[INFO] Everything connection: Connected (1.5.0.1423) path=…`。

### 症状：Everything 正在运行，但结果为空或明显不全

**原因。** Everything 尚未加载完数据库，或者目标目录所在驱动器未被索引。

**处理。**
1. 等待 Everything 完成索引（看它自己的状态栏）。
2. 在 Everything 中检查 *工具 → 选项 → 索引*（NTFS 卷 / 文件夹索引），把出问题的驱动器或目录加入。
3. 在工具之外确认数据库与驱动器状态：

```powershell
Add-Type -Namespace T -Name I -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
'@
$h = [T.I]::FindWindow('EVERYTHING_TASKBAR_NOTIFICATION', $null)
"ipc window: $h"                                    # 0 = Everything 未运行
"db loaded : $([T.I]::SendMessage($h,0x400,[IntPtr]401,[IntPtr]::Zero))"   # 1 = 已加载
"NTFS C:   : $([T.I]::SendMessage($h,0x400,[IntPtr]400,[IntPtr]3))"        # 1 = 已索引
```

在本项目的验证机上两者都返回 `1`。工具用 `EverythingIpc.IsDatabaseLoaded` / `IsDriveIndexed` 查询同样的值，但**在搜索路径上从不使用它们**，因此“数据库未加载”不会被告知给你，只表现为结果为空。

---

## 搜索没有反应，或范围不对

### 症状：在资源管理器搜索框里输入，什么都不会打开

**最可能的原因。** 监听被关闭，或者搜索框从未被检测到。

**处理。**
1. 托盘 → **启用监听**（或 `"enabled": true`）。
2. 确认日志里出现了窗口与其搜索框：

```text
[INFO] Explorer detected hwnd=1904286
[INFO] SearchBox detected hwnd=1904286 name=" 在 数据目录 空格 中搜索"
```

3. 若窗口出现了但搜索框始终没有，日志会有 `search box not found yet in hwnd=… (attempt N)`。Explorer 的搜索框是延迟创建的：打开窗口、点进搜索框，等待周期性重扫（`explorerRescanSeconds`，默认 60 秒）—— 或重启工具。
4. 若连 `Explorer detected` 都没有，说明 WinEvent 钩子没装成功，或窗口类不是 `CabinetWClass`（第三方文件管理器）：`[WARN] could not install the WinEvent hook for 0x…`。

### 症状：结果的范围限定到了错误的目录

**原因。** 范围取自**触发搜索的窗口**的 Shell 位置。若该窗口在工具启动时（或窗口被附加时）就已经处于搜索结果视图，来源目录可能是从别的视图记录下来的，或者根本没被记录。

**处理。**
1. 先把该资源管理器窗口导航到目标目录，再输入查询。
2. 在日志中核对解析结果：

```text
[INFO] ResolvedPath="C:\Users\…\ees-smoke\数据目录 空格"
[INFO] SearchScope=CurrentDirectoryAndSubdirectories (carried over from the window's folder)
```

`(carried over from the window's folder)` 表示目录来自记忆的实时位置（同一窗口的第二次及后续搜索都属正常，因为 Explorer 此时已把窗口变成搜索结果视图）。若路径不符合预期，清空搜索框、重新导航后再试。

### 症状：搜索普通目录时出现 `SearchScope=AllVolumes`（或没有范围）

**原因。** 该窗口显示的是**这台电脑**，它被有意映射为“所有卷” —— 此时没有单一目录可限定；**主文件夹**同理，映射为已知文件夹的并集。由于“这台电脑”与“主文件夹”都按窗口被记住，从这里发起的搜索即使窗口已变成结果视图也会保持该语义。

**处理。** 这是设计行为；参见 [README](../README.zh-CN.md#这台电脑--主文件夹--搜索结果视图的作用域语义) 中的作用域表。要搜索单个目录，打开那个目录。

### 症状：在 `…\inside` 中搜索，也命中了 `…\inside2`

**原因。** 这不是工具的问题：当目录路径被当作*搜索文本*使用时就会这样，Everything 会按子串匹配路径。

**处理。** 无需处理 —— 工具从不把路径放进查询文本，而是用 `-path <目录>`（按目录匹配）或 `<ancestor:…>`（并集）。如果你确实想要子串匹配，直接在 Everything 窗口里输入即可（那里是标准的 Everything 搜索框）。供参考：`-path "a;b"` 是**单个 token**，不是并集。

### 症状：出现“搜索结果视图作用域未知”提示

```text
[WARN] search not redirected: SearchScopeUnknown detail="Explorer is showing a search result view and the folder it was started from is unknown" raw="“此电脑”中的搜索结果&thistpc"
```

**原因。** 该窗口此前已经提交过搜索，其 Shell 位置变成了合成的搜索视图名，而此前从未为该窗口记录过实时位置 —— 典型情形是：工具启动或附加该窗口时，窗口已经在搜索结果视图里。（从**这台电脑**或**主文件夹**发起的搜索不再落到这里：这些位置按窗口记忆，会解析为所有卷 / 已知文件夹并集。）

**处理（任选其一）。**
1. 直接在该窗口搜索框里再输入一次：一次查询的首次按键会记录实时目录（`_onQueryStarted`），因此该窗口的**下一次**提交就能解析出范围。
2. 点击面包屑（离开搜索视图）后重新搜索。
3. 用 ✕ 关闭搜索视图后重开窗口。

这一拒绝是刻意的：否则就只能到未知位置去搜索，结果更糟。同类提示每会话首次弹消息框，之后弹气泡。

### 症状：回收站 / 网络 / 控制面板出现 `UnsupportedShellNamespace`

```text
[WARN] search not redirected: UnsupportedShellNamespace detail="回收站" raw="::{645FF040-5081-101B-9F08-00AA002F954E}"
```

**原因。** 该位置是没有文件系统搜索范围的虚拟文件夹。

**处理。** 无需处理 —— 这是设计行为。导航到真实目录才会把搜索重定向到 Everything。

### 症状：`DirectoryUnavailable` ——“解析出的目录已不存在”

**原因。** 在导航与搜索之间，目录被移动/重命名/删除，或者驱动器/网络共享离线。该窗口记忆的范围会被清除。

**处理。** 导航到新位置后重新搜索。

### 症状：查询明显没问题，却既不打开 Everything 也没有任何日志

**可能原因。** 工具会刻意忽略**空的**搜索框（纯空白文本触发 `Cancel`），并抑制同一窗口/范围/文本的重复提交。

**处理。** 打开**打开日志目录**，查找 `identical search already performed for this window; skipped`、`search text is empty; nothing submitted` 或 `search superseded hwnd=… text=…`（快速输入时，一串输入中只有最后一条查询会被执行）。

---

## Enter 与自动搜索行为

### 症状：按 Enter 不会立即提交，但稍后会自动出现搜索

**原因。** 低级键盘钩子没有生效（它是唯一精确的 Enter 信号），而焦点启发式也没命中。可能原因：`detectEnterByKeyboardHook` 为 `false`、安全软件拦截了 `SetWindowsHookEx`、或者搜索框实际上从未获得键盘焦点。

**处理。**
1. 查日志 —— 钩子会自报状态：

```text
[DEBUG] Enter detection: keyboard hook installed (an Explorer search box has the focus)
[WARN] could not install the keyboard hook for Enter detection; falling back to the focus and commit heuristics
```

2. 把本工具加入安全软件白名单后重启，或
3. 接受兜底机制并设置：

```json
{ "detectEnterByKeyboardHook": false, "detectEnterByFocusChange": true, "detectEnterByCommitTiming": true }
```

关闭钩子后，只有在焦点从搜索框离开、进入同一窗口的结果区，或 Explorer 在最后一次按键后 `enterCommitWindowMs`（默认 400 ms）内提交且标题携带搜索框文本时，才认为按了 Enter。**该兜底路径未做过人工验证 —— 见 [verification.md](verification.md#11-not-verified--unverified)。**

4. 如果 Enter 必须可用而以上都无效，可调小 `autoSearchDelay`（例如 `300`），让空闲触发更快。

**查找。** `[DEBUG] Enter detected (<原因>)`，原因为 `the Enter key was pressed in the search box`（钩子）、`focus left the search box -> …`（焦点）、或 `Explorer commit after N ms (…)`（时序）。

### 症状：我还在输入就已经提交了搜索

**原因。** `autoSearchDelay` 到期（默认 1000 ms）。任何超过一秒的停顿都会提交当时的半截文本。

**处理。** 调大 `autoSearchDelay`（最大 60000）。停顿期间的文本会随下一次按键重新计时，不会丢失，但已经发出的那次不完整查询仍会出现在 Everything 中。

### 症状：连按两次 Enter 只提交一次；或自动搜索刚结束就按 Enter 没反应

**原因。** 刻意的去重：键盘钩子与焦点兜底都会观察到同一次按键，因此 400 ms 内的第二次上报被忽略；窗口/范围/文本完全相同的提交会被整体跳过。

**查找。** `[DEBUG] Enter was already submitted for this window; ignoring the second report (…)` / `[DEBUG] identical search already performed for this window; skipped`。

---

## Everything 窗口与焦点

### 症状：Everything 窗口抢走了键盘焦点，我的输入跑到它那里去了

**原因 / 设计。** 窗口用 `ShowWindow(SW_SHOWNOACTIVATE)` + `SetWindowPos(..., SWP_NOACTIVATE | SWP_SHOWWINDOW)` 提升 —— **只置顶，绝不激活**。若 Everything 仍然占用了前台，工具会对资源管理器搜索框调用 `SetFocus()` 把焦点还回去，但**仅当**你在搜索发出之后没有产生自己的输入（`GetLastInputInfo`）。若期间你已开始别的操作，工具会刻意不动焦点，以免把你从正在做的事里拽出来。

**处理。**
1. 先看哪条分支被执行：

```text
[DEBUG] returning the keyboard focus to the Explorer search box (hwnd=1904286)
[DEBUG] Everything did not take the keyboard focus; the Explorer search box keeps it
[DEBUG] the user produced input after the search; leaving the keyboard focus alone
```

2. 若看到第三条，那就是预期行为。点回资源管理器搜索框继续输入即可。
3. 若焦点始终不返回，可能是当前视图下搜索框不可聚焦 —— 日志会有 `could not focus the search box (hwnd=…): …`（`Debug` 级）。这不是故障；窗口与结果都不受影响。
4. 若你根本不想让 Everything 窗口盖在 Explorer 上，可手动关闭/复用它（`reuseEverythingWindow: false` 会为每次搜索新建窗口，但这不改变置顶行为 —— 置顶目前不可配置）。

### 症状：Everything 窗口看不见 / 一直待在 Explorer 后面

**可能原因。** 窗口找到了但无法提升（可能被最小化、位于其它虚拟桌面，或存在其它 topmost 窗口）。

**处理。** 手动切到前面，查询已经在里面了。留意 `[WARN] Everything window title does not reflect the query yet; using the located window`（回退取了标题尚未更新的窗口）与 `Everything window reused` / `Everything window created`。

### 症状：Everything 窗口被关掉后，搜索再也不会打开新窗口

**原因。** 预期是复用（`reuseEverythingWindow: true`），而工具在发出命令行后没找到可复用的窗口。可能原因：Everything 完全没在运行（走“不可用”路径），或窗口未在 15 秒等待内出现。

**处理。**
1. 托盘 → **重新连接 Everything**。
2. 手动启动 Everything 后重新搜索。
3. 查找 `[ERROR] Everything search window did not appear within 15000 ms` 与 `[DEBUG] Everything launcher still running: Everything was started by this call`（本次是工具自己启动了 Everything；那个启动进程*就是* Everything，因此既不会等它结束也不会杀它）。

---

## 日志

### 症状：`logs\app.log` 为空或不存在

**原因。**
1. `"loggingEnabled": false`，或 `"logLevel": "None"` —— 完全不会产生日志（设计如此，连文件句柄都会关闭）。
2. 文件在别处：确认 `<root>`，即 EXE 所在目录或 `--root` 指定目录。
3. 根目录不可写 —— 此时工具以内存设置运行，并只提示一次（`配置不可写`）。

**处理。** 设置 `"loggingEnabled": true, "logLevel": "Debug"`，重启并复现，然后托盘 → **打开日志目录**。开头应类似：

```text
[INFO] configuration loaded from <root>\config.json
[INFO] Explorer Everything Search 1.0.0.0 starting
[INFO] application directory: <root>
[INFO] configuration: <root>\config.json
[INFO] OS: Microsoft Windows NT 10.0.26200.0 (X64)
[INFO] Everything: Connected (…) path=…
```

### 症状：日志文件长得太快，想要更小的文件

**原因。** `logLevel: Debug`/`Trace` 很啰嗦（每次按键都会产生 `SearchBox text changed:` 行）。

**处理。** 改用 `"logLevel": "Information"`，并/或调小 `maxLogFileSizeMb`（默认 5）与 `maxLogFiles`（默认 5）。轮转文件名为 `app.log`、`app.1.log` … `app.<maxLogFiles-1>.log`，最旧的被删除。**轮转未做人工验证** —— 见 [verification.md](verification.md#11-not-verified--unverified)。

### 症状：点“清理日志”报失败，或文件立刻又出现

**原因。** 文件被其它读取者占用（工具会以 50 ms 间隔重试 5 次），或者日志仍开着、删除后立刻又写入新行 —— 后者是预期行为：清理日志不会停止记录日志。

**处理。** 关闭其它读取者后重试。失败消息会列出无法删除的文件：`log files could not be cleared: could not remove: app.1.log`。

---

## 开机启动、实例、显示、权限

### 症状：不会随 Windows 启动（或移动位置后仍从旧路径启动）

**原因。** `Run` 值存的是绝对路径：`"<EXE 路径>" --startup`。移动目录后该路径即失效。

**处理。**
1. **在 `startWithWindows` 为 true 的前提下**，下次启动会自动修复，日志为：

```text
[INFO] start with Windows entry repaired: "<新路径>" --startup
```

2. 手动：托盘 → **设置...** → 出现“开机启动项指向了其他位置”告警 → **修复开机启动项**。
3. 自行核对注册表值：

```powershell
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ExplorerEverythingSearch |
  Select-Object -ExpandProperty ExplorerEverythingSearch
```

4. 若写入失败，日志会有 `[WARN] could not register start with Windows: could not write HKCU\...\Run` —— 通常是策略限制或 HKCU 被锁定。

### 症状：第二次启动 EXE 似乎什么都没发生

**原因。** 设计如此：第二次启动不会生成第二个托盘图标。带 `--settings` 时它会请求正在运行的实例打开设置窗口；带 `--exit` 时请求其退出；不带参数时静默退出。

**处理。** 使用正在运行实例的托盘图标。这是有意的单实例行为，不是故障。可确认每个根目录下 `[INFO] configuration loaded from …` 只出现一次。

### 症状：同时有两个实例在跑 / 我的测试实例干扰了正式实例

**原因。** `--root <目录>` 会刻意创建**独立实例**（互斥体与命名事件带一个由根路径派生的 SHA-256 后缀），因此测试根永远不会与正式实例互相通信 —— 也无法被它控制。

**处理。** 用相同的 `--root` 启动测试实例；或省略 `--root` 使用便携默认值（EXE 目录）。不要指望不带 `--root` 的 `--exit` 能停掉一个 `--root` 实例。

### 症状：托盘图标不见了，或设置窗口跑到别的显示器上 / 显示模糊

**原因。** 托盘图标可能被 Windows 的折叠区隐藏；设置窗口以 `WindowStartupLocation=CenterScreen` 打开。清单声明了 `PerMonitorV2` DPI 感知，因此布局会跟随显示器缩放。

**处理。** 在任务栏设置里显示隐藏的托盘图标，或再次执行 `--settings`。混合 DPI/多显示器行为**未做人工验证** —— 见 [verification.md](verification.md#11-not-verified--unverified)。

### 症状：需要管理员权限吗？以管理员身份运行会怎样？

**原因 / 设计。** 不需要：清单请求 `asInvoker`，所用的一切（WinEvent 钩子、UI Automation、`Shell.Application`、HKCU `Run`、在 EXE 旁创建目录）普通用户都可用。也没有请求 `uiAccess`，这正是 UAC 提权提示（安全桌面）出现时低级键盘钩子仍可能被屏蔽的原因。

**处理 / 注意。**
- 若把 EXE 装到 `C:\Program Files`，普通用户无法在那里创建 `config.json`/`logs\`。请放到用户可写目录（或使用 `--root`）。此时工具会提示一次 `配置不可写`，并以内存设置继续工作。

**位数 / 打包的注意点。** 本项目的 Enter 检测证据来自**框架依赖的 64 位**构建（`bin\Debug\net8.0-windows`，x64）运行在 64 位 Windows 上。**单文件自包含**发布下低级键盘钩子的行为、以及 64 位进程的钩子能否覆盖 32 位消费方，**均未测试 —— UNKNOWN**。若你的 Enter 检测不稳定：改用框架依赖发布，并/或设 `detectEnterByKeyboardHook: false` 走焦点/提交兜底。
