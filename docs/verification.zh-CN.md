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

到 **02:49:45** 该文件已不存在：`ees-smoke-root\logs\` 目录仍在但**为空**，且 `ees-smoke-root\config.json` 被重写过（mtime 02:18:37 → 02:48:32）。删除原因事后已查明：本仓库的智能体在验证“日志可关闭 / 日志可清理”这条要求时，对正在运行的实例**故意删除**了日志文件，并顺带重写了 `config.json`。片段仍为 02:45 那次读取的原文照录，因此作为“证据文件”依然是 **STALE** 的——只是原因已明确；**只有重跑下列场景才能真正重新验证它们**。

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
| 3 | ~~以 `...\smokedata` 与 `...\smokedata\sub` 作为两个窗口的那组对照~~ | **已关闭**：测了两次，先是手工（`hwnd=10422348` → `...\smokedata`，`hwnd=1576348` → `...\smokedata\sub`，见 §4），随后由 E2E 的 `multi-window` 场景覆盖（`hwnd=1181104` → `multi-window-a`，`hwnd=2622926` → `multi-window-b`，131/132 ms）。 |
| 4 | **日志轮转**（`maxLogFileSizeMb` / `maxLogFiles`，`app.1.log` … `app.N.log`） | 捕获的运行中从未产生过轮转文件。办法：设 `maxLogFileSizeMb: 1`、`logLevel: Debug`，反复搜索直到轮转，检查 `logs\`。 |
| 5 | ~~运行中清理日志~~ | **已关闭**：E2E 的 `log-clear` 报告 `ClearLogs` **5/5 轮成功**，且每轮之后日志继续写入；单元测试在 logger 层面也断言了同样行为。过程中发现并修复了一个真实缺陷：交还“文件已关闭”用的是 `Set()`/`Reset()` 脉冲，调用方可能错过，导致清理失败并让调用线程阻塞 5 秒（见 §12.4）。 |
| 6 | **托盘与设置中的“打开日志目录”** | 代码对目录调用 `Process.Start`；未演练。 |
| 7 | **托盘菜单端到端**（各菜单项、状态文本、气泡、双击） | 日志中只出现图标创建（`notification area icon created`）。 |
| 8 | ~~**开机启动**：写入 HKCU `Run` 值、检测过期项、自动修复、以及设置里的“修复开机启动项”按钮~~ | **注册的四个分支已关闭**：用打包 EXE 加 `--root` 对真实的 `HKCU\...\Run\ExplorerEverythingSearch` 演练，创建 / 修复（过期值指向 `C:\gone\...`）/ 保留 / 移除四条分支都在注册表与 `app.log` 中得到确认——见 §12.7。设置里那个按钮（同一代码的第二入口）未点击。 |
| 9 | **`--startup`、`--settings`、`--exit`、`--help`、`--version`** | `--startup` **已验证**（§12.7），`--exit` 每轮 E2E 都会用到。`--settings`、`--help`、`--version` 仍未验证：后两者会弹出消息框，控制台 harness 无法驱动或断言。 |
| 10 | **第二次启动带 `--settings` 能否到达正在运行的实例**（互斥体 + 命名事件） | 未演练。办法：先启动应用，再执行 `ExplorerEverythingSearch.exe --settings`，确认第一个实例弹出对话框。 |
| 11 | **用两个不同 `--root` 得到两个互相独立的实例** | 由 `SingleInstanceGuard` 的哈希后缀推理，未运行。 |
| 12 | ~~空闲 CPU / 不唤醒的论断~~ | **已关闭（实测）**：开两个 Explorer 窗口、工具空闲 12 秒，CPU 时间为 **15.6 ms**（24 核机器，`TotalProcessorTime` 差值，约 **0.005%** 单核），26 个线程。 |
| 13 | ~~60 秒自愈重扫确实能修补漏掉的窗口事件~~ | **部分关闭**：在 Explorer 重启那次运行中观察到 `Explorer rescan (periodic): windows=1 searchBoxes=1`，E2E 的 `explorer-restart` 场景也复现了恢复过程；但“重扫修补一次**被漏掉**的窗口事件”仍未被刻意构造出来。 |
| 14 | ~~主文件夹（Home）范围 = 已知文件夹并集~~ | **已关闭**：`SearchScope=KnownFoldersUnion` 且包含六个目录（`D:\ASUS\Desktop … D:\ASUS\Music`，即已重定向的已知文件夹）与对应的 `ancestor:` 并集查询；E2E 的 `home` 场景也覆盖了它。 |
| 15 | **重定向到非系统盘 / UNC 路径的已知文件夹** | 通过 `SHGetKnownFolderPath` 实现；未用真实重定向测试。 |
| 16 | **Everything 未安装 / 未运行 / 数据库未加载的通知路径** | 测试机上 Everything 始终在运行且数据库已加载。办法：停掉 Everything（或设置一个伪造的 `everythingPath`）后搜索。 |
| 17 | **安全软件拦截 `SetWindowsHookEx`** → 回退到焦点/提交启发式 | 本机钩子安装成功。办法：设 `detectEnterByKeyboardHook: false`，确认 Enter 仍能通过兜底生效（这也是故障排查中记录的规避办法）。 |
| 18 | **Everything 窗口标题尚未反映查询**（`Everything window title does not reflect the query yet`） | 捕获运行中未触发该警告路径。 |
| 19 | **搜索结果视图的来源目录在离开并重新进入后仍能被记住** | 只观察到第 4 节的“范围承载”情形。 |
| 20 | **高 DPI / 多显示器 / 按显示器 DPI 变化** | 清单声明了 `PerMonitorV2`；未在多显示器或混合 DPI 环境下观察行为。 |
| 21 | **在受限机器上以非管理员身份运行** | 清单为 `asInvoker`，测试运行也未提权，但未构造 ACL 受限场景（例如只读安装目录，会触发“配置不可写”通知）。 |
| 22 | ~~单元测试与 E2E 测试~~ | **已关闭**：`dotnet test tests\ExplorerEverythingSearch.Tests\… -c Release` → **237 个测试，236 通过，1 跳过**，约 4 秒（跳过那个会写 `HKCU\…\Run`，留待手工运行）；`tests\ExplorerEverythingSearch.E2E --scenario all` → **13/13 通过，72.9 秒**，健康会话跑了两次，退化会话见 §12.5。 |
| 23 | ~~`tools\package.ps1`~~ | **已关闭**：执行过两次（其中一次包含单元测试）；产出 `ExplorerEverythingSearch-1.0.0-win-x64-portable.zip`（57.7 MB：单个自包含 EXE + README/LICENSE）与 `…-win-x64-framework-dependent.zip`（0.2 MB）。portable 包被解到临时目录并从中启动，用于验证“发布出来的二进制”（§12.5）。 |
| 23b | **GitHub 工作流** `.github\workflows\build.yml` 与 `release.yml` | 仍然**从未执行过**（开发机到 github.com 无网络，未推送）。已人工审查其 YAML 并手工执行了各步骤；审查中发现一个真实缺陷：`ExplorerEverythingSearch.sln` 里原本只有两个测试工程，CI 会在**完全不编译产品**的情况下变绿——已用 `dotnet sln add` 加入 `Core`/`App`，现在 `dotnet build ExplorerEverythingSearch.sln` 会构建全部四个工程。 |
| 24 | **`IShellBrowser`/`IFolderView` 返回 `E_NOINTERFACE` 这一结果** | 写在 `ExplorerLocationResolver` 类注释中，来自早期探测；撰写本文档时未重新实测。 |
| 25 | **`EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8` 被接受但无效** | 写在 `EverythingIpc` 类注释中，来自早期探测；撰写本文档时未重新实测。 |
| 26 | **`EverythingIpc.IsDatabaseLoaded` / `IsDriveIndexed` 被应用任何路径使用** | 它们对 IPC 有响应（已验证），但应用从不查询 —— 仅供诊断。 |
| 27 | **当 Explorer 搜索框以普通 UIA `Edit` 暴露时的行为**（较旧的 Windows 版本 / 不同的 Shell 布局） | 只测过 Windows 11 build 26200 的形态（`AutoSuggestBox` 宿主）。在 Windows 10 上 `FileExplorerSearchBox` 这个 AutomationId 兜底是否匹配，**UNKNOWN**。 |
| 28 | **`--root` 实例隔离与“正式”实例并发运行时的情况** | 捕获运行确实用了 `--root`，但同时没有正式实例在运行。 |
| 29 | ~~上述 `app.log` 证据的留存性~~ | **已解释**：删除是“日志可关闭 / 日志可清理”验证中对运行实例的一步故意操作（见本文档开头说明）。片段仍是 **STALE** 证据，但原因已知；§12 用新日志复现了当前行为。 |

## 12. 如何运行 E2E 校验工程

`tests\ExplorerEverythingSearch.E2E`（2026-09-13 新增）是一个控制台程序而不是 `dotnet test` 工程，因为每个场景都要驱动**真实**的 Explorer 搜索框并检查**真实**的 Everything 窗口。它刻意**不**放进 `.github\workflows\build.yml`：CI 代理没有交互式桌面会话，也没有 Everything 安装。

```powershell
# 全量（会先构建被测程序）
dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --scenario all

# 单个场景，保留工作目录以便查看
dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --scenario basic-idle --keep-artifacts

# 列出全部场景
dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --list
```

| 参数 | 含义 |
|---|---|
| `--scenario <name>\|all` | 要运行的场景，默认 `all` |
| `--root <directory>` | 指定工作根目录（默认 `%TEMP%\ees-e2e-<8 位十六进制>`） |
| `--keep-artifacts` | 保留工作根目录（`config.json`、`logs\`、测试文件夹） |
| `--no-build` | 不重新构建，直接使用现有被测程序 |

一次运行做的事：构建 `ExplorerEverythingSearch.App`（Debug）→ 创建临时根目录 → 写入 `config.json`（`enabled=true`、`autoSearchDelay=1000`、`startWithWindows=false`、`showNotifications=false`、`logLevel=Debug`、`loggingEnabled=true`、`reuseEverythingWindow=true`）→ 以 `ExplorerEverythingSearch.exe --root <root>` 启动 → 依次运行场景 → 停掉**它自己启动**的实例（先 `--exit`，必要时再强杀）、只关闭**它自己打开**的 Explorer 窗口（HWND 差集 + `WM_CLOSE`）并删除临时根目录。退出码：`0` 全部通过，`1` 至少一项失败，`2` 参数错误。

场景失败时会打印失败原因、被测程序日志的最后若干行；若被测进程已崩溃，还会打印其标准错误尾部（`<root>\app\stderr.log`）——12.3 的崩溃就是这样被定位的。

前置条件：交互式桌面会话 + 已安装并运行 Everything。窗口标题会同时匹配中英文 shell 名称（`此电脑`/`This PC`、`主文件夹`/`Home`、`回收站`/`Recycle Bin`），且新窗口靠 HWND 差集定位，正常流程不依赖标题文本。

### 12.1 实测结果（2026-09-13，Windows 11 26200，Everything 1.5.0.1423b，.NET SDK 8.0.425）

`--scenario all` → **13 通过 / 0 失败，77.7 秒，退出码 0**（在 12.3 的修复之后；全量共重跑三次，最容易崩溃的 `multi-window` 另外单独重跑三次）。

| 场景 | 结果 | 耗时 | 本次运行的关键证据 |
|---|---|---|---|
| `basic-idle` | PASS | 3.9 s | `trigger=IdleTimeout text="e2ealphatoken" hwnd=722350 path="...\data\basic-idle" scope=CurrentDirectoryAndSubdirectories query="e2ealphatoken" window=reused latency=149ms`；Everything 标题 `...\data\basic-idle\ e2ealphatoken - Everything` |
| `enter-immediate` | PASS | 2.5 s | `Enter -> submit visible after 240 ms; trigger=Enter text="e2eenterprobe" ... query="e2eenterprobe"`（远早于 1000 ms 空闲窗口，确属 Enter 路径） |
| `continuous` | PASS | 4.2 s | 先 `trigger=IdleTimeout text="e2econtone"`，随后**不重新点击**搜索框即 `trigger=Enter text="e2econtonextra"`，且 `hwnd` 相同 → 焦点确实可用 |
| `this-pc` | PASS | 3.4 s | `scope=AllVolumes query="e2evolumescan"`，查询中既无 `-path` 也无 `ancestor:` |
| `home` | PASS | 3.7 s | `scope=KnownFoldersUnion`，`path="D:\ASUS\Desktop \| D:\ASUS\Documents \| D:\ASUS\Downloads \| D:\ASUS\Pictures \| D:\ASUS\Videos \| D:\ASUS\Music"`，查询为 `<ancestor:D:\ASUS\Desktop\|…\|ancestor:D:\ASUS\Music>` |
| `multi-window` | PASS | 7.3 s | 窗口 A `hwnd=1181104 path="...\multi-window-a"`，窗口 B `hwnd=2622926 path="...\multi-window-b"`，耗时 131/132 ms——无交叉污染 |
| `explorer-restart` | PASS | 12.8 s | `taskkill /F /IM explorer.exe` 之后出现 `[INFO] Explorer restarted: monitoring re-established`，随后新窗口搜索成功 |
| `everything-closed` | PASS | 4.1 s | 关闭全部 Everything 搜索窗口后：`window=created` |
| `window-reuse` | PASS | 5.4 s | 第一次 `window=reused`，第二次（新查询）仍 `window=reused`，`query="e2ereusetwo"` |
| `unsupported-namespace` | PASS | 4.9 s | `[WARN] search not redirected: UnsupportedShellNamespace detail="回收站" raw="::{645FF040-5081-101B-9F08-00AA002F954E}"`，且没有该文本的 `Query=` 行、Everything 标题也不含该文本 |
| `unicode-path` | PASS | 3.4 s | `path="...\data\数据 目录" text="文件alpha" query="文件alpha"`（Unicode 按键与 UTF-8 日志往返正常） |
| `logging-disabled` | PASS | 15.6 s | `loggingEnabled=false` 时**完全没有** `logs\app.log`，而 Everything 标题仍显示 `e2enologscan` |
| `log-clear` | PASS | 6.7 s | `ClearLogs succeeded in 5/5 rounds`，每轮之后仍能继续写日志 |

### 12.2 原始证据片段

```
Enter -> submit visible after 240 ms; trigger=Enter text="e2eenterprobe" hwnd=2166756
        path="...\data\enter-immediate" scope=CurrentDirectoryAndSubdirectories query="e2eenterprobe" window=reused latency=137ms

first:  trigger=IdleTimeout text="e2econtone"     hwnd=853270 path="...\data\continuous" ...
second: trigger=Enter       text="e2econtonextra" hwnd=853270 path="...\data\continuous" ...
```

### 12.3 E2E 发现并已修复的缺陷

运行过程中，**被测进程被 .NET 运行时直接终止**：

```
Process terminated. A callback was made on a garbage collected delegate of type
'ExplorerEverythingSearch.Core!…NativeMethods+WinEventDelegate::Invoke'.
Repeat 2 times:
   at …NativeMethods.PeekMessage(MSG ByRef, IntPtr, UInt32, UInt32, UInt32)
   at …StaDispatcher.PumpMessages()
   at …StaDispatcher.Run()
```

`ExplorerWindowMonitor.InstallHooks()` 当时把 `WinEventDelegate` 建成了**局部变量**，只保留了返回的 hook 句柄，因此该委托可能被 GC 回收，下一次 Explorer 事件就会调用已释放的委托——正常使用中会**随机杀掉整个进程**，`multi-window` 场景正是撞上了它（超时、无任何提交）。现已改为字段驻留（`_winEventHandler`）；修复后 `multi-window` 连续 4 次全部通过。

### 12.4 E2E 未能证明的部分（局限）

- `log-clear` 在这里每次都 5/5 通过（累计 15 轮），但单元测试工程在自己的环境里报告 `ClearLogs` **3/3 失败**（`AppLogger.RequestMaintenance` 的一次性脉冲竞态）。E2E 路径复现不出该竞态，因此当时**不能**据此排除该问题——单元测试的证据是对的：`RequestMaintenance` 用 `Set()` 紧接 `Reset()` 交还控制权，调用方可能错过脉冲，随后一直等到 5 秒超时。于是只要 logger 线程赢下竞态，清理日志就失败（且调用它的 UI 线程会卡住）。现在改为每次请求新建一次性 `ManualResetEventSlim`；原先被跳过的两个单元测试已启用并通过，`log-clear` 依旧 5/5。
- `explorer-restart` 场景会关闭当前会话的**所有** Explorer 窗口（任务书允许）。
- 未覆盖：多显示器/混合 DPI、托盘菜单项与气泡、`--settings`（`--exit` 用于停掉自己启动的实例）、`--help`/`--version`（会弹消息框，控制台 harness 无法驱动）、日志轮转、Everything 未安装/数据库未加载的通知路径、以及安全软件拦截键盘钩子的情形。`--startup` **现已覆盖**，见 §12.7。

### 12.5 会话 Shell 处于退化状态时暴露的缺陷（已修，含回归证据）

在会话里多次强杀 `explorer.exe` 之后（`explorer-restart` 场景每轮会做一次）复跑，13 个场景中 12 个变红，且原因**完全同一个**——每次提交都变成

```
[WARN] search not redirected: LocationUnavailable detail="RuntimeBinderException: Cannot perform runtime binding on a null reference" raw=""
```

于是所有 `ResolvedPath`/`Scope` 都为空，**任何**搜索都不再跳转，连本轮第一个场景也一样。应用本身没有异常：输入、Enter、空闲提交都照常识别并提交，而且**显式报了错**（没有静默失败）。应用二进制未变（`src` 除了 12.3 的 hook 修复外是干净的），所以这不是 harness 的回归。

在真实 Explorer 窗口打开时做的独立验证：

```
CabinetWClass hwnds: 2296904
ShellWindows Count = 2
  [0] <NULL ITEM>                    <- C# dynamic 读 .HWND 必抛 RuntimeBinderException
  [1] HWND=2296904 URL='file:///D:/SearchOptimization' Name='SearchOptimization'
```

根因在 `src\ExplorerEverythingSearch.Core\Shell\ExplorerLocationResolver.cs` 的 `TryResolve`：它遍历 `Shell.Application.Windows()` 的每一项并读 `(int)window.HWND`，而唯一的 `catch` 在**循环之外**。被强杀的 Explorer 进程会留下 `ShellWindows` 的**僵尸 NULL 项**，只要存在一项，第一轮循环就抛异常并中断整轮枚举，后面那个索引上的真实窗口永远读不到，于是**全部** Explorer 窗口都报 `LocationUnavailable`。同一文件的 `Enumerate()` 本来就是逐项 try/catch，只有 `TryResolve` 有这个单点全灭问题。实际影响：只要存在一个僵尸项，整个会话的搜索就都不再跳转——这正好落在"必须支持 Explorer 重启后恢复"这条核心要求上，因为该状态恰恰是强杀/重启 Explorer 造成的。僵尸项随每次强杀**累积**（排查过程中从 1 个涨到 3 个），并且重启 `explorer.exe`、重启 `RuntimeBroker`/`dllhost` 都清不掉（INFERENCE：需要注销或重启 Windows）。

对上面结论的影响：13/13 那几次是在**健康 Shell** 下取得的；在退化会话里同一二进制失败 12/13（父智能体独立复现：2/13，退出码 1），原因即上述缺陷。

**修复（2026-09-13）。** `TryResolve` 现在逐项处理 `ShellWindows` 的每个条目：读取 `Item(i)` 与该条目的 `HWND` 都在 `try`/`catch` 内，`null` 项被识别为僵尸注册并跳过，同时输出 `Debug` 日志（`ShellWindows[i] is a stale entry left behind by a killed Explorer process; skipped`），查找目标窗口的过程继续。`Enumerate()` 也做了同样加固。日志器由 `AppRoot` 传入，因此原因在 `app.log` 里可见——当初正因为没有这行日志，定位才花费了额外时间。

**回归证据（同一个退化会话，期间未做任何恢复）：** 会话中仍是 `ShellWindows Count = 3`，且 **3 项全为 null**（运行前后各确认一次），而

```
dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --scenario all
  passed 13, failed 0, total 72.9s     (exit code 0)
```

修复后的二进制会记录被跳过的项，随后正常解析：

```
[DEBUG] ShellWindows[0] is a stale entry left behind by a killed Explorer process; skipped
[DEBUG] ShellWindows[1] is a stale entry left behind by a killed Explorer process; skipped
[DEBUG] ShellWindows[2] is a stale entry left behind by a killed Explorer process; skipped
[INFO] ResolvedPath="C:\Users\…\ees-release2-…\data"
[INFO] SearchScope=CurrentDirectoryAndSubdirectories (carried over from the window's folder)
[INFO] Everything window created
[INFO] Search completed in 203 ms
```

最后一段来自**打包后的发布二进制**（`artifacts\ExplorerEverythingSearch-1.0.0-win-x64-portable.zip` 解到临时目录并用 `--root` 启动），即修复后重新打包的 EXE 也在退化 Shell 上重新验证过；修复前打的那个包则复现了故障。

### 12.6 harness 加固

`LogTail.WaitForSubmit` 现在接受可选的来源窗口句柄，每个场景都会传入自己驱动的那个窗口，因此上一轮残留窗口的提交不会再被算到当前场景头上。加固前 `continuous` 就曾匹配到 `hwnd=4984110` 的提交（其路径属于上一轮的 root），而该场景实际驱动的是 `hwnd=5050160`。

### 12.7 开机启动（真实注册表，`--startup`）

`StartupRegistration` 这条路径是对**真实**的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 值
`ExplorerEverythingSearch` 演练的（托盘开关与 `--startup` 命令行都走同一条路径），用的是**打包后的 EXE**：解到临时
目录、加 `--root <临时目录>` 启动，使行为由工具自己的 `config.json` 决定。四条分支都观察到了：

| 分支 | 构造条件 | 观察结果 |
|---|---|---|
| 创建 | 值不存在，`startWithWindows=true` | `[INFO] start with Windows entry created: "<exe>" --startup`；注册表值出现且指向该 EXE（已校验：解析出的路径等于 EXE 路径） |
| 修复 | 值存在但指向 `"C:\gone\ExplorerEverythingSearch.exe" --startup` | `[INFO] start with Windows entry repaired to "<exe>" --startup (the previous entry pointed elsewhere)` |
| 保留 | 值存在且已正确 | `[INFO] start with Windows entry is valid`（未重写该值） |
| 移除 | 值存在，`startWithWindows=false` | `[INFO] start with Windows entry removed`；之后注册表值**不存在** |

创建/移除那一轮的原始输出（`$env:TEMP\ees-startup-90d4ee`，打包 EXE 位于 `<root>\app`）：

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

方法论上有两点必须写下来，因为它们正是重复此验证时容易出错的地方：

- 工具读取的配置文件是 `<root>\config.json`（`--root` 指定的目录；不指定时是 EXE 所在目录）。从**已被删除**的 root
  启动的实例会静默重建一份默认配置，其中 `startWithWindows` 为 `true`；本验证第一次做“移除”步骤时正是如此，
  结果得到的是 `start with Windows entry is valid` 而不是移除。因此在得出“移除没生效”的结论之前，先确认配置文件存在。
- 删除临时 root 之前必须先停掉所有 `ExplorerEverythingSearch.exe` 进程（文件被占用时删除会留下半删的目录树），
  并在事后重新读取注册表值——断言的对象是注册表，不是日志。

本机在该测试前没有这个 `Run` 值，测试后也回到没有；该测试没有改动任何 Explorer 状态、服务或用户设置。
