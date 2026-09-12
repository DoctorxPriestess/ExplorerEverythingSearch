# 架构与关键机制

[简体中文](architecture.zh-CN.md) | [English](architecture.md) · [返回 README](../README.zh-CN.md)

本文说明 Explorer Everything Search 的内部实现：分层结构、线程模型、事件驱动监控设计、Enter 检测的三重机制、空闲自动提交、搜索上下文如何从 Explorer 提取、Everything 集成方式，以及日志、配置与错误处理策略。

下文每条论断都注明对应的源文件。真实机器上实测过的写成 **实测**；由代码推导、但未人工演练的写成 **未人工验证**。

## 1. 分层结构

```
ExplorerEverythingSearch.sln
├── src\ExplorerEverythingSearch.Core      (net8.0-windows，无 UI、无入口点)
│   ├── Configuration\  AppConfig, ConfigStore
│   ├── Diagnostics\    AppLogger, LogLevel/LogLevels
│   ├── Everything\     EverythingLocator, EverythingIpc, EverythingBridge, EsCliClient
│   ├── Explorer\       ExplorerWindowMonitor, SearchBoxWatcher, ExplorerSearchSession
│   ├── Interop\        NativeMethods（全部 Win32 P/Invoke 集中于此）
│   ├── Search\         SearchModel, SearchDebouncer, SearchCoordinator, EverythingQueryBuilder
│   ├── Shell\          ExplorerLocationResolver, ShellLocationClassifier, SearchScopeResolver,
│   │                   WindowScopeTracker, KnownFolderResolver
│   ├── Startup\        StartupRegistration（+ IStartupRegistry / HkcuRunRegistry）
│   └── Threading\      StaDispatcher
├── src\ExplorerEverythingSearch.App       (WinExe；程序集名 ExplorerEverythingSearch)
│   ├── App.xaml.cs     入口、命令行、单实例判定
│   ├── AppRoot.cs      组合根 / 应用控制器
│   ├── AppPaths.cs     根目录解析（EXE 目录或 --root）
│   ├── SingleInstanceGuard.cs
│   ├── CommandLineOptions.cs
│   ├── Tray\           TrayIconController, TrayIconFactory
│   ├── Views\          SettingsWindow（WPF，纯 code-behind）
│   ├── Notifications\  NotificationService
│   └── Localization\   Strings（内置 en-US + zh-CN）
├── tests\ExplorerEverythingSearch.Tests   单元测试（xUnit）；E2E 工程仍缺失
└── tools\probes\ExplorerProbe             开发者/诊断探针，不随发布
```

- `Core` 引用 WPF **仅**为了 `System.Windows.Automation`；它没有 UI，也没有入口点（`ExplorerEverythingSearch.Core.csproj`）。
- `Core` 向测试程序集暴露内部成员：`InternalsVisibleTo("ExplorerEverythingSearch.Tests")` 与 `...("ExplorerEverythingSearch.E2E")`。单元测试工程已存在（`tests\ExplorerEverythingSearch.Tests`，xUnit）；E2E 工程**尚不存在**。
- `App` 是仅托盘常驻的 WPF 应用，同时为托盘图标使用 WinForms `NotifyIcon`；它移除了隐式的 `System.Windows.Forms` using，以保证 `Application`/`MessageBox` 仍解析到 WPF。
- 组合只发生在一个地方 —— `AppRoot`（`AppRoot.cs`）：配置存储 → 日志 → STA 派发器 → 位置解析器 → 范围解析器 → Everything 定位器/桥 → 协调器 → 通知服务 → 监控器 → 启动项注册 → 托盘。

## 2. 线程模型

| 线程 | 创建位置 | 职责 | 空闲行为 |
|---|---|---|---|
| WPF UI 线程 | `App` | 托盘图标、设置窗口、气泡/消息框、应用生命周期 | 常规 WPF 消息循环 |
| `EES-STA` | `StaDispatcher` | UI Automation 调用与事件、Shell（`Shell.Application`）自动化对象、WinEvent 回调、低级键盘钩子回调 | **阻塞在 `MsgWaitForMultipleObjectsEx`**，直到有工作入队*或*有窗口消息到达 |
| `EES-SearchWorker` | `SearchCoordinator` | 消费提交的搜索，在 STA 线程解析范围，构建并执行 Everything 查询 | 阻塞在 `BlockingCollection.GetConsumingEnumerable()` |
| `EES-LogWriter` | `AppLogger` | 批量写入并 flush 日志行 | 阻塞在 `SemaphoreSlim` |
| `EES-SingleInstance` | `SingleInstanceGuard` | 等待 `--settings` / `--exit` 信号 | 阻塞在 `WaitHandle.WaitAny` |

### 为什么需要一个专用 STA 线程（`StaDispatcher.cs`）

本工具依赖的两个子系统都要求如此：

- **UI Automation 只向泵消息的线程投递事件。** 因此 `Automation.AddAutomationPropertyChangedEventHandler` / `AddAutomationEventHandler` / `AddAutomationFocusChangedEventHandler` 的订阅者都活在 `EES-STA` 上。
- **Shell 自动化对象模型（`Shell.Application`、`ShellWindows`）是单元线程（apartment threaded）的**：必须在同一个 STA 线程上创建和使用。

`StaDispatcher` 严格事件驱动 —— 不轮询、不自旋：

```csharp
waitResult = NativeMethods.MsgWaitForMultipleObjectsEx(
    1, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
```

- `WAIT_OBJECT_0` → 有新工作入队：排空队列（循环开头已排空一次），并丢弃多余的信号量计数。
- `WAIT_OBJECT_0 + 1` 或 `WAIT_TIMEOUT` → 有待处理窗口消息：用 `PeekMessage`/`TranslateMessage`/`DispatchMessage` 派发到队列为空。
- 其它情况（`WAIT_FAILED`、被放弃）→ `Thread.Sleep(1)`，保证线程绝不空转。

`Invoke<T>` 在该线程上执行并等待结果（默认 30 s；协调器解析范围时传 15 s）；`Post` 是即发即忘，WinEvent 与 UI Automation 回调都用它，从而绝不阻塞 Explorer 的回调线程。`Dispose` 会 `PostThreadMessage(WM_QUIT)`、释放信号量并以 2 s 超时 join。

**空闲开销：** 没有 Explorer 活动、没有排队工作、没有窗口消息时，四个工作线程全部阻塞在内核等待上。监控器的定时器在无事可做时一律以 `Timeout.Infinite` 装配（`ScheduleNextTick`）。

## 3. 事件驱动监控

`ExplorerWindowMonitor`（`Core\Explorer\ExplorerWindowMonitor.cs`）为每个 Explorer 窗口持有一个 `SearchBoxWatcher` 和一个 `ExplorerSearchSession`。

**窗口发现 —— WinEvent 钩子（out-of-context，`WINEVENT_SKIPOWNPROCESS`）：**

```csharp
(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE)                 // 窗口出现/消失
(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE)       // 标题变化（搜索提交信号）
```

回调运行在 `EES-STA` 上。类名为 `CabinetWClass` 的 `EVENT_OBJECT_CREATE`/`SHOW` 添加窗口；`DESTROY`/`HIDE` 移除窗口；已跟踪窗口的 `NAMECHANGE` 送入提交启发式，并在搜索框为空时刷新记忆的目录。

**搜索框 —— UI Automation 事件**（`SearchBoxWatcher.Attach`）：搜索框自身的 `ValuePattern.ValueProperty` 与 `TextPattern.TextChangedEvent`（这是每次按键的触发源），外加窗口上的 `AutomationElement.NameProperty`（提交/标题信号）。订阅可选的 TextPattern 或标题处理器失败时只记 `Debug`，不致命。

**低频自愈重扫：** 一个自调度的 `System.Threading.Timer`（`Tick`）执行一次 `FindTopLevelWindows("CabinetWClass")` 扫描，重新附加搜索框尚未就绪的窗口，并清理已失效的句柄。间隔由 `explorerRescanSeconds` 决定（默认 **60 秒**，夹紧到 0–3600，`0` = 从不）。同一个定时器兼任附加重试调度器（指数退避 300 ms × 2^attempts，上限 30 s）。无事到期时定时器被设为 `Timeout.Infinite`，完全不会唤醒。

**Explorer 重启**是隐式处理的：最后一个被跟踪窗口消失时置 `_allClosedSince`，之后发现的第一个窗口会在上一条 `Explorer detected hwnd=...` 之后打印 `Explorer restarted: monitoring re-established`。

**搜索框查找**（`SearchBoxWatcher.FindSearchBox`）刻意保守：

1. `AutomationId = FileExplorerSearchBox` 的元素，再取其后代 `Edit`；
2. 否则取任意 `AutoSuggestBox` 内部的 `Edit`，但 `AutomationId` **不能**是 `PART_AutoSuggestBox`（地址栏）；
3. 绝不使用“窗口里第一个 `Edit`” —— 详细信息视图里存在用于列值的 `Edit` 控件。

形状校验（`IsSearchBoxElementShape`）确认找到的元素确实是某个非地址栏 `AutoSuggestBox` 宿主下的 `Edit`，因此列编辑器永远不会被订阅。失效的订阅（元素消失）会被标记，并在下次重扫时重新附加。

## 4. Enter 检测 —— 三重机制

Explorer 不提供直接的“用户按了 Enter”通知，而它自己又会在用户停止输入时自动搜索。因此使用三重独立机制，优先级如下（对应 `AppConfig.DetectEnterByKeyboardHook`、`DetectEnterByFocusChange`、`DetectEnterByCommitTiming`）。

### 4.1 首选：仅在搜索框获得焦点时安装的低级键盘钩子

`ExplorerWindowMonitor.InstallKeyboardHook` 按需安装 `WH_KEYBOARD_LL`：

- `Automation.AddAutomationFocusChangedEventHandler` 报告焦点进入搜索框（`SearchBoxWatcher.IsSearchBoxElement`）→ 安装钩子。
- 焦点离开搜索框，或订阅失效 → `UninstallKeyboardHook`。

回调只检查 `WM_KEYDOWN`/`WM_SYSKEYDOWN` 且 `vkCode == VK_RETURN`，读取记住的搜索框句柄（`_focusedSearchBoxHwnd`），然后调用 `ExplorerSearchSession.OnEnterDetected`。回调投递在 `EES-STA` 上，只做最小工作（一次 `Interlocked.Read`、一次字典查找），并且始终返回 `CallNextHookEx`，因此绝不会从 Explorer 那里吞掉按键。

这一设计带来三个有意为之的结果：

- 没有 Explorer 搜索框获得焦点时，工具**完全不观察键盘输入**；
- Enter **即使搜不到任何结果**也有效，也不依赖窗口标题变化；
- 实测：在获得焦点的搜索框里按 Enter，10–20 ms 后进入 Explorer 搜索结果视图，且提交的文本包含紧邻 Enter 之前输入的那个字符。到 Everything 窗口显示查询的全链路耗时早先报告为约 100–200 ms，但**未在同一轮中测得** —— 见 [verification.zh-CN.md](verification.zh-CN.md#11-未验证--unverified)。

若 `SetWindowsHookEx` 失败（安全软件、策略、特殊桌面），会记一条警告，并由下面两个兜底机制接管。

### 4.2 次选：焦点从搜索框离开、进入结果区

`OnFocusChanged` 把“键盘焦点离开搜索框并移动到**同一窗口**的其它元素”视为 Enter，并带有以下守卫：

- 焦点移动到 Explorer 自己的搜索视图输入站点（`ControlType.Pane` + `ClassName = InputSiteWindowClass`）时明确**不算** Enter —— Explorer 在输入过程中会自行把焦点移进去（实测最快在按键后约 140 ms），而且从那里打字依然能到达搜索框；
- 焦点移动到**其它窗口**时不算 Enter；
- 焦点变化时若鼠标按键处于按下状态（`GetAsyncKeyState`）不算 Enter；
- 距上一次全系统输入（`GetLastInputInfo`）超过 `EnterInputFreshnessMs`（**500 ms**）的焦点变化不算 Enter —— 因为 Explorer 自身的视图刷新也会在没有按键的情况下把焦点移出搜索框。

### 4.3 兜底：提交时序

`ExplorerSearchSession.OnExplorerCommitDetected` 针对每次窗口标题 / UIA 名称变化执行，并**同时**要求：

1. `detectEnterByCommitTiming` 开启；
2. 搜索框非空；
3. 新标题以搜索框文本为前缀（重新显示的旧查询不满足）；
4. 变化发生在最后一次按键后的 `enterCommitWindowMs`（默认 **400 ms**）内 —— Explorer 自身的自动提交发生在最后一次按键后约 800 ms，这正是 400 ms 用来区分两者的原因；
5. 窗口确实处于**搜索结果视图**：通过 Shell 重新解析并分类（`ShellLocationKind.SearchResults`）。用户输入过程中 Explorer 显示的 `查询 - 目录` 标题预览不满足该条件。

重复上报会被抑制：400 ms 内相同的提交详情被忽略，`OnEnterDetected` 也会忽略 400 ms 内的第二次 Enter 上报。4.1 与 4.2 会看到同一次按键，因此这一去重是必需的。

## 5. 空闲自动提交（`autoSearchDelay`）

每次按键都会经 `SearchBoxWatcher.ReportTextChange` 到达 `ExplorerSearchSession.OnTextChanged`，并以 `autoSearchDelay`（默认 **1000 ms**）重启 `SearchDebouncer` 里的单次 `System.Threading.Timer`。

- 每次文本变化都重新读取配置，因此设置改动对下一次按键即生效。
- 搜索框为空或仅空白时取消待触发定时器（`Cancel`）。
- Enter（`SubmitNow`）取消待触发定时器并立即提交。
- `SubmitNow` 即使没有待触发定时器也一定提交（连按两次 Enter）；被取消的定时器不可能为旧文本触发：`OnTimer` 先在同一把锁内清掉 `_pending`，再回调。
- **实测：** `autoSearchDelay = 1000` 时，从最后一次按键到 `Search submitted trigger=IdleTimeout` 日志行的时间为 997–1005 ms。

提交时刻前后还有两道保险：

- **实时重读。** `CurrentTextAtSubmit` 在提交时重新读取搜索框真实值（优先 ValuePattern，其次 TextPattern）。Explorer 在离开搜索视图时会自行清空搜索框，而该通知不一定会到达，因此跟踪到的文本只是后备；若实时值与跟踪值不同会记录 `search box text changed without a notification`。
- **按窗口的新旧覆盖。** `SearchCoordinator` 为每个窗口的每次提交打上单调递增序号，同一窗口有更新请求时丢弃排队中的旧项（`search superseded hwnd=... text=...`）。完全相同（同窗口、同范围、同文本）的重复提交会被跳过。

## 6. 搜索上下文提取（以及为什么不能用 `IShellBrowser`/`IFolderView`）

`ShellAutomationLocationResolver`（`Core\Shell\ExplorerLocationResolver.cs`）针对一个 `HWND` 读取：

```
Shell.Application → .Windows() → 满足 .HWND == hwnd 的项
                  → .LocationName、.LocationURL、.Document.Folder.Self.Path
```

全程走**双接口（IDispatch 封送）**，因为这是**跨进程**唯一可用的接口类型：`IShellBrowser` / `IFolderView` 是 Explorer 进程的原始 vtable 接口，**不会**被封送到外部客户端，从另一个进程探测它们会以 `E_NOINTERFACE` 失败（没有注册代理/桩）。`Document.Folder.Self.Path` 就是 Explorer 地址栏所依据的值，因此它是真实的、可能已被重定向的文件系统路径。这一点写在 `ExplorerLocationResolver.cs` 的类注释里，来自早期的探测工作；`E_NOINTERFACE` 这一结果本身在撰写本文档时**未重新实测**。

`ShellLocationClassifier.Classify(selfPath, displayName)` 把原始 parsing name 归类为 `ShellLocationKind`：

| 原始值 | 类别 |
|---|---|
| `\\server\share\...`（UNC） | `FileSystemPath` |
| `C:` / `C:\...` | `FileSystemPath` |
| `::{20d04fe0-3aea-1069-a2d8-08002b30309d}`（CLSID_MyComputer） | `ThisPc` |
| `::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}`（Windows 11 主文件夹） | `Home` |
| `::{...}`（其它） | `OtherVirtualFolder` |
| 其它，例如 `“<目录>”中的搜索结果&<查询>` / `Search results in <folder>&<query>` | `SearchResults`（会从弯引号/直角引号/CJK 引号中提取范围提示） |
| 空 | `Unknown` |

`SearchScopeResolver.Resolve(hwnd)` 把分类结果映射为 `ResolvedScope`：

- `FileSystemPath` → `CurrentDirectoryAndSubdirectories`，路径即该目录（UNC 路径跳过本地存在性检查）；
- `ThisPc` → `AllVolumes`；
- `Home` → `KnownFoldersUnion` = `KnownFolderResolver` 解析出的、当前存在的已知文件夹（对 桌面/文档/下载/图片/视频/音乐 调用 `SHGetKnownFolderPath`，去重，跳过不存在者 —— 因此被重定向/迁移的文件夹会解析到真实位置）；
- `SearchResults` → 该窗口**记忆的**位置，否则 `SearchScopeUnknown`；
- `OtherVirtualFolder` → `UnsupportedShellNamespace`（通知，不重定向）；
- 目录不存在 → `DirectoryUnavailable`；窗口已关闭 / 无 Shell 窗口 → `ExplorerWindowNotFound`。

### 搜索结果视图的范围回退（`WindowScopeBeforeSearch`）

Explorer 一旦提交搜索 —— 无论是 Enter，还是最后一次按键后约 800 ms 的自动搜索 —— 就会把窗口的 Shell 位置替换成合成名称，例如 `“数据目录 空格”中的搜索结果&alpha`；此时该搜索应限定的位置已经无法读取。因此 `WindowScopeTracker` 按窗口记忆最后一次实时位置，结构为 `{ path, displayName, observedAt, origin, kind }`（`kind` 默认 `FileSystemPath`），记录时机如下（此时窗口仍处于实时视图）：

1. 搜索框刚被找到时（窗口附加）；
2. 每次落在真实文件系统路径上的实时 Shell 解析（即实时目录的每次提交）；
3. 搜索框由空变为非空时（一次查询的首次按键，`_onQueryStarted`）；
4. 搜索框为空时发生窗口标题变化（导航 / 离开搜索视图）。

`TryRecordLiveScope` 也会记录**这台电脑**与**主文件夹**（`path` 为空并带上对应的 `kind`），因为从那里发起的搜索必须保持为“所有卷”或“已知文件夹并集”；搜索结果视图本身会被跳过，以免覆盖搜索真正发起时的位置：

```text
[DEBUG] window scope recorded hwnd=1904286 path="C:\...\ees-smoke\数据目录 空格"
[DEBUG] window scope recorded hwnd=1709020 kind=ThisPc
```

从搜索结果视图提交的搜索使用该记录，并显式写入日志：

```text
[INFO] ResolvedPath="C:\...\ees-smoke\数据目录 空格"
[INFO] SearchScope=CurrentDirectoryAndSubdirectories (carried over from the window's folder)
```

类注释表述为“一次查询的首次按键”。实现是在**文本由空变为非空的跃迁**上捕获（`startedQuery = _text.Length == 0 && text.Length > 0`），即同一次首次按键；上面的日志行显示实测运行中承载过来的路径是正确的。

当记忆到的位置是**这台电脑**时，搜索结果视图解析为 `AllVolumes`；是**主文件夹**时解析为已知文件夹并集 —— 两者的 `IsSearchResultView` 均为 `true`，只有记忆到真实 `FileSystemPath` 时才使用 `ScopeResolutionSource.WindowScopeBeforeSearch`。这正是“从这台电脑或主文件夹发起的搜索”在 Explorer 已把窗口变成结果视图之后仍能保持原语义的原因。

若窗口显示搜索结果视图、却从未记录过任何位置，则**不**重定向搜索，用户会看到：

```text
[WARN] search not redirected: SearchScopeUnknown detail="Explorer is showing a search result view and the folder it was started from is unknown" raw="“此电脑”中的搜索结果&thistpc"
```

这是刻意采取的“宁可拒绝也不猜”策略。

## 7. Everything 集成

### 7.1 定位 Everything（`EverythingLocator`）

解析顺序（没有任何硬编码到开发机的路径）：

1. 配置的 `everythingPath`（不存在时记警告）；
2. **正在运行**的 Everything 进程的可执行文件路径；
3. `%ProgramFiles%`、`%ProgramFiles(x86)%`、`%ProgramW6432%`、应用目录、`%LocalAppData%` 下的 `\Everything\Everything.exe`；
4. HKCU+HKLM、64 位与 32 位视图的卸载注册表项（`DisplayName` 含 "Everything"、`InstallLocation`、`DisplayIcon`）中的目录；
5. `PATH`。

`es.exe` 用同样方式解析，优先 Everything 目录。结果会缓存，直到 `Invalidate()`（保存设置时、以及托盘“重新连接 Everything”时调用）。

### 7.2 查询构建（`EverythingQueryBuilder`）

| 范围 | 命令行 |
|---|---|
| 单个目录 | `-no-new-window -path "<目录>" -s* <文本>` |
| 多目录并集（主文件夹） | `-no-new-window -s* <文本> <ancestor:"a"\|ancestor:"b"\|...>` |
| 所有卷（这台电脑） | `-no-new-window -s* <文本>` |

- `reuseEverythingWindow` 为真时用 `-no-new-window`，否则 `-new-window`。
- `-s*` 表示“命令行剩余部分就是字面搜索文本”（Everything 1.5+）。较旧版本使用 `-s "<文本>"`，其中字面 `"` 写作 `"""`。能力依据 IPC 版本查询决定；版本未知时回退到安全的 1.4 形式。
- 用户文本**原样**作为 Everything 搜索语法传入，绝不拼进一个可能改变其含义的合成查询串。唯一附加的是范围，且只以 `-path` 或 `ancestor:` 并集形式出现。
- 仅当路径包含空格、`&`、`(`、`)` 时才给路径加引号。
- 多目录并集需要 Everything 1.5；版本未知时构建失败并给出 `a multi-folder search scope requires Everything 1.5 or later`，搜索被报告为不可用，而不是静默地搜遍全盘。

**为什么用 `-path` 而不是把路径放进查询文本：** 把目录路径放在*查询文本*里时，Everything 按**路径子串**匹配 —— 在 `D:\a\inside` 中搜索也会命中 `D:\a\inside2`。而 `-path <目录>` 匹配的是目录本身，不是子串。此为实测；另外注意 `-path "a;b"` 是**单个 token**，不是并集。

### 7.3 执行与窗口处理（`EverythingBridge`）

`Execute(invocation, explorerHwnd)` —— 用一把锁串行化，避免两个 Explorer 窗口的 Everything 更新互相插入：

1. 快照已有 Everything 搜索窗口（`FindTopLevelWindows("EVERYTHING")`），以判断是否预期复用。
2. 以该命令行启动 `Everything.exe`（`UseShellExecute = false`、`CreateNoWindow = true`，工作目录为 Everything 所在目录）。
3. 最多等待启动进程退出 2 秒。Everything 已在运行时，启动进程会经 IPC 转发命令行并在约 100 ms 后退出；Everything **未**运行时，这个进程*就是*新的 Everything 实例，因此既不会无限等待它，也不会杀掉它。
4. `WaitForSearchWindow` 轮询 `EVERYTHING` 窗口（20 ms 起，倍增，上限 200 ms），查找标题包含查询前缀的窗口（最多取前 24 个字符，因为长查询在标题中会被省略）。超过 `windowWaitMs`（默认 15 000 ms）后，回退使用新建窗口，否则第一个已有窗口，并记录一条“标题尚未反映查询”的警告。
5. 提升该窗口但**不激活**（`NativeMethods.BringToFrontWithoutActivating`），随后恢复 Explorer 搜索框焦点（见下）。
6. 返回 `WindowCreated` / `WindowReused`、墙钟耗时，以及作为验证信号的 `TitleReflectsQuery`。

### 7.4 IPC 的使用范围

Everything 文档化的消息式 IPC（`everything_ipc.h`）只用于**状态与能力信息**，通过 `FindWindow("EVERYTHING_TASKBAR_NOTIFICATION")` + `WM_USER` 消息获取：主/次/修订/构建版本号、数据库是否加载完成、某个 NTFS 驱动器是否已被索引。搜索本身**不**经 IPC 下发：据 `EverythingIpc.cs` 的类注释，文档化的 `EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8` 消息虽被 Everything 1.5.0.1423b 接受但**没有效果**，因此改用官方命令行。（该结论来自早期探测工作，撰写本文档时未重新实测。`EverythingIpc.IsDatabaseLoaded` 与 `IsDriveIndexed` 仅供诊断，不在搜索路径上调用。）

### 7.5 焦点策略

需求：结果必须显示在 Explorer 前面，但用户的输入必须继续进入 Explorer 搜索框。

```csharp
ShowWindow(hWnd, SW_SHOWNOACTIVATE);
SetWindowPos(hWnd, HWND_TOP, 0,0,0,0, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_SHOWWINDOW);
```

`SWP_NOACTIVATE` 在提升 z 序的同时不赋予键盘焦点。若 Everything 仍然成为前台窗口，`EverythingBridge.RestoreSearchBoxFocus` 会对搜索框调用 `SearchBoxWatcher.FocusSearchBoxThreadSafe`（`AutomationElement.SetFocus()`）把键盘焦点还回去 —— 但**仅在同时满足**以下条件时：

- 当前前台窗口就是那个 Everything 窗口（否则 Everything 从未抢焦点，日志记录 `Everything did not take the keyboard focus; the Explorer search box keeps it`）；
- 自发送命令之前起，全系统最后输入时刻未变化（`GetLastInputInfo`）—— 即用户没有在此期间开始做别的事（`the user produced input after the search; leaving the keyboard focus alone`）。

Explorer 拒绝设置焦点（当前视图中搜索框可能不可聚焦）时只记 `Debug`，不是错误；窗口及其结果不受影响。

**实测：** 一次自动（空闲）搜索之后，日志出现 `returning the keyboard focus to the Explorer search box (hwnd=1904286)`，且继续输入仍被同一个搜索框接收并再次触发提交。

**未人工验证：** 当 Explorer 搜索框是以普通 UIA `Edit` 暴露（而非 WinUI `AutoSuggestBox` 宿主）时的行为，以及“Everything 主动抢占前台”的分支。

## 8. 日志与配置

- **`AppLogger`**（`Core\Diagnostics\AppLogger.cs`）：`<root>\logs\app.log`，`[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] 消息`，UTF-8 无 BOM，共享方式 `FileShare.ReadWrite | FileShare.Delete`。`AppRoot` 在配置加载期间以 `fileLogging: false` 调用 `Create(root, config, fileLogging)`，之后才通过 `SetFileLogging(config.LoggingEnabled)` 启用 —— 因此把日志关闭的配置根本不会创建 `logs\` 或 `app.log`（连空文件都不会有）。写入只是 `ConcurrentQueue` 入队加一次信号量释放；`EES-LogWriter` 线程排空队列、每批 flush 一次，并在文件超过 `maxLogFileSizeMb` 时轮转（`app.log` → `app.1.log` → … → `app.<maxLogFiles-1>.log`，最旧的被删除）。`SetFileLogging(false)`（或级别 `None`）会关闭句柄且不再产生任何输出。`ClearLogs` 请求写入端关闭（5 s 超时），删除所有 `*.log`（5 次尝试、每次间隔 50 ms），并让写入端按需重新打开。所有路径都吞掉异常：日志永远不会破坏应用。
- **`ConfigStore`**（`Core\Configuration\ConfigStore.cs`）：根目录下的 `config.json`，绝不写 `%AppData%`。原子保存（先写 `config.json.tmp`，再 `File.Move(..., overwrite: true)`）。文件缺失时按默认值创建；不可读/损坏时回落到内存默认值并记录错误；不可写时置 `IsPersistDisabled` + `LastError` 并触发一次通知，工具继续以内存设置运行。`AppRoot` 在日志器配置完成之后再报告结果，因此 `configuration loaded from …` 这一行（读取失败时会追加 `(defaults are used in memory: …)`）本身就会写进日志文件。
- **`AppConfig.Normalize`** 夹紧所有数值字段（`autoSearchDelay` 100–60000、`enterCommitWindowMs` 50–5000、`explorerRescanSeconds` 0–3600、`maxLogFileSizeMb` 1–1024、`maxLogFiles` 1–100），去空白并去引号处理两个路径，规范化 `logLevel` 与 `language`（`auto` / `en-US` / `zh-CN`，未知 → `auto`）。因此手工编辑的文件绝不可能产生越界的运行时取值。
- **结构化搜索日志行**（由 `SearchCoordinator.Process` 按如下顺序输出）：

```text
[INFO] Search submitted trigger=Enter|IdleTimeout
[INFO] SourceExplorerHwnd=<hwnd>
[INFO] SearchText="<文本>"
[INFO] ResolvedPath="<路径>"            （或  <all volumes> ）
[INFO] SearchScope=<种类> [ (carried over from the window's folder) ]
[INFO] Query="<查询>"
[INFO] Everything window created|reused
[INFO] Search completed in <n> ms
```

## 9. 错误处理与降级

| 情形 | 行为 |
|---|---|
| `config.json` 缺失 / 损坏 / 不可写 | 创建 / 内存默认值 + 错误日志 / 内存设置 + 一次通知；工具继续运行 |
| 找不到 Everything | 搜索失败，`EverythingUnavailable` → 首次弹消息框，之后弹气泡，并提示设置 `everythingPath` |
| 找到 Everything 但未运行 | `Probe` 报告 `Available`（已知可执行文件），搜索时启动它 |
| 15 s 内 Everything 窗口未出现 | 记录失败并通知；Explorer 搜索框不受影响 |
| Everything 窗口标题尚未反映查询 | 警告，仍使用定位到的窗口 |
| 多目录范围但 Everything 版本未知 | 查询构建失败并给出明确错误；绝不静默退化为“搜遍全盘” |
| 不支持的虚拟文件夹（回收站、网络等） | `UnsupportedShellNamespace`，通知，不重定向 |
| 搜索结果视图但来源目录未知 | `SearchScopeUnknown`，通知，不重定向 |
| 解析出的目录已不存在 | `DirectoryUnavailable`，清除该窗口记忆的范围，通知 |
| 输入过程中 Explorer 窗口被关闭 | `ExplorerWindowNotFound`，通知 |
| Shell 解析抛异常 | `LocationUnavailable` 并附带异常信息；错误日志包含堆栈 |
| 键盘钩子 `SetWindowsHookEx` 失败 | 警告，另两个 Enter 兜底机制继续工作 |
| 无法订阅 UIA 焦点/标题/文本处理器 | `Debug` 日志；其余信号继续工作 |
| 任何日志失败 | 吞掉；不写日志、不崩溃 |
| 运行中修改设置 | 尽可能立即生效：日志级别/开关、语言、Everything 路径缓存、启动项、监听启停、托盘标签 |
| 托盘/菜单失败 | 捕获并记录；应用不会弹出未处理异常对话框（唯一例外是启动失败：弹消息框并以退出码 1 结束） |

通知遵循统一策略（`NotificationService`）：**错误绝不静默** —— 某类错误首次出现弹模态消息框，之后弹托盘气泡；纯提示性消息（日志已清理、配置只读、启动项已修复）受 `showNotifications` 控制。
