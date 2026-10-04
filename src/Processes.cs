using System.Runtime.InteropServices;
using static EdgeGlide.Native;

namespace EdgeGlide;

/// <summary>
/// Process names, read from a process snapshot rather than by opening processes, so EdgeGlide never holds a handle
/// to a game.
/// </summary>
internal static unsafe class Processes
{
    public static string NameOf(uint pid) => Scan(pid, null);

    /// <summary>Exe names of apps that currently have a visible window, for the settings window's picker.</summary>
    public static List<string> WithWindows()
    {
        s_windowPids = [];
        EnumWindows((nint)(delegate* unmanaged<nint, nint, int>)&OnWindow, 0);
        var names = new Dictionary<uint, string>();
        Scan(0, names);
        uint self = (uint)Environment.ProcessId;
        return s_windowPids.Where(pid => pid != self && names.ContainsKey(pid))
            .Select(pid => names[pid])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static HashSet<uint> s_windowPids = [];

    [UnmanagedCallersOnly]
    static int OnWindow(nint hwnd, nint data)
    {
        if (IsWindowVisible(hwnd) && GetWindowTextLengthW(hwnd) > 0 && (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, &pid);
            s_windowPids.Add(pid);
        }
        return 1;
    }

    // With `all` set, collects every process; otherwise returns the name of `pid` ("" if it's gone).
    static string Scan(uint pid, Dictionary<uint, string>? all)
    {
        nint snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return "";
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            for (bool ok = Process32FirstW(snapshot, &entry); ok; ok = Process32NextW(snapshot, &entry))
            {
                if (all != null) all[entry.th32ProcessID] = new string(entry.szExeFile);
                else if (entry.th32ProcessID == pid) return new string(entry.szExeFile);
            }
            return "";
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}
