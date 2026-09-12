# 验证方法与实测证据

[简体中文](verification.zh-CN.md) | [English](verification.md) · [返回 README](../README.zh-CN.md)

本文列出实际测过的内容，并给出复现每一条所需的命令/日志片段；同时**单独、明确**列出**没有**验证的内容。

约定：

- **实测** —— 在测试机上执行过，并在命令输出或应用自身的 `app.log` 中观察到。
- **仅代码** —— 源码中存在、但未人工演练的行为，统一列在[未验证清单](#11-未验证--unverified)。
- 复现步骤使用测量时的真实路径/取值。

**证据文件当前状态。** 下文所有 `app.log` 片段均读取于 **2026-09-13 约 02:45**，来源：

```
C:\Users\ControlxSaria\AppData\Local\Temp\ees-smoke-root\logs\app.log   （8 363 字节，82 行，最后一条 02:44:56）
```

到 **02:49:45** 该文件已不存在：`ees-smoke-root\logs\` 目录仍在但**为空**，且 `ees-smoke-root\config.json` 被重写过（mtime 02:18:37 → 02:48:32）。该删除不是本文档作者所为，也未运行过任何构建。由此有两种可能，但都无法从这里确认：并行进行的“清空日志”功能验证（该功能会在工具运行时删除 `*.log`），或外部对临时目录的清理。片段均为 02:45 那次读取的原文照录；**只有重跑下列场景才能真正重新验证它们**。因此本文档中的日志片段作为“证据文件”是 **STALE** 的，尽管读取当时它们是准确的。

## 1. 测试环境（撰写本文档时重新测量）

| 项 | 取值 | 读取方式 |
|---|---|---|
| 操作系统 | Windows 11，`10.0.26200.0`，25H2，build 26200，x64 | `[Environment]::OSVersion`、`HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion` 的 `CurrentBuild`/`DisplayVersion` |
| Everything | `C:\Program Files\Everything\Everything.exe`，文件/产品版本 **1.5.0.1423b**，5 138 088 字节 | `Get-Item ... \| % VersionInfo` |
| Everything IPC 版本查询 | `1.5.0.1423` | `FindWindow("EVERYTHING_TASKBAR_NOTIFICATION")` + `WM_USER` 0..3（`SendMessage`） |
| Everything 数据库已加载 | `1`（已加载） | 同窗口，`WM_USER` 401 |
| NTFS 驱动器已索引 | `C:` → `1`（已索引） | 同窗口，`WM_USER` 400，wParam 3 |
| Everything 进程 | 2 个 `Everything.exe`（`C:\Program Files\Everything\Everything.exe`） | `Get-Process Everything` |
| 日志中的应用版本 | `Explorer Everything Search 1.0.0.0`、`OS: Microsoft Windows NT 10.0.26200.0 (X64)` | `app.log` |
| 该次运行使用的程序目录 | `C:\Users\ControlxSaria\AppData\Local\Temp\ees-smoke-root`（`--root`） | `app.log` |
| 测试数据目录 | `...\ees-smoke\smokedata`、`...\ees-smoke\smokedata\sub`、`...\ees-smoke\数据目录 空格`（各含一个 token 文件） | `Get-ChildItem` |
| 观察到的 Everything 窗口 | `C:\Users\...\Temp\ees-smoke\smokedata\ afterrestart - Everything`，呈现为层级（树形）目录视图 | `Get-Process \| ? MainWindowTitle -like '*Everything*'` |

`es.exe` 相关项以及下面“暂停”类条目**不属于**本轮测量范围 —— 见未验证清单。

## 2. Enter 检测：键盘钩子是首选机制

**实测**（日志，`logLevel: Debug`）。钩子仅在 Explorer 搜索框拥有焦点时安装，并且正是它负责 Enter 提交：

```text
[2026-09-13 02:44:44.221] [DEBUG] search box has the focus (hwnd=1709020)
[2026-09-13 02:44:44.222] [DEBUG] Enter detection: keyboard hook installed (an Explorer search box has the focus)
[2026-09-13 02:44:44.225] [DEBUG] Explorer moved the focus into its own search view input site; not treated as Enter
...
[2026-09-13 02:44:49.275] [DEBUG] Enter detection: keyboard hook removed (no Explorer search box has the focus)
```

它的价值在于同时证明两件事：(a) 低级钩子恰好在搜索框获得焦点时武装；(b) Explorer 自行把焦点移进其搜索视图输入站点时**不会**被误判为 Enter。

**实测**（探针 `ev-enter.txt`、`ev-enter4.txt`）：搜索框获得焦点时按 Enter，10–20 ms 后进入搜索结果视图，按键后约 320 ms 结果视图构建完毕：

```text
   2333ms [probe] PRESSING ENTER
   2353ms [UIA-FOCUS] aid=[0] name=[inner] cls=[UIItem]      <- 焦点离开搜索框
   2780ms [POLL-TITLE] [qzxed - “evprobe-enter”中的搜索结果 - 文件资源管理器]
```

**实测**（探针 `ev-enter4.txt`）：Enter 在**零结果**时同样有效 —— 搜索视图显示 `没有与搜索条件匹配的项。`，焦点移动到 `EmptyTextFocusable`。这是任何标题启发式都无法捕捉的情形，也正是键盘钩子作为首选机制的原因。

**项目早先报告、本轮未重测：** 从按键到 Everything 窗口显示查询的约 100–200 ms。本轮的证据分别是“到 *Explorer* 结果视图 10–20 ms”和“Everything 端到端 154 ms”，二者不在同一次运行中测得，因此全链路区间**未验证**。

## 3. 空闲自动提交（`autoSearchDelay` = 1000 ms）

**实测**（日志，三个独立窗口/按键）。最后一次按键 → `Search submitted trigger=IdleTimeout`：

| 窗口 | 最后一次按键 | 提交 | Δ |
|---|---|---|---|
| `hwnd=1709020`（`thistpc`） | `02:44:45.147` | `02:44:46.162` | **1015 ms** |
| `hwnd=1904286`（`alpha`） | `02:44:50.137` | `02:44:51.152` | **1015 ms** |
| `hwnd=2624920`（`recyc`） | `02:44:55.142` | `02:44:56.147` | **1005 ms** |

“最后一次按键”取最后一条 `SearchBox text changed:` 的时间戳，“提交”取 `Search submitted trigger=IdleTimeout` 行。两者都是毫秒精度，因此这些差值包含日志写入顺序，以及忙碌 STA 线程上 `System.Threading.Timer` 的调度尾巴（三行中有两行 +15 ms）。

项目早先报告的 997–1005 ms 区间与这些记录一致；本日志显示的是更宽的 1005–1015 ms。两者都在定时器精度可解释的约 ±2% 之内。

**实测**：该计时是**按窗口**独立的，因此第二个窗口的输入不会重启另一个窗口的定时器（同一次会话中出现三个不同的 `SourceExplorerHwnd`，各自独立提交）。

## 4. 两个 Explorer 窗口之间的范围隔离

**实测**（日志）。两个位于不同目录的 Explorer 窗口各自独立触发，各自拥有独立解析出的范围：

```text
[2026-09-13 02:44:51.152] [INFO] SourceExplorerHwnd=1904286
[2026-09-13 02:44:51.170] [INFO] ResolvedPath="C:\Users\ControlxSaria\AppData\Local\Temp\ees-smoke\数据目录 空格"
[2026-09-13 02:44:51.171] [INFO] SearchScope=CurrentDirectoryAndSubdirectories (carried over from the window's folder)
[2026-09-13 02:44:51.172] [INFO] Query="alpha"
```

并且更早时 `hwnd=1709020` 解析为**这台电脑**（`AllVolumes`），`hwnd=2624920` 解析为回收站（`OtherVirtualFolder` → 被拒绝并通知）。一次运行中四个不同窗口、四种不同分类 —— 范围绑定在**触发搜索的窗口**上，而非某个全局的“当前目录”。

**实测**（实时检查）：可复用的 Everything 窗口标题为 `C:\Users\...\Temp\ees-smoke\smokedata\ afterrestart - Everything`，其结果列表是**层级**文件夹视图，在 `smokedata\` 下列出了 `sub` —— 即范围覆盖了该目录及其子目录。（树形视图证明子目录**在范围内**；是否在任何情况下都能穷举所有后代则未做穷尽测试。）

**早先报告、未重测：** 具体以 `...\smokedata` 与 `...\smokedata\sub` 作为两个窗口的那组对照。上面的日志证据使用的是其它目录；机制相同（按 `hwnd` 分别记录），代码中可见。

## 5. Everything 端到端耗时与窗口复用

**实测**（日志）：

```text
[2026-09-13 02:44:51.327] [INFO] Everything window reused
[2026-09-13 02:44:51.327] [INFO] Search completed in 154 ms
```

`154 ms` 是从启动 Everything 启动进程，到定位并提升“显示该查询”的窗口的墙钟耗时（`EverythingBridge.Execute` 的 Stopwatch）。

**实测**（数分钟后的实时检查）：该 Everything 窗口仍然存在并显示该查询，因此在多次搜索之间确实发生了复用，而不是不断堆叠窗口。

**早先报告、未重测：** 多次运行的 114–208 ms 区间。该带宽**未验证**；此处有证据的仅有 154 ms。

## 6. 焦点保留

**实测**（日志）：

```text
[2026-09-13 02:44:51.327] [DEBUG] returning the keyboard focus to the Explorer search box (hwnd=1904286)
[2026-09-13 02:44:51.327] [INFO] Everything window reused
```

焦点恢复分支得以执行，是因为 Everything 确实抢占了前台，而用户自身的输入时刻未变化。同一次运行中还有旁证：`hwnd=1904286` 的搜索框持有焦点期间，用户随后（切换到 `hwnd=2624920`）触发的是 `Enter detection: keyboard hook removed ...` / `focus left the Explorer search box for another window; not treated as Enter` —— 说明后续按键仍然被投递到一个拥有焦点的 Explorer 搜索框，而不是被 Everything 窗口吞掉。

**本轮未重测：** “搜索后用户又输入 → 刻意不还焦点”这一分支（`the user produced input after the search; leaving the keyboard focus alone`）在代码中存在，但未出现在捕获的日志里。

## 7. Everything 命令行语义（查询为何这样构建）

**早先实测，已固化到代码与探针脚本中**（`EverythingQueryBuilder.cs`、`sdk\ipc\everything_ipc.h`、`sdk\include\Everything.h`、`functs.txt`、`cliopt.txt`）：

| 观察 | 代码中的后果 |
|---|---|
| `-path <目录>` 匹配目录本身，**不是**路径子串 | 范围始终用 `-path`（单目录）表达 |
| 把目录路径放进**查询文本**时按*路径子串*匹配 —— 在 `D:\a\inside` 中搜索也命中 `D:\a\inside2` | 路径绝不拼进查询文本 |
| `-path "a;b"` 是**单个 token**，不是并集 | 多目录范围改用 `<ancestor:"a"\|ancestor:"b">` |
| `ancestor:` 是 Everything 1.5 的搜索函数 | 构建器要求版本 ≥ 1.5，否则拒绝 |
| `-s*`（1.5+）把命令行剩余部分当作字面查询 | IPC 版本显示 1.5+ 时用 `-s*`，否则用 `-s "…"` 加 `"""` 转义 |
| `EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8` 虽被 1.5.0.1423b 接受但**没有效果** | 搜索走官方命令行；IPC 只用于版本/数据库状态 |

**撰写本文档时重新验证：** IPC **版本**查询可用（返回 `1.5.0.1423`），`is_db_loaded` / `is_ntfs_drive_indexed` 均有响应 —— 这正是能力探测所依赖的机制。`-path` 与子串之别、以及 `COPYDATA` 无效这两条结论，**本轮未重跑**。

## 8. 不支持的位置会被拒绝，而不是乱猜

**实测**（日志，与第 3 节同一次运行）：

```text
# 回收站 (::{645FF040-...})
[2026-09-13 02:44:56.150] [DEBUG] Shell location hwnd=2624920 kind=OtherVirtualFolder self="::{645FF040-5081-101B-9F08-00AA002F954E}"
[2026-09-13 02:44:56.150] [WARN] search not redirected: UnsupportedShellNamespace detail="回收站" raw="::{645FF040-5081-101B-9F08-00AA002F954E}"

# 这台电脑：来源目录从未被记录 -> 拒绝，而不是“搜遍全盘”
[2026-09-13 02:44:46.173] [DEBUG] Shell location hwnd=1709020 kind=SearchResults self="“此电脑”中的搜索结果&thistpc"
[2026-09-13 02:44:46.175] [WARN] search not redirected: SearchScopeUnknown detail="Explorer is showing a search result view and the folder it was started from is unknown" raw="“此电脑”中的搜索结果&thistpc"
```

两行都会伴有用户可见的通知（`NotificationService.ReportUnsupportedScope`）。

## 9. 对伪 Enter 信号的拒绝

**实测**（日志）—— 这些正是让朴素实现在错误时刻提交的情形：

| 日志行 | 说明 |
|---|---|
| `Explorer only previewed the query in its title after 156 ms ("thistpc - 此电脑 - 文件资源管理器") - not a commit` | 每次按键的标题预览不是提交 |
| `Explorer committed a search after 830 ms ("thistpc - 文件资源管理器") - treated as its own auto search` / `after 844 ms` | Explorer 的自动提交发生在最后一次按键后约 800 ms → `enterCommitWindowMs = 400` 用来与真正的 Enter 区分 |
| `Explorer moved the focus into its own search view input site; not treated as Enter` | Explorer 在输入过程中会自行移动焦点 |
| `focus moved inside Explorer hwnd=... -> ControlType.ListItem ... while the search box was not focused` | 列表/鼠标焦点变化属于噪声 |
| `focus left the Explorer search box for another window; not treated as Enter` | 切换窗口不是 Enter |

## 10. 日志格式

**实测**：`<root>\logs\app.log` 存在，格式与 UTF-8 编码符合文档描述：

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

同样**实测**：该次运行使用了 `--root C:\Users\ControlxSaria\AppData\Local\Temp\ees-smoke-root`，并在该目录下创建了 `config.json` 与 `logs\app.log` —— 即便携布局与 `--root` 重定向按文档工作。该目录中的 `config.json` 也确认了持久化的默认结构（当次为 16 个键；当前 `AppConfig` 多一个 `detectEnterByKeyboardHook`，那次运行的文件早于该字段 —— 磁盘上的旧配置对新键只需沿用默认值）。

## 11. 未验证 / UNVERIFIED

本清单中的一切都是**仅代码**：已实现、已推理，但**未**端到端人工演练过。请勿视为已验证。

| # | 项 | 为何未验证 / 如何验证 |
|---|---|---|
| 1 | **Everything 端到端耗时区间 114–208 ms** | 捕获的日志中只有一个 154 ms 样本。办法：计 N≥10 次搜索，记录 `Search completed in <n> ms`。 |
| 2 | **Enter 全链路约 100–200 ms（按键 → Everything 窗口）** | 捕获的探针测的是 Explorer 结果视图（10–20 ms），不是 Everything 窗口。办法：同一次运行里对按键（探针）与 Everything 标题更新（桥日志）打时间戳。 |
| 3 | **以 `...\smokedata` 与 `...\smokedata\sub` 作为两个窗口的那组对照** | 本次会话日志用的是其它目录。办法：重跑双窗口场景，检查两条 `ResolvedPath`。 |
| 4 | **日志轮转**（`maxLogFileSizeMb` / `maxLogFiles`，`app.1.log` … `app.N.log`） | 捕获的运行中从未产生过轮转文件。办法：设 `maxLogFileSizeMb: 1`、`logLevel: Debug`，反复搜索直到轮转，检查 `logs\`。 |
| 5 | **运行中清理日志**（托盘“清理日志” / 设置按钮） | 本文档作者从未执行过 `AppLogger.ClearLogs`（关闭 → 删除 → 重开）。**仅有间接证据：** 在 02:45 的读取与 02:49:45 之间，`--root` 目录树的 `logs\` 变空且 `config.json` 被重写，这与并行进程执行了一次清理日志相符 —— 但把它归因于该功能属于 **INFERENCE**，不是实测。办法：开启日志后点击，确认 `app.log` 消失（句柄已关闭，删除能成功）并随后出现新行。 |
| 6 | **托盘与设置中的“打开日志目录”** | 代码对目录调用 `Process.Start`；未演练。 |
| 7 | **托盘菜单端到端**（各菜单项、状态文本、气泡、双击） | 日志中只出现图标创建（`notification area icon created`）。 |
| 8 | **开机启动**：写入 HKCU `Run` 值、检测过期项、自动修复、以及设置里的“修复开机启动项”按钮 | 捕获运行中 `startWithWindows: false`，该代码路径从未执行。办法：启用后检查 `HKCU\...\Run\ExplorerEverythingSearch`，再移动 EXE 验证修复日志行。 |
| 9 | **`--startup`、`--settings`、`--exit`、`--help`、`--version`** | 捕获的运行只用了 `--root`。办法：逐个运行并观察文档描述的效果。 |
| 10 | **第二次启动带 `--settings` 能否到达正在运行的实例**（互斥体 + 命名事件） | 未演练。办法：先启动应用，再执行 `ExplorerEverythingSearch.exe --settings`，确认第一个实例弹出对话框。 |
| 11 | **用两个不同 `--root` 得到两个互相独立的实例** | 由 `SingleInstanceGuard` 的哈希后缀推理，未运行。 |
| 12 | **空闲 CPU / 不唤醒的论断** | 由 `MsgWaitForMultipleObjectsEx` + `Timeout.Infinite` 定时器推理；未做测量（例如用 Process Explorer 观察空闲一小时的 CPU 时间）。 |
| 13 | **60 秒自愈重扫确实能修补漏掉的窗口事件** | 捕获日志中没有 `Explorer rescan (periodic)` 行。办法：`logLevel: Debug`，制造一次窗口事件丢失（例如窗口未销毁但钩子未触发），等待周期性重扫日志行。 |
| 14 | **主文件夹（Home）范围 = 已知文件夹并集** | 捕获日志中没有 `SearchScope=KnownFoldersUnion` 行。办法：从主文件夹视图发起搜索，检查日志与最终 `ancestor:` 查询。 |
| 15 | **重定向到非系统盘 / UNC 路径的已知文件夹** | 通过 `SHGetKnownFolderPath` 实现；未用真实重定向测试。 |
| 16 | **Everything 未安装 / 未运行 / 数据库未加载的通知路径** | 测试机上 Everything 始终在运行且数据库已加载。办法：停掉 Everything（或设置一个伪造的 `everythingPath`）后搜索。 |
| 17 | **安全软件拦截 `SetWindowsHookEx`** → 回退到焦点/提交启发式 | 本机钩子安装成功。办法：设 `detectEnterByKeyboardHook: false`，确认 Enter 仍能通过兜底生效（这也是故障排查中记录的规避办法）。 |
| 18 | **Everything 窗口标题尚未反映查询**（`Everything window title does not reflect the query yet`） | 捕获运行中未触发该警告路径。 |
| 19 | **搜索结果视图的来源目录在离开并重新进入后仍能被记住** | 只观察到第 4 节的“范围承载”情形。 |
| 20 | **高 DPI / 多显示器 / 按显示器 DPI 变化** | 清单声明了 `PerMonitorV2`；未在多显示器或混合 DPI 环境下观察行为。 |
| 21 | **在受限机器上以非管理员身份运行** | 清单为 `asInvoker`，测试运行也未提权，但未构造 ACL 受限场景（例如只读安装目录，会触发“配置不可写”通知）。 |
| 22 | **单元测试与 E2E 测试** | `tests\ExplorerEverythingSearch.Tests` **现在已经存在**（xUnit，`AppConfigTests` + `ConfigStoreTests` + `TestSupport` 假实现），但**从未执行过** —— 没有任何测试运行结果，因此目前什么都还没被证明。`tests\ExplorerEverythingSearch.E2E` 仍然**不存在**。办法：运行 `dotnet test tests\ExplorerEverythingSearch.Tests\ExplorerEverythingSearch.Tests.csproj -c Release` 并记录结果。 |
| 23 | **`tools\package.ps1`** | 该文件**现在已经存在**（需要 PowerShell 7+，把 portable 与 framework-dependent 两个 zip 构建到 `artifacts\`，除非加 `-SkipTests` 否则会跑单元测试，并把 README/LICENSE 复制进两个包）。**从未执行过**。办法：运行 `pwsh tools/package.ps1 -SkipTests` 并检查 `artifacts\*.zip`。 |
| 23b | **GitHub 工作流** `.github\workflows\build.yml` 与 `release.yml` | **从未执行过**；它们引用的单元测试工程刚刚才出现，因此第一次 CI 运行才是真正的检验。 |
| 24 | **`IShellBrowser`/`IFolderView` 返回 `E_NOINTERFACE` 这一结果** | 写在 `ExplorerLocationResolver` 类注释中，来自早期探测；撰写本文档时未重新实测。 |
| 25 | **`EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8` 被接受但无效** | 写在 `EverythingIpc` 类注释中，来自早期探测；撰写本文档时未重新实测。 |
| 26 | **`EverythingIpc.IsDatabaseLoaded` / `IsDriveIndexed` 被应用任何路径使用** | 它们对 IPC 有响应（已验证），但应用从不查询 —— 仅供诊断。 |
| 27 | **当 Explorer 搜索框以普通 UIA `Edit` 暴露时的行为**（较旧的 Windows 版本 / 不同的 Shell 布局） | 只测过 Windows 11 build 26200 的形态（`AutoSuggestBox` 宿主）。在 Windows 10 上 `FileExplorerSearchBox` 这个 AutomationId 兜底是否匹配，**UNKNOWN**。 |
| 28 | **`--root` 实例隔离与“正式”实例并发运行时的情况** | 捕获运行确实用了 `--root`，但同时没有正式实例在运行。 |
| 29 | **上述 `app.log` 证据的留存性** | 该文件在 02:45 被读取（8 363 字节 / 82 行），到 02:49:45 已消失，`[root]\logs\` 变空。删除原因 **UNKNOWN**（见本文档开头的说明）；因此这里所有日志片段都是 **STALE** 的证据，需重跑才能重新验证。 |
