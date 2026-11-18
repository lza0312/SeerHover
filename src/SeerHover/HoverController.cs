namespace SeerHover;

/// <summary>悬停状态机：Idle → Dwell → Shown，负责触发、切换、收窗与各种抑制条件。</summary>
internal sealed class HoverController : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly HoverResolver _resolver;
    private readonly SeerIpc _ipc;
    private readonly SeerV2? _v2;
    private readonly GeometryController _geometry;
    private readonly System.Threading.Timer _timer;

    private Native.POINT _cursor;
    private bool _hasCursor;
    private string? _candidatePath;
    private HoverTarget? _candidate;
    private long _candidateSince;
    private volatile string? _activePath;
    private long _offSince;
    private long _lastInvoke;
    private string? _suppressedPath;
    private int _tick;
    private int _busy;
    private int _period;
    private CancellationTokenSource? _verifyCts;

    // 光标下窗口的判断结果缓存：静止时同一窗口不必反复查询进程名
    private IntPtr _lastRoot;
    private bool _lastRootIsSeer;

    public bool Paused { get; set; }
    public string? ActivePath => _activePath;
    public event Action<string>? StateChanged;
    public event Action<string>? PreviewMismatch;

    public HoverController(AppConfig cfg, SeerIpc ipc, SeerV2? v2 = null)
    {
        _cfg = cfg;
        _ipc = ipc;
        _v2 = v2;
        _resolver = new HoverResolver(cfg);
        _geometry = new GeometryController(cfg, ipc);
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => Restart();

    public void Restart()
    {
        _period = Math.Max(25, _cfg.PollMs);
        _timer.Change(0, _period);
    }

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void Dispose()
    {
        _timer.Dispose();
        _geometry.Cancel();
        var v = Interlocked.Exchange(ref _verifyCts, null);
        if (v != null) { try { v.Cancel(); } catch (ObjectDisposedException) { } }
    }

    private void Raise(string s) => StateChanged?.Invoke(s);

    /// <summary>
    /// 待触发/正在预览时把轮询提到 ≤25ms（dwell 计时更准、切换更快），
    /// 空闲时回到配置值（少唤醒、少耗电）。
    /// </summary>
    private void UpdatePeriod()
    {
        int want = _candidatePath != null || _activePath != null
            ? Math.Max(20, Math.Min(25, _cfg.PollMs))
            : Math.Max(25, _cfg.PollMs);

        if (want == _period) return;
        _period = want;
        try { _timer.Change(want, want); } catch (ObjectDisposedException) { }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            Step();
        }
        catch (Exception ex)
        {
            Logger.Error("tick failed: " + ex.Message);
        }
        finally
        {
            UpdatePeriod();
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void Step()
    {
        if (Paused || !_cfg.Enabled)
        {
            if (_activePath != null) DoHide(clearSuppress: true);
            return;
        }

        if (!Native.GetCursorPos(out var cur)) return;
        bool moved = !_hasCursor || cur != _cursor;
        var prevCursor = _cursor;
        _cursor = cur;
        _hasCursor = true;
        _tick++;
        long now = Environment.TickCount64;

        // 点击：预览窗上的点击（媒体控件、缩放等）交给 Seer 处理，不收窗；
        // 其它位置的点击按 HideOnClick 立即收窗并抑制当前项
        if (_cfg.HideOnClick && Native.IsKeyDown(Native.VK_LBUTTON))
        {
            IntPtr clickRoot = Native.RootAt(cur, out _);
            // 媒体控制条/弹出面板（NOACTIVATE）不属于 Seer 进程，但在上面点击是调音量/倍速，不能收窗
            bool clickOnSeer = (clickRoot != IntPtr.Zero && Native.IsProcess(clickRoot, "seer"))
                               || MediaOverlayForm.OwnsCursor(cur.X, cur.Y);
            if (!clickOnSeer)
            {
                if (_activePath != null)
                {
                    _suppressedPath = _activePath;
                    DoHide(clearSuppress: false);
                }
                ClearCandidate();
            }
            return;
        }

        // 空闲常态：光标没动、没有候选、也没有在显示的窗口 → 什么都不用查
        if (!moved && _candidatePath == null && _activePath == null) return;

        if (!_ipc.IsRunning)
        {
            bool wasActive = _activePath != null;
            ResetAll();
            if (wasActive) Raise("idle");
            return;
        }

        // 与外部关闭同步（用户按空格 / Esc / 点了别处），每 3 拍抽查一次就够
        if (_activePath != null && now - _lastInvoke > 400 && _tick % 3 == 0)
        {
            if (!_ipc.IsVisibleFast)
            {
                if (Logger.IsVerbose) Logger.Info($"seer hidden externally: {_activePath}");
                _suppressedPath = _activePath;
                _activePath = null;
                ClearCandidate();
                _geometry.Cancel();
                Raise("idle");
            }
        }

        if (_cfg.SuppressOnModifiers &&
            (Native.IsKeyDown(Native.VK_CONTROL) || Native.IsKeyDown(Native.VK_SHIFT) || Native.IsKeyDown(Native.VK_MENU)))
        {
            ClearCandidate();
            return;
        }

        // 光标落在 Seer 预览窗上 → 保持
        IntPtr root = Native.RootAt(cur, out _);
        if (root != IntPtr.Zero)
        {
            if (root != _lastRoot)
            {
                _lastRoot = root;
                _lastRootIsSeer = Native.IsProcess(root, "seer");
            }
            if (_lastRootIsSeer)
            {
                _offSince = 0;
                return;
            }
        }

        // 媒体控制条 / 音量 / 倍速弹出面板上：同属「操作预览」，不能计时收窗
        // （面板是 NOACTIVATE 的独立窗口，不命中上面的 Seer 进程判定，否则一碰就关预览）
        if (MediaOverlayForm.OwnsCursor(cur.X, cur.Y))
        {
            _offSince = 0;
            return;
        }

        // 只有光标移动时才重新识别，静止时只做 dwell / 宽限判定
        if (moved)
        {
            var hit = _resolver.ResolveCore(cur, root, out string why);

            if (hit == null)
            {
                if (_candidatePath != null && Logger.IsVerbose) Logger.Info($"left item ({why})");
                ClearCandidate();
                if (_offSince == 0) _offSince = now;
            }
            else
            {
                if (hit.Path != _candidatePath)
                {
                    _candidatePath = hit.Path;
                    _candidate = hit;
                    _candidateSince = now;
                    if (_suppressedPath != null && _suppressedPath != hit.Path) _suppressedPath = null;
                    if (Logger.IsVerbose) Logger.Info($"candidate: {hit}");
                    // 「原比例最大化」需要源文件像素尺寸，趁 dwell 阶段先探好，触发时直接命中缓存
                    if (GeometryController.EffectiveSizeMode(_cfg.Geometry, hit.Path) == "auto")
                        MediaSize.Prefetch(hit.Path);
                }
                else
                {
                    _candidate = hit; // 矩形可能变化，保持更新
                }
                _offSince = 0;
            }
        }

        // ── 收窗宽限的方向感知 ────────────────────────────────────────
        // 光标已离开文件项但预览还开着：朝预览窗方向移动 = 想去操作媒体控件，不计时；
        // 朝相反方向移动 / 移出预览窗 = 不需要了，宽限 HideGraceMs 后收窗。
        if (_activePath != null && _candidatePath == null && moved && _offSince == 0)
        {
            var pr = _ipc.GetRect();
            if (!pr.IsEmpty)
            {
                double vx = cur.X - prevCursor.X, vy = cur.Y - prevCursor.Y;
                double px = (pr.Left + pr.Right) / 2.0 - cur.X;
                double py = (pr.Top + pr.Bottom) / 2.0 - cur.Y;
                if (vx * px + vy * py > 0) _offSince = -1;   // -1 = 朝预览窗移动中，暂不开始计时
                else _offSince = now;
            }
            else if (_offSince == 0) _offSince = now;
        }
        // 之前是「朝预览窗移动」，这一拍停住或改朝反方向 → 恢复正常计时
        if (_activePath != null && _candidatePath == null && _offSince < 0)
        {
            if (!moved)
            {
                // 停在半路：给个宽限起点，别无限期挂着
                _offSince = now - Math.Max(0, _cfg.HideGraceMs - 100);
            }
            else
            {
                var pr2 = _ipc.GetRect();
                double vx2 = cur.X - prevCursor.X, vy2 = cur.Y - prevCursor.Y;
                double px2 = pr2.IsEmpty ? 0 : (pr2.Left + pr2.Right) / 2.0 - cur.X;
                double py2 = pr2.IsEmpty ? 0 : (pr2.Top + pr2.Bottom) / 2.0 - cur.Y;
                if (pr2.IsEmpty || vx2 * px2 + vy2 * py2 <= 0) _offSince = now;
            }
        }

        // 离开文件项后的宽限收窗（_offSince 为 -1 表示正朝预览窗移动，不计时）
        if (_activePath != null && _candidatePath == null && _offSince > 0 &&
            now - _offSince >= _cfg.HideGraceMs)
        {
            if (Logger.IsVerbose) Logger.Info("hide: cursor left item");
            DoHide(clearSuppress: true);
            return;
        }

        // dwell 满足 → 触发
        if (_candidatePath != null && _candidate != null && _candidatePath != _activePath &&
            _candidatePath != _suppressedPath)
        {
            if (now - _candidateSince >= _cfg.DwellMs && now - _lastInvoke >= _cfg.MinInvokeIntervalMs)
                DoInvoke(_candidate, cur);
        }
    }

    private void DoInvoke(HoverTarget target, Native.POINT cursor)
    {
        Native.RECT? planRect = null;
        bool openedViaV2 = false;
        string previewPath = target.Path;

        if (_v2 != null && _v2.IsAvailable)
        {
            planRect = _geometry.Plan(target, cursor);
            if (planRect.HasValue && !planRect.Value.IsEmpty)
            {
                // 预览窗显示前先摆到位：Seer 应用 rect 是异步的，不预摆会先露一帧小窗
                _geometry.PrePosition(planRect.Value);

                // 放大显示：Seer 带 rect 不放大图片，只能把小图先重采样成临时大图。
                if (_cfg.Geometry.UpscaleToFill)
                {
                    var up = PreviewUpscaler.Get(target.Path, planRect.Value.Width, planRect.Value.Height);
                    if (up != null) previewPath = up;
                }
                openedViaV2 = _v2.PreviewOpen(previewPath, planRect.Value);
                if (openedViaV2) _geometry.Watch(planRect.Value);   // 打开后短盯一会，防 Seer 回跳尺寸
            }
        }

        if (!openedViaV2)
        {
            // V2 不可用 / 失败（如 -32001 NOT_ACTIVE）→ legacy 5000 回退。
            // 实测 Seer 显示瞬间会用它自己的尺寸覆盖预摆位，所以关键是
            // 「先起盯梢再开窗」：Watch 等窗口一可见就 MoveTo，闪变压到一帧以内。
            planRect ??= _geometry.Plan(target, cursor);
            if (planRect.HasValue && !planRect.Value.IsEmpty)
            {
                _geometry.PrePosition(planRect.Value);
                _geometry.Watch(planRect.Value);
                _ipc.Invoke(target.Path);
            }
            else
            {
                // seer 尺寸模式：只触发，几何交给 Seer
                _ipc.Invoke(target.Path);
                _geometry.Apply(target, cursor);
            }
        }

        _lastInvoke = Environment.TickCount64;
        _activePath = target.Path;
        _offSince = 0;
        if (Logger.IsVerbose) Logger.Info($"invoke: {target.Path} (dwell {_cfg.DwellMs}ms, v2={openedViaV2}, upscaled={previewPath != target.Path})");
        Raise(target.Path);
        VerifyLater(target.Path, previewPath);
    }

    /// <summary>
    /// Seer 若开着「跟随资源管理器选中项」，会在预览出现后把画面切回选中项。
    /// 这里回查一次，确认显示的确是悬停文件；不一致就收窗并上报，避免一直显示错文件。
    /// </summary>
    private void VerifyLater(string path, string? previewPath = null)
    {
        if (!_cfg.VerifyPreview) return;

        var previous = Interlocked.Exchange(ref _verifyCts, null);
        if (previous != null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _verifyCts, cts);

        int delay = Math.Max(300, _cfg.VerifyDelayMs);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                if (_activePath != path) return;      // 已经切到别的文件/收窗
                string cur = _ipc.GetCurrentFile();
                if (string.IsNullOrEmpty(cur)) return; // 查询不可用，不做判断
                // 预览的是放大副本时，Seer 回报的也是副本路径；但「跟随选中项」也可能
                // 把它切回原路径，两者都算命中，避免误判成显示错文件而收窗。
                if (Matches(cur, previewPath ?? path) || Matches(cur, path)) return;

                if (Logger.IsVerbose) Logger.Info($"preview mismatch: want '{path}' got '{cur}'");
                PreviewMismatch?.Invoke(path);
                if (_activePath == path && _ipc.IsVisibleFast) DoHide(clearSuppress: false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Error("verify failed: " + ex.Message); }
            finally
            {
                Interlocked.CompareExchange(ref _verifyCts, null, cts);
                try { cts.Dispose(); } catch (ObjectDisposedException) { }
            }
        });
    }

    /// <summary>5006 返回的路径可能被截断，用前缀比较（忽略大小写与斜杠方向）。</summary>
    private static bool Matches(string got, string expect)
    {
        string g = got.Replace('/', '\\').TrimEnd('\0');
        string e = expect.Replace('/', '\\');
        if (g.Length == 0) return true;
        return e.StartsWith(g, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearCandidate()
    {
        _candidatePath = null;
        _candidate = null;
        _candidateSince = 0;
    }

    private void DoHide(bool clearSuppress)
    {
        _geometry.Cancel();
        _ipc.Hide();
        if (clearSuppress) _suppressedPath = null;
        _activePath = null;
        _offSince = 0;
        Raise("idle");
    }

    private void ResetAll()
    {
        ClearCandidate();
        _activePath = null;
        _suppressedPath = null;
        _offSince = 0;
        _geometry.Cancel();
    }

    /// <summary>切换暂停状态时清空一切，避免残留窗口。</summary>
    public void ForceHide()
    {
        if (_ipc.IsRunning) _ipc.Hide();
        ResetAll();
        Raise("idle");
    }
}
