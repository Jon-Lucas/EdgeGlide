using System.ComponentModel;
using System.Runtime.InteropServices;
using static EdgeGlide.Native;

namespace EdgeGlide;

/// <summary>
/// Owns EdgeGlide's hidden window and wires everything together. All work happens on this one thread in response
/// to events: WM_INPUT (a mouse report arrived), the settle timer, the foreground-change WinEvent,
/// WM_DISPLAYCHANGE, a 250 ms re-check timer, the tray icon and the pause hotkey. Between those, the thread sleeps.
/// </summary>
internal sealed unsafe class App
{
    public const string WindowClass = "EdgeGlide.Hidden";

    const uint WM_TRAY = WM_APP + 1;
    const uint WM_CONFIG_CHANGED = WM_APP + 2;

    /// <summary>Posted by a second copy of EdgeGlide.exe: open the settings window.</summary>
    public const uint WM_SHOW_SETTINGS = WM_APP + 3;
    const uint TimerRecheck = 1, TimerConfig = 2;
    const uint RecheckMs = 250, ConfigSettleMs = 300;
    const int HotkeyPause = 1;
    const uint CmdSettings = 1, CmdPause = 2, CmdStartup = 3, CmdExit = 4;

    /// <summary>A cursor sample older than this can't say where a crossing started.</summary>
    const long FreshMs = 100;

    /// <summary>A transient suspension (hidden cursor...) only shows in the tray if the mouse moved this recently.</summary>
    const long TransientDisplayMs = 500;

    /// <summary>How long after the last report near an edge to look at the cursor once more (in 100 ns units: 2 ms).</summary>
    const long SettleDelay = 20_000;

    static App? s_app;

    readonly EdgeMap _map = new();
    readonly SafetyGate _gate = new();
    readonly ConfigStore _config = new();
    readonly Rect[] _monitorBuffer = new Rect[16];
    Tray? _tray;
    SettingsWindow? _settings;
    nint _hwnd, _foregroundHook, _settleTimer;
    uint _taskbarCreated;
    bool _ready, _paused, _listening, _hotkeyRegistered;
    GateResult _shown = GateResult.Starting;

    Pt _last;
    bool _haveLast;
    long _lastTick, _lastInputTick;

    public int Run(bool openSettings = false)
    {
        VerifyLayouts();
        s_app = this;

        nint instance = GetModuleHandleW(null);
        fixed (char* className = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WndProcThunk,
                hInstance = instance,
                lpszClassName = className,
            };
            if (RegisterClassExW(&wc) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx failed");
        }

        // A hidden top-level window rather than a message-only one: message-only windows never receive broadcast
        // messages, and both WM_DISPLAYCHANGE and "TaskbarCreated" are broadcasts.
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, WindowClass, "EdgeGlide", WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx failed");

        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _tray = new Tray(_hwnd, WM_TRAY);
        _tray.Add(_shown);

        string? configError = _config.Load();
        _gate.Blocklist = _config.Blocklist;
        _gate.SuspendWhenFullscreen = _config.SuspendWhenFullscreen;
        _config.Watch(() => PostMessageW(_hwnd, WM_CONFIG_CHANGED, 0u, 0));

        _hotkeyRegistered = RegisterHotKey(_hwnd, HotkeyPause, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'M');
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0,
            (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&ForegroundThunk,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // Windows hands out a raw mouse report a moment before it moves the cursor, so a report can still show the
        // old position. The next report normally catches up; after the last one near an edge, this timer looks once more.
        _settleTimer = CreateWaitableTimerExW(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (_settleTimer == 0) _settleTimer = CreateWaitableTimerExW(0, null, 0, TIMER_ALL_ACCESS);

        RefreshLayout();
        Recheck();
        SetCoalescableTimer(_hwnd, TimerRecheck, RecheckMs, 0, 0);

        Log.Info($"started; {_map.Describe()}; {ConfigSummary}; " +
                 $"hotkey {(_hotkeyRegistered ? "Ctrl+Alt+M" : "unavailable")}");
        if (configError != null) _tray.Warn("config.json has an error", configError + "\nUsing an empty blocklist.");
        if (!_hotkeyRegistered) _tray.Warn("Ctrl+Alt+M is taken", "Another app already uses Ctrl+Alt+M. Pause from the tray menu instead.");

        _ready = true;
        if (openSettings) OpenSettings();
        try
        {
            return RunMessageLoop();
        }
        finally
        {
            if (_settleTimer != 0) CloseHandle(_settleTimer);
        }
    }

    // A normal message loop that also wakes when the settle timer fires. Blocks until there's something to do.
    int RunMessageLoop()
    {
        nint settle = _settleTimer;
        MSG msg;
        while (true)
        {
            uint wake = MsgWaitForMultipleObjectsEx(settle != 0 ? 1u : 0u, &settle, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (wake == WAIT_FAILED)
            {
                if (GetMessageW(&msg, 0, 0, 0) <= 0) return (int)msg.wParam;
                Dispatch(&msg);
                continue;
            }
            if (settle != 0 && wake == WAIT_OBJECT_0)
            {
                try
                {
                    OnSettled();
                }
                catch (Exception ex)
                {
                    Log.Error("settle check", ex);
                }
                continue;
            }
            while (PeekMessageW(&msg, 0, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) return (int)msg.wParam;
                Dispatch(&msg);
            }
        }
    }

    void Dispatch(MSG* msg)
    {
        // Gives the settings window Tab, Enter and Esc.
        if (_settings is { Hwnd: not 0 } settings && IsDialogMessageW(settings.Hwnd, msg)) return;
        TranslateMessage(msg);
        DispatchMessageW(msg);
    }

    [UnmanagedCallersOnly]
    static nint WndProcThunk(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (s_app is { _ready: true } app) return app.WndProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Log.Error($"handling message 0x{msg:X4}", ex);
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    static void ForegroundThunk(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (s_app is { _ready: true } app) app.Recheck();
        }
        catch (Exception ex)
        {
            Log.Error("handling foreground change", ex);
        }
    }

    nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                OnMouseReport(lParam);
                break;   // DefWindowProc below does the cleanup raw input requires

            case WM_TIMER when wParam == TimerRecheck:
                if (_tray is { IsAdded: false }) _tray.Add(_shown);   // Explorer wasn't ready yet at logon
                RefreshLayout();
                Recheck();
                return 0;

            case WM_TIMER when wParam == TimerConfig:
                KillTimer(hwnd, TimerConfig);
                ReloadConfig();
                return 0;

            case WM_CONFIG_CHANGED:   // editors save in bursts; let it settle
                SetCoalescableTimer(hwnd, TimerConfig, ConfigSettleMs, 0, 0);
                return 0;

            case WM_DISPLAYCHANGE:
                RefreshLayout();
                Recheck();
                break;

            case WM_HOTKEY when (int)wParam == HotkeyPause:
                TogglePause();
                return 0;

            case WM_TRAY:
                OnTrayMessage(wParam, lParam);
                return 0;

            case WM_SHOW_SETTINGS:
                OpenSettings();
                return 0;

            case WM_CLOSE:   // also sent by "EdgeGlide.exe --exit"
                DestroyWindow(hwnd);
                return 0;

            case WM_DESTROY:
                Shutdown();
                PostQuitMessage(0);
                return 0;

            default:
                if (msg == _taskbarCreated && _taskbarCreated != 0)   // Explorer restarted: the old icon is gone
                {
                    _tray?.Add(_shown);
                    return 0;
                }
                break;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // The hot path: runs once per mouse report while listening. Reads only; allocates nothing.
    void OnMouseReport(nint rawInput)
    {
        RAWINPUT input;
        uint size = (uint)sizeof(RAWINPUT);
        if (GetRawInputData(rawInput, RID_INPUT, &input, &size, (uint)sizeof(RAWINPUTHEADER)) == uint.MaxValue
            || input.header.dwType != RIM_TYPEMOUSE)
            return;

        long now = Environment.TickCount64;
        _lastInputTick = now;

        // Pens, tablets and remote-desktop pointers report absolute positions: leave them alone.
        if ((input.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0)
        {
            _haveLast = false;
            return;
        }

        POINT p;
        if (!GetCursorPos(&p)) return;
        var cur = new Pt(p.X, p.Y);
        var last = _haveLast ? _last : cur;
        bool fresh = _haveLast && now - _lastTick <= FreshMs;

        if (_map.TryGetWarp(last, fresh, cur, input.mouse.lLastX, input.mouse.lLastY, out Pt target) && TryWarp(target)) cur = target;

        _last = cur;
        _lastTick = now;
        _haveLast = true;

        if (_settleTimer != 0 && _map.IsNearSharedEdge(cur))
        {
            long due = -SettleDelay;   // negative = relative; re-arming pushes it back while reports keep coming
            SetWaitableTimer(_settleTimer, &due, 0, 0, 0, false);
        }
    }

    // Reports have paused near an edge; the last one may have moved the cursor across after we looked.
    void OnSettled()
    {
        POINT p;
        if (!_listening || !_haveLast || !GetCursorPos(&p)) return;
        var cur = new Pt(p.X, p.Y);
        if (cur == _last) return;
        long now = Environment.TickCount64;
        if (_map.TryGetWarp(_last, now - _lastTick <= FreshMs, cur, 0, 0, out Pt target) && TryWarp(target)) cur = target;
        _last = cur;
        _lastTick = now;
    }

    bool TryWarp(Pt target)
    {
        // Every action is decided from fresh facts, never from cached state.
        var state = _gate.Evaluate(_paused, _map.HasSharedEdges);
        if (state.State != AppState.Active)
        {
            Apply(state);
            return false;
        }
        return SetCursorPos(target.X, target.Y);
    }

    void Recheck() => Apply(_gate.Evaluate(_paused, _map.HasSharedEdges));

    void Apply(GateResult state)
    {
        // For anything that won't clear on the next mouse move (paused, fullscreen, blocklisted, confined cursor...)
        // stop receiving mouse reports altogether, so in a game EdgeGlide does no work per mouse movement at all.
        bool listen = state.State == AppState.Active || state.IsTransient;
        if (listen != _listening) SetListening(listen);

        // A cursor hidden while the mouse is idle is usually just "hide pointer while typing": don't flash the tray.
        var shown = state.IsTransient && state != _shown && Environment.TickCount64 - _lastInputTick > TransientDisplayMs
            ? GateResult.Active
            : state;
        if (shown == _shown) return;
        _shown = shown;
        _tray?.Update(shown);
        _settings?.RefreshStatus();
        Log.Info(shown.Describe());
    }

    void SetListening(bool on)
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = HID_USAGE_PAGE_GENERIC,
            usUsage = HID_USAGE_GENERIC_MOUSE,
            dwFlags = on ? RIDEV_INPUTSINK : RIDEV_REMOVE,
            hwndTarget = on ? _hwnd : 0,
        };
        if (RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE)))
        {
            _listening = on;
            _haveLast = false;
        }
        else
        {
            Log.Error($"RegisterRawInputDevices({(on ? "on" : "off")}) failed with error {Marshal.GetLastPInvokeError()}");
        }
    }

    void RefreshLayout()
    {
        var monitors = EnumerateMonitors(_monitorBuffer);
        if (_map.Matches(monitors)) return;
        _map.Rebuild(monitors);
        _haveLast = false;
        _settings?.RefreshStatus();
        Log.Info("layout: " + _map.Describe());
    }

    void TogglePause()
    {
        _paused = !_paused;
        Recheck();
    }

    void ReloadConfig()
    {
        var before = _config.Blocklist;
        bool fullscreenBefore = _config.SuspendWhenFullscreen;
        string? error = _config.Load();
        _gate.Blocklist = _config.Blocklist;
        _gate.SuspendWhenFullscreen = _config.SuspendWhenFullscreen;
        if (error != null) _tray?.Warn("config.json has an error", error + "\nKeeping the previous blocklist.");
        else if (!_config.Blocklist.SetEquals(before) || _config.SuspendWhenFullscreen != fullscreenBefore)
        {
            // A real edit, not our own save coming back.
            Log.Info($"config reloaded; {ConfigSummary}");
            _settings?.RefreshConfig();
        }
        Recheck();
    }

    void OnTrayMessage(nuint wParam, nint lParam)
    {
        uint trayEvent = (uint)(lParam & 0xFFFF);
        if (trayEvent is NIN_SELECT or NIN_KEYSELECT) OpenSettings();
        else if (trayEvent == WM_CONTEXTMENU) ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
    }

    void ShowMenu(int x, int y)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenuW(menu, MF_STRING | MF_GRAYED, 0u, "Status: " + _shown.Describe());
            AppendMenuW(menu, MF_SEPARATOR, 0u, null);
            AppendMenuW(menu, MF_STRING, CmdSettings, "Settings...");
            SetMenuDefaultItem(menu, CmdSettings, 0);   // bold: what a left-click opens
            AppendMenuW(menu, MF_STRING | (_paused ? MF_CHECKED : 0), CmdPause, _hotkeyRegistered ? "Pause\tCtrl+Alt+M" : "Pause");
            AppendMenuW(menu, MF_STRING | (Startup.IsEnabled() ? MF_CHECKED : 0), CmdStartup, "Start with Windows");
            AppendMenuW(menu, MF_SEPARATOR, 0u, null);
            AppendMenuW(menu, MF_STRING, CmdExit, "Exit");

            SetForegroundWindow(_hwnd);   // without this the menu won't close when you click elsewhere
            uint align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            uint command = (uint)TrackPopupMenuEx(menu, align | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                x, y, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0u, 0);

            switch (command)
            {
                case CmdPause: TogglePause(); break;
                case CmdStartup: ToggleStartup(); break;
                case CmdSettings: OpenSettings(); break;
                case CmdExit: DestroyWindow(_hwnd); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    void ToggleStartup()
    {
        try
        {
            Startup.Set(!Startup.IsEnabled());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _tray?.Warn("Couldn't change the startup setting", ex.Message);
        }
    }

    void OpenSettings() => (_settings ??= new SettingsWindow(this)).Show();

    // ---- what the settings window reads and changes ----
    internal IReadOnlySet<string> Blocklist => _config.Blocklist;
    internal bool SuspendWhenFullscreen => _config.SuspendWhenFullscreen;
    internal bool IsPaused => _paused;
    internal bool HotkeyRegistered => _hotkeyRegistered;
    internal string StatusText =>
        $"{_shown.Describe()}  ·  {Count(_map.MonitorCount, "monitor")}, {Count(_map.SharedEdgeCount, "shared edge")}";

    internal void SetPaused(bool paused)
    {
        if (paused != _paused) TogglePause();
    }

    internal void SetBlocklist(IEnumerable<string> names)
    {
        if (!Saved(_config.SaveBlocklist(names))) return;
        _gate.Blocklist = _config.Blocklist;
        Log.Info($"blocklist set to [{string.Join(", ", _config.Blocklist)}]");
        Recheck();
    }

    internal void SetSuspendWhenFullscreen(bool suspend)
    {
        if (!Saved(_config.SaveSuspendWhenFullscreen(suspend))) return;
        _gate.SuspendWhenFullscreen = suspend;
        Log.Info($"turn off while a fullscreen app is in front: {(suspend ? "yes" : "no")}");
        Recheck();
    }

    bool Saved(string? error)
    {
        if (error != null) _tray?.Warn("Couldn't save config.json", error);
        return error == null;
    }

    string ConfigSummary =>
        $"blocklist [{string.Join(", ", _config.Blocklist)}], off for fullscreen: {(_config.SuspendWhenFullscreen ? "yes" : "no")}";

    internal void OpenConfigFile() => _config.OpenInEditor();

    static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    void Shutdown()
    {
        KillTimer(_hwnd, TimerRecheck);
        KillTimer(_hwnd, TimerConfig);
        if (_listening) SetListening(false);
        if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook);
        if (_hotkeyRegistered) UnregisterHotKey(_hwnd, HotkeyPause);
        _settings?.Close();
        _config.Dispose();
        _tray?.Dispose();
        Log.Info("exited");
    }

    // ---- monitor enumeration (into a reused buffer, so the 250 ms check allocates nothing) ----
    static Rect[] s_enumTarget = [];
    static int s_enumCount;

    static ReadOnlySpan<Rect> EnumerateMonitors(Rect[] buffer)
    {
        s_enumTarget = buffer;
        s_enumCount = 0;
        EnumDisplayMonitors(0, null, (nint)(delegate* unmanaged<nint, nint, RECT*, nint, int>)&MonitorThunk, 0);
        return new ReadOnlySpan<Rect>(buffer, 0, s_enumCount);
    }

    [UnmanagedCallersOnly]
    static int MonitorThunk(nint monitor, nint hdc, RECT* bounds, nint data)
    {
        if (s_enumCount < s_enumTarget.Length)
            s_enumTarget[s_enumCount++] = new Rect(bounds->Left, bounds->Top, bounds->Right, bounds->Bottom);
        return 1;
    }
}
