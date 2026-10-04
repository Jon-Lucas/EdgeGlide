namespace EdgeGlide;

internal static unsafe class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Take the current folder out of the DLL search order (EdgeGlide's own imports are pinned to System32).
        Native.SetDllDirectoryW("");
        // The manifest already asks for Per-Monitor-V2; this is a harmless no-op when that worked.
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        if (args is ["--exit" or "/exit"])
        {
            nint running = FindRunningCopy();
            if (running != 0) Native.PostMessageW(running, Native.WM_CLOSE, 0u, 0);
            return 0;
        }

        using var instance = new Mutex(false, @"Local\EdgeGlide.SingleInstance", out bool first);
        if (!first)
        {
            // Already running: starting it again opens the running copy's settings window.
            nint running = FindRunningCopy();
            if (running != 0)
            {
                uint pid;
                Native.GetWindowThreadProcessId(running, &pid);
                Native.AllowSetForegroundWindow(pid);
                Native.PostMessageW(running, App.WM_SHOW_SETTINGS, 0u, 0);
            }
            return 0;
        }

        try
        {
            return new App().Run(openSettings: args is ["--settings" or "/settings"]);
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            Native.MessageBoxW(0, ex.Message, "EdgeGlide stopped", Native.MB_ICONERROR);
            return 2;
        }
    }

    // EdgeGlide's hidden window, but only if it really belongs to an EdgeGlide.exe process: any program can create a
    // window with the same class name, and it shouldn't get our messages or foreground rights.
    static nint FindRunningCopy()
    {
        nint hwnd = Native.FindWindowW(App.WindowClass, null);
        if (hwnd == 0) return 0;
        uint pid;
        Native.GetWindowThreadProcessId(hwnd, &pid);
        string ours = Path.GetFileName(Environment.ProcessPath ?? "EdgeGlide.exe");
        return string.Equals(Processes.NameOf(pid), ours, StringComparison.OrdinalIgnoreCase) ? hwnd : 0;
    }
}
