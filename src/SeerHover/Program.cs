using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace SeerHover;

internal static class Program
{
    [STAThread]
    private static void CrashLog(string source, Exception? ex)
    {
        try
        {
            string p = Path.Combine(Path.GetTempPath(), "seerhover-crash.log");
            File.AppendAllText(p, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}\n{ex}\n\n");
        }
        catch { }
    }

    private static int Main(string[] args)
    {
        // 必须在任何窗口/坐标调用之前设置
        Native.EnablePerMonitorDpi();

        if (args.Length > 0)
        {
            Native.AttachParentConsole();
            switch (args[0].ToLowerInvariant())
            {
                case "--probe": return Cli.Run(Cli.Probe, "probe");
                case "--selftest": return Cli.Run(Cli.SelfTest, "selftest");
                case "--diag": return Cli.Run(Cli.Diag, "diag");
                case "--uia-tree": return Cli.Run(Cli.UiaTree, "uiatree");
                case "--geom-probe": return Cli.Run(() => Cli.GeomProbe(args), "geomprobe");
                case "--geom-monitor": return Cli.Run(Cli.GeomMonitor, "geommon");
                case "--e2e": return Cli.Run(Cli.E2E, "e2e");
                case "--e2e-selected": return Cli.Run(Cli.E2ESelected, "e2esel");
                case "--status": return Cli.Run(Cli.Status, "status");
                case "--items": return Cli.Run(Cli.Items, "items");
                case "--ipc": return Cli.Run(() => Cli.IpcTest(args.Length > 1 ? args[1] : null), "ipc");
                case "--media": return Cli.Run(() => Cli.MediaInfo(args.Length > 1 ? args[1] : null), "media");
                case "--windows": return Cli.Run(Cli.SeerWindows, "windows");
                case "--fix-tracking": return Cli.Run(Cli.FixTracking, "fixtracking");
                case "--keep-window": return Cli.Run(Cli.KeepWindow, "keepwindow");
                case "--restore-window": return Cli.Run(Cli.RestoreWindow, "restorewindow");
                case "--restore-tracking": return Cli.Run(Cli.RestoreTracking, "restore");
                case "--track": return Cli.Run(() => Cli.TrackTest(args.Length > 1 ? args[1] : "once"), "track");
                case "--install-startup": return Cli.Run(Cli.InstallStartup, "install");
                case "--uninstall-startup": return Cli.Run(Cli.UninstallStartup, "uninstall");
                case "--menu-test": return Cli.Run(() => MenuTest(args), "menutest");
                case "--up-probe": return Cli.Run(() => Cli.UpProbe(args), "upprobe");
                case "--bench": return Cli.Run(() => Cli.Bench(args), "bench");
                case "--hide": return Cli.Run(Cli.HideSeer, "hide");
            }
        }

        using var mutex = new Mutex(true, "SeerHover.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("SeerHover 已在运行（见系统托盘）。", "SeerHover",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        bool openSettings = args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));

        var cfg = AppConfig.LoadOrCreate();
        Logger.Init(cfg.VerboseLog);

        // 崩溃兜底：托盘进程没有控制台，异常必须落盘才看得到
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog("Task", e.Exception);
            e.SetObserved();
        };
        Application.ThreadException += (_, e) => CrashLog("UI", e.Exception);

        // 注意：以前这里全局 TimeBeginPeriod(1)，会让整机 24 小时跑在 1ms 定时精度上（更耗电、唤醒更多）。
        // 现在只由 GeometryController 在「预览窗口定位」那几百毫秒内临时提高精度。
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayHost(cfg, openSettings));
        return 0;
    }

    /// <summary>调试：--menu-test [x y] [scale%]，按托盘同款配色/字号弹出一次菜单（验证深浅色与缩放），4 秒后自动关闭。</summary>
    private static int MenuTest(string[] args)
    {
        var cfg = AppConfig.LoadOrCreate();
        if (args.Length > 3 && int.TryParse(args[3], out var sc)) cfg.UiScale = Math.Clamp(sc, 80, 160);
        Theme.SetScale(cfg.Scale);
        Theme.Apply(cfg.DarkMode ?? Theme.DetectSystemDark());

        int x = args.Length > 1 && int.TryParse(args[1], out var px) ? px : 200;
        int y = args.Length > 2 && int.TryParse(args[2], out var py) ? py : 200;

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem("预览中: photo.jpg") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("设置…") { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("暂停悬停预览"));
        menu.Items.Add(new ToolStripMenuItem("重载配置"));
        menu.Items.Add(new ToolStripMenuItem("打开日志"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出"));
        MenuTheme.Apply(menu);

        var form = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Opacity = 0,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(0, 0, 1, 1)
        };
        menu.AutoClose = false;
        form.Shown += (_, _) => menu.Show(form, new Point(x, y));

        // 1.2s 后把菜单自绘成 PNG（供自动化验证），再退出
        var t = new System.Windows.Forms.Timer { Interval = 1200 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try
            {
                using var bmp = new Bitmap(Math.Max(1, menu.Width), Math.Max(1, menu.Height));
                menu.DrawToBitmap(bmp, new Rectangle(0, 0, menu.Width, menu.Height));
                bmp.Save(Path.Combine(Path.GetTempPath(), "seer-menu.png"));
                Console.WriteLine("saved: " + Path.Combine(Path.GetTempPath(), "seer-menu.png"));
            }
            catch (Exception ex) { Console.WriteLine("capture failed: " + ex.Message); }
            finally { menu.Close(); form.Close(); }
        };
        t.Start();

        Application.Run(form);
        return 0;
    }
}
