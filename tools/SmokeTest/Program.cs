using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class SeerSmoke
{
    private const uint WM_COPYDATA = 0x004A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam,
        ref COPYDATASTRUCT lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private static IntPtr Send(IntPtr hwnd, int command, string payload)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        IntPtr buf = IntPtr.Zero;
        try
        {
            var cds = new COPYDATASTRUCT { dwData = new IntPtr(command) };
            if (payload != null)
            {
                buf = Marshal.StringToHGlobalUni(payload);
                cds.cbData = Encoding.Unicode.GetByteCount(payload) + 2;
                cds.lpData = buf;
            }
            IntPtr result;
            IntPtr ok = SendMessageTimeoutW(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds,
                SMTO_ABORTIFHUNG, 1500, out result);
            if (ok == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine($"    [warn] SendMessageTimeout failed, win32err={err}");
                return IntPtr.Zero;
            }
            return result;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    private static string FindTestFile()
    {
        string[] candidates =
        {
            @"C:\Windows\Web\Wallpaper\Windows\img0.jpg",
            @"C:\Windows\Web\Screen\img100.jpg",
            @"C:\Windows\System32\drivers\etc\hosts",
            @"C:\Windows\win.ini"
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Seer IPC smoke test ===");

        IntPtr hwnd = FindWindowW("SeerWindowClass", null);
        Console.WriteLine($"SeerWindowClass hwnd   : 0x{hwnd.ToInt64():X}");

        if (hwnd == IntPtr.Zero)
        {
            IntPtr viewer = FindWindowW("SeerViewerWindowClass", null);
            Console.WriteLine($"SeerViewerWindowClass  : 0x{viewer.ToInt64():X}");
            Console.WriteLine("RESULT: FAIL - Seer main window not found (packaged-app / UIPI issue or Seer closed)");
            Environment.ExitCode = 2;
            return;
        }

        IntPtr version = Send(hwnd, 5007, null);
        Console.WriteLine($"GET_VERSION (5007)     : {version.ToInt64()}");

        IntPtr tracking = Send(hwnd, 5008, null);
        Console.WriteLine($"IS_TRACKING (5008)     : {tracking.ToInt64()}");

        IntPtr visBefore = Send(hwnd, 5004, null);
        Console.WriteLine($"IS_VISIBLE before(5004): {visBefore.ToInt64()}");

        string file = FindTestFile();
        if (file == null)
        {
            Console.WriteLine("RESULT: FAIL - no test file found");
            Environment.ExitCode = 3;
            return;
        }
        Console.WriteLine($"test file              : {file}");

        IntPtr r = Send(hwnd, 5000, file);
        Console.WriteLine($"INVOKE_W32 (5000) ret  : {r.ToInt64()}");
        Thread.Sleep(700);

        IntPtr visAfter = Send(hwnd, 5004, null);
        Console.WriteLine($"IS_VISIBLE after (5004): {visAfter.ToInt64()}");

        Thread.Sleep(1200);
        IntPtr visLater = Send(hwnd, 5004, null);
        Console.WriteLine($"IS_VISIBLE +1.2s(5004) : {visLater.ToInt64()}");

        if (visAfter.ToInt64() != 0 || visLater.ToInt64() != 0)
        {
            Console.WriteLine("RESULT: PASS - Seer accepted the IPC preview request");
        }
        else
        {
            Console.WriteLine("RESULT: INCONCLUSIVE - message delivered but window not visible");
        }

        Thread.Sleep(2500);
        Send(hwnd, 5005, null);
        Console.WriteLine("hide sent (5005)");
    }
}
