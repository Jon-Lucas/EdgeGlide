using static EdgeGlide.Native;

namespace EdgeGlide;

internal enum AppState { Active, Suspended, Paused }

internal enum Reason
{
    None, Starting, Paused, NoSharedEdge, NoForeground, Blocklisted, Fullscreen,
    WindowsD3DFullscreen, PresentationMode, SessionAway,
    CursorClipped, CursorHidden, TouchInput, QueryFailed,
}

internal readonly record struct GateResult(AppState State, Reason Cause, string? Detail = null)
{
    public static readonly GateResult Active = new(AppState.Active, Reason.None);
    public static readonly GateResult Starting = new(AppState.Suspended, Reason.Starting);

    /// <summary>
    /// Clears on its own with the next mouse move or within moments, so keep receiving mouse reports
    /// (every action is still re-checked first). Everything else also stops mouse reports entirely.
    /// </summary>
    public bool IsTransient => Cause is Reason.NoForeground or Reason.CursorHidden or Reason.TouchInput or Reason.QueryFailed;

    public string Describe() => State switch
    {
        AppState.Active => "Active",
        AppState.Paused => "Paused",
        _ => "Suspended (" + Cause switch
        {
            Reason.Starting => "starting",
            Reason.NoSharedEdge => "no monitors share an edge",
            Reason.NoForeground => "no foreground window",
            Reason.Blocklisted => "blocklisted: " + Detail,
            Reason.Fullscreen => string.IsNullOrEmpty(Detail) ? "fullscreen window" : "fullscreen: " + Detail,
            Reason.WindowsD3DFullscreen => "Windows reports D3D fullscreen",
            Reason.PresentationMode => "presentation mode",
            Reason.SessionAway => "locked or screen saver",
            Reason.CursorClipped => "cursor confined by an app",
            Reason.CursorHidden => "cursor hidden by an app",
            Reason.TouchInput => "touch or pen input",
            _ => "state unknown",
        } + ")",
    };
}

/// <summary>
/// Decides whether EdgeGlide may act right now. Every check is a read-only query, and anything unclear
/// counts as "suspended".
/// </summary>
internal sealed unsafe class SafetyGate
{
    uint _foregroundPid;
    string _foregroundExe = "";

    /// <summary>Exe file names, compared case-insensitively.</summary>
    public IReadOnlySet<string> Blocklist { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stay off while the app in front is fullscreen (the Settings checkbox; on by default).</summary>
    public bool SuspendWhenFullscreen { get; set; } = true;

    public GateResult Evaluate(bool paused, bool haveSharedEdge)
    {
        if (paused) return new(AppState.Paused, Reason.Paused);
        if (!haveSharedEdge) return Suspend(Reason.NoSharedEdge);

        nint foreground = GetForegroundWindow();
        if (foreground == 0) return Suspend(Reason.NoForeground);

        string exe = ForegroundExe(foreground);
        if (exe.Length > 0 && Blocklist.Contains(exe)) return Suspend(Reason.Blocklisted, exe);
        if (SuspendWhenFullscreen && IsFullscreen(foreground)) return Suspend(Reason.Fullscreen, exe);

        // Windows' general "busy" state (QUNS_BUSY) is deliberately not used: it stays on while a fullscreen game is
        // merely visible on another monitor behind the app you're using. The foreground check above covers the case
        // that matters, precisely.
        int notificationState;
        if (SHQueryUserNotificationState(&notificationState) < 0) return Suspend(Reason.QueryFailed);
        switch (notificationState)
        {
            case QUNS_RUNNING_D3D_FULL_SCREEN when SuspendWhenFullscreen: return Suspend(Reason.WindowsD3DFullscreen);
            case QUNS_PRESENTATION_MODE: return Suspend(Reason.PresentationMode);
            case QUNS_NOT_PRESENT: return Suspend(Reason.SessionAway);
        }

        RECT clip;
        if (!GetClipCursor(&clip)) return Suspend(Reason.QueryFailed);
        if (IsClipped(clip)) return Suspend(Reason.CursorClipped);

        var cursor = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!GetCursorInfo(&cursor)) return Suspend(Reason.QueryFailed);
        if ((cursor.flags & CURSOR_SUPPRESSED) != 0) return Suspend(Reason.TouchInput);
        if ((cursor.flags & CURSOR_SHOWING) == 0 || cursor.hCursor == 0) return Suspend(Reason.CursorHidden);

        return GateResult.Active;
    }

    static GateResult Suspend(Reason cause, string? detail = null) => new(AppState.Suspended, cause, detail);

    // With no confinement, GetClipCursor reports the whole virtual screen.
    static bool IsClipped(RECT clip)
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return clip.Left > x || clip.Top > y || clip.Right < x + w || clip.Bottom < y + h;
    }

    static bool IsFullscreen(nint hwnd)
    {
        if (hwnd == GetShellWindow()) return false;
        char* className = stackalloc char[16];
        var name = new ReadOnlySpan<char>(className, Math.Max(0, GetClassNameW(hwnd, className, 16)));
        if (name is "Progman" or "WorkerW") return false;   // the desktop covers the screen but isn't an app

        RECT window;
        var monitor = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!GetWindowRect(hwnd, &window) || !GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &monitor))
            return true;   // the window vanished mid-check: assume the worst
        RECT m = monitor.rcMonitor;
        if (window.Left > m.Left || window.Top > m.Top || window.Right < m.Right || window.Bottom < m.Bottom)
            return false;

        // It covers the whole monitor. An ordinary maximized window can too (its frame hangs off the edges when the
        // taskbar auto-hides), so that one shape is exempt. Borderless and exclusive fullscreen windows are not.
        bool captionedMaximized = IsZoomed(hwnd) && (GetWindowLongPtrW(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
        return !captionedMaximized;
    }

    string ForegroundExe(nint hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid != _foregroundPid)
        {
            _foregroundPid = pid;
            _foregroundExe = Processes.NameOf(pid);
        }
        return _foregroundExe;
    }
}
