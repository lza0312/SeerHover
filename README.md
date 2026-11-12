# SeerHover — 给 Seer 加「鼠标悬停即预览」

在文件资源管理器里把光标停在某个文件上 **300ms**，Seer 自动弹出预览；光标移开自动收起。
不模拟按键、不改 Seer 本体，走的是 Seer 官方 SDK 的 IPC（`WM_COPYDATA`）。

## 下载

安装包只在 [**Releases**](https://github.com/lza0312/SeerHover/releases/latest) 发布：
`SeerHover.exe` 单文件（328 KB，需 .NET 8 桌面运行时），下载后放到任意目录双击即可，无需安装。

本仓库只放源码 —— `dist/` 是本地构建产物，已加入 `.gitignore`，不再入库。

---

## 为什么不是 Seer 插件

Seer 的插件体系只做「预览渲染」，不做「触发」：

- `convert` 插件：外部 exe/ps1 把文件转成 html/json/png，Seer 再渲染
- `dll` 插件：Qt 6.8 + C++ 实现 `ViewerBase` 渲染控件

两者的入口都是「文件已经决定要预览了，你来渲染」，插件拿不到全局鼠标事件，也无法控制触发时机。
所以触发层只能由外部进程做——SDK 的 IPC（命令 5000 预览 / 5005 隐藏 / 5004 查可见性）正是为此设计的。

## 工作原理

```
光标轮询(55ms) → 是否在资源管理器窗口内 → UIA 枚举文件项矩形 → 命中测试
      → 停稳 300ms → WM_COPYDATA 5000 + 绝对路径 → Seer 弹出
      → 光标离开 → 宽限 250ms → WM_COPYDATA 5005 收起
```

几个关键取舍（都是这台机器上实测出来的）：

1. **不用 `ElementFromPoint`。** 这版 Windows（26H2）的文件列表已换成 WinUI 控件，祖先链里取不到 `ListItem`。
   改成「枚举窗口内所有 `ListItem` 的矩形 + 坐标命中测试」，顺带获得缓存能力。
2. **先声明 Per-Monitor V2 DPI 感知。** 否则 `GetWindowRect` 返回的是被 150% 缩放虚拟化后的坐标，和 UIA 的物理坐标差 1.5 倍，命中永远不中。
3. **窗口几何必须靠 Seer 自己的机制固定下来。** Seer 的 IPC 没有位置/尺寸指令，而它默认每次显示都按内容重设窗口
   （图片会先撑成原始尺寸再收回），硬顶就会「预览大小来回跳」。解决办法是打开它的 `keep_wnd_sz_pos_mode`，见下一节。
4. **必须关掉 Seer 的「跟随资源管理器选中项」。** 详见下一节——开着它，悬停会一直跳回上次点选的文件。

## 关键前提一：Seer 的「保持窗口大小位置」要打开

`uwp.ini` 里的 `keep_wnd_sz_pos_mode`（Seer 设置里的 *Keep window size & position*）。

- **默认值 0**：每次预览都按内容重设窗口尺寸。图片会先铺成原始尺寸（实测能到 1794×1794），
  把窗口缩到设定值后它又会撑回去 —— 这就是「预览大小来回跳」的根源，光靠外部 `SetWindowPos` 顶不住。
- **设为 1 之后**：窗口尺寸不再随内容变化，而且会记住上次的尺寸和位置。实测第二次触发时
  窗口**一出现就是 900×700 且位置正确，全程零变化**。

工具在启动时会自动把它设为 1（带一次性备份），托盘也会提示一次「重启 Seer 生效」。
手动执行：`SeerHover.exe --keep-window`（开启）/ `--restore-window`（恢复 Seer 默认）。

副作用的实话：打开后**用空格预览也不再按图片大小自适应**，窗口固定成你设定的尺寸。
如果想恢复 Seer 原生行为，跑 `--restore-window`（代价是悬停时窗口大小又会跳）。

## 关键前提二：Seer 的「跟随选中项」必须关闭

Seer 有个选项 **Track selected file in Explorer**（`uwp.ini` 里的 `tracking_file`）。
开着它时，预览窗弹出约 300–500ms 后会把画面切到「资源管理器当前选中项」：

| 场景 | 现象 |
|---|---|
| 刚从文件夹里点选过 A，悬停 B | 先闪一下 B，随后被切回 **A** |
| 没有显式选中项 | 被切到文件夹里的 **第一项** |
| 关掉追踪 | 悬停哪个就显示哪个，不再跳 |

这是 Seer 侧的行为，不是 IPC 没生效——工具发出的路径是对的，只是被 Seer 自己的追踪覆盖了。
所以本工具做了三件事：

1. **启动时自动检查**：发现 `tracking_file=true` 就写回 `false`，并弹托盘提示「请重启 Seer 生效」。
2. **运行时兜底校验**：每次触发后 700ms 用 IPC `5006` 回查 Seer 真正在预览的文件；
   对不上就立刻收窗（宁可什么都不显示，也不显示错的文件），并再次提示修复。
3. **托盘菜单「修复 Seer『跟随选中项』」**，随时可手动执行。

想恢复 Seer 原来的跟随行为：`SeerHover.exe --restore-tracking`（会把 `uwp.ini.seerhover.bak` 还原回去），
或直接在 Seer 设置里重新勾选。


## 快速开始

```text
1. 确保 Seer 正在运行（托盘里有图标）
2. 双击 SeerHover.exe（从 Releases 下载）   ← 启动后常驻托盘，无主窗口
3. 打开资源管理器，把鼠标停在某个文件上 0.3 秒
```

首次运行会在 exe 同目录生成 `config.json`。托盘图标右键菜单：设置…、暂停/恢复、重载配置、
打开日志、退出。**双击托盘图标 = 打开图形设置窗口**（也可用 `--settings` 启动时直接弹出）。

设置窗口**可自由调整大小 / 最大化**（尺寸会被记住，下次打开恢复），四页覆盖全部 `config.json` 项，
底部三个按钮：**应用**（保存并生效，窗口不关）、**取消**、**确认**（保存并关闭）：

- **常规**：启用悬停预览、文件夹预览、开机自启、**深色模式**（即时切换，跟随系统/可强制）、
  **界面缩放**（80%–160%，设置窗口与托盘右键菜单整体缩放）、
  悬停触发延迟 / 收起延迟（滑杆）、修饰键抑制、点击收起、
  **双击空白处回到上一层**（资源管理器里等同 Alt+↑）
- **窗口**：弹出位置（光标旁 / 屏幕角落 / 固定坐标）、**不遮挡鼠标**、间距 / 角落 / 边距 / 坐标、
  尺寸策略（**固定尺寸 / 原比例最大化 / 跟随 Seer**）、宽度 / 高度 / 最大占屏比
- **过滤**：排除扩展名、排除路径前缀（多行编辑，右侧自绘深色滚动条）
- **高级**：预览回查、轮询间隔、详细日志、Seer 联动按钮
  （修复/恢复「跟随选中项」、开启/恢复「固定窗口尺寸」、打开配置文件 / 日志）
- 左下角「恢复默认」只重置表单，仍需点「应用 / 确认」才写入。
- **深色模式**同时作用于设置窗口与托盘右键菜单（含自绘滚动条，系统原生滚动条无法跟随深色）。
- **界面缩放**同时作用于设置窗口与托盘右键菜单：字号、间距、滚动条一起缩放；
  拖动滑杆（或直接输入数值）**立即预览**，点「应用 / 确认」长期生效；设置窗口尺寸按 100% 记忆，换缩放不跑偏。
- 所有数值项（延迟 / 坐标 / 宽高等）除了拖滑杆，还能**直接在右侧输入框里敲数字**
  （回车或点别处生效，↑↓ / 滚轮步进；输入会按步长取整并回显）。

## 双击空白处回到上一层

在资源管理器「文件列表的空白处」双击 = 按 **Alt+↑** 回到上一层文件夹（开关在「常规 → 鼠标」）。

- 只认文件列表区域（`SHELLDLL_DefView` 矩形）：导航窗格、地址栏、工具栏上的双击一律不管；
- 双击文件 / 文件夹本身不受影响：判定时用 UIA 项矩形排除，并用「双击前抓的窗口标题有没有变」
  和「当前选中项数量是否为 0」双重兜底，避免出现「打开了文件夹又被退回上一层」；
- 桌面（Progman / WorkerW）不是资源管理器窗口，天然不触发；Ctrl / Shift / Alt + 双击也不触发；
- 实现是全局低级鼠标钩子（只做双击判定）+ 后台线程判定 + 合成 Alt+↑，钩子里不做重活，不影响鼠标手感。

**固定尺寸**：上限 8K（7680×4320），4K 屏可以直接填 3840×2160 铺满整屏；
只受屏幕实际尺寸约束，不再被「最大占屏比」压小。带鱼屏（5120×1440 等）同理。

**原比例最大化**：按图片/视频的原始像素比例显示 —— 原尺寸放得下就 1:1 显示，
放不下按整块屏幕 ×「最大占屏比」等比缩小；原尺寸比 Seer 最小窗还小时放大到最小窗。
源尺寸取自 WIC（图片，只读文件头）与 Shell 属性（视频），悬停候选阶段就会预热，
触发时命中缓存不拖慢弹出。

可选：把开机自启装上

```powershell
.\SeerHover.exe --install-startup     # 在 shell:startup 里创建快捷方式
.\SeerHover.exe --uninstall-startup
```

## 配置（config.json）

改完在托盘菜单点「重载配置」即可生效，不用重启。

| 字段 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `true` | 总开关 |
| `DwellMs` | `300` | 停稳多久触发（毫秒），80–5000 |
| `HideGraceMs` | `250` | 离开文件项后多久收窗 |
| `PollMs` | `55` | 光标轮询间隔 |
| `MinInvokeIntervalMs` | `220` | 两次触发的硬间隔（Seer IPC 要求 ≥200ms） |
| `PreviewFolders` | `false` | 目录项是否也悬停预览 |
| `SuppressOnModifiers` | `true` | 按住 Ctrl/Shift/Alt 时不触发（避免干扰多选） |
| `ExcludeExtensions` | `[]` | 不触发预览的扩展名，不带点，如 `["mp4","mkv"]` |
| `ExcludePaths` | `[]` | 不触发的路径前缀，如 `["D:\\临时"]` |
| `HideOnClick` | `true` | 点击时立即收窗 |
| `VerifyPreview` | `true` | 触发后回查 Seer 实际预览的文件，不一致就收窗 |
| `VerifyDelayMs` | `700` | 回查延迟，要大于 Seer 追踪刷新间隔 |
| `VerboseLog` | `false` | 写详细日志到 `%APPDATA%\SeerHover\seerhover.log` |
| `DoubleClickUp` | `true` | 资源管理器文件列表空白处双击 = 回到上一层（Alt+↑） |
| `DarkMode` | `null` | 设置窗口与托盘菜单主题；`null` 跟随系统，`true/false` 强制深/浅 |
| `UiScale` | `100` | 界面缩放百分比（80–160），设置窗口与托盘右键菜单的字号 / 间距一起缩放 |
| `SettingsWidth` / `SettingsHeight` | `0` | 设置窗口上次的尺寸（关闭窗口时自动记录，0 = 用默认） |
| `Geometry.Mode` | `cursor` | `cursor` 文件项旁边（自动挑空间更大的方向）/ `fixed` 固定坐标 / `corner` 屏幕角落 |
| `Geometry.SizeMode` | `fixed` | `fixed` 用下面的宽高；`auto` **原比例最大化**（按图片/视频原始比例，放得下就用原尺寸，放不下按屏幕缩，太小则放大到 Seer 最小窗）；`seer` 不干预尺寸（只摆位置） |
| `Geometry.AvoidCursor` | `true` | 浮窗不遮挡鼠标：在右/左/下/上四个方向里挑能放下且空间最大的一侧弹出 |
| `Geometry.Width` / `Height` | `900 / 700` | 仅 `fixed` 模式使用（物理像素），上限 7680×4320（8K），会被夹到当前屏幕尺寸内；下限是 Seer 最小窗口（765×645） |
| `Geometry.Gap` | `16` | `cursor` 模式下与文件项的间距 |
| `Geometry.Corner` | `topright` | `topleft`/`topright`/`bottomleft`/`bottomright` |
| `Geometry.MaxScreenRatio` | `0.9` | 「原比例最大化」时窗口最多占屏幕的比例；固定尺寸不受它限制 |

## 命令行（排查用）

| 命令 | 作用 |
|---|---|
| `--menu-test [x y] [scale%]` | 调试：按托盘同款配色/字号弹一次菜单并自绘成 `%TEMP%\seer-menu.png`（验证深浅色与缩放渲染） |
| `--up-probe [x y]` | 调试：报告「这个点双击会不会被判定为空白处」（窗口类、文件区矩形、是否压在项上、选中数、最终结论） |
| `--settings` | 启动托盘进程并直接弹出图形设置窗口（已在运行时弹窗会失败，改用双击托盘图标） |
| `--media <文件>` | 打印图片/视频的原始像素尺寸（验证「原比例最大化」的取数；图片走 WIC，视频走 Shell 属性） |
| `--status` | 打印 Seer 是否在运行、版本、窗口可见性、当前配置 |
| `--probe` | 实时打印光标下识别到的路径（校验准确率） |
| `--selftest` | 自动在资源管理器窗口里网格扫描，验证「光标 → 路径」链路 |
| `--e2e` | 端到端：放下光标 → 检查 Seer 是否弹出、离开是否收起（需托盘进程在跑） |
| `--e2e-selected` | 复现「先点选 A、再悬停 B」，确认 Seer 显示的是 B（需托盘进程在跑） |
| `--ipc <路径>` | 直接发送一个路径，并回读 Seer 实际预览的文件 |
| `--track [once\|twice\|pin\|sep\|standalone]` | 选中 A 后发送 B，观察是否被追踪切回 A（验证追踪问题） |
| `--items` | 打印枚举到的文件项、矩形，并自检「矩形中心能否命中自己」 |
| `--bench [n]` | 量性能：UIA 扫描耗时（子树 vs 整窗口）、缓存命中、各 Win32 原语的单次成本 |
| `--hide` | 让 Seer 收起预览窗（清理测试残留的预览窗口） |
| `--windows` | 列出 Seer 的所有顶层窗口与标题 |
| `--fix-tracking` | 关闭 Seer 的「跟随选中项」并备份原配置 |
| `--restore-tracking` | 还原 Seer 的「跟随选中项」配置 |
| `--keep-window` | 开启 Seer 的「保持窗口大小位置」（防尺寸跳动） |
| `--restore-window` | 恢复 Seer 默认（窗口大小随内容变化） |
| `--geom-monitor` | 悬停后连续采样窗口尺寸，报告出现后变化了几次（量化「跳」） |
| `--geom-probe [early]` | 探测 Seer 接受哪些尺寸、多久后被改回 |
| `--diag` | 打印资源管理器窗口、当前文件夹、UIA 采样结果 |
| `--uia-tree` | 打印窗口内 `ListItem` 数量与前几个（判断系统是否仍暴露列表项） |

## 已实测的环境与结论

- Seer **微软商店版 4.7.0（40700）/ 4.8.0（40800）**：外部进程 `WM_COPYDATA` 5000 可正常唤起预览，商店版没有 UIPI 拦截。
  4.8.0 下 IPC 5004（是否可见）返回值不可靠，改用本地 `IsWindowVisible` 判断。
- Windows 11 **26H2（build 26300）**：文件列表为 WinUI 控件，`ListItem` 仍暴露（28 项全部带完整文件名与屏幕矩形）。
- 缩放 150%：必须显式 `SetProcessDpiAwarenessContext(-4)`，否则坐标错位 1.5 倍。
- **窗口尺寸跳动的实测数据**：
  - `keep_wnd_sz_pos_mode=0`（默认）：给 900×620 得到 900×645（不是 620，被夹到最小值）；
    图片还会先铺成原始尺寸（1794×1794 / 886×1920 / 1566×2088 都出现过），多轮硬顶就是每秒数次抖动。
  - `keep_wnd_sz_pos_mode=1`：窗口出现即按记忆尺寸，**第二次起 2.5 秒内零变化**（`--geom-monitor` 实测 PASS）。
- Seer 的最小窗口尺寸：150% 缩放下约 **765×645 物理像素**，比它小的设置会被立刻夹回。
- 性能（`--bench` 实测，20 项文件夹）：UIA 枚举 **~19ms**（改版前 75–135ms，因为以前扫整窗口 + 逐元素取属性）；
  索引命中 **0.0003ms/次**；空闲轮询一次 GetCursorPos + GetKeyState **约 0.001ms**。
  光标不动且没有候选时轮询直接短路返回；有候选/正在预览时轮询自动提到 ≤25ms，dwell 计时更准、切换更快。
- 1ms 定时精度只在「预览窗口定位」那几百毫秒内临时开启（`TimerResolution`），进程空闲时不再让整机跑在 1ms 定时上。
- 源文件尺寸探测固定用一根低于普通优先级的后台 STA 线程 + 复用的 Shell 实例，不再「一个文件一根线程」。
- **追踪干扰（实测数据）**：`tracking_file=true` 时，发送 B 后的 250ms 还显示 B，500ms 已切回选中的 A；
  重复发送同样无效。改为 `false` 后，2 秒内稳定显示 B。商店版该键在
  `%LOCALAPPDATA%\Packages\<包名>\LocalCache\Local\Corey\Seer\uwp.ini`（`user.ini` 里没有这一项）。

## 已知限制

- **只对资源管理器窗口生效**，桌面（`Progman`/`WorkerW`）不触发；目录项默认不触发。
- **虚拟目录拿不到真实路径**：搜索结果、压缩包内部、回收站、此电脑等会静默跳过（宁可不弹，也不弹错）。
- 资源管理器开启「隐藏已知扩展名」时，靠 `名字.*` 唯一匹配兜底；同名多扩展会跳过。
- 分辨率/比例差别太大时，`Geometry.Width/Height` 需要重调；低于 Seer 的最小窗口（150% 缩放下 765×645）会被自动抬升。
- 首次开启「保持窗口大小位置」后，第一次预览会从上次的旧尺寸过渡一次（约 10ms），之后固定不变。
- **不能开 Seer 的「跟随选中项」**：开了就会跳回选中项/文件夹第一项。工具会尽量自动关掉，
  但 Seer 每次启动会读回自己的配置，所以如果在 Seer 界面里又勾上，需要再执行一次修复并重启 Seer。
- **不要关掉「保持窗口大小位置」**：关掉后 Seer 会按内容重设窗口，悬停预览的大小会跳（工具只能兜底纠正，仍有约 20–30ms 的过渡）。
- 依赖 **.NET 8 桌面运行时**（本机已装 SDK，自带）。换机器需装 .NET 8 Desktop Runtime。
- Seer 升级后若改了窗口类名或 IPC 命令，需要跟着更新（`SeerIpc.cs` 里集中定义）。

## 建议的 Seer 端设置

- 关掉 Seer 的「窗口跟随鼠标」定位（交给本工具接管，避免和光标抢位置）
- 若要更顺手，可把 `user.ini` 里 `enable_esc_key` 设为 `true`（商店版路径在
  `%LOCALAPPDATA%\Packages\CNABA5E861-...\LocalCache\Local\Corey\Seer\user.ini`），这样按 Esc 随时收窗

## 重新构建

```powershell
cd src\SeerHover
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -o ..\..\dist
```

## 开发与发布

```powershell
# 构建 + 单文件发布到 dist\（版本号会写进程序集）
powershell -ExecutionPolicy Bypass -File .\tools\build.ps1 -Version 1.0.1

# 只编译不发布
powershell -ExecutionPolicy Bypass -File .\tools\build.ps1 -NoPublish
```

`-ExecutionPolicy Bypass` 只对这次进程生效，不修改系统策略（本机默认禁止运行 .ps1）。
若想长期直接运行 `.\tools\build.ps1`，自行执行 `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`。

`dist/` 是本地构建产物，已被 `.gitignore` 排除；**安装包只在 [Releases](https://github.com/lza0312/SeerHover/releases/latest) 发布**，
不要把 exe 提交进仓库。发布流程：`build.ps1` → 本地验证 → 打 tag 并用 `gh release create <tag> dist/SeerHover.exe` 上传资产 → 更新 `CHANGELOG.md`。

推送代码：正常网络直接 `git push`。如果网络只放行 `api.github.com`（`git push` 报 `CONNECT tunnel failed`），用：

```bash
python tools/push-api.py -m "提交说明"
```

脚本用 Git Data API 从 `git ls-files` 构建 blobs/tree/commit，以**远端分支头**为父节点，**不依赖本地 git 历史**。
因此本机与远端的提交 SHA 不同（历史分叉）是正常的，不会造成 non-fast-forward；二进制文件也能原样推送。
凭据依次取 `--token` → `GITHUB_TOKEN` / `GH_TOKEN` → Git Credential Manager → `gh auth token`，权限需要 `repo`。
**如果改动包含 `.github/workflows/*.yml`，凭据还需要 `workflow` scope** —— GitHub 会以 404 拒绝 `git/trees`（不是文件不存在），
此时执行 `gh auth refresh -h github.com -s workflow`，或换用带 `repo + workflow` 的 PAT。
若将来网络恢复想改回 `git push`，先 `git fetch origin && git reset --hard origin/main` 把本地历史对齐。

CI：`.github/workflows/build.yml` 在 Windows Runner 上编译（`-warnaserror`）并上传 `SeerHover.exe` 产物，不自动发 Release。

## 目录结构

```
seer-hover/
├─ .github/workflows/build.yml  # CI：编译校验 + 产物上传
├─ CHANGELOG.md                # 版本记录
├─ LICENSE                     # MIT
├─ tools/
│  ├─ build.ps1                # 构建 / 单文件发布（可注入版本号）
│  ├─ push-api.py              # 只放行 api.github.com 时的推送通道
│  └─ SmokeTest/               # 最小 IPC 冒烟测试（验证 Seer 能否被唤起）
├─ src/SeerHover/
│  ├─ Program.cs            # 入口（单实例、崩溃兜底、启动托盘）
│  ├─ Cli.cs                # 命令行诊断命令（--status / --items / --bench …）
│  ├─ HoverController.cs    # 悬停状态机（dwell / 切换 / 收窗 / 抑制 / 自适应轮询）
│  ├─ HoverResolver.cs      # 光标 → 文件绝对路径
│  ├─ ExplorerItems.cs      # 文件项矩形索引与命中测试（悬停与双击共用同一份，扫描期间不阻塞调用方）
│  ├─ Uia.cs                # 托管 UI Automation 封装（STA 线程 + 批量属性缓存 + 只扫文件列表子树）
│  ├─ Geometry.cs           # 预览窗位置尺寸接管
│  ├─ SeerIpc.cs            # Seer IPC 封装（含 5006 回查）
│  ├─ SeerSettings.cs       # 读写 Seer 配置：关闭「跟随选中项」
│  ├─ SettingsForm.cs       # 图形设置窗口（托盘「设置…」/ 双击托盘图标）
│  ├─ UiControls.cs         # 自绘控件：开关 / 滑杆 / 分段按钮 / 导航 / 圆角按钮
│  ├─ Theme.cs              # 设置窗口配色与字体（深浅两套，可运行时切换）
│  ├─ ExplorerUp.cs         # 资源管理器空白处双击 → Alt+↑（鼠标钩子 + 空白判定）
│  ├─ MediaSize.cs          # 图片/视频原始尺寸探测（WIC + Shell 属性，带缓存）
│  ├─ TrayHost.cs           # 托盘与菜单
│  ├─ AppConfig.cs          # 配置模型
│  └─ ...                   # 详见源码中的分区注释
└─ dist/                    # 本地构建产物（不入库）：SeerHover.exe + 运行时生成的 config.json
```

## 许可

MIT License，见 [LICENSE](LICENSE)。
