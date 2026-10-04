<#
.SYNOPSIS
    Live cursor readout for testing EdgeGlide.

.DESCRIPTION
    Shows the cursor position, which monitor it is on, and how far down that monitor it is (%).
    Each time the cursor changes monitor it prints the height it left at and the height it arrived at.
    With EdgeGlide active those agree to within about 1% (the rest is your hand moving vertically between
    samples). With EdgeGlide paused you see stock Windows: leaving A at 50% arrives at about 33% on B.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Watch-Cursor.ps1
#>

Add-Type -Namespace EdgeGlideTools -Name CursorProbe -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
[DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT point, uint flags);
[DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
'@

# Physical pixels: the same coordinates EdgeGlide works in.
[void][EdgeGlideTools.CursorProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))

function Get-CursorSample {
    $p = New-Object EdgeGlideTools.CursorProbe+POINT
    [void][EdgeGlideTools.CursorProbe]::GetCursorPos([ref]$p)
    $monitor = [EdgeGlideTools.CursorProbe]::MonitorFromPoint($p, 2)
    $info = New-Object EdgeGlideTools.CursorProbe+MONITORINFO
    $info.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($info)
    [void][EdgeGlideTools.CursorProbe]::GetMonitorInfo($monitor, [ref]$info)
    $r = $info.rcMonitor
    [pscustomobject]@{
        X       = $p.X
        Y       = $p.Y
        Monitor = $monitor
        Name    = '{0}x{1}@({2},{3})' -f ($r.Right - $r.Left), ($r.Bottom - $r.Top), $r.Left, $r.Top
        Percent = 100.0 * ($p.Y - $r.Top + 0.5) / ($r.Bottom - $r.Top)
    }
}

Write-Host 'Move the cursor between monitors. Ctrl+C to stop.'
$previous = Get-CursorSample
while ($true) {
    Start-Sleep -Milliseconds 5
    $now = Get-CursorSample
    if ($now.Monitor -ne $previous.Monitor) {
        Start-Sleep -Milliseconds 40   # let EdgeGlide finish its move
        $after = Get-CursorSample
        Write-Host ("`r{0} -> {1}: left at {2,5:N1}%, arrived at {3,5:N1}%  (difference {4,5:N1})        " -f
            $previous.Name, $after.Name, $previous.Percent, $after.Percent, ($after.Percent - $previous.Percent))
        $previous = $after
        continue
    }
    Write-Host -NoNewline ("`r{0,5},{1,5}  {2}  {3,5:N1}%        " -f $now.X, $now.Y, $now.Name, $now.Percent)
    $previous = $now
}
