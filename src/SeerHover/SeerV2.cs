using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SeerHover;

/// <summary>
/// Seer V2 Named Pipe JSON-RPC 2.0 客户端。
/// 连接 \\.\pipe\Seer.IPC.v2.&lt;SessionId&gt;，复用官方示例的句柄打开、权限掩码、
/// session / owner SID 校验与 WriteFrame / ReadFrame（含 ERROR_MORE_DATA）逻辑。
/// 一个实例只开一条 pipe，所有 RPC 在 _sync 锁内同步完成；不订阅事件。
/// </summary>
internal sealed class SeerV2 : IDisposable
{
    private const string PipePrefix = @"\\.\pipe\Seer.IPC.v2.";
    private const int MaxFrameBytes = 64 * 1024;

    // 服务器 DACL 不含 FILE_CREATE_PIPE_INSTANCE (0x4)，所以 GENERIC_WRITE 会失败。
    private const uint DesiredAccess = 0x0012018B;
    private const uint OpenExisting = 3;
    private const uint SecuritySqosPresent = 0x00100000;
    private const uint SecurityIdentification = 0x00010000;
    private const uint PipeReadModeMessage = 0x00000002;
    private const uint ErrorMoreData = 234;

    private const int SeKernelObject = 6;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int WinLocalSystemSid = 22;
    private const int WinBuiltinAdministratorsSid = 26;

    // JSON-RPC 错误码
    private const int ErrSessionInvalid = -32002;   // Seer 重启 / session 失效：重连重试一次

    private const int CooldownMs = 2000;            // 失败后 2 秒内不再尝试连接
    private const int CoordLimit = 32768;           // SDK 坐标 / 尺寸上限，避免 -32602

    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly Dictionary<string, object?> EmptyParams = new();

    private readonly object _sync = new();
    private IntPtr _pipe = InvalidHandleValue;
    private int _nextId = 1;
    private long _lastFailTick;                     // Environment.TickCount64，0 表示不在冷却期
    private bool _available;
    private string _lastError = string.Empty;

    // ---- 对外状态 -------------------------------------------------------------

    /// <summary>上次探测（EnsureConnected）是否成功；不发起新连接，不阻塞。</summary>
    public bool IsAvailable { get { lock (_sync) return _available; } }

    /// <summary>最近一次连接 / RPC 失败原因，便于诊断。</summary>
    public string LastError { get { lock (_sync) return _lastError; } }

    // ---- 对外 API -------------------------------------------------------------

    /// <summary>尝试连接（或重连）。线程安全、可重复调用；失败后 2 秒内直接返回 false。</summary>
    public bool EnsureConnected()
    {
        lock (_sync) return EnsureConnectedLocked();
    }

    /// <summary>强制下次调用重连（Seer 重启时用），同时清掉失败冷却。</summary>
    public void Invalidate()
    {
        lock (_sync) InvalidateLocked();
    }

    public (string AppVersion, int VersionInt, bool Licensed)? GetInfo()
    {
        using var doc = Call("seer.getInfo", null, out int err);
        if (doc == null || err != 0) return null;

        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("result", out JsonElement r) || r.ValueKind != JsonValueKind.Object)
            return null;

        string appVersion = r.TryGetProperty("appVersion", out JsonElement av) && av.ValueKind == JsonValueKind.String
            ? av.GetString() ?? string.Empty : string.Empty;
        int versionInt = GetInt(r, "versionInt");
        bool licensed = r.TryGetProperty("licensed", out JsonElement lic) && ReadBool(lic);
        return (appVersion, versionInt, licensed);
    }

    /// <summary>seer.app.getState.tracking；失败返回 null。</summary>
    public bool? IsTracking()
    {
        using var doc = Call("seer.app.getState", null, out int err);
        if (doc == null || err != 0) return null;

        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("result", out JsonElement r) || r.ValueKind != JsonValueKind.Object)
            return null;
        if (!r.TryGetProperty("tracking", out JsonElement t)) return null;
        return ReadBool(t);
    }

    /// <summary>
    /// 以 main 模式打开预览。显式传 rect 会关闭 SDK 的 content-fit 自动缩放。
    /// noop=true 用 ifAlreadyOpen:"noop"（已打开则复用），false 用 "toggle"。
    /// </summary>
    public bool PreviewOpen(string path, Native.RECT rect, bool noop = true)
    {
        var p = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["mode"] = "main",
            ["ifAlreadyOpen"] = noop ? "noop" : "toggle",
            ["rect"] = ClampRect(rect),
        };

        using var doc = Call("seer.preview.open", p, out int err);
        if (doc == null || err != 0) return false;

        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("result", out JsonElement r) || r.ValueKind != JsonValueKind.Object)
            return false;

        // 成功结果是 {"sessionId": ...}（实测 4.8.10：带 rect + noop 时返回 sessionId，
        // 且随后 getState 的 rect 精确等于请求值、loadState=content）。
        // toggle 把已有预览关掉时是 {"closed":true}，没有 sessionId → 视作「未打开」。
        return r.TryGetProperty("sessionId", out JsonElement sid) && sid.ValueKind != JsonValueKind.Null;
    }

    public bool PreviewClose()
    {
        using var doc = Call("seer.preview.close", null, out int err);
        return doc != null && err == 0;
    }

    public (bool Visible, string Path, Native.RECT? Rect)? PreviewState()
    {
        using var doc = Call("seer.preview.getState", null, out int err);
        if (doc == null || err != 0) return null;

        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("result", out JsonElement r) || r.ValueKind != JsonValueKind.Object)
            return null;

        bool visible = r.TryGetProperty("visible", out JsonElement vis) && ReadBool(vis);
        string path = r.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty : string.Empty;

        Native.RECT? rect = null;
        if (r.TryGetProperty("rect", out JsonElement re) && re.ValueKind == JsonValueKind.Object)
        {
            int x = GetInt(re, "x");
            int y = GetInt(re, "y");
            int w = GetInt(re, "width");
            int h = GetInt(re, "height");
            rect = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        }
        return (visible, path, rect);
    }

    /// <summary>移动 / 缩放当前可见的预览窗口。</summary>
    public bool SetBounds(Native.RECT rect)
    {
        var p = new Dictionary<string, object?> { ["rect"] = ClampRect(rect) };
        using var doc = Call("seer.window.setBounds", p, out int err);
        return doc != null && err == 0;
    }

    public void Dispose()
    {
        lock (_sync) ClosePipe();
    }

    // ---- 连接 / 调用内部实现 ---------------------------------------------------

    private bool EnsureConnectedLocked()
    {
        if (IsPipeValid(_pipe)) return true;

        long now = Environment.TickCount64;
        if (_lastFailTick != 0 && now - _lastFailTick < CooldownMs) return false;

        bool ok = TryConnectLocked();
        _lastFailTick = ok ? 0 : now;
        _available = ok;
        return ok;
    }

    private void InvalidateLocked()
    {
        ClosePipe();
        _lastFailTick = 0;
        _available = false;
    }

    private bool TryConnectLocked()
    {
        IntPtr h = InvalidHandleValue;
        try
        {
            h = OpenPipe();
            _pipe = h;
            _lastError = string.Empty;
            if (Logger.IsVerbose) Logger.Info("SeerV2 connected");
            return true;
        }
        catch (Exception ex)
        {
            if (h != InvalidHandleValue && h != IntPtr.Zero) CloseHandle(h);
            _pipe = InvalidHandleValue;
            _lastError = ex.Message;
            Logger.Error("SeerV2 connect failed: " + ex.Message);
            return false;
        }
    }

    private static IntPtr OpenPipe()
    {
        if (!ProcessIdToSessionId(GetCurrentProcessId(), out uint sessionId))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        string name = PipePrefix + sessionId;
        IntPtr h = CreateFileW(name, DesiredAccess, 0, IntPtr.Zero, OpenExisting,
            SecuritySqosPresent | SecurityIdentification, IntPtr.Zero);
        if (h == InvalidHandleValue || h == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            VerifyPipeServer(h, sessionId);
            uint mode = PipeReadModeMessage;
            if (!SetNamedPipeHandleState(h, ref mode, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return h;
        }
        catch
        {
            CloseHandle(h);
            throw;
        }
    }

    private static void VerifyPipeServer(IntPtr handle, uint expectedSessionId)
    {
        if (!GetNamedPipeServerSessionId(handle, out uint serverSessionId))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (serverSessionId != expectedSessionId)
            throw new InvalidOperationException($"SeerV2 pipe session mismatch: {serverSessionId} != {expectedSessionId}");

        uint secErr = GetSecurityInfo(handle, SeKernelObject, OwnerSecurityInformation,
            out IntPtr ownerSid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr sd);
        if (secErr != 0) throw new Win32Exception((int)secErr);

        IntPtr token = IntPtr.Zero;
        IntPtr tokenBuf = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out uint needed);
            if (needed == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            tokenBuf = Marshal.AllocHGlobal((int)needed);
            if (!GetTokenInformation(token, TokenUser, tokenBuf, needed, out needed))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr userSid = Marshal.ReadIntPtr(tokenBuf);

            byte[] adminSid = new byte[68];
            uint adminLen = (uint)adminSid.Length;
            if (!CreateWellKnownSid(WinBuiltinAdministratorsSid, IntPtr.Zero, adminSid, ref adminLen))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            byte[] systemSid = new byte[68];
            uint systemLen = (uint)systemSid.Length;
            if (!CreateWellKnownSid(WinLocalSystemSid, IntPtr.Zero, systemSid, ref systemLen))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!EqualSid(ownerSid, userSid) && !EqualSid(ownerSid, adminSid) && !EqualSid(ownerSid, systemSid))
                throw new UnauthorizedAccessException("Untrusted SeerV2 pipe server owner SID.");
        }
        finally
        {
            if (tokenBuf != IntPtr.Zero) Marshal.FreeHGlobal(tokenBuf);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    /// <summary>整条 RPC（含 -32002 重连重试一次）都在 _sync 锁内完成。</summary>
    private JsonDocument? Call(string method, Dictionary<string, object?>? @params, out int errorCode)
    {
        errorCode = 0;
        lock (_sync)
        {
            try
            {
                if (!EnsureConnectedLocked()) { errorCode = -1; return null; }

                JsonDocument? doc = CallOnce(method, @params, out errorCode);
                if (errorCode == ErrSessionInvalid)
                {
                    // Seer 重启：丢掉旧句柄，清冷却后重连，只对同一请求重试一次。
                    if (Logger.IsVerbose) Logger.Info("SeerV2 session invalid (-32002), reconnecting");
                    ClosePipe();
                    _lastFailTick = 0;
                    if (EnsureConnectedLocked())
                        doc = CallOnce(method, @params, out errorCode);
                }

                if (errorCode != 0)
                {
                    _lastError = $"{method}: rpc error {errorCode}";
                    Logger.Error("SeerV2 " + _lastError);
                }
                return doc;
            }
            catch (Exception ex)
            {
                errorCode = -1;
                _lastError = $"{method}: {ex.Message}";
                Logger.Error("SeerV2 " + _lastError);
                InvalidateLocked();   // 传输层失败：句柄可能已失效，下次调用强制重连
                return null;
            }
        }
    }

    private JsonDocument? CallOnce(string method, Dictionary<string, object?>? @params, out int errorCode)
    {
        errorCode = 0;
        if (_nextId == int.MaxValue) _nextId = 1;
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = _nextId++,
            ["method"] = method,
            ["params"] = @params ?? EmptyParams,
        };

        byte[] frame = Utf8NoBom.GetBytes(JsonSerializer.Serialize(payload) + "\n");
        if (!WriteFile(_pipe, frame, (uint)frame.Length, out uint written, IntPtr.Zero) || written != frame.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        using var ms = new MemoryStream();
        byte[] buf = new byte[MaxFrameBytes];
        while (true)
        {
            bool ok = ReadFile(_pipe, buf, (uint)buf.Length, out uint read, IntPtr.Zero);
            if (read > 0) ms.Write(buf, 0, (int)read);
            if (ok) break;

            uint err = (uint)Marshal.GetLastWin32Error();
            if (err == ErrorMoreData)
            {
                if (ms.Length > MaxFrameBytes) throw new InvalidDataException("SeerV2 frame exceeds 64 KiB");
                continue;
            }
            throw new Win32Exception((int)err);
        }

        var doc = JsonDocument.Parse(ms.ToArray());
        if (doc.RootElement.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.Object)
        {
            if (errEl.TryGetProperty("code", out JsonElement codeEl) && codeEl.TryGetInt32(out int code))
                errorCode = code;
        }
        return doc;
    }

    // ---- 小工具 ---------------------------------------------------------------

    private static bool IsPipeValid(IntPtr h) => h != InvalidHandleValue && h != IntPtr.Zero;

    private void ClosePipe()
    {
        if (IsPipeValid(_pipe))
        {
            CloseHandle(_pipe);
            _pipe = InvalidHandleValue;
        }
    }

    private static object ClampRect(Native.RECT rect)
    {
        int x = Clamp(rect.Left, -CoordLimit, CoordLimit);
        int y = Clamp(rect.Top, -CoordLimit, CoordLimit);
        int w = Clamp(rect.Width, 1, CoordLimit);
        int h = Clamp(rect.Height, 1, CoordLimit);
        return new { x, y, width = w, height = h };
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

    private static int GetInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i) ? i : 0;

    private static bool ReadBool(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n != 0;
        return false;
    }

    // ---- P/Invoke -------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerSessionId(IntPtr pipe, out uint serverSessionId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetNamedPipeHandleState(
        IntPtr hNamedPipe, ref uint lpMode, IntPtr lpMaxCollectionCount, IntPtr lpCollectDataTimeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(
        IntPtr handle, int objectType, uint securityInfo,
        out IntPtr ppsidOwner, IntPtr ppsidGroup, IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSecurityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
        uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateWellKnownSid(
        int wellKnownSidType, IntPtr domainSid, byte[] pSid, ref uint cbSid);

    [DllImport("advapi32.dll")]
    private static extern bool EqualSid(IntPtr pSid1, IntPtr pSid2);

    [DllImport("advapi32.dll")]
    private static extern bool EqualSid(IntPtr pSid1, byte[] pSid2);
}
