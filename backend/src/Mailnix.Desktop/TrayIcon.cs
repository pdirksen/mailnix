using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Mailnix.Desktop;

/// <summary>A menu entry; <see cref="Separator"/> draws a line.</summary>
internal sealed record TrayMenuItem(string Text, Action? OnClick, bool Checked = false, bool Enabled = true, bool IsDefault = false)
{
    public static readonly TrayMenuItem Separator = new("", null);
}

/// <summary>
/// The notification-area icon with its menu and balloons, and the message loop of the thread that created it. Other
/// threads hand work to that thread with <see cref="Invoke"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const string ClassName = "mailnix-desktop";
    private const uint IconId = 1;
    private const uint WmTray = WmApp + 1;
    private const uint WmInvoke = WmApp + 2;

    private static TrayIcon? s_instance;

    private readonly nint _module;
    private readonly nint _window;
    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private readonly ConcurrentQueue<Action> _pending = new();
    private string _tooltip;
    private long _lastActivation;
    private bool _disposed;

    /// <param name="menu">Built each time the menu opens, so it shows the current state.</param>
    public TrayIcon(string tooltip, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        if (s_instance is not null)
        {
            throw new InvalidOperationException("There is one tray icon per process.");
        }

        s_instance = this;
        _tooltip = tooltip;
        _menu = menu;
        _module = GetModuleHandleW(null);

        fixed (char* className = ClassName)
        {
            var windowClass = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WindowProc = &WindowProc,
                Instance = _module,
                ClassName = className,
            };
            if (RegisterClassExW(&windowClass) == 0)
            {
                throw new Win32Exception();
            }

            // A hidden top-level window rather than a message-only one: only those get WM_ENDSESSION and TaskbarCreated.
            _window = CreateWindowExW(0, className, className, 0, 0, 0, 0, 0, 0, 0, _module, null);
            if (_window == 0)
            {
                throw new Win32Exception();
            }
        }

        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        nint small;
        _icon = ExtractIconExW(Environment.ProcessPath!, 0, null, &small, 1) > 0 ? small : 0;
        AddIcon();
    }

    /// <summary>Left click or Enter on the icon.</summary>
    public event Action? Activated;

    public event Action? BalloonClicked;

    /// <summary>Windows is shutting down or the user signs out; the process ends soon after this returns.</summary>
    public event Action? SessionEnding;

    /// <summary>The window got WM_CLOSE, e.g. from taskkill without /f.</summary>
    public event Action? CloseRequested;

    /// <summary>An exception in a handler; the loop keeps running.</summary>
    public event Action<Exception>? Error;

    /// <summary>Runs messages until <see cref="Quit"/>.</summary>
    public void Run()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Msg message;
        while (GetMessageW(&message, 0, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    /// <summary>Ends <see cref="Run"/>; from any thread.</summary>
    public void Quit() => Invoke(() => PostQuitMessage(0));

    /// <summary>Runs <paramref name="action"/> on the loop's thread.</summary>
    public void Invoke(Action action)
    {
        _pending.Enqueue(action);
        PostMessageW(_window, WmInvoke, 0, 0);
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        var data = NewData(NifTip | NifShowTip);
        Copy(tooltip, data.Tip, 128);
        Shell_NotifyIconW(NimModify, &data);
    }

    public void ShowBalloon(string title, string text, bool warning)
    {
        var data = NewData(NifInfo);
        Copy(title, data.InfoTitle, 64);
        Copy(text, data.Info, 256);
        data.InfoFlags = warning ? NiifWarning : NiifInfo;
        Shell_NotifyIconW(NimModify, &data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var data = NewData(0);
        Shell_NotifyIconW(NimDelete, &data);
        if (_icon != 0)
        {
            DestroyIcon(_icon);
        }

        DestroyWindow(_window);
        UnregisterClassW(ClassName, _module);
        s_instance = null;
    }

    private void AddIcon()
    {
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        data.CallbackMessage = WmTray;
        data.Icon = _icon;
        Copy(_tooltip, data.Tip, 128);
        Shell_NotifyIconW(NimAdd, &data);
        // Version 4: the callback carries the event in the low word and the anchor point in wParam.
        data.TimeoutOrVersion = NotifyIconVersion4;
        Shell_NotifyIconW(NimSetVersion, &data);
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        Size = (uint)sizeof(NotifyIconData),
        Window = _window,
        Id = IconId,
        Flags = flags,
    };

    private bool Handle(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmTray:
                switch ((uint)(lParam & 0xFFFF))
                {
                    case WmContextMenu:
                        ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                        break;
                    case NinSelect or NinKeySelect:
                        // A double click selects twice.
                        var now = Environment.TickCount64;
                        if (now - _lastActivation > 1000)
                        {
                            _lastActivation = now;
                            Activated?.Invoke();
                        }

                        break;
                    case NinBalloonUserClick:
                        BalloonClicked?.Invoke();
                        break;
                }

                return true;
            case WmInvoke:
                while (_pending.TryDequeue(out var action))
                {
                    action();
                }

                return true;
            case WmEndSession when wParam != 0:
                SessionEnding?.Invoke();
                return true;
            case WmClose:
                CloseRequested?.Invoke();
                return true;
            default:
                if (message == _taskbarCreated)
                {
                    // Explorer restarted and lost all icons.
                    AddIcon();
                    return true;
                }

                return false;
        }
    }

    private void ShowMenu(int x, int y)
    {
        var items = _menu();
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var id = (nuint)(i + 1);
                if (ReferenceEquals(item, TrayMenuItem.Separator))
                {
                    AppendMenuW(menu, MfSeparator, 0, null);
                    continue;
                }

                AppendMenuW(menu, MfString | (item.Checked ? MfChecked : 0) | (item.Enabled ? 0 : MfGrayed), id, item.Text);
                if (item.IsDefault)
                {
                    SetMenuDefaultItem(menu, (uint)id, 0);
                }
            }

            // Without this the menu stays open when the user clicks elsewhere.
            SetForegroundWindow(_window);
            var chosen = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify, x, y, _window, null);
            PostMessageW(_window, WmNull, 0, 0);
            if (chosen > 0 && chosen <= items.Count)
            {
                items[chosen - 1].OnClick?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        var self = s_instance;
        if (self is not null && window == self._window)
        {
            try
            {
                if (self.Handle(message, wParam, lParam))
                {
                    return 0;
                }
            }
            catch (Exception e)
            {
                // Nothing may escape into native code.
                self.Error?.Invoke(e);
                return 0;
            }
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private static void Copy(string text, char* buffer, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(buffer, capacity));
        buffer[length] = '\0';
    }

    private const uint WmApp = 0x8000;
    private const uint WmNull = 0x0000;
    private const uint WmClose = 0x0010;
    private const uint WmEndSession = 0x0016;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NinBalloonUserClick = 0x0405;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;
    private const uint NiifInfo = 0x01;
    private const uint NiifWarning = 0x02;
    private const uint NotifyIconVersion4 = 4;
    private const uint MfString = 0x0000;
    private const uint MfGrayed = 0x0001;
    private const uint MfChecked = 0x0008;
    private const uint MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WndClassEx* windowClass);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClassW(string className, nint instance);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, void* param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(Msg* message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(Msg* message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Msg* message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, void* parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string file, int index, nint* large, nint* small, uint count);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, NotifyIconData* data);
}
