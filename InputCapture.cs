using System.Runtime.InteropServices;

namespace OverlayKit;

internal sealed class InputCapture : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmQuit = 0x0012;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;

    private readonly Action<object> _publish;
    private readonly Thread _messageThread;
    private readonly ManualResetEventSlim _started = new();
    private readonly HashSet<uint> _pressedKeys = new();
    private readonly HashSet<uint> _shownKeys = new();
    private HookProcedure? _keyboardProcedure;
    private HookProcedure? _mouseProcedure;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private uint _threadId;
    private Exception? _startupError;

    public InputCapture(Action<object> publish)
    {
        _publish = publish;
        _messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "OBS Input Capture"
        };
        _messageThread.Start();

        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Timed out while installing global keyboard and mouse hooks.");
        }

        if (_startupError is not null)
        {
            throw new InvalidOperationException("Could not install global input hooks.", _startupError);
        }
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            _keyboardProcedure = KeyboardCallback;
            _mouseProcedure = MouseCallback;
            var module = GetModuleHandle(null);
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProcedure, module, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Keyboard hook installation failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProcedure, module, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Mouse hook installation failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _started.Set();
            while (true)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result < 0)
                {
                    throw new InvalidOperationException($"The Windows input message loop failed (Win32 error {Marshal.GetLastWin32Error()}).");
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _started.Set();
        }
        finally
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
            }

            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
            }
        }
    }

    private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var key = Marshal.PtrToStructure<KeyboardData>(lParam);
            if (message is WmKeyDown or WmSysKeyDown)
            {
                HandleKeyDown(key.VirtualKey);
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                HandleKeyUp(key.VirtualKey);
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void HandleKeyDown(uint key)
    {
        if (!_pressedKeys.Add(key))
        {
            return;
        }

        var allowed = IsModifier(key)
            || key is >= 0x70 and <= 0x7B
            || !IsPrintable(key)
            || _pressedKeys.Any(IsShortcutModifier);

        if (allowed && _shownKeys.Add(key))
        {
            _publish(new { type = "key", phase = "down", vk = key, label = GetKeyLabel(key) });
        }
    }

    private void HandleKeyUp(uint key)
    {
        _pressedKeys.Remove(key);
        if (_shownKeys.Remove(key))
        {
            _publish(new { type = "key", phase = "up", vk = key, label = GetKeyLabel(key) });
        }
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var mouse = Marshal.PtrToStructure<MouseData>(lParam);
            switch (message)
            {
                case WmLButtonDown:
                case WmLButtonUp:
                    PublishMouse("left", message == WmLButtonDown ? "down" : "up");
                    break;
                case WmMButtonDown:
                case WmMButtonUp:
                    PublishMouse("middle", message == WmMButtonDown ? "down" : "up");
                    break;
                case WmRButtonDown:
                case WmRButtonUp:
                    PublishMouse("right", message == WmRButtonDown ? "down" : "up");
                    break;
                case WmMouseWheel:
                case WmMouseHWheel:
                    var delta = unchecked((short)((mouse.Data >> 16) & 0xFFFF));
                    if (delta != 0)
                    {
                        var horizontal = message == WmMouseHWheel;
                        var direction = horizontal
                            ? delta > 0 ? "right" : "left"
                            : delta > 0 ? "up" : "down";
                        _publish(new { type = "scroll", direction, horizontal });
                    }
                    break;
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void PublishMouse(string button, string phase) =>
        _publish(new { type = "mouse", button, phase });

    private static bool IsModifier(uint key) =>
        key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static bool IsShortcutModifier(uint key) =>
        key is 0x11 or 0x12 or 0x5B or 0x5C or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static bool IsPrintable(uint key) =>
        key is >= 0x30 and <= 0x39
            or >= 0x41 and <= 0x5A
            or >= 0x60 and <= 0x69
            or >= 0x6A and <= 0x6F
            or 0x20
            or >= 0xBA and <= 0xC0
            or >= 0xDB and <= 0xDE
            or 0xE2;

    private static string GetKeyLabel(uint key)
    {
        if (key is >= 0x41 and <= 0x5A)
        {
            return ((char)key).ToString();
        }

        if (key is >= 0x30 and <= 0x39)
        {
            return ((char)key).ToString();
        }

        if (key is >= 0x60 and <= 0x69)
        {
            return $"Num {key - 0x60}";
        }

        if (key is >= 0x70 and <= 0x7B)
        {
            return $"F{key - 0x6F}";
        }

        return key switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x10 or 0xA0 or 0xA1 => "Shift",
            0x11 or 0xA2 or 0xA3 => "Ctrl",
            0x12 or 0xA4 or 0xA5 => "Alt",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5B or 0x5C => "Win",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            0xE2 => "\\",
            _ => $"Key {key:X2}"
        };
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _messageThread.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    private delegate IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public Point Point;
        public uint Data;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr HWnd;
        public uint Value;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProcedure callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
}
