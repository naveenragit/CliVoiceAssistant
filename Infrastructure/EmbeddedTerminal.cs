using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceAssistant.Infrastructure;

/// <summary>
/// Launches copilot.exe directly inside a conhost terminal window and embeds
/// that window into a WinForms Panel via Win32 SetParent.
///
/// V4 Architecture: No node.js bridge, no xterm.js, no WebView2.
/// copilot.exe runs in a real conhost — native rendering, native keyboard.
/// Voice commands use a separate prompt-mode process (CopilotCliTool).
/// </summary>
public sealed class EmbeddedTerminal : IDisposable
{
    // ── Win32 imports ─────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int GWL_STYLE   = -16;
    private const int GWL_EXSTYLE = -20;

    private const uint WS_CHILD       = 0x40000000;
    private const uint WS_VISIBLE     = 0x10000000;
    private const uint WS_CAPTION     = 0x00C00000;
    private const uint WS_THICKFRAME  = 0x00040000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_MAXIMIZEBOX = 0x00010000;
    private const uint WS_SYSMENU     = 0x00080000;

    private const uint WS_EX_APPWINDOW  = 0x00040000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private const uint WM_CLOSE   = 0x0010;
    private const uint WM_CHAR    = 0x0102;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP   = 0x0101;
    private const int  VK_RETURN  = 0x0D;
    private const int  VK_UP      = 0x26;
    private const int  VK_DOWN    = 0x28;
    private const int  VK_ESCAPE  = 0x1B;
    private const int  VK_TAB     = 0x09;

    private const uint RDW_INVALIDATE  = 0x0001;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW   = 0x0100;

    // ── State ─────────────────────────────────────────────────────────────────

    private Process? _process;
    private IntPtr   _terminalHwnd;
    private Panel?   _hostPanel;

    public bool IsRunning => _process is { HasExited: false };
    public IntPtr TerminalHandle => _terminalHwnd;

    /// <summary>
    /// Launches copilot.exe in a conhost window and embeds it into the given panel.
    /// </summary>
    public async Task StartAsync(Panel hostPanel, string workingDirectory, string? sessionId = null)
    {
        _hostPanel = hostPanel;

        var copilotPath = FindCopilotExe();
        AppLog.Info($"EmbeddedTerminal: launching copilot.exe directly at {copilotPath}");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{copilotPath}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized,
        };

        // Pass session ID via environment variable so copilot picks up context
        if (!string.IsNullOrEmpty(sessionId))
            psi.EnvironmentVariables["COPILOT_SESSION_ID"] = sessionId;

        _process = Process.Start(psi);
        if (_process == null)
            throw new InvalidOperationException("Failed to start copilot.exe");

        AppLog.Info($"EmbeddedTerminal: copilot launched (PID: {_process.Id})");

        // Wait for the console window to appear
        _terminalHwnd = await WaitForWindowAsync(_process.Id, timeoutMs: 15000);

        if (_terminalHwnd == IntPtr.Zero)
        {
            AppLog.Error("EmbeddedTerminal: could not find terminal window");
            return;
        }

        AppLog.Info($"EmbeddedTerminal: found window 0x{_terminalHwnd:X} — embedding");

        // Reparent into our panel
        EmbedWindow(_terminalHwnd, hostPanel);

        // Click on the panel → focus the embedded terminal
        hostPanel.Click += (_, _) => Focus();
        hostPanel.MouseDown += (_, _) => Focus();

        // Wire up resize — conhost handles column/row recalculation natively
        hostPanel.Resize += (_, _) => ResizeTerminal();
        ResizeTerminal();

        // Give it initial focus
        await Task.Delay(500);
        Focus();
    }

    private void EmbedWindow(IntPtr hwnd, Panel panel)
    {
        // Strip window decorations
        uint style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLong(hwnd, GWL_STYLE, style);

        // Remove from taskbar
        uint exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_APPWINDOW;
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        // Reparent
        SetParent(hwnd, panel.Handle);

        // Show and fit
        ShowWindow(hwnd, 1); // SW_SHOWNORMAL
        MoveWindow(hwnd, 0, 0, panel.Width, panel.Height, true);
    }

    public void ResizeTerminal()
    {
        if (_terminalHwnd == IntPtr.Zero || _hostPanel == null) return;

        MoveWindow(_terminalHwnd, 0, 0, _hostPanel.Width, _hostPanel.Height, true);

        // Force redraw after resize (terminal renderers sometimes lag)
        Task.Delay(50).ContinueWith(_ =>
        {
            if (_terminalHwnd != IntPtr.Zero)
            {
                InvalidateRect(_terminalHwnd, IntPtr.Zero, true);
                RedrawWindow(_terminalHwnd, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
            }
        });
    }

    /// <summary>Give keyboard focus to the embedded terminal.</summary>
    public void Focus()
    {
        if (_terminalHwnd != IntPtr.Zero)
            SetForegroundWindow(_terminalHwnd);
    }

    /// <summary>
    /// Inject text into the embedded terminal as keystrokes, optionally pressing Enter.
    /// Used by the voice assistant to type voice commands directly into Copilot CLI.
    /// </summary>
    public async Task SendTextAsync(string text, bool submitWithEnter = true)
    {
        if (_terminalHwnd == IntPtr.Zero)
            throw new InvalidOperationException("Terminal window not available");

        AppLog.Info($"EmbeddedTerminal: injecting {text.Length} chars, enter={submitWithEnter}");

        // Focus the terminal first
        Focus();
        await Task.Delay(100);

        // Send each character via WM_CHAR
        foreach (var ch in text)
        {
            PostMessage(_terminalHwnd, WM_CHAR, (IntPtr)ch, IntPtr.Zero);
            await Task.Delay(5); // small delay for reliability
        }

        if (submitWithEnter)
        {
            await Task.Delay(50);
            PostMessage(_terminalHwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
            PostMessage(_terminalHwnd, WM_KEYUP,   (IntPtr)VK_RETURN, IntPtr.Zero);
        }

        AppLog.Info("EmbeddedTerminal: keystroke injection complete");
    }

    /// <summary>
    /// Send a special key (arrow, enter, escape, tab) to the embedded terminal.
    /// Used to navigate Copilot CLI selection menus via voice commands.
    /// </summary>
    public async Task SendKeyAsync(string key, int repeat = 1)
    {
        if (_terminalHwnd == IntPtr.Zero)
            throw new InvalidOperationException("Terminal window not available");

        int vk = key.ToLowerInvariant() switch
        {
            "up"     => VK_UP,
            "down"   => VK_DOWN,
            "enter"  => VK_RETURN,
            "escape" or "esc" => VK_ESCAPE,
            "tab"    => VK_TAB,
            _ => throw new ArgumentException($"Unknown key: {key}")
        };

        Focus();
        await Task.Delay(50);

        for (int i = 0; i < repeat; i++)
        {
            PostMessage(_terminalHwnd, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
            PostMessage(_terminalHwnd, WM_KEYUP,   (IntPtr)vk, IntPtr.Zero);
            await Task.Delay(50);
        }

        AppLog.Info($"EmbeddedTerminal: sent key '{key}' x{repeat}");
    }

    /// <summary>Wait for a visible window owned by the given PID.</summary>
    private static async Task<IntPtr> WaitForWindowAsync(int pid, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            IntPtr found = IntPtr.Zero;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out var winPid);
                if ((int)winPid == pid)
                {
                    found = hwnd;
                    return false; // stop
                }
                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero)
                return found;

            await Task.Delay(100);
        }

        return IntPtr.Zero;
    }

    /// <summary>Find copilot.exe path — same logic as CopilotCliTool.</summary>
    private static string FindCopilotExe()
    {
        // Check WinGet install location
        var wingetPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Packages\GitHub.Copilot_Microsoft.Winget.Source_8wekyb3d8bbwe\copilot.exe");
        if (File.Exists(wingetPath)) return wingetPath;

        // Check PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "copilot.exe");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("copilot.exe not found. Install GitHub Copilot CLI via WinGet.");
    }

    public void Dispose()
    {
        AppLog.Info("EmbeddedTerminal: disposing — killing copilot process tree");

        if (_terminalHwnd != IntPtr.Zero)
        {
            PostMessage(_terminalHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _terminalHwnd = IntPtr.Zero;
        }

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
                AppLog.Info($"EmbeddedTerminal: process tree killed (PID {_process.Id})");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"EmbeddedTerminal: kill failed: {ex.Message}");
            }
        }
        _process?.Dispose();
        _process = null;
    }

    /// <summary>
    /// Kill any orphaned copilot processes. Called from ProcessExit as a safety net.
    /// </summary>
    public static void KillOrphans()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("copilot"))
            {
                try
                {
                    // Only kill copilot processes that are children of cmd.exe
                    // (i.e., launched by us, not the user's own terminal)
                    if (p.MainWindowTitle.Length == 0) continue;
                    AppLog.Info($"EmbeddedTerminal: killing orphan copilot PID {p.Id}");
                    p.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }
        catch { }
    }
}
