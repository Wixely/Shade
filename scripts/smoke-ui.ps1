param([switch]$Software, [switch]$Preview, [switch]$Capture, [switch]$Tray, [switch]$Recovery, [switch]$BrokerSetup, [switch]$RecoveryShortcut, [switch]$KeyboardNavigation, [switch]$AccessibleText, [switch]$PropertyEvents, [switch]$InvokeNavigation,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$ExecutablePath, [string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
if ($PropertyEvents) {
    $AccessibleText = [switch]::new($true)
    . (Join-Path $PSScriptRoot 'uia-events.ps1')
    function Wait-PropertyEvent([int]$property, [object]$value) {
        $limit = [Diagnostics.Stopwatch]::StartNew()
        while (-not [ShadeAutomationEvents]::Seen($property, $value)) {
            if ($limit.Elapsed.TotalSeconds -gt 8) { throw ('Missing property event ' + $property + '; received ' + [ShadeAutomationEvents]::Describe()) }
            Start-Sleep -Milliseconds 50
        }
    }
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ShadeSmokeWindow {
    private delegate bool Callback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int maximum, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMenuItemRect(IntPtr window, IntPtr menu, uint item, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    private static Point priorCursor;
    private static bool cursorCaptured;
    public static void RestoreCursor() {
        if (!cursorCaptured) return;
        var priorDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { SetCursorPos(priorCursor.X, priorCursor.Y); cursorCaptured = false; }
        finally { if (priorDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(priorDpi); }
    }
    public static bool SelectExitMenu(uint process) {
        var priorDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
        uint owner; GetWindowThreadProcessId(GetForegroundWindow(), out owner);
        if (owner != process || !IsWindowVisible(TrayMenuWindow)) return false;
        var menu = SendMessage(TrayMenuWindow, 0x01e1, IntPtr.Zero, IntPtr.Zero);
        var count = GetMenuItemCount(menu);
        Rect bounds;
        if (count < 1 || !GetMenuItemRect(IntPtr.Zero, menu, (uint)(count - 1), out bounds)) return false;
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        cursorCaptured = GetCursorPos(out priorCursor);
        if (!SetCursorPos((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2)) return false;
        System.Threading.Thread.Sleep(100); // Let the native menu process hover before selecting.
        GetWindowThreadProcessId(GetForegroundWindow(), out owner);
        if (owner != process || !IsWindowVisible(TrayMenuWindow)) return false;
        mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        return true;
        } finally { if (priorDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(priorDpi); }
    }
    public static IntPtr TrayMenuWindow;
    public static bool ExitTrayMenu(uint process) {
        IntPtr popup = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr state) {
            uint id; GetWindowThreadProcessId(window, out id);
            if (id != process) return true;
            var name = new StringBuilder(64); GetClassName(window, name, name.Capacity);
            if (name.ToString() == "#32768") { popup = window; return false; }
            return true;
        }, IntPtr.Zero);
        if (popup == IntPtr.Zero) return false;
        var menu = SendMessage(popup, 0x01e1, IntPtr.Zero, IntPtr.Zero);
        var count = GetMenuItemCount(menu);
        if (count < 1) return false;
        var label = new StringBuilder(128); GetMenuString(menu, (uint)(count - 1), label, label.Capacity, 0x0400);
        if (label.ToString() != "Exit Shade") return false;
        TrayMenuWindow = popup;
        return true;
    }
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    public static void TrayActivate(IntPtr window, bool menu) {
        PostMessage(window, 0x8000 + 77, new IntPtr(1), new IntPtr(menu ? 0x0205 : 0x0202));
    }
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    public static void OpenAutomation(IntPtr window) {
        PostMessage(window, 0x0100, new IntPtr(13), new IntPtr(0x001c0001));
        PostMessage(window, 0x0101, new IntPtr(13), new IntPtr(unchecked((int)0xc01c0001)));
    }
    public static void Tab(IntPtr window, bool reverse) {
        if (reverse) PostMessage(window, 0x0100, new IntPtr(0x10), new IntPtr(0x002a0001));
        PostMessage(window, 0x0100, new IntPtr(9), new IntPtr(0x000f0001));
        PostMessage(window, 0x0101, new IntPtr(9), new IntPtr(unchecked((int)0xc00f0001)));
        if (reverse) PostMessage(window, 0x0101, new IntPtr(0x10), new IntPtr(unchecked((int)0xc02a0001)));
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    public static bool WithinContent(IntPtr window, double top, double bottom) {
        var prior = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            Rect rect; if (!GetClientRect(window, out rect)) return false;
            var origin = new Point(); if (!ClientToScreen(window, ref origin)) return false;
            var scale = GetDpiForWindow(window) / 96.0;
            return top >= origin.Y + 296 * scale - 1 && bottom <= origin.Y + rect.Bottom - 28 * scale + 1;
        } finally { if (prior != IntPtr.Zero) SetThreadDpiAwarenessContext(prior); }
    }
    public static void Scroll(IntPtr window, int delta) {
        Rect bounds; GetClientRect(window, out bounds);
        var point = new Point { X = Math.Min(120, bounds.Right / 3), Y = bounds.Bottom * 3 / 4 };
        PostMessage(window, 0x0200, IntPtr.Zero, new IntPtr((point.Y << 16) | point.X));
        ClientToScreen(window, ref point);
        PostMessage(window, 0x020A, new IntPtr(unchecked(delta << 16)), new IntPtr((point.Y << 16) | (point.X & 65535)));
    }
    public static void ClickBackground(IntPtr window) {
        // SDL tracks pointer position from mouse movement, rather than the button message coordinates.
        PostMessage(window, 0x0200, IntPtr.Zero, new IntPtr((20 << 16) | 20));
        PostMessage(window, 0x0201, new IntPtr(1), new IntPtr((20 << 16) | 20));
        PostMessage(window, 0x0202, IntPtr.Zero, new IntPtr((20 << 16) | 20));
    }
    public static void TypeText(IntPtr window, string text) {
        foreach (var character in text) PostMessage(window, 0x0102, new IntPtr(character), IntPtr.Zero);
    }
    public static void ClearText(IntPtr window, int length) {
        PostMessage(window, 0x0100, new IntPtr(0x23), new IntPtr(0x014f0001));
        PostMessage(window, 0x0101, new IntPtr(0x23), new IntPtr(unchecked((int)0xc14f0001)));
        for (var i = 0; i < length; i++) {
            PostMessage(window, 0x0100, new IntPtr(8), new IntPtr(0x000e0001));
            PostMessage(window, 0x0101, new IntPtr(8), new IntPtr(unchecked((int)0xc00e0001)));
        }
    }
    public static IntPtr Find(int process) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr state) {
            uint id; GetWindowThreadProcessId(window, out id);
            if (id != process) return true;
            var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
            if (title.ToString().StartsWith("Shade - feasibility")) { found = window; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
$repo = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $repo "src/Shade/bin/$Configuration/net10.0/Shade.exe"
$arguments = '--ephemeral --no-tray'
if ($RecoveryShortcut -and -not $Preview) { throw 'RecoveryShortcut exercises the preview backend; combine it with Preview.' }
if ($Tray) { $arguments = '--ephemeral' }
if ($Preview) {
    $executable = Join-Path $repo "tests/Shade.Tests/bin/$Configuration/net10.0/Shade.Tests.exe"
    $arguments = '--preview --real-layout'
    if ($RecoveryShortcut) { $arguments += ' --recovery-preview' }
    if ($Tray) { $arguments += ' --tray' }
}
if ($ExecutablePath) {
    if ($Preview) { throw 'ExecutablePath tests the actual application; do not combine it with Preview.' }
    $executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
}
$artifacts = if ($EvidenceDirectory) { [System.IO.Path]::GetFullPath($EvidenceDirectory) } else { Join-Path $repo 'artifacts' }
New-Item -ItemType Directory -Force $artifacts | Out-Null
if ($Recovery) {
    if ($Preview -or $Tray) { throw 'Recovery requires the actual application with normal close-to-exit.' }
    $recoveryDirectory = Join-Path $artifacts ('recovery-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $recoveryDirectory | Out-Null
    $settingsFile = Join-Path $recoveryDirectory 'settings.json'
    $automationFile = Join-Path $recoveryDirectory 'automation.json'
    [System.IO.File]::WriteAllText($settingsFile, 'synthetic unreadable screen settings')
    [System.IO.File]::WriteAllText($automationFile, 'synthetic unreadable integration settings')
    $settingsHash = (Get-FileHash -LiteralPath $settingsFile -Algorithm SHA256).Hash
    $automationHash = (Get-FileHash -LiteralPath $automationFile -Algorithm SHA256).Hash
    $arguments = '--no-tray --settings-directory "' + $recoveryDirectory + '"'
}
$previousSoftware = $env:CUPRIFACE_SOFTWARE
$previousCapture = $env:CUPRIFACE_FRAME_DUMP
if ($Capture) {
    $Software = [switch]::new($true)
    $env:CUPRIFACE_FRAME_DUMP = Join-Path $artifacts 'capture-current.png'
}
if ($Software) { $env:CUPRIFACE_SOFTWARE = '1' } else { $env:CUPRIFACE_SOFTWARE = $null }
$process = $null
$brokerProcess = $null
try {
    if ($BrokerSetup) {
        if ($Recovery -or $Preview -or $Tray) { throw 'BrokerSetup requires an isolated actual-app run without Recovery, Preview or Tray.' }
        $brokerDirectory = Join-Path $artifacts ('broker-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $brokerDirectory | Out-Null
        $brokerExecutable = Join-Path $repo 'tests/Shade.Tests/bin/Release/net10.0/Shade.Tests.exe'
        $brokerProcess = Start-Process -FilePath $brokerExecutable -ArgumentList ('--ui-broker "' + $brokerDirectory + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $brokerDirectory 'stdout.log') -RedirectStandardError (Join-Path $brokerDirectory 'stderr.log')
        $brokerProcess.Handle | Out-Null
        $brokerDeadline = [DateTime]::UtcNow.AddSeconds(10)
        $brokerSnapshot = Join-Path $brokerDirectory 'broker.json'
        while (-not (Test-Path -LiteralPath $brokerSnapshot)) {
            if ($brokerProcess.HasExited -or [DateTime]::UtcNow -gt $brokerDeadline) { throw 'Isolated UI broker did not start.' }
            Start-Sleep -Milliseconds 100
        }
        $brokerPort = (Get-Content -LiteralPath $brokerSnapshot -Raw | ConvertFrom-Json).Port
        $brokerSettings = Join-Path $brokerDirectory 'settings'
        $arguments = '--no-tray --settings-directory "' + $brokerSettings + '"'
    }
    # Ephemeral mode exercises the real catalog and overlays without loading/writing personal settings.
    $startupClock = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $artifacts 'ui-stdout.log') -RedirectStandardError (Join-Path $artifacts 'ui-stderr.log')
    $processHandle = $process.Handle
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $window = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw 'Shade exited before its UI became available.' }
        $nativeWindow = [ShadeSmokeWindow]::Find($process.Id)
        if ($nativeWindow -ne [IntPtr]::Zero) {
            [ShadeSmokeWindow]::ShowWindow($nativeWindow, 4) | Out-Null
            $window = [System.Windows.Automation.AutomationElement]::FromHandle($nativeWindow)
        }
        if ($window -and $window.Current.Name -like 'Shade - feasibility*') { break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $window) { throw 'Shade window did not appear.' }
    $sliderCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)
    $sliders = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $sliders = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCondition)
        if ($sliders.Count -gt 0) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($sliders.Count -eq 0) { throw 'No sliders exposed through Windows UI Automation.' }
    $startupClock.Stop()
    @{ StartupToAccessibleSliderMilliseconds = $startupClock.ElapsedMilliseconds; Software = [bool]$Software } | ConvertTo-Json | Set-Content (Join-Path $artifacts 'startup.json')
    if ($sliders.Count -ne 1) { throw 'Advanced sliders were not hidden initially.' }
    if ($PropertyEvents) { [ShadeAutomationEvents]::Start($sliders[0]); Start-Sleep -Milliseconds 500 }
    $sliders[0].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(12)
    if ($PropertyEvents) {
        Wait-PropertyEvent ([System.Windows.Automation.RangeValuePattern]::ValueProperty.Id) ([double]12)
        [ShadeAutomationEvents]::Stop()
        Write-Output 'PASS global dimming value-change notification'
    }
    Start-Sleep -Milliseconds 300
    $screenButtons = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like 'Screen *dimming off*' })
    if ($screenButtons.Count -eq 0) { throw 'Global slider unexpectedly enabled screens.' }
    if ($PropertyEvents) { [ShadeAutomationEvents]::Start($screenButtons[0]); Start-Sleep -Milliseconds 500 }
    $screenButtons[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 200
    if ($PropertyEvents) {
        $limit = [Diagnostics.Stopwatch]::StartNew()
        while ($screenButtons[0].Current.Name -like '*dimming off*') {
            if ($limit.Elapsed.TotalSeconds -gt 5) { throw 'Screen toggle did not update its label.' }
            Start-Sleep -Milliseconds 50
        }
        Wait-PropertyEvent ([System.Windows.Automation.AutomationElement]::NameProperty.Id) $screenButtons[0].Current.Name
        [ShadeAutomationEvents]::Stop()
        Write-Output 'PASS screen enable-state label notification'
    }
    function Save-CupriFrame([string]$name) {
        if (-not $Capture) { return }
        # CupriFace's debug readback captures every 15th actual presentation. Generate changes
        # through the real global slider, never enable a recurring application redraw timer.
        $started = [DateTime]::UtcNow
        for ($frame = 0; $frame -lt 32; $frame++) {
            $currentSliders = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCondition)
            $currentSliders[0].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(3 + ($frame % 2))
            [ShadeSmokeWindow]::ClickBackground($nativeWindow)
            Start-Sleep -Milliseconds 120
        }
        $captureFile = Get-Item -LiteralPath $env:CUPRIFACE_FRAME_DUMP -ErrorAction Stop
        if ($captureFile.LastWriteTimeUtc -lt $started) { throw 'CupriFace did not produce a fresh frame capture.' }
        Copy-Item -LiteralPath $captureFile.FullName -Destination (Join-Path $artifacts $name)
    }
    function Invoke-SettingsRecovery([string]$label, [string]$file, [string]$originalHash, [int]$version) {
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $originalHash) { throw 'Unreadable settings were overwritten before recovery.' }
        [ShadeSmokeWindow]::Scroll($nativeWindow, -12000)
        Start-Sleep -Milliseconds 300
        $recoveryCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $label)
        $action = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $recoveryCondition)
        if (-not $action) { throw ('Recovery action was not exposed: ' + $label) }
        Save-CupriFrame ('recovery-' + [System.IO.Path]::GetFileNameWithoutExtension($file) + '.png')
        $action.SetFocus()
        Start-Sleep -Milliseconds 200
        [ShadeSmokeWindow]::OpenAutomation($nativeWindow) # Enter activates the focused recovery button.
        $recoveryDeadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 100
            $backups = @(Get-ChildItem -LiteralPath $recoveryDirectory -Filter ([System.IO.Path]::GetFileName($file) + '.backup-*'))
        } while ($backups.Count -eq 0 -and [DateTime]::UtcNow -lt $recoveryDeadline)
        if ($backups.Count -ne 1 -or (Get-FileHash -LiteralPath $backups[0].FullName -Algorithm SHA256).Hash -ne $originalHash) {
            throw 'Recovery did not preserve the exact unreadable file.'
        }
        $replacement = $null
        do {
            try { $replacement = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } catch { $replacement = $null }
            if ($replacement -and $replacement.Version -eq $version) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $recoveryDeadline)
        if ($replacement.Version -ne $version) { throw 'Recovery saved an invalid settings version.' }
        if ($version -eq 3) {
            $globalSlider = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCondition)[0]
            if ($replacement.GlobalLevel -ne $globalSlider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value) { throw 'Recovery did not save the current global value.' }
            if (@($replacement.Controls.PSObject.Properties | Where-Object { $_.Value.Enabled }).Count -ne 0) { throw 'Recovery re-enabled a restored screen.' }
        }
        if ($version -eq 1 -and ($replacement.Enabled -or $replacement.ProtectedPassword)) { throw 'Integration recovery enabled automation or saved a credential.' }
        Write-Output ('PASS published UI recovery: ' + [System.IO.Path]::GetFileName($file) + '; exact backup and valid replacement')
        [ShadeSmokeWindow]::Scroll($nativeWindow, 12000)
        Start-Sleep -Milliseconds 300
    }
    Save-CupriFrame 'controls-main.png'
    if ($RecoveryShortcut) {
        [ShadeSmokeWindow]::Scroll($nativeWindow, -12000)
        Start-Sleep -Milliseconds 300
        Save-CupriFrame 'controls-recovery-shortcut.png'
        $shortcutCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Set up recovery shortcut')
        $shortcut = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $shortcutCondition)
        if (-not $shortcut) { throw 'Recovery shortcut setup action was not exposed.' }
        $shortcut.SetFocus()
        Start-Sleep -Milliseconds 200
        [ShadeSmokeWindow]::OpenAutomation($nativeWindow)
        Start-Sleep -Milliseconds 300
        if ($window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $shortcutCondition)) { throw 'Recovery setup action did not hide after preview registration.' }
        [ShadeSmokeWindow]::Scroll($nativeWindow, 12000)
        Start-Sleep -Milliseconds 300
        Write-Output 'PASS live recovery setup action (preview backend)'
    }
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $advanced = $buttons | Where-Object { $_.Current.Name -like 'Advanced*' } | Select-Object -First 1
    if (-not $advanced) { throw 'Advanced disclosure was not exposed.' }
    $advanced.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    $sliders = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCondition)
    if ($sliders.Count -lt 2) { throw 'Advanced did not expose individual sliders.' }
    if ($PropertyEvents) {
        $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $membership = $all | Where-Object { $_.Current.Name -like 'Use global for screen *' } | Select-Object -First 1
        if (-not $membership) { throw 'Global membership switch missing.' }
        $toggle = $membership.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        [ShadeAutomationEvents]::Start($membership)
        Start-Sleep -Milliseconds 500
        $originalState = $toggle.Current.ToggleState
        $expectedState = if ($originalState -eq [System.Windows.Automation.ToggleState]::On) { [System.Windows.Automation.ToggleState]::Off } else { [System.Windows.Automation.ToggleState]::On }
        $toggle.Toggle()
        Wait-PropertyEvent ([System.Windows.Automation.TogglePattern]::ToggleStateProperty.Id) ([int]$expectedState)
        $toggle.Toggle()
        Wait-PropertyEvent ([System.Windows.Automation.TogglePattern]::ToggleStateProperty.Id) ([int]$originalState)
        [ShadeAutomationEvents]::Stop()
        Write-Output 'PASS global-membership switch notifications in both directions'
    }
    for ($i = 2; $i -lt $sliders.Count; $i++) {
        if ($sliders[$i].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 0) { throw 'Global enabled a disabled screen.' }
    }
    if ($Capture) {
        [ShadeSmokeWindow]::Scroll($nativeWindow, -720)
        Start-Sleep -Milliseconds 300
        Save-CupriFrame 'controls-advanced.png'
        [ShadeSmokeWindow]::Scroll($nativeWindow, 2400)
        Start-Sleep -Milliseconds 300
    }
    for ($i = 0; $i -lt $sliders.Count; $i++) {
        $sliders = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCondition)
        $range = $sliders[$i].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        $range.SetValue(3 + ($i % 4))
        $updateDeadline = [DateTime]::UtcNow.AddSeconds(3)
        do {
            Start-Sleep -Milliseconds 100
            $sliders = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCondition)
            $updated = $sliders[$i].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
            if ($updated -ne (3 + ($i % 4))) {
                $sliders[$i].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(3 + ($i % 4))
            }
        } while ($updated -ne (3 + ($i % 4)) -and [DateTime]::UtcNow -lt $updateDeadline)
    }
    $sliders = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCondition)
    for ($i = 0; $i -lt $sliders.Count; $i++) {
        $range = $sliders[$i].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        if ($range.Current.Value -ne (3 + ($i % 4))) { throw ("Independent slider {0} write did not update reported value: {1}." -f $i, $range.Current.Value) }
    }
    $restoreCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Restore all displays')
    $restore = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $restoreCondition)
    $restore.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    $sliders = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCondition)
    if ($sliders[0].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 3) { throw 'Restore changed independent global value.' }
    for ($i = 1; $i -lt $sliders.Count; $i++) {
        if ($sliders[$i].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 0) { throw 'Restore left nonzero screen state.' }
    }
    $process.Refresh()
    $cpuBefore = $process.TotalProcessorTime
    Start-Sleep -Seconds 5
    $process.Refresh()
    $cpuMs = ($process.TotalProcessorTime - $cpuBefore).TotalMilliseconds
    Write-Output ("PASS live UI Automation: {0} sliders; set/restore; idle process CPU {1:F1} ms / 5000 ms; software={2}" -f $sliders.Count, $cpuMs, $Software.IsPresent)
    if ($Recovery) {
        Invoke-SettingsRecovery 'Back up unreadable file and save current settings' $settingsFile $settingsHash 3
    }
    $allControls = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $automationButton = $allControls | Where-Object { $_.Current.Name -eq 'Home Assistant' } | Select-Object -First 1
    if ($InvokeNavigation) {
        function Find-NavigationButton([string]$name) {
            $items = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            return $items | Where-Object { $_.Current.Name -eq $name -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1
        }
        for ($cycle = 0; $cycle -lt 10; $cycle++) {
            foreach ($label in @('Home Assistant', 'Back to screens')) {
                $button = Find-NavigationButton $label
                if (-not $button) { throw 'Navigation button missing before Invoke.' }
                $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                $expected = if ($label -eq 'Home Assistant') { 'Back to screens' } else { 'Home Assistant' }
                $limit = [Diagnostics.Stopwatch]::StartNew()
                while (-not (Find-NavigationButton $expected)) {
                    if ($limit.Elapsed.TotalSeconds -gt 5) { throw ('Navigation Invoke failed in cycle ' + $cycle) }
                    Start-Sleep -Milliseconds 50
                }
                $visibleItems = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
                $brokerInput = $visibleItems | Where-Object { $_.Current.Name -eq 'Broker hostname' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit } | Select-Object -First 1
                if (($label -eq 'Home Assistant') -ne [bool]$brokerInput) { throw 'Navigation label changed without switching the accessible form.' }
            }
        }
        $sliders = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCondition)
        if ($sliders[0].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 3) { throw 'Navigation changed global value.' }
        for ($index = 1; $index -lt $sliders.Count; $index++) {
            if ($sliders[$index].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 0) { throw 'Navigation enabled a disabled screen.' }
        }
        Write-Output 'PASS 20 direct navigation Invoke actions without keyboard fallback'
        $automationButton = Find-NavigationButton 'Home Assistant'
    }
    $automationButton.SetFocus()
    Start-Sleep -Milliseconds 300
    [ShadeSmokeWindow]::OpenAutomation($nativeWindow)
    Start-Sleep -Milliseconds 300
    Save-CupriFrame 'controls-automation-top.png'
    [ShadeSmokeWindow]::Scroll($nativeWindow, -12000)
    Start-Sleep -Milliseconds 400
    $allControls = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $hostField = $allControls | Where-Object { $_.Current.Name -eq 'Broker hostname' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit } | Select-Object -First 1
    $formDeadline = [DateTime]::UtcNow.AddSeconds(3)
    while (-not $hostField -and [DateTime]::UtcNow -lt $formDeadline) {
        Start-Sleep -Milliseconds 100
        $allControls = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $hostField = $allControls | Where-Object { $_.Current.Name -eq 'Broker hostname' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit } | Select-Object -First 1
    }
    if ($Capture) {
        [ShadeSmokeWindow]::Scroll($nativeWindow, -12000)
        Start-Sleep -Milliseconds 300
        Save-CupriFrame 'controls-automation.png'
    }
    if (-not $hostField) {
        $allControls | ForEach-Object { '{0}: {1}' -f $_.Current.ControlType.ProgrammaticName, $_.Current.Name } | Set-Content (Join-Path $artifacts 'automation-accessibility.txt')
        throw 'Broker form input not exposed.'
    }
    # Retain native keyboard coverage independently of optional ValuePattern editing.
    $hostField.SetFocus()
    Start-Sleep -Milliseconds 300
    [ShadeSmokeWindow]::TypeText($nativeWindow, 'localhost')
    Start-Sleep -Milliseconds 400
    $allControls = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $hostField = $allControls | Where-Object { $_.Current.Name -eq 'Broker hostname' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit } | Select-Object -First 1
    if ($hostField.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'localhost') { throw 'Broker keyboard input did not update.' }
    $passwordField = $allControls | Where-Object { $_.Current.Name -eq 'Broker password' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit } | Select-Object -First 1
    if (-not $passwordField) { throw 'Password input is missing its accessible name.' }
    if (-not $passwordField.Current.IsPassword) { throw 'Password field does not advertise UIA IsPassword.' }
    if ($hostField.Current.IsPassword) { throw 'Ordinary hostname field incorrectly advertises IsPassword.' }
    if ($hostField.GetCurrentPropertyValue([System.Windows.Automation.ValuePattern]::ValueProperty, $true) -ne 'localhost') { throw 'Ordinary text value is no longer readable.' }
    $passwordField.SetFocus()
    Start-Sleep -Milliseconds 300
    if (-not [ShadeSmokeWindow]::WithinContent($nativeWindow, $passwordField.Current.BoundingRectangle.Top, $passwordField.Current.BoundingRectangle.Bottom)) { throw 'UIA SetFocus left password field clipped.' }
    $initialPassword = 'synthetic-password'
    if ($AccessibleText) { $initialPassword = 'incorrect-initial-password' }
    [ShadeSmokeWindow]::TypeText($nativeWindow, $initialPassword)
    # The built-in password control briefly peeks at the last typed character (1.4 seconds).
    Start-Sleep -Milliseconds 2000
    $allControls = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $passwordField = $allControls | Where-Object { $_.Current.Name -eq 'Broker password' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit } | Select-Object -First 1
    if (-not $passwordField.Current.IsPassword) { throw 'Typing lost the UIA password flag.' }
    $passwordPattern = $passwordField.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    if ($AccessibleText) {
        $hostPattern = $hostField.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($hostPattern.Current.IsReadOnly -or $passwordPattern.Current.IsReadOnly) { throw 'Editable inputs advertise read-only.' }
        if ($PropertyEvents) {
            [ShadeAutomationEvents]::Start($hostField)
            Start-Sleep -Milliseconds 500
        }
        foreach ($value in @('replacement.example', '', 'localhost')) {
            $hostPattern.SetValue($value)
            $editDeadline = [Diagnostics.Stopwatch]::StartNew()
            while ($hostPattern.Current.Value -cne $value) {
                if ($editDeadline.Elapsed.TotalSeconds -gt 5) { throw ('UIA text replacement did not reach the field (expected length ' + $value.Length + ').') }
                Start-Sleep -Milliseconds 50
            }
            if ($PropertyEvents) {
                while (-not [ShadeAutomationEvents]::Seen([System.Windows.Automation.ValuePattern]::ValueProperty.Id, $value)) {
                    if ($editDeadline.Elapsed.TotalSeconds -gt 8) { throw 'Missing broker field value-change notification.' }
                    Start-Sleep -Milliseconds 50
                }
            }
        }
        if ($PropertyEvents) {
            [ShadeAutomationEvents]::Stop()
            [ShadeAutomationEvents]::Start($passwordField)
            Start-Sleep -Milliseconds 500
        }
        # A later authenticated broker test proves this replaces the wrong entry.
        $passwordPattern.SetValue('synthetic-password')
        Start-Sleep -Milliseconds 300
        if ($PropertyEvents) {
            Start-Sleep -Milliseconds 500
            if ([ShadeAutomationEvents]::AnyValueEvent()) { throw 'Broker password emitted a protected value event.' }
            [ShadeAutomationEvents]::Stop()
            Write-Output 'PASS broker value notifications and password value-event exclusion'
        }
        Write-Output 'PASS UIA text replacement, empty value and editable password'
    }
    $valueRejected = $false
    try {
        $rawPasswordValue = $passwordField.GetCurrentPropertyValue([System.Windows.Automation.ValuePattern]::ValueProperty, $true)
        $valueRejected = [object]::ReferenceEquals($rawPasswordValue, [System.Windows.Automation.AutomationElement]::NotSupported)
    }
    catch { $valueRejected = $true }
    if (-not $valueRejected) { throw 'Password ValuePattern read was not rejected.' }
    Write-Output 'PASS Home Assistant form accessibility'
    if ($KeyboardNavigation) {
        $hostField.SetFocus()
        Start-Sleep -Milliseconds 300
        $order = @('Broker hostname', 'Broker port', 'Use TLS', 'Broker username', 'Broker password', 'Forget saved password', 'Test connection', 'Save and enable')
        foreach ($reverse in @($false, $true)) {
            $indices = if ($reverse) { 6..0 } else { 1..7 }
            foreach ($index in $indices) {
                [ShadeSmokeWindow]::Tab($nativeWindow, $reverse)
                $focusDeadline = [DateTime]::UtcNow.AddSeconds(5)
                $target = $null
                do {
                    Start-Sleep -Milliseconds 80
                    $allControls = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
                    $target = $allControls | Where-Object { $_.Current.Name -eq $order[$index] -and $_.Current.HasKeyboardFocus } | Select-Object -First 1
                } while (-not $target -and [DateTime]::UtcNow -lt $focusDeadline)
                if (-not $target) { throw ('Native Tab focus did not reach ' + $order[$index]) }
                $bounds = $target.Current.BoundingRectangle
                if (-not [ShadeSmokeWindow]::WithinContent($nativeWindow, $bounds.Top, $bounds.Bottom)) { throw ('Keyboard target remains clipped: ' + $order[$index]) }
            }
            if ($Capture -and -not $reverse) {
                # Alternate focus between the two adjacent actions to generate Cupri
                # presentations without clicking, changing settings or enabling integration.
                $captureStarted = [DateTime]::UtcNow
                for ($frame = 0; $frame -lt 32; $frame++) {
                    [ShadeSmokeWindow]::Tab($nativeWindow, ($frame % 2 -eq 0))
                    Start-Sleep -Milliseconds 150
                }
                $captureFile = Get-Item -LiteralPath $env:CUPRIFACE_FRAME_DUMP
                if ($captureFile.LastWriteTimeUtc -lt $captureStarted) { throw 'No fresh keyboard focus capture.' }
                Copy-Item -LiteralPath $captureFile.FullName -Destination (Join-Path $artifacts 'controls-keyboard-focus.png')
            }
        }
        Write-Output 'PASS Windows native Tab/Shift+Tab reveal broker fields and actions'
    }
    if ($BrokerSetup) {
        function Find-NamedControl([string]$name) {
            $named = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
            $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $named)
            if (-not $control) { throw ('Missing UI control: ' + $name) }
            return $control
        }
        function Invoke-NamedControl([string]$name) {
            (Find-NamedControl $name).SetFocus()
            Start-Sleep -Milliseconds 200
            [ShadeSmokeWindow]::OpenAutomation($nativeWindow)
            Start-Sleep -Milliseconds 300
        }
        function Wait-Broker([scriptblock]$condition, [string]$failure) {
            $until = [DateTime]::UtcNow.AddSeconds(12)
            do {
                $snapshot = Get-Content -LiteralPath $brokerSnapshot -Raw | ConvertFrom-Json
                if (& $condition $snapshot) { return $snapshot }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $until)
            throw $failure
        }
        $portField = Find-NamedControl 'Broker port'
        $portLength = $portField.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value.Length
        $portField.SetFocus()
        Start-Sleep -Milliseconds 200
        [ShadeSmokeWindow]::ClearText($nativeWindow, $portLength)
        Start-Sleep -Milliseconds 200
        [ShadeSmokeWindow]::TypeText($nativeWindow, [string]$brokerPort)
        Start-Sleep -Milliseconds 300
        if ((Find-NamedControl 'Broker port').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne [string]$brokerPort) { throw 'Broker port keyboard editing failed.' }
        (Find-NamedControl 'Broker username').SetFocus()
        Start-Sleep -Milliseconds 200
        [ShadeSmokeWindow]::TypeText($nativeWindow, 'synthetic-user')
        Start-Sleep -Milliseconds 300
        if ((Find-NamedControl 'Broker username').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'synthetic-user') { throw 'Broker username keyboard editing failed.' }
        $tlsControl = Find-NamedControl 'Use TLS'
        $tlsControl.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 300
        Invoke-NamedControl 'Test connection'
        $tested = Wait-Broker { param($s) $s.TestConnections -gt 0 } 'UI connection test did not authenticate'
        if ($tested.ActiveConfigurations -ne 0 -or $tested.Connections -ne 0) { throw 'Connection test published discovery or enabled integration.' }
        $testedPasswordField = Find-NamedControl 'Broker password'
        if (-not $testedPasswordField.Current.IsPassword) { throw 'Connection test lost the password protection flag.' }
        $testedReadDenied = $false
        try {
            $testedValue = $testedPasswordField.GetCurrentPropertyValue([System.Windows.Automation.ValuePattern]::ValueProperty, $true)
            $testedReadDenied = [object]::ReferenceEquals($testedValue, [System.Windows.Automation.AutomationElement]::NotSupported)
        } catch { $testedReadDenied = $true }
        if (-not $testedReadDenied) { throw 'Connection test exposed a protected value.' }
        # Successful authentication during Save and enable proves the unsaved password
        # survived Test connection without requiring access to its protected UIA value.
        Invoke-NamedControl 'Save and enable'
        $active = Wait-Broker { param($s) $s.Connections -gt 0 -and $s.ExpectedConfigurations -gt 1 -and $s.ActiveConfigurations -eq $s.ExpectedConfigurations } 'UI save/enable did not publish complete discovery'
        $savedFile = Join-Path $brokerSettings 'automation.json'
        $saved = Get-Content -LiteralPath $savedFile -Raw | ConvertFrom-Json
        if (-not $saved.Enabled -or $saved.Tls -or $saved.Port -ne $brokerPort -or $saved.Username -ne 'synthetic-user' -or -not $saved.ProtectedPassword) { throw 'UI broker settings were not saved correctly.' }
        if ((Get-Content -LiteralPath $savedFile -Raw).Contains('synthetic-password')) { throw 'UI saved a plaintext password.' }
        Save-CupriFrame 'broker-connected.png'
        Invoke-NamedControl 'Remove discovery'
        Wait-Broker { param($s) $s.ActiveConfigurations -eq 0 -and $s.RemovedConfigurations -eq $s.ExpectedConfigurations } 'UI discovery removal did not clear all retained configuration' | Out-Null
        Invoke-NamedControl 'Forget saved password'
        $saved = Get-Content -LiteralPath $savedFile -Raw | ConvertFrom-Json
        if ($saved.Enabled -or $saved.ProtectedPassword) { throw 'UI forget action retained enabled integration or credential.' }
        Write-Output 'PASS published UI broker setup: authenticated test, encrypted save/enable, discovery removal and password forgetting'
    }
    if ($Recovery) {
        Invoke-SettingsRecovery 'Back up unreadable settings and reset integration' $automationFile $automationHash 1
    }
    if (-not $process.CloseMainWindow()) { throw 'Could not request normal application close.' }
    if ($Tray) {
        Start-Sleep -Milliseconds 500
        if ($process.HasExited -or [ShadeSmokeWindow]::IsWindowVisible($nativeWindow)) { throw 'Close did not hide the window to the tray.' }
        [ShadeSmokeWindow]::TrayActivate($nativeWindow, $false)
        Start-Sleep -Milliseconds 500
        if (-not [ShadeSmokeWindow]::IsWindowVisible($nativeWindow)) { throw 'Tray activation did not restore the window.' }
        [ShadeSmokeWindow]::TrayActivate($nativeWindow, $true)
        $exitSelected = $false
        $menuDeadline = [DateTime]::UtcNow.AddSeconds(5)
        while ([DateTime]::UtcNow -lt $menuDeadline -and -not $exitSelected) {
            $exitSelected = [ShadeSmokeWindow]::ExitTrayMenu($process.Id)
            if (-not $exitSelected) { Start-Sleep -Milliseconds 100 }
        }
        if (-not $exitSelected) { throw 'Exit Shade was not found in the owned native tray menu.' }
        # Native menu labels/geometry are read from the owned popup. Its provider does not expose
        # menu items through UIA here, so click the verified final item only while our app owns focus.
        if (-not [ShadeSmokeWindow]::SelectExitMenu($process.Id)) { throw 'Could not safely select the owned tray exit item.' }
    }
    if (-not $process.WaitForExit(10000)) {
        throw ('Application did not close normally (control visible={0}; menu visible={1}).' -f [ShadeSmokeWindow]::IsWindowVisible($nativeWindow), [ShadeSmokeWindow]::IsWindowVisible([ShadeSmokeWindow]::TrayMenuWindow))
    }
    if ($Tray) { [ShadeSmokeWindow]::RestoreCursor() }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw ("Application exited with an error: {0}." -f $process.ExitCode) }
    if ($Tray) { Write-Output 'PASS close to tray, restore and native tray Exit Shade action' }
    Write-Output 'PASS normal host exit'
}
finally {
    if ($PropertyEvents) { [ShadeAutomationEvents]::Stop() }
    if ($Tray) { [ShadeSmokeWindow]::RestoreCursor() }
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id }
    if ($brokerProcess) {
        [System.IO.File]::WriteAllText((Join-Path $brokerDirectory 'stop'), '')
        if (-not $brokerProcess.WaitForExit(5000)) { Stop-Process -Id $brokerProcess.Id }
    }
    $env:CUPRIFACE_SOFTWARE = $previousSoftware
    $env:CUPRIFACE_FRAME_DUMP = $previousCapture
}
