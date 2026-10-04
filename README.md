# EdgeGlide

A small Windows 11 tray utility for monitors that don't line up pixel for pixel. It makes the cursor cross between them **at the same relative position along the whole shared edge**, so there are no dead zones and no jump. It never changes mouse settings or mouse input.

```
A → B:  y_B = B.top + (y_A − A.top) × B.height / A.height
B → A:  the inverse
```

With A at 2560×1440 and B at 1920×1080 placed at (2560, 360), stock Windows matches the monitors by pixel row. A's right edge is a wall for y < 360, and every other crossing jumps: leaving A halfway down arrives a third of the way down B. EdgeGlide maps A's full edge onto B's full edge.

### Any arrangement

The layout is read from Windows and re-read whenever it changes. Nothing is hard-coded, and nothing needs restarting when you rearrange monitors, plug one in or rotate one. Wherever monitors meet, the monitors on each side of that line are laid end to end and the whole of one side is mapped proportionally onto the whole of the other. This works for:

- **Side by side**, either way round, at any vertical offset (top-aligned, centred, bottom-aligned).
- **Stacked**, one above the other, at any horizontal offset. The same logic applies along the top and bottom edges.
- **Several monitors beside one edge**, for example two stacked to the right of one. The single monitor's edge spans both of them.
- **Grids, rows, L-shapes and T-shapes.** A row of three has two shared edges, and a 2×2 grid has four.
- **Portrait (rotated) monitors and mixed resolutions or scaling.**

An edge with nothing beside it, like the outer corner of an L-shape, stays an outer edge of the desktop, the same as in stock Windows.

Positions are mapped by *relative* position (half-way down one edge lands half-way down the other). That lines up physically when the monitors are the same physical size along the shared edge, like two 27" screens side by side. With different physical sizes, such as a 24" next to a 32", crossings still have no walls and no jumps, but they won't line up physically.

## Why it can't affect your aim

| EdgeGlide does | EdgeGlide never does |
|---|---|
| Gets a passive copy of each mouse report via Raw Input (`RIDEV_INPUTSINK`). Reports arrive after the fact and nothing waits on it. | Install hooks (`WH_MOUSE_LL` or any other), inject DLLs, inject input, or touch drivers |
| Reads the cursor position (`GetCursorPos`). | Read or write pointer speed, acceleration, `HKCU\Control Panel\Mouse` or driver settings |
| Calls `SetCursorPos` once at an edge crossing. This is the only action it ever takes. | Delay, scale or alter any mouse movement |
| Reads the foreground exe name from a process snapshot. | Open a handle to any other process, so anti-cheat has nothing to see |
| Runs as a normal user. | Ask for admin |

On top of that, it switches itself fully off whenever a game might be involved (see below).

## Download

Get `EdgeGlide.exe` from the [latest release](https://github.com/Jon-Lucas/EdgeGlide/releases/latest). It's one self-contained file for 64-bit Windows 10/11, with nothing to install and no .NET needed.

The exe isn't code-signed, so Windows SmartScreen may say "Windows protected your PC". Click **More info › Run anyway**. Each release lists the file's SHA-256, which you can check with `Get-FileHash .\EdgeGlide.exe` in PowerShell.

## Build

You need the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). From this folder, run:

```
dotnet publish src -c Release
```

This produces `src\bin\Release\net10.0-windows\win-x64\publish\EdgeGlide.exe`: one self-contained, trimmed exe that runs without .NET installed.

Optional:

- `dotnet run --project tests\EdgeMap.Tests` runs the mapping tests, which cover this exact layout plus edge cases.
- `dotnet publish src -c Release -p:PublishAot=true` builds a native exe instead (about 3 MB, faster start, less RAM). This needs Visual Studio Build Tools with "Desktop development with C++".
- `powershell -ExecutionPolicy Bypass -File tools\Make-Icons.ps1 -Preview preview.png` redraws the icons in `src\Assets` (the file icon and one tray icon per state) and saves a preview sheet. The artwork is vector, and the 16 px tray size is hand-drawn pixel by pixel. It uses WPF, which is built into Windows, so there's nothing to install.

## Use

Put `EdgeGlide.exe` somewhere permanent, for example `%LOCALAPPDATA%\Programs\EdgeGlide\`, and run it.

- **Tray icon** (a pointer with glide streaks): teal streaks mean Active, amber means Suspended, grey means Paused. Hover over it to see why.
- **Left-click the icon** to open **Settings**. Running `EdgeGlide.exe` again while it's already running does the same.
- **Right-click the icon** for the menu: Settings... · Pause · Start with Windows · Exit.
- **Ctrl+Alt+M** toggles Pause.
- `EdgeGlide.exe --exit` closes a running copy, which helps before replacing the exe. `EdgeGlide.exe --settings` starts it with Settings open.

### Settings

The Settings window shows the live status (Active / Suspended and why / Paused, plus how many monitors and shared edges were found). It lets you change:

- **Blocklist:** apps that keep EdgeGlide suspended while they're in the foreground. Open the dropdown to pick from apps that are running right now, type an exe name, or click **Browse...** to choose one. Select an entry and click **Remove**, or press Delete. You'll rarely need this, because fullscreen games and games that hide or confine the cursor are already detected automatically.
- **Turn off while a fullscreen app is in front** (ticked by default). Untick it to keep EdgeGlide working over fullscreen apps; it still switches off whenever a game hides or locks the cursor, so aiming in-game isn't affected either way.
- **Start with Windows** and **Paused**.

Every change is saved immediately. It's a standard Windows dialog, so Tab, Enter (Add) and Esc (Close) work, and it scales with your display settings.

### Config file

Settings are stored in `%APPDATA%\EdgeGlide\config.json` (**Open config file** in Settings opens it). You can also edit it by hand; it's reloaded as soon as you save:

```jsonc
{
  "suspendWhenFullscreen": true,                     // false = keep working over fullscreen apps
  "blocklist": [ "SomeGame.exe", "AnotherGame" ]   // ".exe" is optional; names aren't case-sensitive
}
```

## When it switches itself off

EdgeGlide only acts when every check passes. If any check fails, it does nothing.

| Suspended when | Detected by |
|---|---|
| You paused it | the tray menu or Ctrl+Alt+M |
| The foreground exe is blocklisted | config.json |
| The foreground window is fullscreen or borderless-fullscreen (unless you untick this in Settings) | the window covering its whole monitor (the only exception is a normal maximized window with a title bar) |
| Windows reports an exclusive (D3D) fullscreen app or presentation mode, or the session is locked | `SHQueryUserNotificationState` (its general "busy" state isn't used: it stays on while a fullscreen game is merely visible behind the app you're using) |
| An app has confined the cursor | `GetClipCursor` being smaller than the desktop |
| An app has hidden the cursor, or touch/pen input is active | `GetCursorInfo` |

The checks run:

- right before **every** cursor move, from fresh data and never a cached answer;
- whenever the foreground window changes;
- every 250 ms.

Some of these conditions don't clear by themselves: paused, fullscreen, blocklisted, Windows busy, confined cursor, locked. For those, EdgeGlide also **unregisters from raw input**. While you're in a game it doesn't receive your mouse reports at all, so it does zero work per mouse movement. It registers again within 250 ms of the condition clearing.

## Good to know

- **"Ease cursor movement between displays"** (Settings > System > Display > Multiple displays) is Windows' own partial fix. It frees the cursor at a wall, but drops it at the nearest point instead of the proportional one. If it's on, Windows moves the cursor first and EdgeGlide corrects the height a fraction of a millisecond later. Turn it off for the cleanest crossings. If you leave it on, you can still cross (non-proportionally) while EdgeGlide is suspended.
- **Getting through a wall takes a push, not just arriving.** The cursor has to already be on the edge column when the next mouse report or two come in. At 1000 Hz that's 1–2 ms later. This keeps targets on A's right edge, like a scrollbar, reachable.
- **A's top-right corner stops being a "throw the mouse there" target.** That's where a maximized window's Close button sits. If you keep pushing, you end up on B. That's what "no walls" means.
- **Admin windows:** Windows won't let a normal-user app move the cursor while an elevated window has focus, so EdgeGlide has no effect there.
- **Pens, tablets, touch and Remote Desktop pointers** report absolute positions, and EdgeGlide ignores them.
- **Cursor speed** (pixels per mouse count) still differs between monitors of different resolutions. EdgeGlide only changes where the cursor crosses.

## Test checklist

Run the scripts with `powershell -ExecutionPolicy Bypass -File <script>`.

1. **The wall zones now pass through.** Start `tools\Watch-Cursor.ps1`. Put the cursor on A's right edge in the top quarter of A (y < 360, above B's top edge) and keep moving right. It should land near the top of B instead of stopping. Try A's very top-right corner too; it should land at B's top-left. Then press Ctrl+Alt+M to pause and try again: the wall should be back. Unpause when you're done.
2. **The crossing height lines up.** With `Watch-Cursor.ps1` running, cross back and forth near the top, middle and bottom. Each crossing prints `left at X%, arrived at Y%`. With EdgeGlide active, X and Y should agree to within about 1%. While paused you'll see stock Windows instead: leaving A at 50% arrives at about 33%. Both screens are 27", so the pointer should also leave and enter at the same physical height.
3. **The tray shows "Suspended" in a game.** Start a game (fullscreen, borderless, or windowed with the mouse captured), and the tray icon should turn amber. If the game covers the taskbar, either run it on B and watch the tray on A, or check `%APPDATA%\EdgeGlide\edgeglide.log` afterwards. You should see a timestamped `Suspended (fullscreen: Game.exe)` or `Suspended (cursor confined by an app)` from when the game took focus, and `Active` from when you left it.
4. **The mouse registry and SPI values are unchanged.** With EdgeGlide **not** running, take a snapshot:
   ```
   powershell -ExecutionPolicy Bypass -File tools\Check-MouseSettings.ps1 -Save before.json
   ```
   Start EdgeGlide, cross monitors plenty of times, pause and resume, launch a game and quit it. Then compare:
   ```
   powershell -ExecutionPolicy Bypass -File tools\Check-MouseSettings.ps1 -Compare before.json
   ```
   You should get `No differences`. To confirm from the source too, run this; it should find nothing:
   ```
   Select-String -Path src\*.cs -Pattern 'SystemParametersInfo','Control Panel\\Mouse','SetWindowsHookEx','SendInput'
   ```

Two more checks worth doing:

5. **Layout changes apply live.** In Settings > Display, drag B to a different height and click Apply. Crossings should follow the new layout immediately, and the log should show a new `layout:` line.
6. **Idle cost.** Task Manager > Details should show EdgeGlide at 0% CPU while the mouse isn't moving.

## Files

| Location | Contents |
|---|---|
| `%APPDATA%\EdgeGlide\config.json` | The blocklist |
| `%APPDATA%\EdgeGlide\edgeglide.log` | State changes and errors only, capped at 512 KB |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\EdgeGlide` | Present only while "Start with Windows" is ticked |

To uninstall: untick Start with Windows, choose Exit, then delete the exe and `%APPDATA%\EdgeGlide`.

## How it works

Everything runs on a single thread driven by window messages. When nothing is happening, the thread sleeps in `GetMessage`.

On each mouse report (`WM_INPUT`), EdgeGlide reads the cursor position and compares it with the previous one. The geometry lives in [src/EdgeMap.cs](src/EdgeMap.cs), which makes no Windows calls. There are two cases:

- **Pushing against the edge.** The cursor was already on the shared edge column (or row, for stacked monitors), it's still there, and the raw motion points across. EdgeGlide moves it to the mapped point on the other side. This is what removes the walls. Where the monitors face each other, it also crosses a moment before Windows would, so the cursor never appears at the wrong spot.
- **Windows already carried it across.** The previous sample was on one side of the edge, the current one is on the other, and they're a plausible single step apart. EdgeGlide works out where the path crossed the edge and moves the cursor to the mapped position.

Stacked monitors use exactly the same rules with x and y swapped, so one code path handles every orientation.

Only the sign of `dx` is used, so pointer speed and acceleration don't matter. Before every move, [src/SafetyGate.cs](src/SafetyGate.cs) runs a fresh safety check.

Windows hands a raw mouse report to listeners just *before* it moves the cursor, so a report can still show the cursor's old position. The next report catches up. If that was the last report near the edge, a 2 ms high-resolution timer has EdgeGlide look once more. The timer is armed only near the shared edge and only by mouse reports, so it adds no polling.

The passive design has one trade-off. When Windows carries the cursor across (a flick that skips over the edge column), EdgeGlide corrects the height on the next report: about 1 ms later at 1000 Hz, or 2 ms later if you stop right there. Very occasionally you might see one frame at the uncorrected height. A low-level mouse hook could prevent that, but only by sitting in the path of every mouse event on the system, which is exactly what this tool avoids.

## License

[MIT](LICENSE).
