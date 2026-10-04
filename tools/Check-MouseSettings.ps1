<#
.SYNOPSIS
    Snapshots every mouse setting EdgeGlide promises never to touch, then compares against it later.

.DESCRIPTION
    Records every value under HKCU\Control Panel\Mouse and HKCU\Control Panel\Cursors, the mouclass/mouhid
    driver parameters, and the live values Windows reports (pointer speed, acceleration thresholds and
    "Enhance pointer precision", trails, wheel lines, hide-while-typing, sonar, hover time).
    Read-only: this script never changes anything.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Check-MouseSettings.ps1 -Save before.json
    # ...run EdgeGlide, cross monitors, pause/resume, launch and quit a game...
    powershell -ExecutionPolicy Bypass -File tools\Check-MouseSettings.ps1 -Compare before.json
#>
[CmdletBinding(DefaultParameterSetName = 'Save')]
param(
    [Parameter(ParameterSetName = 'Save', Mandatory = $true)] [string] $Save,
    [Parameter(ParameterSetName = 'Compare', Mandatory = $true)] [string] $Compare
)
$ErrorActionPreference = 'Stop'

Add-Type -Namespace EdgeGlideTools -Name MouseQuery -MemberDefinition @'
[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] public static extern bool GetInts(uint action, uint param, int[] values, uint flags);
[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] public static extern bool GetInt(uint action, uint param, ref int value, uint flags);
'@

function Read-RegistryKey([string] $Path) {
    $values = [ordered]@{}
    if (-not (Test-Path $Path)) { return $values }
    $key = Get-Item -Path $Path
    foreach ($name in $key.GetValueNames()) {
        $value = $key.GetValue($name)
        if ($value -is [byte[]]) { $value = [BitConverter]::ToString($value) }
        elseif ($value -is [array]) { $value = $value -join '|' }
        $values["$Path\$name"] = "$value"
    }
    return $values
}

function Get-MouseState {
    $state = [ordered]@{}
    foreach ($path in 'HKCU:\Control Panel\Mouse',
                      'HKCU:\Control Panel\Cursors',
                      'HKLM:\SYSTEM\CurrentControlSet\Services\mouclass\Parameters',
                      'HKLM:\SYSTEM\CurrentControlSet\Services\mouhid\Parameters') {
        $values = Read-RegistryKey $path
        foreach ($k in $values.Keys) { $state[$k] = $values[$k] }
    }

    $mouse = New-Object int[] 3
    [void][EdgeGlideTools.MouseQuery]::GetInts(0x0003, 0, $mouse, 0)   # SPI_GETMOUSE
    $state['SPI_GETMOUSE (threshold1, threshold2, enhance pointer precision)'] = $mouse -join ', '

    $queries = [ordered]@{
        'SPI_GETMOUSESPEED (1-20)' = 0x0070
        'SPI_GETMOUSETRAILS'       = 0x005E
        'SPI_GETWHEELSCROLLLINES'  = 0x0068
        'SPI_GETMOUSEHOVERTIME'    = 0x0066
        'SPI_GETMOUSEVANISH'       = 0x1020
        'SPI_GETMOUSESONAR'        = 0x101C
    }
    foreach ($name in $queries.Keys) {
        $value = 0
        [void][EdgeGlideTools.MouseQuery]::GetInt($queries[$name], 0, [ref]$value, 0)
        $state[$name] = "$value"
    }
    return $state
}

$current = Get-MouseState

if ($PSCmdlet.ParameterSetName -eq 'Save') {
    $current | ConvertTo-Json | Set-Content -Path $Save -Encoding UTF8
    Write-Host "Saved $($current.Count) mouse settings to $Save"
    exit 0
}

$saved = @{}
(Get-Content -Path $Compare -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $saved[$_.Name] = [string]$_.Value }
$now = @{}
foreach ($k in $current.Keys) { $now[$k] = $current[$k] }

$differences = foreach ($name in (@($saved.Keys) + @($now.Keys) | Sort-Object -Unique)) {
    $before = if ($saved.ContainsKey($name)) { $saved[$name] } else { '(missing)' }
    $after = if ($now.ContainsKey($name)) { $now[$name] } else { '(missing)' }
    if ($before -cne $after) { [pscustomobject]@{ Setting = $name; Before = $before; Now = $after } }
}

if ($differences) {
    Write-Host 'CHANGED:' -ForegroundColor Red
    $differences | Format-Table -AutoSize -Wrap
    exit 1
}
Write-Host "No differences: all $($now.Count) mouse settings match $Compare." -ForegroundColor Green
