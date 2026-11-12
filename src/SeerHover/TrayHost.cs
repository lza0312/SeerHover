using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Forms.Application;

namespace SeerHover;

internal sealed class TrayHost : ApplicationContext
{
    private readonly AppConfig _cfg;
    private readonly SeerIpc _ipc = new();
    private readonly HoverController _controller;
    private readonly ExplorerUp _explorerUp;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly Control _invoker;
    private SettingsForm? _settings;

    public TrayHost(AppConfig cfg, bool openSettings = false)
    {
        _cfg = cfg;
        ApplyTheme();
        _controller = new HoverController(cfg, _ipc);
        _controller.StateChanged += OnStateChanged;
        _controller.PreviewMismatch += OnPreviewMismatch;

        // 资源管理器空白处双击 = 回到上一层（开关在设置里，实时读取配置；钩子常驻，代价可忽略）
        _explorerUp = new ExplorerUp(() => _cfg.DoubleClickUp);
        _explorerUp.Start();

        _statusItem = new ToolStripMenuItem("等待悬停…") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("暂停悬停预览", null, (_, _) => TogglePause()) { CheckOnClick = false };

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings())
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("重载配置", null, (_, _) => ReloadConfig()));
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripMenuItem("打开崩溃日志", null, (_, _) => OpenCrashLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        // 菜单配色跟随设置里的深色模式（每次弹出前重新套一遍，切换后无需重启）
        MenuTheme.Apply(menu);
        menu.Opening += (_, _) => MenuTheme.Apply(menu);

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "SeerHover - 悬停预览",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        // 需要一个隐藏控件来把回调切回 UI 线程
        _invoker = new Control();
        _invoker.CreateControl();
        var handle = _invoker.Handle;

        _controller.Start();
        Logger.Info("SeerHover started");
        NotifyPreviousCrash();

        // Seer 配置检查放后台：即使 Seer 卡住（IPC 超时）也不拖慢托盘出现
        _ = Task.Run(() =>
        {
            try { FixTracking(manual: false); }
            catch (Exception ex) { Logger.Error("startup tracking check: " + ex.Message); }
            try { EnsureKeepWindow(manual: false); }
            catch (Exception ex) { Logger.Error("startup keep-window check: " + ex.Message); }
            try { EnsureStartupLinkTarget(); }
            catch (Exception ex) { Logger.Error("startup link check: " + ex.Message); }
        });

        if (openSettings)
        {
            // 等托盘起来再弹设置窗，避免和开机自启抢焦点
            var t = new System.Windows.Forms.Timer { Interval = 300 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); OpenSettings(); };
            t.Start();
        }
    }

    // ── 设置窗口 ────────────────────────────────────────────────────

    /// <summary>上次异常退出时，启动后提示一次；崩溃记录已由 TakeCrashReport 归档，不会重复弹。</summary>
    private void NotifyPreviousCrash()
    {
        string? report = Logger.TakeCrashReport();
        if (report == null) return;
        Logger.Error("previous crash report archived");
        var t = new System.Windows.Forms.Timer { Interval = 1200 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            _tray.ShowBalloonTip(9000, "SeerHover",
                "检测到上次异常退出。崩溃记录已保存，可在托盘菜单「打开崩溃日志」查看。",
                ToolTipIcon.Warning);
        };
        t.Start();
    }

    /// <summary>深浅色 + 界面缩放一起套用。托盘菜单每次弹出前都会重新读，所以不用重启。</summary>
    private void ApplyTheme()
    {
        Theme.Apply(_cfg.DarkMode ?? Theme.DetectSystemDark());
        Theme.SetScale(_cfg.UiScale / 100f);
    }

    private void OpenSettings()
    {
        try
        {
            if (_settings != null && !_settings.IsDisposed)
            {
                if (_settings.WindowState == FormWindowState.Minimized)
                    _settings.WindowState = FormWindowState.Normal;
                _settings.Activate();
                _settings.BringToFront();
                return;
            }

            _settings = new SettingsForm(_cfg, new SettingsHost
            {
                Command = Command,
                IsStartupEnabled = IsStartupEnabled,
                SetStartup = SetStartup,
                Apply = ApplySettings,
                PreviewScale = scale => Theme.SetScale(scale),
                EndPreview = () => Theme.SetScale(_cfg.UiScale / 100f),
                RememberSize = RememberSettingsSize
            });
            _settings.FormClosed += (_, _) =>
            {
                _settings?.Dispose();
                _settings = null;
            };
            _settings.Show();
            _settings.Activate();
            Logger.Info("settings opened");
        }
        catch (Exception ex)
        {
            Logger.Error("open settings failed: " + ex.Message);
            _tray.ShowBalloonTip(4000, "SeerHover", "打开设置窗口失败：" + ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>设置窗口点「应用 / 确认」后调用。返回 false 表示配置未落盘（设置窗会保持打开）。</summary>
    private bool ApplySettings(AppConfig next, bool quiet)
    {
        _cfg.CopyFrom(next);
        bool saved = _cfg.Save();
        ApplyTheme();
        Logger.EnableVerbose(_cfg.VerboseLog);
        _controller.ForceHide();
        _controller.Restart();
        _toggleItem.Text = _controller.Paused ? "恢复悬停预览" : "暂停悬停预览";
        Logger.Info(quiet ? "settings applied (stay open)" : "settings applied");

        if (!saved)
        {
            Logger.Error("settings apply: config.json write failed");
            _tray.ShowBalloonTip(6000, "SeerHover",
                "设置已生效，但 config.json 写入失败，重启后会丢失。", ToolTipIcon.Error);
            return false;
        }
        if (!quiet) _tray.ShowBalloonTip(1500, "SeerHover", "设置已保存并生效。", ToolTipIcon.Info);
        return true;
    }

    /// <summary>设置窗口关闭时记住大小（只改这两项并落盘，不走重启轮询的完整生效流程）。</summary>
    private void RememberSettingsSize(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (_cfg.SettingsWidth == w && _cfg.SettingsHeight == h) return;
        _cfg.SettingsWidth = w;
        _cfg.SettingsHeight = h;
        _cfg.Save();
    }

    /// <summary>设置窗口里的一排按钮（Seer 联动 / 打开文件）。返回要展示给用户的文字。</summary>
    private string Command(string cmd)
    {
        switch (cmd)
        {
            case "fix-tracking":
                return FixTracking(manual: false);
            case "restore-tracking":
                {
                    var restored = SeerSettings.RestoreBackup();
                    return restored.Count > 0
                        ? "已把 Seer 配置还原成备份（跟随选中项会重新打开）。\n重启 Seer 后生效；注意开启后悬停预览会跳回选中项。"
                        : "没有找到备份文件，无法还原。";
                }
            case "keep-window":
                return EnsureKeepWindow(manual: false);
            case "restore-window":
                {
                    var changed = SeerSettings.DisableKeepWindow();
                    return changed.Count > 0
                        ? "已关闭 Seer 的「保持窗口大小位置」，窗口恢复为随内容自适应。\n重启 Seer 后生效；悬停预览的尺寸可能出现跳动。"
                        : "没有需要修改的 Seer 配置（可能本来就是自适应）。";
                }
            case "open-config":
                OpenConfig();
                return string.Empty;
            case "open-log":
                OpenLog();
                return string.Empty;
            default:
                return string.Empty;
        }
    }

    private void OnPreviewMismatch(string path)
    {
        Logger.Info("preview mismatch event: " + path);
        Post(() => FixTracking(manual: false, fromMismatch: true));
    }

    /// <summary>把回调切回 UI 线程（后台检查线程要弹气泡时用）。</summary>
    private void Post(Action action)
    {
        if (!_invoker.IsHandleCreated) return;
        try { _invoker.BeginInvoke(action); } catch { }
    }

    /// <summary>
    /// 确保 Seer 开着「保持窗口大小位置」。
    /// 关闭时（默认）它每次显示都按内容重设大小：图片会先撑成原始尺寸，
    /// 我们把窗口缩到设定尺寸后它又会撑回去 —— 表现就是「预览大小来回跳」。
    /// </summary>
    private string EnsureKeepWindow(bool manual)
    {
        bool enabled = false;
        foreach (var f in SeerSettings.CandidateFiles())
        {
            if (SeerSettings.ReadKeepMode(f) == 1) enabled = true;
        }

        if (enabled)
        {
            if (manual)
                Post(() => _tray.ShowBalloonTip(2500, "SeerHover", "Seer 已开启「保持窗口大小位置」，预览尺寸不会跳。", ToolTipIcon.Info));
            return "Seer 已开启「保持窗口大小位置」，预览尺寸不会跳。";
        }

        var changed = SeerSettings.EnableKeepWindow();
        Logger.Info($"keep window: changed={changed.Count}");

        string msg = changed.Count > 0
            ? "已开启 Seer 的「保持窗口大小位置」，窗口尺寸不再随内容跳动。\n重启 Seer 后完全生效（首次预览会从旧尺寸过渡一次，之后固定）。"
            : "无法写入 Seer 配置。请在 Seer 设置里勾选「保持窗口大小位置」（Keep window size & position），否则预览大小会跳动。";
        if (manual || changed.Count == 0)
        {
            var icon = changed.Count > 0 ? ToolTipIcon.Info : ToolTipIcon.Warning;
            Post(() => _tray.ShowBalloonTip(9000, "SeerHover", msg, icon));
        }
        return msg;
    }

    /// <summary>确保 Seer 的 tracking 处于关闭状态；改完需要重启 Seer 才生效。</summary>
    private string FixTracking(bool manual, bool fromMismatch = false)
    {
        bool iniOn = false;
        foreach (var f in SeerSettings.CandidateFiles())
            if (SeerSettings.ReadTracking(f) == true) iniOn = true;

        bool liveOn = _ipc.IsRunning && _ipc.IsTrackingEnabled;
        if (!iniOn && !liveOn)
        {
            if (manual) Post(() => _tray.ShowBalloonTip(2000, "SeerHover", "Seer「跟随选中项」已关闭，无需处理。", ToolTipIcon.Info));
            return "Seer「跟随选中项」已关闭，无需处理。";
        }

        var changed = SeerSettings.DisableTracking();
        string msg = changed.Count > 0
            ? "已关闭 Seer 的「跟随选中项」。请重启 Seer 后生效（否则预览仍会跳回选中项）。"
            : "Seer 仍开着「跟随选中项」，但配置写不进去。请在 Seer 设置里手动取消勾选 Track selected file in Explorer。";
        Logger.Info("fix tracking: changed=" + changed.Count + " liveOn=" + liveOn);
        if (manual || fromMismatch || changed.Count == 0)
        {
            var icon = changed.Count > 0 ? ToolTipIcon.Info : ToolTipIcon.Warning;
            string title = fromMismatch ? "预览文件不一致" : "SeerHover";
            Post(() => _tray.ShowBalloonTip(8000, title, msg, icon));
        }
        return msg;
    }

    private void OnStateChanged(string state)
    {
        void Update()
        {
            if (state == "idle")
            {
                _statusItem.Text = "等待悬停…";
                _tray.Text = _controller.Paused ? "SeerHover - 已暂停" : "SeerHover - 悬停预览";
            }
            else
            {
                _statusItem.Text = "预览中: " + Path.GetFileName(state);
                _tray.Text = "SeerHover - " + Truncate(Path.GetFileName(state), 40);
            }
        }

        if (_invoker.IsHandleCreated)
        {
            try { _invoker.BeginInvoke(Update); } catch { }
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private void TogglePause()
    {
        _controller.Paused = !_controller.Paused;
        if (_controller.Paused) _controller.ForceHide();
        _toggleItem.Text = _controller.Paused ? "恢复悬停预览" : "暂停悬停预览";
        _statusItem.Text = _controller.Paused ? "已暂停" : "等待悬停…";
        _tray.Text = _controller.Paused ? "SeerHover - 已暂停" : "SeerHover - 悬停预览";
        Logger.Info("paused=" + _controller.Paused);
    }

    private void ReloadConfig()
    {
        var fresh = AppConfig.LoadOrCreate();
        _cfg.CopyFrom(fresh);
        ApplyTheme();
        Logger.EnableVerbose(_cfg.VerboseLog);
        _controller.Restart();
        _tray.ShowBalloonTip(1500, "SeerHover", "配置已重载", ToolTipIcon.Info);
    }

    private void OpenConfig()
    {
        try
        {
            if (!File.Exists(AppConfig.Path)) _cfg.Save();
            Process.Start(new ProcessStartInfo(AppConfig.Path) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error("open config failed: " + ex.Message); }
    }

    private void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(Logger.Directory);
            string p = Path.Combine(Logger.Directory, "seerhover.log");
            if (!File.Exists(p)) File.WriteAllText(p, "");
            Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error("open log failed: " + ex.Message); }
    }

    private void OpenCrashLog()
    {
        try
        {
            Directory.CreateDirectory(Logger.Directory);
            string p = Logger.CrashLogPath;
            string archived = p + ".1";
            string show = File.Exists(p) ? p : (File.Exists(archived) ? archived : p);
            if (!File.Exists(show)) File.WriteAllText(show, "（暂无崩溃记录）\n");
            Process.Start(new ProcessStartInfo(show) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error("open crash log failed: " + ex.Message); }
    }

    private static string StartupLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "SeerHover.lnk");

    private static string CurrentExe =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    private static string StartupTargetOf(string link)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return string.Empty;
        dynamic shell = Activator.CreateInstance(t)!;
        dynamic sc = shell.CreateShortcut(link);
        return (string)(sc.TargetPath ?? string.Empty);
    }

    /// <summary>只判「文件存在」不够：exe 换目录后 .lnk 仍在但指向旧路径，开机自启会静默失效。</summary>
    private static bool IsStartupEnabled()
    {
        try { return File.Exists(StartupLink); }
        catch { return false; }
    }

    /// <summary>启动时校验快捷方式目标；不一致就重建，并提示一次。必须在 UI 线程写 COM。</summary>
    private void EnsureStartupLinkTarget()
    {
        string link = StartupLink;
        if (!File.Exists(link)) return;
        string target;
        try { target = StartupTargetOf(link); }
        catch (Exception ex) { Logger.Error("startup link read: " + ex.Message); return; }
        if (string.IsNullOrWhiteSpace(target)) return;   // 读不到就别乱动用户的快捷方式

        string exe = CurrentExe;
        bool same = string.Equals(Path.GetFullPath(target), Path.GetFullPath(exe),
            StringComparison.OrdinalIgnoreCase);
        if (same) return;

        Logger.Info("startup link points to old exe, repairing: " + target);
        Post(() =>
        {
            SetStartup(true);
            _tray.ShowBalloonTip(6000, "SeerHover",
                "检测到程序位置变化，已自动修复开机自启快捷方式。", ToolTipIcon.Info);
        });
    }

    private void SetStartup(bool on)
    {
        string link = StartupLink;
        try
        {
            if (!on)
            {
                if (File.Exists(link)) File.Delete(link);
                Logger.Info("startup disabled");
                return;
            }

            string exe = CurrentExe;
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null)
            {
                Logger.Error("WScript.Shell 不可用，无法创建开机自启快捷方式");
                return;
            }
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = exe;
            sc.WorkingDirectory = Path.GetDirectoryName(exe);
            sc.WindowStyle = 7;
            sc.Save();
            Logger.Info("startup enabled: " + exe);
        }
        catch (Exception ex)
        {
            Logger.Error("startup toggle failed: " + ex.Message);
            _tray.ShowBalloonTip(4000, "SeerHover", "修改开机自启失败：" + ex.Message, ToolTipIcon.Error);
        }
    }

    private void ExitApp()
    {
        _controller.ForceHide();
        _controller.Stop();
        _controller.Dispose();
        _explorerUp.Dispose();
        _ipc.Dispose();
        if (_settings != null && !_settings.IsDisposed) _settings.Close();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settings?.Dispose();
            _controller?.Dispose();
            _explorerUp?.Dispose();
            _tray?.Dispose();
            _invoker?.Dispose();
        }
        base.Dispose(disposing);
    }
}
