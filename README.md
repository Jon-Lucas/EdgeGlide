# EdgeGlide

**Smooth cursor crossings between monitors that don't line up.**

When your monitors have different resolutions, Windows lines them up pixel for pixel. The cursor gets stuck where one screen sticks out past the other, and where it does cross, it jumps to a different height. EdgeGlide fixes both: leave one screen halfway down its edge and you arrive halfway down the next one. No dead zones, no jump.

It runs quietly in the system tray, works with any arrangement (side by side, stacked, portrait, three or more screens) and follows layout changes automatically.

## Download

Get **EdgeGlide.exe** from the [latest release](https://github.com/Jon-Lucas/EdgeGlide/releases/latest). It's a single file for 64-bit Windows 10 and 11, with nothing to install.

The exe isn't code-signed yet, so Windows may say "Windows protected your PC". Click **More info › Run anyway**. Each release lists the file's SHA-256 if you want to check your download (`Get-FileHash .\EdgeGlide.exe` in PowerShell).

## Use

1. Put `EdgeGlide.exe` somewhere permanent, such as `%LOCALAPPDATA%\Programs\EdgeGlide\`, and run it.
2. Watch the tray icon. **Teal** means it's on. **Amber** means it has switched itself off for now (hover over it to see why). **Grey** means paused.
3. **Left-click** the icon for Settings: apps to ignore, fullscreen behaviour, Start with Windows. **Right-click** it for the menu. **Ctrl+Alt+M** pauses and resumes.

**Tip:** for the cleanest crossings, turn off Windows' own "Ease cursor movement between displays" (Settings › System › Display › Multiple displays).

## Safe for gaming

EdgeGlide never changes mouse speed, acceleration or any other mouse setting, and it never hooks, delays or alters your mouse movement. It only reads where the cursor is and moves it once when it crosses an edge. It doesn't inject anything or open other programs' processes.

It also switches itself completely off while a fullscreen app is in front, and whenever a game hides or locks the cursor, so it stays out of your aim.

## More

To build from source, edit the config file, run the test checklist, or see how it works, read [docs/DETAILS.md](docs/DETAILS.md).

## License

[MIT](LICENSE)
