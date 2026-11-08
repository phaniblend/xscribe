using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace XScribeDesktop;

/// <summary>
/// Focuses the Claude Code Desktop window and pastes+submits text into it via
/// clipboard + simulated keystrokes. Runs entirely in-process (no PowerShell
/// shell-out) since the WPF UI thread is already STA, which Clipboard needs.
/// </summary>
public static class DesktopSender
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    public static async Task<(bool Ok, string? Error)> SendAsync(string text, string processName = "claude", string titleContains = "Claude")
    {
        var proc = Process.GetProcessesByName(processName)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.MainWindowTitle.Contains(titleContains));

        if (proc == null || proc.MainWindowHandle == IntPtr.Zero)
            return (false, "Claude Code Desktop window not found — is it running?");

        // Only un-minimize if it's actually minimized — calling SW_RESTORE unconditionally
        // was snapping the window back to its "restored" bounds even when it was already
        // visible at a custom size/position, which looked like an unwanted resize on every send.
        if (IsIconic(proc.MainWindowHandle))
            ShowWindow(proc.MainWindowHandle, SW_RESTORE);

        if (!SetForegroundWindow(proc.MainWindowHandle))
            return (false, "Could not bring Claude Code Desktop to the foreground");

        await Task.Delay(250);

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            return (false, $"Clipboard error: {ex.Message}");
        }

        await Task.Delay(150);
        SendKeys.SendWait("^v");
        await Task.Delay(200);
        SendKeys.SendWait("{ENTER}");

        return (true, null);
    }
}
