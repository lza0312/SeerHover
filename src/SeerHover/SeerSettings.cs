namespace SeerHover;

/// <summary>
/// Seer 的「跟随资源管理器选中项」(tracking) 会覆盖 IPC 传进来的路径：
/// 预览刚弹出约 300ms 后，Seer 会把画面切回资源管理器当前选中项（没有选中项时切到第一项）。
/// 悬停预览必须关掉它，否则永远会「跳回上次点选的文件」。
/// 商店版设置在 uwp.ini，桌面版在 user.ini，两种都尝试。
/// </summary>
internal static class SeerSettings
{
    private const string TrackingKey = "tracking_file";
    private const string TrackingKeyAlt = "is_tracking";

    public static List<string> CandidateFiles()
    {
        var list = new List<string>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 微软商店版：%LOCALAPPDATA%\Packages\<包名>\LocalCache\Local\Corey\Seer\
        try
        {
            string pkgs = Path.Combine(local, "Packages");
            if (Directory.Exists(pkgs))
            {
                foreach (var dir in Directory.GetDirectories(pkgs, "*Seer*"))
                {
                    string baseDir = Path.Combine(dir, "LocalCache", "Local", "Corey", "Seer");
                    if (!Directory.Exists(baseDir)) continue;
                    foreach (var f in new[] { "uwp.ini", "user.ini" })
                    {
                        string p = Path.Combine(baseDir, f);
                        if (File.Exists(p)) list.Add(p);
                    }
                }
            }
        }
        catch { }

        // 桌面安装版
        string desktop = Path.Combine(local, "Corey", "Seer");
        foreach (var f in new[] { "uwp.ini", "user.ini" })
        {
            string p = Path.Combine(desktop, f);
            if (File.Exists(p)) list.Add(p);
        }
        return list;
    }

    /// <summary>读取 ini 里的 tracking 开关；没有该键返回 null。</summary>
    public static bool? ReadTracking(string iniPath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(iniPath))
            {
                string t = line.Trim();
                foreach (var key in new[] { TrackingKey, TrackingKeyAlt })
                {
                    if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = t[(key.Length + 1)..].Trim();
                        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>把 tracking 改成 false（带一次性备份）。返回被修改的文件列表。</summary>
    public static List<string> DisableTracking()
    {
        var changed = new List<string>();
        foreach (var ini in CandidateFiles())
        {
            if (ReadTracking(ini) != true) continue;
            try
            {
                string backup = ini + ".seerhover.bak";
                if (!File.Exists(backup)) File.Copy(ini, backup, true);

                var lines = File.ReadAllLines(ini);
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    foreach (var key in new[] { TrackingKey, TrackingKeyAlt })
                    {
                        if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                            lines[i] = key + "=false";
                    }
                }
                File.WriteAllLines(ini, lines);
                changed.Add(ini);
                Logger.Info("disabled seer tracking in " + ini);
            }
            catch (Exception ex)
            {
                Logger.Error($"patch {ini} failed: {ex.Message}");
            }
        }
        return changed;
    }

    /// <summary>把备份还原回去（用户想恢复 Seer 的跟随选中项时用）。</summary>
    public static List<string> RestoreBackup()
    {
        var restored = new List<string>();
        foreach (var ini in CandidateFiles())
        {
            string backup = ini + ".seerhover.bak";
            if (!File.Exists(backup)) continue;
            try
            {
                File.Copy(backup, ini, true);
                restored.Add(ini);
            }
            catch (Exception ex) { Logger.Error($"restore {ini} failed: {ex.Message}"); }
        }
        return restored;
    }

    // ── 窗口尺寸记忆 ────────────────────────────────────────────────
    // Seer 默认每次显示都按内容重设窗口大小（图片会撑到原始尺寸 1794x1794），
    // 我们把窗口改小后它又会撑回去，于是「预览大小来回跳」。
    // keep_wnd_sz_pos_mode=1 打开它自带的「保持窗口大小与位置」：
    // 之后窗口尺寸不再随内容变化，且会记住上次的尺寸位置 —— 彻底不跳。
    private const string KeepModeKey = "keep_wnd_sz_pos_mode";

    public static int? ReadKeepMode(string iniPath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(iniPath))
            {
                string t = line.Trim();
                if (t.StartsWith(KeepModeKey + "=", StringComparison.OrdinalIgnoreCase))
                {
                    string v = t[(KeepModeKey.Length + 1)..].Trim();
                    if (int.TryParse(v, out int n)) return n;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>打开「保持窗口大小位置」。返回被修改的文件列表。</summary>
    public static List<string> EnableKeepWindow()
    {
        return PatchKeepMode(1);
    }

    /// <summary>恢复成 Seer 默认（窗口大小随内容变化）。</summary>
    public static List<string> DisableKeepWindow()
    {
        return PatchKeepMode(0);
    }

    private static List<string> PatchKeepMode(int mode)
    {
        var changed = new List<string>();
        foreach (var ini in CandidateFiles())
        {
            if (ReadKeepMode(ini) == mode) continue;
            try
            {
                string backup = ini + ".seerhover.bak";
                if (!File.Exists(backup)) File.Copy(ini, backup, true);

                var lines = File.ReadAllLines(ini).ToList();
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Trim().StartsWith(KeepModeKey + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = KeepModeKey + "=" + mode;
                        found = true;
                    }
                }

                if (!found)
                {
                    // 没有这个键：只往真正带 [Others] 段的配置里加（商店版 uwp.ini）。
                    // 桌面版 user.ini 是另一套键名，往里插会把别的段搞乱。
                    int idx = lines.FindIndex(l => l.Trim().Equals("[Others]", StringComparison.OrdinalIgnoreCase));
                    if (idx < 0)
                    {
                        Logger.Info($"skip {ini}: 没有 {KeepModeKey} 键，也不是 uwp 配置");
                        continue;
                    }
                    lines.Insert(idx + 1, KeepModeKey + "=" + mode);
                }

                File.WriteAllLines(ini, lines);
                changed.Add(ini);
                Logger.Info($"set {KeepModeKey}={mode} in {ini}");
            }
            catch (Exception ex)
            {
                Logger.Error($"patch keep mode {ini} failed: {ex.Message}");
            }
        }
        return changed;
    }
}
