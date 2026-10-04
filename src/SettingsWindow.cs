using System.Runtime.InteropServices;
using static EdgeGlide.Native;

namespace EdgeGlide;

/// <summary>
/// The settings window. It's a standard Windows dialog built in memory (no UI framework), so it adds almost nothing
/// to the exe and gets Windows' own look, DPI scaling and keyboard handling. Every change is saved immediately.
/// </summary>
internal sealed unsafe class SettingsWindow(App app)
{
    const int IdStatus = 100, IdList = 101, IdName = 102, IdBrowse = 103, IdRemove = 104,
        IdStartup = 105, IdPaused = 106, IdOpenFile = 107, IdFullscreen = 108, IdAdd = IDOK, IdClose = IDCANCEL;

    // Styles used only by this dialog.
    const uint DS_SETFONT = 0x40, DS_MODALFRAME = 0x80, DS_CENTER = 0x800;
    const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000,
        WS_TABSTOP = 0x00010000, WS_VSCROLL = 0x00200000, WS_EX_APPWINDOW = 0x00040000, WS_EX_CLIENTEDGE = 0x00000200;
    const uint SS_NOPREFIX = 0x80, SS_ETCHEDHORZ = 0x10;
    const uint BS_PUSHBUTTON = 0, BS_DEFPUSHBUTTON = 1, BS_AUTOCHECKBOX = 3;
    const uint LBS_NOTIFY = 0x1, LBS_SORT = 0x2, LBS_NOINTEGRALHEIGHT = 0x100, LBS_WANTKEYBOARDINPUT = 0x400;
    const uint CBS_DROPDOWN = 0x2, CBS_AUTOHSCROLL = 0x40, CBS_SORT = 0x100;
    const ushort Button = 0x80, Static = 0x82, ListBox = 0x83, ComboBox = 0x85;   // predefined window-class atoms

    static SettingsWindow? s_current;
    nint _bigIcon, _smallIcon;

    public nint Hwnd { get; private set; }

    public void Show()
    {
        if (Hwnd != 0)
        {
            ShowWindow(Hwnd, SW_RESTORE);
            SetForegroundWindow(Hwnd);
            return;
        }
        s_current = this;
        byte[] template = BuildTemplate();
        nint hwnd;
        fixed (byte* t = template)
            hwnd = CreateDialogIndirectParamW(GetModuleHandleW(null), t, 0,
                (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&DialogProc, 0);
        if (hwnd == 0)
        {
            Log.Error($"couldn't open the settings window (error {Marshal.GetLastPInvokeError()})");
            return;
        }
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
    }

    public void Close()
    {
        if (Hwnd != 0) DestroyWindow(Hwnd);
    }

    /// <summary>Re-reads the status line and the Paused box (called when EdgeGlide's state changes).</summary>
    public void RefreshStatus()
    {
        if (Hwnd == 0) return;
        SetDlgItemTextW(Hwnd, IdStatus, "Status: " + app.StatusText);
        CheckDlgButton(Hwnd, IdPaused, app.IsPaused ? BST_CHECKED : BST_UNCHECKED);
    }

    /// <summary>Re-reads everything stored in config.json (called after the file is edited by hand).</summary>
    public void RefreshConfig()
    {
        if (Hwnd == 0) return;
        RefreshList();
        CheckDlgButton(Hwnd, IdFullscreen, app.SuspendWhenFullscreen ? BST_CHECKED : BST_UNCHECKED);
    }

    void RefreshList()
    {
        if (Hwnd == 0) return;
        SendDlgItemMessageW(Hwnd, IdList, LB_RESETCONTENT, 0u, 0);
        foreach (var name in app.Blocklist) SendText(IdList, LB_ADDSTRING, name);
        UpdateRemoveButton();
    }

    // Layout in dialog units, which Windows scales with the font and the monitor's DPI.
    static byte[] BuildTemplate()
    {
        var t = new DialogTemplate("EdgeGlide settings", WS_POPUP | (uint)WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | DS_MODALFRAME | DS_CENTER,
            WS_EX_APPWINDOW, width: 262, height: 253, font: "Segoe UI", points: 9);
        t.Add(Static, IdStatus, "", SS_NOPREFIX, 7, 7, 248, 9);
        t.Add(Static, -1, "Keep EdgeGlide off while these apps are in the foreground:", SS_NOPREFIX, 7, 24, 248, 9);
        t.Add(ListBox, IdList, "", LBS_NOTIFY | LBS_SORT | LBS_NOINTEGRALHEIGHT | LBS_WANTKEYBOARDINPUT | WS_VSCROLL | WS_TABSTOP,
            7, 36, 186, 84, WS_EX_CLIENTEDGE);
        t.Add(Button, IdRemove, "&Remove", BS_PUSHBUTTON | WS_TABSTOP, 199, 36, 56, 14);
        t.Add(ComboBox, IdName, "", CBS_DROPDOWN | CBS_AUTOHSCROLL | CBS_SORT | WS_VSCROLL | WS_TABSTOP, 7, 126, 186, 120);
        t.Add(Button, IdAdd, "&Add", BS_DEFPUSHBUTTON | WS_TABSTOP, 199, 125, 56, 14);
        t.Add(Button, IdBrowse, "&Browse...", BS_PUSHBUTTON | WS_TABSTOP, 199, 143, 56, 14);
        t.Add(Static, -1, "Pick a running app from the list, type an exe name, or browse for one. Apps that hide or lock " +
            "the cursor are always detected automatically, and so are fullscreen apps while the first box below is ticked.",
            SS_NOPREFIX, 7, 143, 186, 34);
        t.Add(Static, -1, "", SS_ETCHEDHORZ, 7, 182, 248, 1);
        t.Add(Button, IdFullscreen, "&Turn off while a fullscreen app is in front", BS_AUTOCHECKBOX | WS_TABSTOP, 7, 189, 240, 10);
        t.Add(Button, IdStartup, "Start with &Windows", BS_AUTOCHECKBOX | WS_TABSTOP, 7, 202, 160, 10);
        t.Add(Button, IdPaused, "&Paused", BS_AUTOCHECKBOX | WS_TABSTOP, 7, 215, 160, 10);
        t.Add(Button, IdOpenFile, "&Open config file", BS_PUSHBUTTON | WS_TABSTOP, 7, 232, 80, 14);
        t.Add(Button, IdClose, "Close", BS_PUSHBUTTON | WS_TABSTOP, 199, 232, 56, 14);
        return t.ToArray();
    }

    [UnmanagedCallersOnly]
    static nint DialogProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (s_current is { } window) return window.Handle(hwnd, msg, wParam);
        }
        catch (Exception ex)
        {
            Log.Error($"settings window message 0x{msg:X4}", ex);
        }
        return 0;
    }

    nint Handle(nint hwnd, uint msg, nuint wParam)
    {
        switch (msg)
        {
            case WM_INITDIALOG:
                Hwnd = hwnd;
                Initialize();
                return 0;   // focus was set in Initialize

            case WM_COMMAND:
                OnCommand((int)(wParam & 0xFFFF), (int)((wParam >> 16) & 0xFFFF));
                return 1;

            case WM_VKEYTOITEM:   // Delete key in the list
                if ((int)(wParam & 0xFFFF) != VK_DELETE) return -1;
                RemoveSelected();
                return -2;

            case WM_CLOSE:
                DestroyWindow(hwnd);
                return 1;

            case WM_DESTROY:
                Hwnd = 0;
                if (_bigIcon != 0) DestroyIcon(_bigIcon);
                if (_smallIcon != 0) DestroyIcon(_smallIcon);
                _bigIcon = _smallIcon = 0;
                return 1;
        }
        return 0;
    }

    void Initialize()
    {
        nint big, small;
        if (ExtractIconExW(Environment.ProcessPath!, 0, &big, &small, 1) > 0)
        {
            _bigIcon = big;
            _smallIcon = small;
            SendMessageW(Hwnd, WM_SETICON, ICON_BIG, big);
            SendMessageW(Hwnd, WM_SETICON, ICON_SMALL, small);
        }
        SetDlgItemTextW(Hwnd, IdPaused, app.HotkeyRegistered ? "&Paused  (Ctrl+Alt+M)" : "&Paused");
        CheckDlgButton(Hwnd, IdStartup, Startup.IsEnabled() ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(Hwnd, IdFullscreen, app.SuspendWhenFullscreen ? BST_CHECKED : BST_UNCHECKED);
        SendText(IdName, CB_SETCUEBANNER, "Exe name, or pick a running app");
        RefreshStatus();
        RefreshList();
        SetFocus(GetDlgItem(Hwnd, IdName));
    }

    void OnCommand(int id, int code)
    {
        switch (id)
        {
            case IdAdd when code == BN_CLICKED: Add(GetText(IdName)); break;
            case IdBrowse when code == BN_CLICKED: Browse(); break;
            case IdRemove when code == BN_CLICKED: RemoveSelected(); break;
            case IdStartup when code == BN_CLICKED: SetStartup(IsDlgButtonChecked(Hwnd, IdStartup) == BST_CHECKED); break;
            case IdPaused when code == BN_CLICKED: app.SetPaused(IsDlgButtonChecked(Hwnd, IdPaused) == BST_CHECKED); break;
            case IdFullscreen when code == BN_CLICKED:
                app.SetSuspendWhenFullscreen(IsDlgButtonChecked(Hwnd, IdFullscreen) == BST_CHECKED);
                break;
            case IdOpenFile when code == BN_CLICKED: app.OpenConfigFile(); break;
            case IdClose: DestroyWindow(Hwnd); break;
            case IdName when code == CBN_DROPDOWN: FillRunningApps(); break;
            case IdList when code == LBN_SELCHANGE: UpdateRemoveButton(); break;
        }
    }

    void Add(string raw)
    {
        string name = ConfigStore.NormalizeName(raw);
        if (name.Length == 0) return;
        app.SetBlocklist(app.Blocklist.Append(name));
        RefreshList();
        nint index = SendText(IdList, LB_FINDSTRINGEXACT, name, nuint.MaxValue);
        if (index >= 0) SendDlgItemMessageW(Hwnd, IdList, LB_SETCURSEL, (nuint)index, 0);
        UpdateRemoveButton();
        SetDlgItemTextW(Hwnd, IdName, "");
    }

    void RemoveSelected()
    {
        int index = (int)SendDlgItemMessageW(Hwnd, IdList, LB_GETCURSEL, 0u, 0);
        if (index < 0) return;
        string name = ListItem(index);
        app.SetBlocklist(app.Blocklist.Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)));
        RefreshList();
        int count = (int)SendDlgItemMessageW(Hwnd, IdList, LB_GETCOUNT, 0u, 0);
        if (count > 0) SendDlgItemMessageW(Hwnd, IdList, LB_SETCURSEL, (nuint)Math.Min(index, count - 1), 0);
        UpdateRemoveButton();
    }

    void Browse()
    {
        char* file = stackalloc char[1024];
        file[0] = '\0';
        fixed (char* filter = "Programs (*.exe)\0*.exe\0All files\0*.*\0\0", title = "Choose an app to keep EdgeGlide off for")
        {
            var dialog = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = Hwnd,
                lpstrFilter = filter,
                lpstrFile = file,
                nMaxFile = 1024,
                lpstrTitle = title,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };
            if (GetOpenFileNameW(&dialog)) Add(new string(file));
        }
    }

    // Refilled each time the list drops down, so it shows what's running right now.
    void FillRunningApps()
    {
        string typed = GetText(IdName);   // resetting the list also clears the text box
        SendDlgItemMessageW(Hwnd, IdName, CB_RESETCONTENT, 0u, 0);
        foreach (var name in Processes.WithWindows())
            if (!app.Blocklist.Contains(name)) SendText(IdName, CB_ADDSTRING, name);
        SetDlgItemTextW(Hwnd, IdName, typed);
    }

    void SetStartup(bool enable)
    {
        try
        {
            Startup.Set(enable);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBoxW(Hwnd, ex.Message, "Couldn't change the startup setting", MB_ICONERROR);
        }
        CheckDlgButton(Hwnd, IdStartup, Startup.IsEnabled() ? BST_CHECKED : BST_UNCHECKED);
    }

    void UpdateRemoveButton() =>
        EnableWindow(GetDlgItem(Hwnd, IdRemove), SendDlgItemMessageW(Hwnd, IdList, LB_GETCURSEL, 0u, 0) >= 0);

    string GetText(int id)
    {
        char* buffer = stackalloc char[512];
        uint length = GetDlgItemTextW(Hwnd, id, buffer, 512);
        return new string(buffer, 0, (int)length);
    }

    string ListItem(int index)
    {
        int length = (int)SendDlgItemMessageW(Hwnd, IdList, LB_GETTEXTLEN, (nuint)index, 0);
        if (length <= 0) return "";
        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            SendDlgItemMessageW(Hwnd, IdList, LB_GETTEXT, (nuint)index, (nint)p);
            return new string(p, 0, length);
        }
    }

    nint SendText(int id, uint msg, string text, nuint wParam = 0)
    {
        fixed (char* p = text) return SendDlgItemMessageW(Hwnd, id, msg, wParam, (nint)p);
    }

    /// <summary>Writes a DLGTEMPLATE (the in-memory form of a dialog resource) plus its controls.</summary>
    sealed class DialogTemplate
    {
        readonly MemoryStream _data = new();
        readonly BinaryWriter _w;
        ushort _count;

        public DialogTemplate(string title, uint style, uint exStyle, short width, short height, string font, short points)
        {
            _w = new BinaryWriter(_data);
            _w.Write(style | DS_SETFONT);
            _w.Write(exStyle);
            _w.Write((ushort)0);   // control count, filled in by ToArray
            _w.Write((short)0); _w.Write((short)0); _w.Write(width); _w.Write(height);
            _w.Write((ushort)0);   // no menu
            _w.Write((ushort)0);   // standard dialog class
            Text(title);
            _w.Write(points);
            Text(font);
        }

        public void Add(ushort classAtom, int id, string text, uint style, short x, short y, short cx, short cy, uint exStyle = 0)
        {
            while (_data.Position % 4 != 0) _w.Write((byte)0);   // each control starts on a DWORD boundary
            _w.Write(style | WS_CHILD | WS_VISIBLE);
            _w.Write(exStyle);
            _w.Write(x); _w.Write(y); _w.Write(cx); _w.Write(cy);
            _w.Write((ushort)id);
            _w.Write((ushort)0xFFFF); _w.Write(classAtom);
            Text(text);
            _w.Write((ushort)0);   // no creation data
            _count++;
        }

        public byte[] ToArray()
        {
            var bytes = _data.ToArray();
            BitConverter.TryWriteBytes(bytes.AsSpan(8, 2), _count);
            return bytes;
        }

        void Text(string s)
        {
            foreach (char c in s) _w.Write((ushort)c);
            _w.Write((ushort)0);
        }
    }
}
