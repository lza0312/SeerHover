# 更新日志

本项目版本号遵循 `主版本.次版本.修订号`；每个版本对应一个 GitHub Release，安装包只在 Releases 发布。

## [1.0.2] - 2026-11-18

适配 Seer 4.8.x Win32 测试版（作者版），用官方 V2 IPC 消除预览跳动。旧版 Seer 自动回退到原路径。

### 新增 / 适配

- **接入 Seer V2 Named Pipe JSON-RPC**（`SeerV2.cs`）：按官方 SDK 用 `\\.\pipe\Seer.IPC.v2.<SessionId>`，权限掩码 `0x0012018B`、SQOS 防冒充、server session / owner SID 校验、失败 2 秒冷却与 `-32002` 重连重试。
- **悬停预览改用 `seer.preview.open` 带 `rect`**：几何和路径一起发给 Seer，从源头禁掉 content-fit 自动缩放。实测 3000×2000 大图请求 900×600，1.5 秒内窗口尺寸位置纹丝不动（仅被最小高度夹到 900×645），彻底消除「先铺成原始尺寸再缩回」的跳动。V2 不可用时自动回退 5000 + SetWindowPos 纠偏。
- **适配新版配置路径**：Seer 4.8.x 把 `tracking_file` / `keep_wnd_sz_pos_mode` 从 `user.ini` 挪到 `%LOCALAPPDATA%\Corey\Seer\config.ini`，候选文件列表已包含它。V2 可用时以 `seer.app.getState.tracking` 为权威状态；实测新版能热加载 tracking 改动，无需重启 Seer。
- **不再擅自改全局 keep 模式**：V2 可用时跳过 `keep_wnd_sz_pos_mode` 修改（rect 已接管尺寸），避免影响空格预览的自适应行为。
- 新增诊断命令 `--v2 [路径]`：连通性、版本、tracking、带 rect 预览与几何回读自检。

## [1.0.1] - 2026-11-12

稳定性与可维护性改进，**功能行为与 1.0.0 完全一致**，用户配置无需改动。

### 修复 / 加固

- 崩溃日志与运行日志统一到 `%APPDATA%\SeerHover\`；上次异常退出时启动时气泡提示一次，托盘菜单新增「打开崩溃日志」。此前崩溃日志写在 `%TEMP%`，且退出没有任何提示。
- 配置写入失败不再静默：`AppConfig.Save()` 返回状态，设置窗口「应用 / 确认」在落盘失败时保持打开并弹出说明，避免「以为保存了、重启全丢」。
- 开机自启快捷方式校验 `TargetPath`：exe 换目录 / 覆盖新版后自动重建并提示，不再出现「开关显示已开启但登录不启动」。
- `Logger.Error` 对同一条错误按 2 秒节流，异常循环不再把日志刷爆；字典超过 128 条自动清理，避免带变量的异常文本撑大内存。
- 单实例 Mutex 加 `Local\` 前缀与 SessionId，多登录会话 / 多用户下不再互相误判。

### 性能

- 图片尺寸探测先用 GDI+ 只读头，命中即不加载 WPF/WIC 程序集；HEIC / WebP 等 GDI+ 不认识的格式仍退回 WIC，格式覆盖不下降。

### 工程化

- `Program.cs` 的 24 个诊断命令由手写 switch 改为命令表，新增命令只改一处。
- 新增 `tools/build.ps1`（构建 + 单文件发布，支持 `-Version` 注入）与 `.github/workflows/build.yml`（Windows Runner 编译校验 + 产物上传）。
- 新增 `tools/push-api.py`：网络只放行 `api.github.com` 时，用 Git Data API 推送整棵工作树（含二进制），以远端分支为父节点，本地历史分叉不影响推送。
- 版本号可由 `-p:SeerHoverVersion=` 注入，不再写死在 csproj。

## [1.0.0] - 2026-11-02

首个公开版本。

- 鼠标悬停资源管理器文件 300ms 触发 Seer 预览，移开自动收起；走 Seer 官方 IPC（`WM_COPYDATA`），不模拟按键。
- UIA 枚举文件项 + 物理坐标命中测试，Per-Monitor V2 DPI 感知；自适应轮询与空闲短路。
- 预览窗几何：原比例最大化、避让鼠标、多屏 / 带鱼屏适配、尺寸可手输（上限 8K）。
- 托盘右键图形设置（常规 / 窗口 / 过滤 / 高级），深色模式、界面缩放 80%–160%。
- 双击资源管理器空白处回到上一层目录（等同 Alt+↑）。
- 自动修复 Seer 的「跟随选中项」与「保持窗口大小位置」两项前提。
