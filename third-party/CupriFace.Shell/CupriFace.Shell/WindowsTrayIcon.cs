using System.Runtime.InteropServices;

namespace CupriFace.Shell;

/// <summary>Native Windows notification-area behavior for close-to-tray applications. It subclasses
/// the app HWND after the accessibility bridge, preserving that procedure in the forwarding chain.</summary>
internal sealed class WindowsTrayIcon : IDisposable
{
    private const int GwlpWndProc = -4;
    private const int GclpHIcon = -14;
    private const int GclpHIconSmall = -34;
    private const uint WmClose = 0x0010;
    private const uint WmGetIcon = 0x007f;
    private const uint WmAppTray = 0x8000 + 77;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint WmRightButtonUp = 0x0205;
    private const nuint IconSmall = 0;
    private const nuint IconBig = 1;
    private const nuint IconSmall2 = 2;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint OpenCommand = 1;
    private const uint CloseCommand = 2;

    private readonly bool _enabled;
    private readonly string _tooltip;
    private readonly string _closeLabel;
    private WndProc? _windowProcedure;
    private nint _window;
    private nint _previousWindowProcedure;
    private uint _taskbarCreatedMessage;
    private bool _iconAdded;
    private bool _exiting;

    public WindowsTrayIcon(bool enabled, string tooltip, string closeLabel)
    {
        _enabled = enabled && OperatingSystem.IsWindows();
        _tooltip = tooltip;
        _closeLabel = closeLabel;
    }

    public void Attach(nint? window)
    {
        if (!_enabled || _window != 0 || window is not { } hwnd || hwnd == 0)
        {
            return;
        }

        _windowProcedure = HandleWindowMessage;
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtrW(
            hwnd,
            GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            _windowProcedure = null;
            return;
        }

        _window = hwnd;
        _previousWindowProcedure = previous;
        try
        {
            _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
            AddIcon();
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            _ = SetWindowLongPtrW(_window, GwlpWndProc, _previousWindowProcedure);
            _window = 0;
            _previousWindowProcedure = 0;
            _windowProcedure = null;
        }
    }

    private nint HandleWindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == WmClose && !_exiting && _iconAdded)
            {
                _ = ShowWindow(window, SwHide);
                return 0;
            }

            if (message == WmAppTray)
            {
                var mouseMessage = unchecked((uint)(long)lParam) & 0xffff;
                if (mouseMessage is WmLeftButtonUp or WmLeftButtonDoubleClick)
                {
                    RestoreWindow();
                    return 0;
                }

                if (mouseMessage == WmRightButtonUp)
                {
                    ShowContextMenu();
                    return 0;
                }
            }

            if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
            {
                _iconAdded = false;
                AddIcon();
                return 0;
            }
        }
        catch
        {
            // Exceptions cannot cross a native window-procedure callback safely. If tray handling
            // ever fails, preserve ordinary CupriFace window behavior through the previous proc.
        }

        return _previousWindowProcedure != 0
            ? CallWindowProcW(_previousWindowProcedure, window, message, wParam, lParam)
            : DefWindowProcW(window, message, wParam, lParam);
    }

    private void RestoreWindow()
    {
        if (_window == 0)
        {
            return;
        }

        _ = ShowWindow(_window, SwShow);
        _ = SetForegroundWindow(_window);
    }

    private void ShowContextMenu()
    {
        if (_window == 0 || !GetCursorPos(out var cursor))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            _ = AppendMenuW(menu, MfString, OpenCommand, $"Open {_tooltip}");
            _ = AppendMenuW(menu, MfSeparator, 0, null);
            _ = AppendMenuW(menu, MfString, CloseCommand, _closeLabel);
            _ = SetForegroundWindow(_window);
            var command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCommand,
                cursor.X,
                cursor.Y,
                0,
                _window,
                0);
            if (command == OpenCommand)
            {
                RestoreWindow();
            }
            else if (command == CloseCommand)
            {
                ExitApplication();
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        RemoveIcon();
        _ = PostMessageW(_window, WmClose, 0, 0);
    }

    private void AddIcon()
    {
        if (_window == 0 || _iconAdded)
        {
            return;
        }

        var icon = SendMessageW(_window, WmGetIcon, IconSmall2, 0);
        if (icon == 0) icon = SendMessageW(_window, WmGetIcon, IconSmall, 0);
        if (icon == 0) icon = SendMessageW(_window, WmGetIcon, IconBig, 0);
        if (icon == 0) icon = GetClassLongPtrW(_window, GclpHIconSmall);
        if (icon == 0) icon = GetClassLongPtrW(_window, GclpHIcon);
        if (icon == 0) icon = LoadIconW(0, (nint)32512);

        var data = CreateIconData(icon);
        _iconAdded = ShellNotifyIconW(NimAdd, ref data);
    }

    private void RemoveIcon()
    {
        if (!_iconAdded || _window == 0)
        {
            return;
        }

        var data = CreateIconData(0);
        _ = ShellNotifyIconW(NimDelete, ref data);
        _iconAdded = false;
    }

    private NotifyIconData CreateIconData(nint icon) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = WmAppTray,
        Icon = icon,
        Tip = _tooltip.Length < 128 ? _tooltip : _tooltip[..127],
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    public void Dispose()
    {
        RemoveIcon();
        if (_window != 0 && _previousWindowProcedure != 0 && IsWindow(_window))
        {
            _ = SetWindowLongPtrW(_window, GwlpWndProc, _previousWindowProcedure);
        }

        _window = 0;
        _previousWindowProcedure = 0;
        _windowProcedure = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point
    {
        public readonly int X;
        public readonly int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint previous, nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetClassLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint item, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint window,
        nint rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIconW(uint message, ref NotifyIconData data);
}
