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
3. **窗口几何必须提前交给 Seer。** 老版 Seer 没有位置/尺寸 IPC，只能在它按内容撑大后硬顶，会「预览大小来回跳」，
   只能靠 `keep_wnd_sz_pos_mode=1` 兜底。**Seer 4.8.x 起用 V2 IPC 的 `seer.preview.open` 带 `rect`**，
   几何和路径一起发出，从源头禁用 content-fit，跳动问题根治（见下方「V2 IPC」）。
4. **必须关掉 Seer 的「跟随资源管理器选中项」。** 详见下一节——开着它，悬停会一直跳回上次点选的文件。

## 关键前提一：窗口尺寸（V2 IPC 已自动处理）

- **Seer 4.8.x（作者测试版）**：走 V2 IPC 的 `seer.preview.open` 并附带 `rect`，几何与路径同时下发，
  Seer 不再按内容自动缩放窗口。**不需要**改 `keep_wnd_sz_pos_mode`，空格预览仍按图片自适应。
- **老版 Seer（无 V2）**：回退到 5000 + `SetWindowPos` 纠偏，仍需打开 `keep_wnd_sz_pos_mode=1`
  （在 `user.ini` / `uwp.ini`；Seer 4.8.x 则在 `%LOCALAPPDATA%\Corey\Seer\config.ini`）。
  工具仅在 V2 不可用时才自动改它，带一次性备份。

手动命令：`SeerHover.exe --keep-window`（开启）/ `--restore-window`（恢复默认）。
诊断 V2 是否接管：`SeerHover.exe --v2`（会打印版本、tracking、rect 回读结果）。

> 老版的副作用：打开 keep 模式后**空格预览也不再按图片大小自适应**。V2 路径下无此问题。

## 关键前提二：Seer 的「跟随选中项」必须关闭

Seer 有个选项 **Track selected file in Explorer**（配置文件里的 `tracking_file`）。
配置文件位置随版本变：4.8.x 在 `%LOCALAPPDATA%\Corey\Seer\config.ini`，老版在 `user.ini` / `uwp.ini`。
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

想恢复 Seer 原来的跟随行为：`SeerHover.exe --restore-tracking`（会把对应配置文件的 `.seerhover.bak` 还原回去），
或直接在 Seer 设置里重新勾选。V2 可用时以 `seer.app.getState.tracking` 为权威状态；实测 Seer 4.8.x 能热加载该改动，无需重启。


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
  尺寸策略（**固定尺寸 / 原比例最大化 / 跟随 Seer**）、宽度 / 高度 / 最大占屏比、
  **按类别的尺寸**（图片 / 视频 / 音频 / 网页 / 文本 / 压缩包 / 文档 / 其他，
  每类可单独选「跟随全局 / 固定尺寸 / 原比例最大化 / Seer 原始」，
  例如图片最大化、文本按 Seer 原始大小）
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

**原比例最大化**：按图片/视频的原始像素比例显示。在「光标旁」模式下，会同时判断光标
上 / 下 / 左 / 右四个方向到屏幕边缘的可用空间，选出空间最大的一侧，再把源图**等比放大**
到刚好装进这块空间（同时受整屏 ×「最大占屏比」限制），窗口边缘与光标保持「间距」设定的
距离且不压住鼠标。宽高始终用同一个缩放系数，不会分别夹取导致比例歪掉；照片的 EXIF
旋转标记也会参与尺寸判定（手机竖拍不会按横图算出宽窗）。

Seer 在 V2 `rect` 模式下有个限制：**比窗口小的图片只按 1:1 居中显示，不会放大填满**，
窗口比图大就会留黑边。设置里的「放大图片填满窗口」默认开启：小图会先在
`%TEMP%\SeerHover\Upscaled\` 生成一份高质量等比放大的临时副本，再交给 Seer 缩小填满
窗口，视觉上就是原图按自身比例放大且无黑边；关掉后窗口严格不超过原图像素。副本按
「路径 + 修改时间 + 目标尺寸」缓存，视频和 GIF 不走这条路径，启动时自动清理两天前的旧文件。
源尺寸取自图片文件头（GDI+/WIC）与 Shell 属性（视频），悬停候选阶段就会预热，
触发时命中缓存不拖慢弹出。某一类文件不想受这套几何控制时（比如文本、音频按 Seer
原始大小显示），在「设置 → 窗口 → 按类别的尺寸」里把该类别设为「Seer 原始」即可，
悬停这类文件时不再发送 rect，完全交给 Seer 自己的 content-fit。

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
| `HideGraceMs` | `250` | 离开文件项后多久收窗；移动方向朝预览窗时不计时（方向感知宽限），改朝反方向或停住超时才收 |
| `PollMs` | `55` | 光标轮询间隔 |
| `MinInvokeIntervalMs` | `220` | 两次触发的硬间隔（Seer IPC 要求 ≥200ms） |
| `PreviewFolders` | `false` | 目录项是否也悬停预览 |
| `SuppressOnModifiers` | `true` | 按住 Ctrl/Shift/Alt 时不触发（避免干扰多选） |
| `ExcludeExtensions` | `[]` | 不触发预览的扩展名，不带点，如 `["mp4","mkv"]` |
| `ExcludePaths` | `[]` | 不触发的路径前缀，如 `["D:\\临时"]` |
| `HideOnClick` | `true` | 点击时立即收窗；点击落在 Seer 预览窗上时不收（媒体控件可正常拖动） |
| `VerifyPreview` | `true` | 触发后回查 Seer 实际预览的文件，不一致就收窗 |
| `VerifyDelayMs` | `700` | 回查延迟，要大于 Seer 追踪刷新间隔 |
| `VerboseLog` | `false` | 写详细日志到 `%APPDATA%\SeerHover\seerhover.log` |
| `DoubleClickUp` | `true` | 资源管理器文件列表空白处双击 = 回到上一层（Alt+↑） |
| `DarkMode` | `null` | 设置窗口与托盘菜单主题；`null` 跟随系统，`true/false` 强制深/浅 |
| `UiScale` | `100` | 界面缩放百分比（80–160），设置窗口与托盘右键菜单的字号 / 间距一起缩放 |
| `SettingsWidth` / `SettingsHeight` | `0` | 设置窗口上次的尺寸（关闭窗口时自动记录，0 = 用默认） |
| `Geometry.Mode` | `cursor` | `cursor` 文件项旁边（自动挑空间更大的方向）/ `fixed` 固定坐标 / `corner` 屏幕角落 |
| `Geometry.SizeMode` | `fixed` | `fixed` 用下面的宽高；`auto` **原比例最大化**（按原始比例，在光标可用空间最大的一侧等比放大到尽量占满）；`seer` 不干预尺寸（只摆位置） |
| `Geometry.AvoidCursor` | `true` | 浮窗不遮挡鼠标：在右/左/下/上四个方向里挑能放下且空间最大的一侧弹出 |
| `Geometry.Width` / `Height` | `900 / 700` | 仅 `fixed` 模式使用（物理像素），上限 7680×4320（8K），会被夹到当前屏幕尺寸内；下限是 Seer 最小窗口（765×645） |
| `Geometry.Gap` | `16` | `cursor` 模式下窗口边缘与光标的最小距离（不遮挡鼠标） |
| `Geometry.Corner` | `topright` | `topleft`/`topright`/`bottomleft`/`bottomright` |
| `Geometry.MaxScreenRatio` | `0.9` | 「原比例最大化」时窗口最多占屏幕的比例；固定尺寸不受它限制 |
| `Geometry.UpscaleToFill` | `true` | 小图生成临时放大副本，让 Seer 填满 rect；关闭则窗口不超过原图尺寸 |
| `Geometry.CategoryModes` | `{}` | 按类别覆盖尺寸策略：key 为 `image`/`video`/`audio`/`web`/`text`/`archive`/`document`/`other`，值为 `auto`/`seer`/`fixed`；缺失的类别沿用全局 `Geometry.SizeMode`。设置 → 窗口 → 按类别的尺寸 里可视化配置 |
| `Geometry.CategorySizes` | `{}` | 按类别覆盖固定宽高（仅类别为 `fixed` 时生效）：值为 `[宽, 高]`，如 `{"text":[1200,800]}`；缺失沿用全局 `Geometry.Width/Height`，自动夹到 765×645 ~ 7680×4320 |

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
| `--tabs` | 列出资源管理器每个标签的 TabItem、Shell 路径、DefView 归属与项数（排查多标签预览失效） |
| `--resolve-at` | 打印当前光标位置的完整解析链路：选中标签、文件夹、DefView 归属、命中项、最终路径 |
| `--select-tab <序号>` | 诊断用：切换资源管理器标签后立即回读解析结果（0 = 第一个标签） |
| `--geom-plan <图片> [x y]` | 给定图片与光标位置，打印四方向候选尺寸、最终矩形、宽高比与光标间距（不弹窗） |
| `--shot-preview <图片> [x y w h] [png]` | 按指定 rect 打开预览并截图，直接检查图片是否填满窗口、有没有黑边 |
| `--upscale <图片> <w> <h> [png]` | 生成/查看放大副本，验证「放大图片填满窗口」的实际输出 |
| `--settings-shot [png] [页序号]` | 弹出设置窗并截图（页序号 0–3），排查文字截断 |
| `--crop <输入> <输出> x y w h` | 把截图局部放大 2 倍，便于检查文字渲染 |
| `--wrap-test [png] [文字]` | 单独渲染一段说明文字，验证自绘折行 |
| `--bench [n]` | 量性能：UIA 扫描耗时（子树 vs 整窗口）、缓存命中、各 Win32 原语的单次成本 |
| `--hide` | 让 Seer 收起预览窗（清理测试残留的预览窗口） |
| `--windows` | 列出 Seer 的所有顶层窗口与标题 |
| `--fix-tracking` | 关闭 Seer 的「跟随选中项」并备份原配置 |
| `--restore-tracking` | 还原 Seer 的「跟随选中项」配置 |
| `--keep-window` | 开启 Seer 的「保持窗口大小位置」（防尺寸跳动） |
| `--restore-window` | 恢复 Seer 默认（窗口大小随内容变化） |
| `--geom-monitor` | 悬停后连续采样窗口尺寸，报告出现后变化了几次（量化「跳」） |
| `--switch-test <A> <B>` | 打开 A 后切到 B，采样切换时刻的窗口 rect（验证 V2 直开无中间帧） |
| `--legacy-test <文件>` | legacy 路径时间线：预摆位 → 显示瞬间的尺寸变化（验证「先盯梢再开窗」） |
| `--media-shot <音/视频>` | 打开音视频预览并分时刻截图（排查媒体控件显示） |
| `--make-alpha <png>` | 生成一张带透明图层的测试 PNG |
| `--bg-next` | 给当前可见的预览窗发一次 B 键（图片背景循环：棋盘格/黑/白/灰/透明） |
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
  重复发送同样无效。改为 `false` 后，2 秒内稳定显示 B。配置位置：Seer 4.8.x 在
  `%LOCALAPPDATA%\Corey\Seer\config.ini`；商店版在
  `%LOCALAPPDATA%\Packages\<包名>\LocalCache\Local\Corey\Seer\uwp.ini`。
  V2 可用时用 `seer.app.getState.tracking` 直接读实时状态，不依赖文件。

## 已知限制

- **只对资源管理器窗口生效**，桌面（`Progman`/`WorkerW`）不触发；目录项默认不触发。
- **虚拟目录拿不到真实路径**：搜索结果、压缩包内部、回收站、此电脑等会静默跳过（宁可不弹，也不弹错）。
- 资源管理器开启「隐藏已知扩展名」时，靠 `名字.*` 唯一匹配兜底；同名多扩展会跳过。
- 分辨率/比例差别太大时，`Geometry.Width/Height` 需要重调；低于 Seer 的最小窗口（150% 缩放下 765×645）会被自动抬升。
  V2 实测最小尺寸为 510×430 × DPI（150% 下约 765×645），请求 900×600 会被夹到 900×645。
- V2 路径下窗口尺寸由 `rect` 一次定好，不再有「保持窗口大小位置」首次预览的旧尺寸过渡。
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
