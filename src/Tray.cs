using System.ComponentModel;
using System.Runtime.InteropServices;
using static EdgeGlide.Native;

namespace EdgeGlide;

/// <summary>
/// The notification-area icon: the EdgeGlide cursor, whose glide streaks show the state:
/// teal (Active), amber (Suspended) or grey (Paused). Artwork comes from tools\Make-Icons.ps1.
/// </summary>
internal sealed unsafe class Tray : IDisposable
{
    const uint IconId = 1;

    readonly nint _hwnd;
    readonly uint _callbackMessage;
    readonly nint _activeIcon, _suspendedIcon, _pausedIcon;

    public Tray(nint hwnd, uint callbackMessage)
    {
        _hwnd = hwnd;
        _callbackMessage = callbackMessage;
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        _activeIcon = LoadEmbeddedIcon("TrayActive.ico", size);
        _suspendedIcon = LoadEmbeddedIcon("TraySuspended.ico", size);
        _pausedIcon = LoadEmbeddedIcon("TrayPaused.ico", size);
    }

    public bool IsAdded { get; private set; }

    /// <summary>Adds the icon. Also used after Explorer restarts, when the old icon is gone.</summary>
    public void Add(GateResult state)
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.uCallbackMessage = _callbackMessage;
        SetState(&data, state);
        IsAdded = Shell_NotifyIconW(NIM_ADD, &data) || Shell_NotifyIconW(NIM_MODIFY, &data);
        if (!IsAdded) return;
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    public void Update(GateResult state)
    {
        var data = NewData(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        SetState(&data, state);
        if (!IsAdded || !Shell_NotifyIconW(NIM_MODIFY, &data)) Add(state);
    }

    public void Warn(string title, string text)
    {
        Log.Info($"{title}: {text}");
        if (!IsAdded) return;
        var data = NewData(NIF_INFO);
        Copy(data.szInfoTitle, 64, title);
        Copy(data.szInfo, 256, text);
        data.dwInfoFlags = NIIF_WARNING | NIIF_NOSOUND;
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    public void Dispose()
    {
        var data = NewData(0);
        Shell_NotifyIconW(NIM_DELETE, &data);
        IsAdded = false;
        DestroyIcon(_activeIcon);
        DestroyIcon(_suspendedIcon);
        DestroyIcon(_pausedIcon);
    }

    NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
    };

    void SetState(NOTIFYICONDATAW* data, GateResult state)
    {
        data->hIcon = state.State switch
        {
            AppState.Active => _activeIcon,
            AppState.Paused => _pausedIcon,
            _ => _suspendedIcon,
        };
        Copy(data->szTip, 128, "EdgeGlide: " + state.Describe());
    }

    static void Copy(char* destination, int capacity, string text)
    {
        int length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }

    // Loads the image in an embedded .ico that best fits: the exact size, else the next larger, else the largest.
    static nint LoadEmbeddedIcon(string resource, int size)
    {
        using var stream = typeof(Tray).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded icon {resource}");
        var ico = new byte[stream.Length];
        stream.ReadExactly(ico);

        // ICONDIR (6 bytes), then one 16-byte entry per image: width, height, ..., byte count, file offset.
        int count = ico.Length >= 6 ? BitConverter.ToUInt16(ico, 4) : 0, pick = -1, pickSize = 0;
        if (count == 0 || 6L + 16L * count > ico.Length) throw new InvalidDataException($"Bad icon {resource}");
        for (int i = 0; i < count; i++)
        {
            int s = ico[6 + 16 * i] == 0 ? 256 : ico[6 + 16 * i];
            bool better = pick < 0
                || (s >= size && (pickSize < size || s < pickSize))
                || (s < size && pickSize < size && s > pickSize);
            if (better) { pick = i; pickSize = s; }
        }
        int entry = 6 + 16 * pick;
        int length = BitConverter.ToInt32(ico, entry + 8), offset = BitConverter.ToInt32(ico, entry + 12);
        if (length <= 0 || offset < 0 || (long)offset + length > ico.Length) throw new InvalidDataException($"Bad icon {resource}");
        nint icon;
        fixed (byte* image = &ico[offset])
            icon = CreateIconFromResourceEx(image, (uint)length, true, 0x00030000, size, size, 0);
        return icon != 0 ? icon : throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Couldn't load icon {resource}");
    }
}
