using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LocalTypeAssist.Models;

namespace LocalTypeAssist.Services;

public sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private IntPtr _hook = IntPtr.Zero;

    public event Func<GlobalKeyEvent, bool>? KeyDown;
    public event Func<GlobalKeyEvent, bool>? KeyUp;

    public KeyboardHook()
    {
        _callback = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = NativeMethods.GetModuleHandle(module?.ModuleName);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _callback, moduleHandle, 0);

        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось установить глобальный обработчик клавиатуры.");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var isKeyDown = wParam == (IntPtr)NativeMethods.WmKeyDown ||
                            wParam == (IntPtr)NativeMethods.WmSysKeyDown;
            var isKeyUp = wParam == (IntPtr)NativeMethods.WmKeyUp ||
                          wParam == (IntPtr)NativeMethods.WmSysKeyUp;

            if (isKeyDown || isKeyUp)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
                if ((data.Flags & NativeMethods.LlkhfInjected) == 0)
                {
                    var keyEvent = BuildEvent((int)data.VkCode, data.ScanCode, isKeyDown);
                    var source = isKeyDown ? KeyDown : KeyUp;
                    var handlers = source?.GetInvocationList().Cast<Func<GlobalKeyEvent, bool>>();
                    if (handlers is not null)
                    {
                        foreach (var handler in handlers)
                        {
                            if (handler(keyEvent))
                            {
                                return (IntPtr)1;
                            }
                        }
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static GlobalKeyEvent BuildEvent(int virtualKey, uint scanCode, bool isKeyDown)
    {
        var shift = IsPressed(NativeMethods.VkShift);
        var control = IsPressed(NativeMethods.VkControl);
        var alt = IsPressed(NativeMethods.VkMenu);
        var win = IsPressed(NativeMethods.VkLWin) || IsPressed(NativeMethods.VkRWin);
        var caps = (NativeMethods.GetAsyncKeyState(NativeMethods.VkCapital) & 0x0001) != 0;

        var text = string.Empty;
        if (isKeyDown && !control && !alt && !win && !IsShiftKey(virtualKey))
        {
            text = TranslateToText(virtualKey, scanCode, shift);
        }

        return new GlobalKeyEvent(virtualKey, text, shift, control, alt, win, caps);
    }

    private static bool IsPressed(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool IsShiftKey(int virtualKey) => virtualKey is
        NativeMethods.VkShift or NativeMethods.VkLShift or NativeMethods.VkRShift;

    private static string TranslateToText(int virtualKey, uint scanCode, bool shift)
    {
        var state = new byte[256];
        if (!NativeMethods.GetKeyboardState(state))
        {
            return string.Empty;
        }

        state[virtualKey] |= 0x80;
        if (shift)
        {
            state[NativeMethods.VkShift] |= 0x80;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var layout = NativeMethods.GetKeyboardLayout(threadId);
        var mappedScanCode = scanCode == 0
            ? NativeMethods.MapVirtualKeyEx((uint)virtualKey, NativeMethods.MapvkVkToVsc, layout)
            : scanCode;

        var buffer = new StringBuilder(8);
        var result = NativeMethods.ToUnicodeEx(
            (uint)virtualKey,
            mappedScanCode,
            state,
            buffer,
            buffer.Capacity,
            0,
            layout);

        if (result > 0)
        {
            return buffer.ToString(0, Math.Min(result, buffer.Length));
        }

        if (result < 0)
        {
            _ = NativeMethods.ToUnicodeEx((uint)virtualKey, mappedScanCode, state, buffer, buffer.Capacity, 0, layout);
        }

        return string.Empty;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
