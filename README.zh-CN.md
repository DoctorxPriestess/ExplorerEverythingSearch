# Explorer Everything Search

[English](README.md) | [简体中文](README.zh-CN.md)

**在 Windows 资源管理器的搜索框里输入，结果由 Everything 打开，并且严格限定在当前这个资源管理器窗口所在的目录。**

资源管理器自带的搜索既慢又容易让人误判搜索范围。本工具监听每个已打开窗口的搜索框输入，把搜索转交给 [Everything](https://www.voidtools.com/)，范围限定为**触发搜索的那个窗口**当前所在目录及其子目录。结果出现在一个被提到资源管理器前面的 Everything 窗口里，**但不抢键盘焦点**，因此你可以继续在资源管理器搜索框里输入，不断细化查询。

> 状态：已在一台 Windows 11 机器上人工验证，并配套自动化测试（235 项单元/集成测试：234 通过、1 项为人工可选；另有一个驱动真实资源管理器与 Everything 窗口的端到端工程）。实测了什么、没实测什么，见 [docs/verification.zh-CN.md](docs/verification.zh-CN.md)。

## 核心工作链路

```
Explorer 搜索框
      │  （UI Automation：每次按键触发 ValuePattern / TextPattern）
      ▼
搜索上下文提取
      │  （Shell.Application IDispatch：窗口真正所在的目录；
      │   并按窗口记忆，因为一旦提交搜索，Explorer 会把窗口位置替换成
      │   合成的“搜索结果视图”位置）
      ▼
Everything 查询桥
      │  （官方命令行：-no-new-window -path <目录> -s* <文本>；
      │   多目录范围使用 <ancestor:"a"|ancestor:"b">）
      ▼
Everything 窗口  →  结果限定在该目录，窗口置顶但不抢焦点
```

## 功能列表

- **Enter 立即提交。** 低级键盘钩子（`WH_KEYBOARD_LL`）**仅在资源管理器搜索框获得键盘焦点时**安装，按下 Enter 立即提交。全链路耗时（按键 → Everything 窗口显示查询）早先报告为约 100–200 ms，**未在同一轮验证中重测**（已有证据：到 Explorer 结果视图 10–20 ms、Everything 端到端 154 ms）。钩子被拦截时有两级兜底。
- **空闲自动搜索。** 停止输入 `autoSearchDelay` 毫秒（默认 1000 ms，日志实测 1005–1015 ms）后自动提交。
- **范围精确，不靠猜。** 范围来自**触发搜索的那个窗口**的 Shell 位置，每次提交都实时解析；同时在窗口附加、导航、以及一次查询的首次按键时快照目录 —— 因为 Explorer 一旦提交搜索就会把窗口位置换成合成的搜索结果视图。
- **多窗口隔离。** 两个位于不同目录的资源管理器窗口产生两次独立搜索、各自独立的范围，即使同时输入也互不干扰。
- **窗口复用。** `-no-new-window` 复用已有的 Everything 搜索窗口，不堆叠窗口（`reuseEverythingWindow`）。
- **焦点不移动。** Everything 窗口用 `SetWindowPos` + `SWP_NOACTIVATE` 置顶；若 Everything 确实抢占了前台，则把键盘焦点还给资源管理器搜索框 —— 但前提是这段时间你没有产生自己的输入。
- **事件驱动，空闲几乎零开销。** 窗口发现用 WinEvent 钩子，搜索框用 UI Automation 事件，STA 消息泵阻塞在 `MsgWaitForMultipleObjectsEx` 上；没有事件时进程完全不唤醒。低频自愈重扫（`explorerRescanSeconds`，默认 60 秒，`0` = 关闭）用于修补漏掉的窗口事件。
- **便携、安静。** 无安装程序、不写 `%AppData%`、不需要管理员权限（`asInvoker`）。`config.json` 和 `logs\` 就在 EXE 旁边。
- **失败如实上报。** 无法映射到文件系统范围的虚拟位置会弹出通知（“本次搜索未重定向到 Everything”），而不是静默地搜错地方。
- **托盘界面、设置窗口、中英双语**（`auto` 跟随 Windows 界面语言）。

## 系统要求

| | 要求 |
|---|---|
| 操作系统 | Windows 10 1809（build 17763）及以上，或 Windows 11。**仅在 Windows 11 build 26200 上验证过。** |
| 运行时 | .NET 8 桌面运行时 —— **仅**框架依赖包需要；单文件自包含包不需要任何运行时。 |
| Everything | voidtools 的 [Everything](https://www.voidtools.com/) 1.4 或 1.5，推荐 1.5（会用到 `-s*` 与 `ancestor:` 函数）。已在 `C:\Program Files\Everything` 下的 1.5.0.1423b x64 上验证。 |
| es.exe | 可选。仅用于诊断和人工验证时交叉核对范围，运行本工具不需要。 |
| 权限 | 不需要。以当前用户身份运行。 |

## 安装与运行

**单文件自包含（推荐，无需运行时）：**

```powershell
dotnet publish src\ExplorerEverythingSearch.App\ExplorerEverythingSearch.App.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

把 `ExplorerEverythingSearch.exe` 放到任意目录（例如 `C:\Tools\ExplorerEverythingSearch\`）后直接运行。首次运行会在 **EXE 所在目录**创建 `config.json` 和 `logs\`。

**框架依赖（需要 .NET 8 桌面运行时）：**

```powershell
dotnet publish src\ExplorerEverythingSearch.App\ExplorerEverythingSearch.App.csproj -c Release
```

`tools\package.ps1`（需要 PowerShell 7+）会把两个发布包构建到 `artifacts\`：

```powershell
pwsh tools/package.ps1                       # 构建 + 跑单元测试 + 打包两个包
pwsh tools/package.ps1 -Version 1.0.0 -SkipTests
```

- `ExplorerEverythingSearch-<版本>-win-x64-portable.zip` —— 自包含单文件，无需运行时
- `ExplorerEverythingSearch-<版本>-win-x64-framework-dependent.zip` —— 体积小，需要 .NET 8 桌面运行时（x64）

参数：`-Configuration`（默认 `Release`）、`-Version`（默认取 `Directory.Build.props`）、`-OutputDirectory`（默认 `<仓库>\artifacts`）、`-SkipTests`。两个包都包含程序本体、`README.md`、`README.zh-CN.md` 与 `LICENSE`；`config.json` 在首次启动时于 EXE 旁创建。若单元测试工程不存在，脚本只警告并继续打包，不会失败。**该脚本尚未执行过 —— 未验证**（见 [docs/verification.zh-CN.md](docs/verification.zh-CN.md#11-未验证--unverified)）。

### 命令行参数

| 参数 | 作用 |
|---|---|
| `--startup` | 最小化启动（只在通知区域出现）。HKCU `Run` 项使用的就是它。 |
| `--settings` | 打开设置窗口。若已有实例在运行，则由该实例打开设置窗口，本进程退出。 |
| `--exit` | 请求正在运行的实例退出，然后本进程退出。 |
| `--root <目录>` | 把 `config.json` 与 `logs\` 存到 `<目录>` 而不是 EXE 旁边。同时使它成为**独立实例**（自己的互斥体/信号），因此测试运行不会干扰正式实例。也支持 `--root=<目录>` 写法。 |
| `--help`、`-h`、`/?` | 用消息框显示用法。 |
| `--version` | 用消息框显示版本。 |

未知参数会被忽略。工具本身没有主窗口（只有托盘）；`--help` / `--version` 是仅有的交互式输出。

### 首次运行

1. 启动 EXE，通知区域出现托盘图标（无主窗口）。
2. 打开两个位于不同目录的资源管理器窗口，点击其中一个的搜索框，输入文件名片段。
3. 按 Enter，或停止输入约 1 秒。Everything 窗口置顶显示，结果限定在该目录。
4. 继续在资源管理器搜索框里输入以细化 —— 焦点始终留在那里。

## 配置文件 `config.json`

首次运行按默认值创建，可以手工编辑。所有数值在加载时都会被夹紧（clamp），因此写错数字不会破坏运行时。

| 字段 | 默认值 | 含义 | 取值 / 范围 |
|---|---|---|---|
| `enabled` | `true` | 资源管理器搜索监听总开关。 | `true` / `false` |
| `autoSearchDelay` | `1000` | 空闲自动搜索延迟：最后一次按键后多少毫秒提交。 | 100–60000 ms |
| `everythingPath` | `""` | 显式指定 `Everything.exe` 路径。留空 = 自动检测（配置 → 正在运行的实例 → 卸载注册表项 → 常见安装位置 → `PATH`）。 | 路径或 `""` |
| `esPath` | `""` | 显式指定 `es.exe` 路径（可选，仅诊断用）。留空 = 自动检测。 | 路径或 `""` |
| `startWithWindows` | `true` | 注册/刷新 HKCU `...\Run` 项。 | `true` / `false` |
| `showNotifications` | `false` | 是否显示提示性托盘气泡（日志已清理、配置只读、启动项已修复）。错误通知不受此开关影响。 | `true` / `false` |
| `logLevel` | `"Information"` | 写入日志文件的最低级别。 | `Trace`、`Debug`、`Information`、`Warning`、`Error`、`None` |
| `loggingEnabled` | `true` | 是否写日志文件。为 `false` 时完全不产生、不写入文件。 | `true` / `false` |
| `reuseEverythingWindow` | `true` | 复用已有 Everything 搜索窗口（`-no-new-window`），而不是每次搜索新建窗口（`-new-window`）。 | `true` / `false` |
| `detectEnterByKeyboardHook` | `true` | **首选** Enter 识别：低级键盘钩子，仅在资源管理器搜索框获得焦点时安装。设置窗口中不暴露。 | `true` / `false` |
| `detectEnterByFocusChange` | `true` | **次选**：焦点从搜索框离开、进入同一窗口的结果区，视为 Enter。 | `true` / `false` |
| `detectEnterByCommitTiming` | `true` | **兜底**：Explorer 在最后一次按键后 `enterCommitWindowMs` 内提交，且标题携带搜索框文本，视为 Enter。 | `true` / `false` |
| `enterCommitWindowMs` | `400` | 提交时序启发式的上界。Explorer 自身的自动提交发生在最后一次按键后约 800 ms，故取 400 ms。 | 50–5000 ms |
| `explorerRescanSeconds` | `60` | 低频自愈重扫间隔。`0` 表示关闭（完全事件驱动）。 | 0–3600 秒 |
| `language` | `"auto"` | 界面语言。`auto` 跟随 Windows 界面语言。 | `auto`、`en-US`、`zh-CN` |
| `maxLogFileSizeMb` | `5` | `app.log` 达到该大小后轮转。 | 1–1024 MB |
| `maxLogFiles` | `5` | 保留的轮转文件数量（`app.1.log` … `app.N.log`）。 | 1–100 |

示例：

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

保存是原子的（临时文件 + 替换）。若 `config.json` 读不出来，会在内存中回落为默认值并记下错误；若写不进去，会通知你“本次会话中的设置修改只保留在内存中”。

## 日志

- 位置：`<root>\logs\app.log`，`<root>` 为 EXE 所在目录或 `--root` 指定目录。
- 格式：每行 `[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] 消息`，UTF-8 **无 BOM**，共享方式 `ReadWrite | Delete`（运行中也能读取该文件）。
- 级别标记：`TRACE`、`DEBUG`、`INFO`、`WARN`、`ERROR`。
- 由专用 `EES-LogWriter` 线程异步批量写入并 flush；写日志永不阻塞 Explorer/UIA 线程，也永不向应用抛异常。
- 轮转：`app.log` 达到 `maxLogFileSizeMb` 后，`app.1.log` → `app.2.log` … 最旧的 `app.<maxLogFiles-1>.log` 被删除，当前文件变为 `app.1.log`。
- `loggingEnabled: false` 完全停止写入（文件句柄会被关闭，不会占着文件）。把 `logLevel` 设为 `None` 效果相同。日志器创建时**不带**日志文件，只有在配置加载完成后才开始写入，因此把日志关闭的配置根本不会创建 `logs\` 或 `app.log`。
- 托盘菜单 **打开日志目录** 用资源管理器打开 `logs\`；**清理日志** 会 flush、关闭、删除该目录下所有 `*.log`，之后写入端按需重新打开。运行中清理在设计上支持 —— **尚未人工验证**（见 [docs/verification.md](docs/verification.md)）。

真实日志片段（级别 `Debug`）：

```text
[2026-09-13 02:44:44.222] [DEBUG] Enter detection: keyboard hook installed (an Explorer search box has the focus)
[2026-09-13 02:44:45.147] [DEBUG] SearchBox text changed: "thistpc"
[2026-09-13 02:44:45.978] [DEBUG] Explorer committed a search after 830 ms ("thistpc - 文件资源管理器") - treated as its own auto search
[2026-09-13 02:44:46.162] [INFO] Search submitted trigger=IdleTimeout
[2026-09-13 02:44:46.162] [INFO] SourceExplorerHwnd=1709020
[2026-09-13 02:44:46.162] [INFO] SearchText="thistpc"
[2026-09-13 02:44:46.175] [WARN] search not redirected: SearchScopeUnknown detail="Explorer is showing a search result view and the folder it was started from is unknown" raw="“此电脑”中的搜索结果&thistpc"
```

以及对应的成功情形：

```text
[2026-09-13 02:44:51.152] [INFO] Search submitted trigger=IdleTimeout
[2026-09-13 02:44:51.170] [INFO] ResolvedPath="C:\Users\ControlxSaria\AppData\Local\Temp\ees-smoke\数据目录 空格"
[2026-09-13 02:44:51.171] [INFO] SearchScope=CurrentDirectoryAndSubdirectories (carried over from the window's folder)
[2026-09-13 02:44:51.172] [INFO] Query="alpha"
[2026-09-13 02:44:51.327] [DEBUG] returning the keyboard focus to the Explorer search box (hwnd=1904286)
[2026-09-13 02:44:51.327] [INFO] Everything window reused
[2026-09-13 02:44:51.327] [INFO] Search completed in 154 ms
```

## 托盘菜单

左键双击打开设置窗口。

| 菜单项 | 行为 |
|---|---|
| `Explorer 搜索监听：已启用 / 已暂停` | 仅显示状态（不可点击）。 |
| `Everything：已连接（x.y.z.b）/ 不可用` | 仅显示状态；版本在每次刷新菜单时通过 Everything IPC 查询。 |
| `启用监听` / `暂停监听` | 切换 `enabled` 并持久化。 |
| `设置...` | 打开设置窗口（监听开关、延迟、Enter 识别选项、Everything 路径、开机启动、通知、日志开关、日志级别、日志大小、语言；以及测试/重连、打开日志、清理日志、修复启动项按钮）。 |
| `重新连接 Everything` | 清除 Everything 检测缓存、重新探测，并用气泡显示结果。 |
| `打开日志目录` | 打开 `<root>\logs`。 |
| `清理日志` | 运行中删除日志文件（见“日志”）。 |
| `日志记录：开` / `日志记录：关` | 切换 `loggingEnabled` 并持久化。 |
| `退出` | 退出工具。 |

## 开机启动

- 启动项位于 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `ExplorerEverythingSearch`，值为 `"<EXE 路径>" --startup`。不涉及管理员权限。
- 启动时**以及**每次保存配置时，都会把期望值与已存值比对。若 EXE 被移动，已存路径不再匹配：该项会被自动重写，且如果它原本已失效，会弹出通知告知已修复。
- 当启动项失效（存在但指向的路径已不存在）时，设置窗口会显示告警和 **修复开机启动项** 按钮。自动修复的日志行与手动按钮均**尚未人工验证**。

## 多窗口、多语言与已知文件夹

- **多窗口**：每个资源管理器窗口（`CabinetWClass`）都有各自的搜索框监听与搜索会话。搜索在桥接层串行化，避免两个窗口的 Everything 更新互相插入，但各自保留自己的范围和去重状态。
- **多语言**：`language` 为 `auto`、`en-US` 或 `zh-CN`；`auto` 在 Windows 界面语言为中文时选中文，否则选英文。两套字符串都内置在二进制中。保存后立即生效。
- **已知文件夹**：Windows 11 的 **主文件夹** 视图，范围是用户已知文件夹的并集（桌面、文档、下载、图片、视频、音乐），通过 `SHGetKnownFolderPath` 解析。由于路径取自 Shell，**重定向到非系统盘（或 UNC 路径）的下载/文档/图片等文件夹会解析到其真实位置**并在那里搜索。不存在的文件夹会被跳过；如果一个都解析不出来，本次搜索**不会**被重定向，并会有通知说明原因。

## “这台电脑 / 主文件夹 / 搜索结果视图”的作用域语义

| Explorer 显示 | Everything 范围 | 表达方式 |
|---|---|---|
| 普通文件夹（含被重定向/迁移的已知文件夹） | 该目录及其全部子目录 | `-no-new-window -path "<目录>" -s* <文本>` |
| 搜索结果视图（`“<目录>”中的搜索结果&<查询>`） | 发起该搜索的目录 —— 按窗口从上一次实时 Shell 解析中记忆 | 同上，日志中标为 `(carried over from the window's folder)` |
| **主文件夹**（Windows 11） | 上述已知文件夹的并集 | 在查询后附加 `<ancestor:"a"\|ancestor:"b"\|...>` |
| **这台电脑** | 所有卷（不加路径限制） | 仅查询文本，无 `-path` / `ancestor:` |
| 从**这台电脑**发起的搜索结果视图 | 所有卷 —— “这台电脑”按窗口被记住，因此从这里发起的搜索即使窗口已变成结果视图也保持原语义 | 仅查询文本 |
| 从**主文件夹**发起的搜索结果视图 | 已知文件夹的并集 | `<ancestor:…>` 并集 |
| 回收站、网络、控制面板等虚拟文件夹 | **不重定向** —— 明确弹出通知 | — |
| 搜索结果视图，但其来源位置从未被观察到 | **不重定向** —— 通知“无法确定该搜索是从哪个目录发起的” | — |

关于范围与匹配的重要细节：Everything 的 `-path <目录>` 匹配的是目录本身，**不是路径子串**，所以 `-path "D:\a\inside"` 不会同时命中 `D:\a\inside2`。相反，如果把目录路径**放进搜索文本里**，它会被当作路径子串匹配，两者都会命中 —— 这正是范围必须用 `-path` / `ancestor:` 表达、而绝不拼进查询文本的原因。

## 架构

分层结构、线程模型、事件驱动监控、Enter 检测的三重机制、基于 Shell 的范围解析（及其按窗口的回退）以及 Everything 集成方式，见 **[docs/architecture.zh-CN.md](docs/architecture.zh-CN.md)**。

## 构建

```powershell
dotnet build ExplorerEverythingSearch.sln -c Release
```

- 解决方案：`ExplorerEverythingSearch.sln` —— `src\ExplorerEverythingSearch.Core`（net8.0-windows，无 UI；引用 WPF 只是为了托管 UI Automation 客户端）和 `src\ExplorerEverythingSearch.App`（WinExe，WPF + WinForms 用于托盘图标；程序集名 `ExplorerEverythingSearch`）。
- 版本与作者来自 `Directory.Build.props`（`1.0.0`，"Explorer Everything Search contributors"）。
- 指定了 `RuntimeIdentifier` 时，App 项目按自包含、单文件、压缩、**不裁剪**（WPF 不支持裁剪）且不生成 PDB 发布。
- CI：`.github\workflows\build.yml` 在 `windows-latest` 上还原、`Release` 构建、运行单元测试并上传 `test-results.trx`；`.github\workflows\release.yml`（`v*` 标签或手动触发）解析版本、执行 `tools/package.ps1`、上传两个包并创建/更新 GitHub Release。**两个工作流都尚未执行过 —— 未验证。**

## 测试

- 单元/集成测试：`tests\ExplorerEverythingSearch.Tests`（xUnit 2.9、`Microsoft.NET.Test.Sdk` 17.11、net8.0-windows，Core 已用 `InternalsVisibleTo` 暴露内部成员）。覆盖：配置默认值与钳制/序列化、配置存储（默认值、损坏文件、原子保存、不可写根目录）、日志（格式、级别、轮转、清理、开关）、Everything 查询构建（作用域、引号、能力集回退）、Shell 位置分类与作用域解析（live 目录、这台电脑、主文件夹、未知搜索结果视图、不支持的命名空间）、空闲去抖、搜索协调器（取代、去重、多窗口隔离）以及 Enter/空闲会话逻辑。

```powershell
dotnet test tests\ExplorerEverythingSearch.Tests\ExplorerEverythingSearch.Tests.csproj -c Release
```

  本仓库最近一次运行：**237 项，通过 236，跳过 1**（跳过项会写 `HKCU\...\Run`，需人工执行），约 4 秒。清理日志的测试覆盖了一个被发现并已修复的真实缺陷：日志线程用 `Set()`/`Reset()` 脉冲交接“文件已关闭”状态，调用方可能错过该脉冲，导致“清空日志”失败并使 UI 线程卡住 5 秒。
- 端到端测试：`tests\ExplorerEverythingSearch.E2E` 通过 UI Automation 驱动真实的资源管理器与 Everything 窗口，并校验日志、Everything 窗口标题与结果数量。它需要交互式桌面会话，因此 `build.yml` 有意不包含它；请手工运行（见 [docs/verification.zh-CN.md](docs/verification.zh-CN.md)）。本仓库最近一次运行：**13 个场景全部通过，72.9 秒**（`dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --scenario all`）；其中一次是在被反复强杀 `explorer.exe` 之后处于退化状态的 Shell 会话里跑的，那次运行即 `docs/verification.zh-CN.md` §12.5 所记 `ShellWindows` 僵尸项缺陷的回归验证。
- 实际执行过的人工验证流程（含耗时与日志片段）见 [docs/verification.zh-CN.md](docs/verification.zh-CN.md)。底层观测所用的开发者探针是 `tools\probes\ExplorerProbe`（`resolve` 转储 Shell 位置，`events` 驱动真实搜索框并记录所有 UI Automation 信号）。

## 已知问题

- **退出耗时约 6 秒，并会记录一条 dispatcher 警告。** 任何退出路径（托盘菜单、`--exit`、关闭最后一个窗口）都会写入

  ```
  [DEBUG] stopping the Explorer monitor reported: STA dispatcher did not complete the requested work in time
  ```

  约 1 秒后完成退出。应用仍以退出码 0 结束，配置与日志正常写入，运行期间的搜索不受影响；代价只是进程比应有的多停留约 5 秒。这条停止请求以 5 秒上限交给专用的 STA 线程，而该线程没有及时取走它——但同一份日志显示它在几秒前仍在正常工作（`Search submitted` / `Search completed`），因此并非线程已死。原因**尚未确认**；证据与"需要什么才能确认"写在 [docs/verification.zh-CN.md](docs/verification.zh-CN.md) 的"未结问题"一节。
- 所有**未**验证的项目都在 [docs/verification.zh-CN.md](docs/verification.zh-CN.md) 中明确列出：多显示器与混合 DPI、托盘菜单项与气泡、安全软件拦截低级键盘钩子、`--startup` 与重启配合、以及设置对话框的交互式修改。

## 故障排查

症状 → 原因 → 处理的条目见 [docs/troubleshooting.md](docs/troubleshooting.md)，覆盖：Everything 未安装/未运行、结果为空或范围不对、“搜索范围未知”提示、安全软件拦截键盘钩子、焦点行为、日志为空、开机启动失效、Everything 被关闭后的恢复、多实例、高 DPI/多显示器、权限。

## 许可证

MIT —— 见 [LICENSE](LICENSE)。Copyright (c) 2026 DoctorxPriestess。

## 致谢

- [Everything](https://www.voidtools.com/)，作者 **voidtools** —— 本工具驱动的搜索引擎。本项目与 voidtools 无隶属关系。
- Everything 的 IPC/SDK 文档（`everything_ipc.h`）是这里所用版本查询接口的依据。

## 免责声明

本软件按“原样”提供，不附带任何形式的保证。它会安装一个低级键盘钩子（**仅在**资源管理器搜索框拥有键盘焦点时生效），会读取资源管理器窗口标题与 Shell 位置，并会为当前用户写入一个 `Run` 启动项。它不读取文件内容，也不向网络发送任何数据。运行前请审阅源码，风险自负。
