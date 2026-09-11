param([switch]$Software, [Parameter(Mandatory = $true)][string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia-events.ps1')
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
Add-Type -ReferencedAssemblies @([System.Windows.Automation.AutomationElement].Assembly.Location, [System.Windows.Automation.AutomationProperty].Assembly.Location, [System.Windows.Rect].Assembly.Location) @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Windows.Automation;
public static class ShadeStateWindow {
    private delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    public static IntPtr ShowOwned(int process) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, data) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner != process) return true;
            var title = new StringBuilder(128); GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "Shade accessibility state fixture") return true;
            found = window; ShowWindow(window, 4); return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@
$repo = Split-Path -Parent $PSScriptRoot
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Use a fresh evidence directory.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$previousSoftware = $env:CUPRIFACE_SOFTWARE
$process = $null
try {
    if ($Software) { $env:CUPRIFACE_SOFTWARE = '1' } else { $env:CUPRIFACE_SOFTWARE = $null }
    $process = Start-Process -FilePath (Join-Path $repo 'tests/Shade.Tests/bin/Release/net10.0/Shade.Tests.exe') `
        -ArgumentList '--accessibility-state-preview' -WorkingDirectory $repo -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $evidence 'stdout.log') -RedirectStandardError (Join-Path $evidence 'stderr.log')
    # Retain the process handle so PowerShell 5.1 can read ExitCode after shutdown.
    $processHandle = $process.Handle
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $window = $null
    while ($null -eq $window) {
        if ($process.HasExited -or $timer.Elapsed.TotalSeconds -gt 20) { throw 'Fixture did not open.' }
        $nativeWindow = [ShadeStateWindow]::ShowOwned($process.Id)
        if ($nativeWindow -ne [IntPtr]::Zero) {
            $candidate = [System.Windows.Automation.AutomationElement]::FromHandle($nativeWindow)
            if ($candidate.Current.Name -eq 'Shade accessibility state fixture') { $window = $candidate }
        }
        Start-Sleep -Milliseconds 100
    }
    function Find-Control([string]$Name) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
        return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    function Wait-For([scriptblock]$Check, [string]$Failure) {
        $limit = [Diagnostics.Stopwatch]::StartNew()
        while (-not (& $Check)) {
            if ($process.HasExited -or $limit.Elapsed.TotalSeconds -gt 8) { throw ($Failure + ' Events: ' + [ShadeAutomationEvents]::Describe()) }
            Start-Sleep -Milliseconds 100
        }
    }
    Wait-For { $null -ne (Find-Control 'Fixture value') } 'Missing fixture input'
    function Get-ValuePattern { return (Find-Control 'Fixture value').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) }
    function Select-State([string]$Name) {
        (Find-Control $Name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    [ShadeAutomationEvents]::Start((Find-Control 'Fixture value'))
    Start-Sleep -Milliseconds 500
    (Get-ValuePattern).SetValue('first replacement')
    Wait-For { (Get-ValuePattern).Current.Value -ceq 'first replacement' } 'Initial edit failed'
    Wait-For { [ShadeAutomationEvents]::Seen([System.Windows.Automation.ValuePattern]::ValueProperty.Id, 'first replacement') } 'Missing value-change notification'
    foreach ($state in @('Read only', 'Disabled')) {
        Select-State $state
        Wait-For { (Get-ValuePattern).Current.IsReadOnly } 'Restricted state still advertises editable'
        if ($state -eq 'Disabled') {
            Wait-For { -not (Find-Control 'Fixture value').Current.IsEnabled } 'Disabled state still reports enabled'
        }
        elseif (-not (Find-Control 'Fixture value').Current.IsEnabled) { throw 'Read-only field incorrectly reports disabled' }
        $rejected = $false
        try { (Get-ValuePattern).SetValue('must not apply') } catch { $rejected = $true }
        if (-not $rejected) { throw 'Restricted field accepted SetValue.' }
        Select-State 'Editable'
        Wait-For { -not (Get-ValuePattern).Current.IsReadOnly -and (Find-Control 'Fixture value').Current.IsEnabled } 'Editable state did not recover'
        if ((Get-ValuePattern).Current.Value -cne 'first replacement') { throw 'Rejected write changed field content.' }
    }
    (Get-ValuePattern).SetValue('second replacement')
    Wait-For { (Get-ValuePattern).Current.Value -ceq 'second replacement' } 'Editing after state transitions failed'
    foreach ($textValue in @('  padded text  ', '   ', '', ('caf' + [char]0xE9 + [char]0x03A9))) {
        (Get-ValuePattern).SetValue($textValue)
        Wait-For { (Get-ValuePattern).Current.Value -ceq $textValue } 'Text value lost whitespace, Unicode or exposed placeholder'
    }
    foreach ($changed in @($true, $false)) {
        Wait-For { [ShadeAutomationEvents]::Seen([System.Windows.Automation.ValuePattern]::IsReadOnlyProperty.Id, $changed) } 'Missing editability notification'
        Wait-For { [ShadeAutomationEvents]::Seen([System.Windows.Automation.AutomationElement]::IsEnabledProperty.Id, $changed) } 'Missing enabled-state notification'
    }
    [ShadeAutomationEvents]::Stop()
    $secretField = Find-Control 'Fixture password'
    if (-not $secretField.Current.IsPassword) { throw 'Fixture password lacks protected semantics' }
    [ShadeAutomationEvents]::Start($secretField)
    Start-Sleep -Milliseconds 500
    $secretField.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('synthetic-event-password')
    Wait-For { $null -ne (Find-Control 'Secret updated') } 'Password update did not reach model'
    Start-Sleep -Milliseconds 500
    if ([ShadeAutomationEvents]::AnyValueEvent()) { throw 'Protected field emitted a value event' }
    [ShadeAutomationEvents]::Stop()
    $process.Refresh()
    if (-not $process.CloseMainWindow()) { throw 'Could not request normal close' }
    if (-not $process.WaitForExit(10000)) { throw 'Normal close timed out' }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw ('Normal close exit code: ' + $process.ExitCode) }
    if ((Get-Item (Join-Path $evidence 'stderr.log')).Length -ne 0) { throw 'Unexpected host stderr' }
    [pscustomobject]@{ Passed = $true; Software = [bool]$Software; ReadOnly = $true; Disabled = $true; EditingRestored = $true; ExactTextValues = $true; PropertyEvents = $true; ProtectedValueEventsAbsent = $true } |
        ConvertTo-Json | Set-Content (Join-Path $evidence 'result.json') -Encoding UTF8
    Write-Output 'PASS live UIA states, exact text, property notifications and protected-value event exclusion'
}
finally {
    [ShadeAutomationEvents]::Stop()
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:CUPRIFACE_SOFTWARE = $previousSoftware
}
