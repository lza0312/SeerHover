using System.Runtime.InteropServices;

namespace SeerHover;

/// <summary>
/// 控制 Seer 进程（Seer.exe / SeerViewer.exe）的音频会话音量。
/// Seer 的媒体预览没有音量增减快捷键（只有 M 静音），音量只能从 Windows 音频会话外部控制——
/// 这是唯一不需要 Seer 配合的通道，改的是 Seer 输出的会话音量，不影响系统总音量。
/// 实现方式：纯 P/Invoke 裸 vtable 调用，不走 CLR ComImport 封送（本机 CLR QI 路径行为异常）。
/// 接口 GUID 全部使用 Windows SDK 文档值（mmdeviceapi.h / audiopolicy.h）。
/// </summary>
internal static class AudioSession
{
    private static readonly object Sync = new();
    private static IntPtr _volume;           // ISimpleAudioVolume 裸指针
    private static string? _cachedFor;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    // 以下 IID 通过 --audio-discover 从正版 MMDevApi.dll / AudioSes.dll 二进制提取并经裸 QI/Activate 实测验证。
    // 注意：网上流传的多个 IID 值与本机实际不符，勿用文档值覆盖。
    private static readonly Guid IID_IMMDevice = new("D666063F-1587-4E43-81F1-B948E807363F");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QIProc(IntPtr self, ref Guid iid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ReleaseProc(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDefaultEndpointProc(IntPtr self, int flow, int role, out IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ActivateProc(IntPtr self, ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetIdProc(IntPtr self, out IntPtr idPtr);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetSessionEnumeratorProc(IntPtr self, out IntPtr enumerator);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetCountProc(IntPtr self, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetSessionProc(IntPtr self, int index, out IntPtr ctl);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetProcessIdProc(IntPtr self, out uint pid);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetMasterVolumeProc(IntPtr self, float level, ref Guid ctx);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetMasterVolumeProc(IntPtr self, out float level);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetMuteProc(IntPtr self, bool mute, ref Guid ctx);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetMuteProc(IntPtr self, out bool mute);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr pUnkOuter, uint clsCtx, ref Guid iid, out IntPtr punk);

    // ---- 裸 COM 辅助 ----
    private static IntPtr Slot(IntPtr obj, int i) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), i * 8);

    private static int RawQI(IntPtr obj, Guid iid, out IntPtr ppv)
    {
        var f = Marshal.GetDelegateForFunctionPointer<QIProc>(Slot(obj, 0));
        return f(obj, ref iid, out ppv);
    }

    private static int RawRelease(IntPtr obj) =>
        Marshal.GetDelegateForFunctionPointer<ReleaseProc>(Slot(obj, 2))(obj);

    /// <summary>当前静音态；找不到 Seer 的会话返回 null。</summary>
    public static bool? GetMute()
    {
        lock (Sync)
        {
            var vol = Ensure();
            if (vol == IntPtr.Zero) return null;
            try
            {
                Marshal.GetDelegateForFunctionPointer<GetMuteProc>(Slot(vol, 6))(vol, out bool m);
                return m;
            }
            catch { FreeVolume(); return null; }
        }
    }

    /// <summary>切换静音。返回是否成功（会话尚未创建时返回 false）。</summary>
    public static bool SetMute(bool mute)
    {
        lock (Sync)
        {
            var vol = Ensure();
            if (vol == IntPtr.Zero) return false;
            try
            {
                Guid empty = Guid.Empty;
                return Marshal.GetDelegateForFunctionPointer<SetMuteProc>(Slot(vol, 5))(vol, mute, ref empty) == 0;
            }
            catch { FreeVolume(); return false; }
        }
    }

    /// <summary>当前音量 0..1；找不到 Seer 的会话返回 null。</summary>
    public static float? GetVolume()
    {
        lock (Sync)
        {
            var vol = Ensure();
            if (vol == IntPtr.Zero) return null;
            try
            {
                Marshal.GetDelegateForFunctionPointer<GetMasterVolumeProc>(Slot(vol, 4))(vol, out float f);
                return f;
            }
            catch { FreeVolume(); return null; }
        }
    }

    /// <summary>设置音量 0..1。返回是否成功（会话尚未创建时返回 false）。</summary>
    public static bool SetVolume(float v)
    {
        lock (Sync)
        {
            var vol = Ensure();
            if (vol == IntPtr.Zero) return false;
            try
            {
                Guid empty = Guid.Empty;
                int hr = Marshal.GetDelegateForFunctionPointer<SetMasterVolumeProc>(Slot(vol, 3))(vol, Math.Clamp(v, 0f, 1f), ref empty);
                if (hr != 0) { FreeVolume(); return false; }
                return true;
            }
            catch { FreeVolume(); return false; }
        }
    }

    private static void FreeVolume()
    {
        if (_volume != IntPtr.Zero) { try { RawRelease(_volume); } catch { } _volume = IntPtr.Zero; }
        _cachedFor = null;
    }

    private static IntPtr Ensure()
    {
        try
        {
            var names = string.Join("|", Process.GetProcesses()
                .Where(p => p.ProcessName.StartsWith("Seer", StringComparison.OrdinalIgnoreCase))
                .Select(p => (p.ProcessName + ".exe").ToLowerInvariant()));
            if (names.Length == 0) { FreeVolume(); return IntPtr.Zero; }
            if (_volume != IntPtr.Zero && _cachedFor == names)
            {
                try
                {
                    Marshal.GetDelegateForFunctionPointer<GetMasterVolumeProc>(Slot(_volume, 4))(_volume, out _);
                    return _volume;                                     // 还活着就复用
                }
                catch { FreeVolume(); }
            }

            Guid unk = IID_IUnknown;
            Guid clsid = CLSID_MMDeviceEnumerator;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref unk, out IntPtr punk) != 0 || punk == IntPtr.Zero)
                return IntPtr.Zero;
            try
            {
                // IMMDeviceEnumerator vtable: [3]=EnumAudioEndpoints, [4]=GetDefaultAudioEndpoint
                var getDefault = Marshal.GetDelegateForFunctionPointer<GetDefaultEndpointProc>(Slot(punk, 4));
                if (getDefault(punk, 0 /*eRender*/, 1 /*eMultimedia*/, out IntPtr dev) != 0 || dev == IntPtr.Zero)
                    return IntPtr.Zero;
                try
                {
                    // IMMDevice vtable: [3]=Activate, [4]=OpenPropertyStore, [5]=GetId, [6]=GetState
                    var activate = Marshal.GetDelegateForFunctionPointer<ActivateProc>(Slot(dev, 3));
                    Guid mgr2 = IID_IAudioSessionManager2;
                    if (activate(dev, ref mgr2, 0x1, IntPtr.Zero, out IntPtr mgr) != 0 || mgr == IntPtr.Zero)
                        return IntPtr.Zero;
                    try
                    {
                        // IAudioSessionManager2 vtable: [3]=GetAudioSessionControl, [4]=GetSimpleAudioVolume, [5]=GetSessionEnumerator
                        var getEnum = Marshal.GetDelegateForFunctionPointer<GetSessionEnumeratorProc>(Slot(mgr, 5));
                        if (getEnum(mgr, out IntPtr sessions) != 0 || sessions == IntPtr.Zero)
                            return IntPtr.Zero;
                        try
                        {
                            var getCount = Marshal.GetDelegateForFunctionPointer<GetCountProc>(Slot(sessions, 3));
                            var getSession = Marshal.GetDelegateForFunctionPointer<GetSessionProc>(Slot(sessions, 4));
                            if (getCount(sessions, out int count) != 0) return IntPtr.Zero;

                            var pids = new HashSet<int>();
                            foreach (var p in Process.GetProcesses())
                                if (p.ProcessName.StartsWith("Seer", StringComparison.OrdinalIgnoreCase))
                                    pids.Add(p.Id);

                            for (int i = count - 1; i >= 0; i--)
                            {
                                if (getSession(sessions, i, out IntPtr ctl) != 0 || ctl == IntPtr.Zero) continue;
                                try
                                {
                                    // 会话对象 QI IAudioSessionControl2 拿 pid，QI ISimpleAudioVolume 拿音量
                                    Guid c2 = IID_IAudioSessionControl2;
                                    if (RawQI(ctl, c2, out IntPtr ctl2) == 0 && ctl2 != IntPtr.Zero)
                                    {
                                        try
                                        {
                                            // IAudioSessionControl2 vtable: 9 个基类槽位 + [12]GetSessionIdentifier,[13]GetSessionInstanceIdentifier,[14]GetProcessId
                                            var getPid = Marshal.GetDelegateForFunctionPointer<GetProcessIdProc>(Slot(ctl2, 14));
                                            getPid(ctl2, out uint pid);
                                            if (!pids.Contains((int)pid)) continue;
                                        }
                                        finally { RawRelease(ctl2); }
                                    }
                                    Guid sav = IID_ISimpleAudioVolume;
                                    if (RawQI(ctl, sav, out IntPtr vol) == 0 && vol != IntPtr.Zero)
                                    {
                                        FreeVolume();
                                        _volume = vol;
                                        _cachedFor = names;
                                        return vol;
                                    }
                                }
                                finally { RawRelease(ctl); }
                            }
                            return IntPtr.Zero;
                        }
                        finally { RawRelease(sessions); }
                    }
                    finally { RawRelease(mgr); }
                }
                finally { RawRelease(dev); }
            }
            finally { RawRelease(punk); }
        }
        catch
        {
            FreeVolume();
            return IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileName(IntPtr hModule, System.Text.StringBuilder name, int size);

    /// <summary>诊断：从系统 DLL 提取全部 GUID，对设备对象穷举 QI/Activate，找本机真实接口 IID。--audio-discover 用。流式输出。</summary>
    public static void Discover()
    {
        try
        {
            // 进程实际加载的 MMDevApi 路径
            var modPath = new System.Text.StringBuilder(512);
            IntPtr hMod = GetModuleHandle("MMDevApi.dll");
            if (hMod != IntPtr.Zero) { GetModuleFileName(hMod, modPath, 512); Console.Out.WriteLine("loaded MMDevApi: " + modPath); }
            else Console.Out.WriteLine("loaded MMDevApi: (not loaded)");

            var cands = new List<Guid>();
            foreach (var dll in new[] { @"C://Windows//System32//MMDevApi.dll", @"C://Windows//System32//AudioSes.dll" })
            {
                byte[] data = File.ReadAllBytes(dll);
                var buf = new byte[16];
                int n0 = cands.Count;
                for (int i = 0; i + 16 <= data.Length; i += 4)   // 只取 4 字节对齐位置
                {
                    Array.Copy(data, i, buf, 0, 16);
                    cands.Add(new Guid(buf));
                }
                Console.Out.WriteLine($"{Path.GetFileName(dll)}: {data.Length}B, +{cands.Count - n0} candidates");
            }
            cands = cands.Distinct().ToList();
            Console.Out.WriteLine("distinct candidates: " + cands.Count);

            Guid unk = IID_IUnknown;
            Guid clsid = CLSID_MMDeviceEnumerator;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref unk, out IntPtr punk) != 0 || punk == IntPtr.Zero)
            { Console.Out.WriteLine("CoCreateInstance failed"); return; }
            try
            {
                var getDefault = Marshal.GetDelegateForFunctionPointer<GetDefaultEndpointProc>(Slot(punk, 4));
                if (getDefault(punk, 0, 1, out IntPtr dev) != 0 || dev == IntPtr.Zero)
                { Console.Out.WriteLine("GetDefaultAudioEndpoint failed"); return; }
                try
                {
                    // 阶段1：对 dev 穷举 QI（安全）
                    int qiOk = 0;
                    foreach (var g in cands)
                    {
                        Guid t = g;
                        if (RawQI(dev, t, out IntPtr ppv) == 0) { qiOk++; Console.Out.WriteLine("dev QI ok: " + g); RawRelease(ppv); }
                    }
                    Console.Out.WriteLine($"stage1 done, QI ok = {qiOk}");

                    // 阶段2：对 dev 穷举 Activate（iid 为要激活的接口）
                    var activate = Marshal.GetDelegateForFunctionPointer<ActivateProc>(Slot(dev, 3));
                    int actOk = 0;
                    foreach (var g in cands)
                    {
                        Guid t = g;
                        try
                        {
                            if (activate(dev, ref t, 0x1, IntPtr.Zero, out IntPtr mgr) == 0 && mgr != IntPtr.Zero)
                            { actOk++; Console.Out.WriteLine("Activate ok: " + g); RawRelease(mgr); }
                        }
                        catch { }
                    }
                    Console.Out.WriteLine($"stage2 done, Activate ok = {actOk}");
                }
                finally { RawRelease(dev); }
            }
            finally { RawRelease(punk); }
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine("ERR: " + ex);
        }
    }

    /// <summary>诊断：裸 QI 矩阵 + 全会话枚举。--audio-probe 用。</summary>
    public static string Probe()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var seerProcs = Process.GetProcesses()
                .Where(p => p.ProcessName.StartsWith("Seer", StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.ProcessName}#{p.Id}").ToList();
            sb.AppendLine("seer procs   : " + (seerProcs.Count > 0 ? string.Join(", ", seerProcs) : "(none)"));

            Guid unk = IID_IUnknown;
            Guid clsid = CLSID_MMDeviceEnumerator;
            int ccHr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref unk, out IntPtr punk);
            sb.AppendLine($"CoCreateInstance: hr=0x{ccHr:X8} punk=0x{punk.ToInt64():X}");
            if (ccHr != 0 || punk == IntPtr.Zero) return sb.ToString();
            try
            {
                // QI 鉴别实验：IUnknown 应必成功、随机 GUID 应失败
                int q1 = RawQI(punk, IID_IMMDeviceEnumerator, out IntPtr t1);
                sb.AppendLine($"raw QI punk/IMMDeviceEnumerator : hr=0x{q1:X8}");
                if (q1 == 0) RawRelease(t1);
                Guid rnd = Guid.NewGuid();
                int q2 = RawQI(punk, rnd, out IntPtr t2);
                sb.AppendLine($"raw QI punk/random             : hr=0x{q2:X8}");
                if (q2 == 0) RawRelease(t2);

                var getDefault = Marshal.GetDelegateForFunctionPointer<GetDefaultEndpointProc>(Slot(punk, 4));
                int gHr = getDefault(punk, 0, 1, out IntPtr dev);
                sb.AppendLine($"GetDefaultAudioEndpoint(slot4): hr=0x{gHr:X8} dev=0x{dev.ToInt64():X}");
                if (gHr != 0 || dev == IntPtr.Zero) return sb.ToString();
                try
                {
                    // dev 上的鉴别实验
                    int d0 = RawQI(dev, IID_IUnknown, out IntPtr t0);
                    sb.AppendLine($"raw QI dev/IUnknown            : hr=0x{d0:X8}");
                    if (d0 == 0) RawRelease(t0);
                    Guid rnd2 = Guid.NewGuid();
                    int d1 = RawQI(dev, rnd2, out IntPtr t1d);
                    sb.AppendLine($"raw QI dev/random              : hr=0x{d1:X8}");
                    if (d1 == 0) RawRelease(t1d);

                    foreach (var (name, iid) in new (string, Guid)[]
                    {
                        ("IMMDevice", IID_IMMDevice),
                        ("IAudioSessionManager2", IID_IAudioSessionManager2),
                    })
                    {
                        Guid g = iid;
                        int hr = RawQI(dev, g, out IntPtr ppv);
                        sb.AppendLine($"raw QI {name,-28}: hr=0x{hr:X8}");
                        if (hr == 0) RawRelease(ppv);
                    }
                    var getId = Marshal.GetDelegateForFunctionPointer<GetIdProc>(Slot(dev, 5));
                    int idHr = getId(dev, out IntPtr idPtr);
                    string devId = "";
                    if (idHr == 0 && idPtr != IntPtr.Zero)
                    {
                        devId = Marshal.PtrToStringUni(idPtr) ?? "";
                        Marshal.FreeCoTaskMem(idPtr);
                    }
                    sb.AppendLine($"GetId(slot5): hr=0x{idHr:X8} id={devId}");

                    var activate = Marshal.GetDelegateForFunctionPointer<ActivateProc>(Slot(dev, 3));
                    Guid mgr2 = IID_IAudioSessionManager2;
                    int aHr = activate(dev, ref mgr2, 0x1, IntPtr.Zero, out IntPtr mgr);
                    sb.AppendLine($"Activate(slot3, ASM2): hr=0x{aHr:X8} mgr=0x{mgr.ToInt64():X}");
                    if (aHr != 0 || mgr == IntPtr.Zero) return sb.ToString();
                    try
                    {
                        var getEnum = Marshal.GetDelegateForFunctionPointer<GetSessionEnumeratorProc>(Slot(mgr, 5));
                        int eHr = getEnum(mgr, out IntPtr sessions);
                        sb.AppendLine($"GetSessionEnumerator: hr=0x{eHr:X8}");
                        if (eHr != 0 || sessions == IntPtr.Zero) return sb.ToString();
                        try
                        {
                            var getCount = Marshal.GetDelegateForFunctionPointer<GetCountProc>(Slot(sessions, 3));
                            var getSession = Marshal.GetDelegateForFunctionPointer<GetSessionProc>(Slot(sessions, 4));
                            getCount(sessions, out int count);
                            sb.AppendLine("session count: " + count);
                            for (int i = 0; i < count; i++)
                            {
                                if (getSession(sessions, i, out IntPtr ctl) != 0 || ctl == IntPtr.Zero)
                                {
                                    sb.AppendLine($"  [{i}] GetSession failed");
                                    continue;
                                }
                                try
                                {
                                    string pidText = "?";
                                    float vol = -1;
                                    bool muted = false;
                                    Guid c2 = IID_IAudioSessionControl2;
                                    if (RawQI(ctl, c2, out IntPtr ctl2) == 0 && ctl2 != IntPtr.Zero)
                                    {
                                        try
                                        {
                                            var getPid = Marshal.GetDelegateForFunctionPointer<GetProcessIdProc>(Slot(ctl2, 14));
                                            getPid(ctl2, out uint pid);
                                            string pname;
                                            try { pname = Process.GetProcessById((int)pid).ProcessName; }
                                            catch { pname = "(dead)"; }
                                            pidText = $"{pid} ({pname})";
                                        }
                                        finally { RawRelease(ctl2); }
                                    }
                                    Guid sav = IID_ISimpleAudioVolume;
                                    if (RawQI(ctl, sav, out IntPtr vp) == 0 && vp != IntPtr.Zero)
                                    {
                                        try
                                        {
                                            Marshal.GetDelegateForFunctionPointer<GetMasterVolumeProc>(Slot(vp, 4))(vp, out vol);
                                            sb.AppendLine($"  [{i}] pid={pidText} vol={vol:0.00} mute={muted}");
                                        }
                                        finally { RawRelease(vp); }
                                    }
                                    else sb.AppendLine($"  [{i}] pid={pidText} (no ISimpleAudioVolume)");
                                }
                                finally { RawRelease(ctl); }
                            }
                        }
                        finally { RawRelease(sessions); }
                    }
                    finally { RawRelease(mgr); }
                }
                finally { RawRelease(dev); }
            }
            finally { RawRelease(punk); }
        }
        catch (Exception ex)
        {
            sb.AppendLine("ERR: " + ex.Message);
        }
        return sb.ToString();
    }
}
