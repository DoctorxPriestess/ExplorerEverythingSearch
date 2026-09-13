# Verification helper (not part of the product): drives the command line contract of the
# built EXE and reads the real windows it opens through UI Automation.
#   --version            -> message box with the version, process exits 0
#   --help               -> message box listing the switches, process exits 0
#   --settings  (2nd)    -> the running instance opens its settings window
#   --exit      (2nd)    -> the running instance shuts down
# Usage: pwsh -NoProfile -File tools\verify-cli.ps1 [-Exe <path>]
#
# The default target is the RID specific Release build of the application:
#   dotnet build src\ExplorerEverythingSearch.App\ExplorerEverythingSearch.App.csproj -c Release -r win-x64

param(
    [string] $Exe = (Join-Path $PSScriptRoot '..\src\ExplorerEverythingSearch.App\bin\Release\net8.0-windows\win-x64\ExplorerEverythingSearch.exe')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Get-Windows([int] $targetPid) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $targetPid)
    return @($AE::RootElement.FindAll($TS::Children, $cond))
}

function Wait-Window([int] $targetPid, [string] $className, [int] $timeoutMs = 20000) {
    $deadline = [datetime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([datetime]::UtcNow -lt $deadline) {
        foreach ($w in Get-Windows $targetPid) {
            if ($className -eq '' -or $w.Current.ClassName -eq $className) { return $w }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

# The content of a standard message box is read and dismissed through Win32: the UI Automation
# view of that dialog is empty in this session (no Text/Button children are exposed).
if (-not ('VerifyCli.Native' -as [type])) {
    Add-Type -Namespace VerifyCli -Name Native -MemberDefinition @'
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int max);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
'@
}

function Get-DialogText($window) {
    $handle = [IntPtr] $window.Current.NativeWindowHandle
    $script:dialogParts = @()
    $callback = [VerifyCli.Native+EnumProc] {
        param($child, $lParam)
        $cls = New-Object System.Text.StringBuilder 256
        [VerifyCli.Native]::GetClassName($child, $cls, 256) | Out-Null
        $text = New-Object System.Text.StringBuilder 8192
        [VerifyCli.Native]::GetWindowText($child, $text, 8192) | Out-Null
        if ($text.Length -gt 0) { $script:dialogParts += ("[{0}] {1}" -f $cls.ToString(), $text.ToString()) }
        return $true
    }
    [VerifyCli.Native]::EnumChildWindows($handle, $callback, [IntPtr]::Zero) | Out-Null
    return ($script:dialogParts -join "`n")
}

function Close-Dialog($window) {
    $handle = [IntPtr] $window.Current.NativeWindowHandle
    return [VerifyCli.Native]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_CLOSE
}

function Stop-Quietly($process) {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
}

$results = @()
function Check([string] $name, [bool] $ok, [string] $detail) {
    $script:results += [pscustomobject]@{ Check = $name; Ok = $ok; Detail = $detail }
    Write-Output ("{0,-4} {1,-42} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

Write-Output "EXE: $Exe"
Write-Output ("exists: {0}  size: {1:N0} bytes" -f (Test-Path $Exe), (Get-Item $Exe).Length)

# ---------- 1. --version ----------
$p = Start-Process -FilePath $Exe -ArgumentList '--version' -PassThru
$w = Wait-Window $p.Id '#32770'
if ($w) {
    $text = Get-DialogText $w
    $exited = $p.WaitForExit(15000)
    $code = if ($exited) { $p.ExitCode } else { $null }
    Check 'version: dialog appears' $true "title='$($w.Current.Name)' class='#32770'"
    Check 'version: text carries a version' ($text -match '\d+\.\d+\.\d+') "text='$($text -replace "`n", ' | ')'"
    Check 'version: dismissed by its OK button' (Close-Dialog $w) 'InvokePattern on the button'
    $exited = $p.WaitForExit(15000)
    Check 'version: process exits 0' ($exited -and $p.ExitCode -eq 0) "exited=$exited code=$(if ($exited) { $p.ExitCode } else { 'still running' })"
} else {
    Check 'version: dialog appears' $false 'no #32770 window within 20 s'
    Stop-Quietly $p
}

# ---------- 2. --help ----------
$p = Start-Process -FilePath $Exe -ArgumentList '--help' -PassThru
$w = Wait-Window $p.Id '#32770'
if ($w) {
    $text = Get-DialogText $w
    $missing = @()
    foreach ($switch in '--root', '--settings', '--exit', '--startup', '--version', '--help') {
        if ($text -notmatch [regex]::Escape($switch)) { $missing += $switch }
    }
    Check 'help: dialog appears' $true "title='$($w.Current.Name)'"
    Check 'help: documents every switch' ($missing.Count -eq 0) $(if ($missing.Count -eq 0) { 'all six switches present' } else { "missing: $($missing -join ', ')" })
    Close-Dialog $w | Out-Null
    $exited = $p.WaitForExit(15000)
    Check 'help: process exits 0' ($exited -and $p.ExitCode -eq 0) "exited=$exited code=$(if ($exited) { $p.ExitCode } else { 'still running' })"
} else {
    Check 'help: dialog appears' $false 'no #32770 window within 20 s'
    Stop-Quietly $p
}

# ---------- 3. second instance: --settings and --exit ----------
$root = Join-Path $env:TEMP ("ees-cli-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Force -Path $root | Out-Null
$cfg = Join-Path $root 'config.json'
@'
{
  "enabled": true, "autoSearchDelay": 1000, "everythingPath": "", "esPath": "",
  "startWithWindows": false, "showNotifications": false, "logLevel": "Debug",
  "loggingEnabled": true, "reuseEverythingWindow": true, "language": "auto"
}
'@ | Set-Content $cfg -Encoding UTF8

$first = Start-Process -FilePath $Exe -ArgumentList @('--root', $root) -PassThru
Start-Sleep -Seconds 6
Check 'second instance: primary is running' (-not $first.HasExited) "pid=$($first.Id)"

# the primary may already own hidden top-level windows, so compare the set before/after
$before = @{}
foreach ($w in Get-Windows $first.Id) { $before[$w.Current.NativeWindowHandle] = $true }

# a --root instance that is already running must react to --settings instead of starting a second copy
$second = Start-Process -FilePath $Exe -ArgumentList @('--settings', '--root', $root) -PassThru
$secondExited = $second.WaitForExit(20000)
$settings = $null
$deadline = [datetime]::UtcNow.AddSeconds(20)
while (-not $settings -and [datetime]::UtcNow -lt $deadline) {
    foreach ($w in Get-Windows $first.Id) {
        if (-not $before.ContainsKey($w.Current.NativeWindowHandle)) { $settings = $w; break }
    }
    if (-not $settings) { Start-Sleep -Milliseconds 150 }
}
if ($settings) {
    Check 'settings signal: a new window of the primary opens' $true "title='$($settings.Current.Name)' class='$($settings.Current.ClassName)'"
} else {
    Check 'settings signal: a new window of the primary opens' $false 'no new window of the primary within 20 s'
}
Check 'settings signal: the second process exits 0' ($secondExited -and $second.ExitCode -eq 0) "exited=$secondExited code=$(if ($secondExited) { $second.ExitCode } else { 'still running' })"
Check 'settings signal: the primary is still running' (-not $first.HasExited) 'the signalling process did not replace it'

# close the settings window so the shutdown path is the only thing left running
if ($settings) {
    $windowPattern = $null
    if ($settings.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref] $windowPattern)) { $windowPattern.Close() | Out-Null }
    Start-Sleep -Seconds 2
}

$third = Start-Process -FilePath $Exe -ArgumentList @('--exit', '--root', $root) -PassThru
$thirdExited = $third.WaitForExit(20000)
$firstExited = $first.WaitForExit(20000)
Check 'exit signal: the second process exits 0' ($thirdExited -and $third.ExitCode -eq 0) "exited=$thirdExited code=$(if ($thirdExited) { $third.ExitCode } else { 'still running' })"
Check 'exit signal: the primary shuts down' ($firstExited -and $first.ExitCode -eq 0) "exited=$firstExited code=$(if ($firstExited) { $first.ExitCode } else { 'still running' })"
Check 'exit signal: no instance left' (@(Get-Process ExplorerEverythingSearch -ErrorAction SilentlyContinue).Count -eq 0) "processes=$(@(Get-Process ExplorerEverythingSearch -ErrorAction SilentlyContinue).Count)"

if (Test-Path (Join-Path $root 'logs\app.log')) {
    Write-Output '--- tail of the instance log ---'
    Get-Content (Join-Path $root 'logs\app.log') -Tail 12 | ForEach-Object { "  $_" }
}

Stop-Quietly $first
Get-Process ExplorerEverythingSearch -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 2
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
Write-Output "cleanup: root removed=$(-not (Test-Path $root)) processes=$(@(Get-Process ExplorerEverythingSearch -ErrorAction SilentlyContinue).Count)"

$failed = @($results | Where-Object { -not $_.Ok }).Count
Write-Output ""
Write-Output "checks: $($results.Count), failed: $failed"
exit $(if ($failed -eq 0) { 0 } else { 1 })
