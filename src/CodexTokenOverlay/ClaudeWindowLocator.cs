using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexTokenOverlay;

internal static class ClaudeWindowLocator
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    public static bool TryGetForegroundClaudeTarget(out CodexWindowTarget target)
    {
        var hwnd = GetAncestor(GetForegroundWindow(), 2);
        return TryReadClaudeTarget(hwnd, out target);
    }

    public static bool TryRefreshKnownClaudeTarget(CodexWindowTarget previous, out CodexWindowTarget target) =>
        TryReadClaudeTarget(previous.HostWindow.Handle, out target);

    private static bool TryReadClaudeTarget(IntPtr hwnd, out CodexWindowTarget target)
    {
        target = null!;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || !IsClaudeDesktop(pid)) return false;
        if (!GetWindowRect(hwnd, out var rect)) return false;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 320 || height < 220) return false;
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96d;
        var work = Screen.FromHandle(hwnd).WorkingArea;
        var bounds = new IntRect(rect.Left, rect.Top, width, height);
        // Claude title-bar metrics are not public. Use a conservative in-window anchor.
        var chrome = new WindowChromeMetrics((int)(45 * scale), (int)(30 * scale),
            (int)(8 * scale), (int)(8 * scale), (int)(4 * scale));
        target = new CodexWindowTarget(new CodexWindowInfo(hwnd, bounds, bounds,
            null, IntRect.FromRectangle(work), dpi, chrome));
        return true;
    }

    private static bool IsClaudeDesktop(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, "Claude", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.ProcessName, "Claude Desktop", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception) { return false; }
    }
}
