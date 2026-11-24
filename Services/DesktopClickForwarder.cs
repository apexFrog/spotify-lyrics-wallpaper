using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SpotifyLyricsWallpaper.Services;

/// <summary>
/// Lightweight low-level mouse hook that detects left-clicks on the Windows desktop surface
/// (SysListView32 / SHELLDLL_DefView / WorkerW / Progman) so interactive buttons on the wallpaper
/// respond to clicks even when the wallpaper is embedded behind desktop icons.
/// </summary>
public sealed class DesktopClickForwarder : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONUP = 0x0202;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private readonly LowLevelMouseProc _proc;
    private IntPtr _hookId;
    private bool _disposed;

    public event Action<int, int>? DesktopClicked;

    public DesktopClickForwarder()
    {
        _proc = HookCallback;
        try
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(curModule?.ModuleName), 0);
        }
        catch
        {
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONUP)
        {
            try
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var hwnd = WindowFromPoint(info.pt);
                if (hwnd != IntPtr.Zero && IsDesktopSurface(hwnd))
                {
                    DesktopClicked?.Invoke(info.pt.X, info.pt.Y);
                }
            }
            catch { }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsDesktopSurface(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        return cls is "SysListView32" or "SHELLDLL_DefView" or "WorkerW" or "Progman";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}
