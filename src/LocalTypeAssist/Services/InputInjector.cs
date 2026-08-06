using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalTypeAssist.Services;

public static class InputInjector
{
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputs = new List<NativeMethods.Input>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(CreateUnicodeInput(character, keyUp: false));
            inputs.Add(CreateUnicodeInput(character, keyUp: true));
        }

        Send(inputs);
    }

    public static void TypeTextAndSelectSuffix(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputs = new List<NativeMethods.Input>(text.Length * 4 + 2);
        foreach (var character in text)
        {
            inputs.Add(CreateUnicodeInput(character, keyUp: false));
            inputs.Add(CreateUnicodeInput(character, keyUp: true));
        }

        inputs.Add(CreateVirtualKeyInput(NativeMethods.VkShift, keyUp: false));
        for (var i = 0; i < text.Length; i++)
        {
            inputs.Add(CreateVirtualKeyInput(NativeMethods.VkLeft, keyUp: false));
            inputs.Add(CreateVirtualKeyInput(NativeMethods.VkLeft, keyUp: true));
        }
        inputs.Add(CreateVirtualKeyInput(NativeMethods.VkShift, keyUp: true));
        Send(inputs);
    }

    public static void SendBackspaces(int count)
    {
        SendRepeatedKey(NativeMethods.VkBack, count);
    }

    public static void MoveCaretRight()
    {
        SendRepeatedKey(NativeMethods.VkRight, 1);
    }

    public static void SelectPreviousCharacters(int count)
    {
        if (count <= 0)
        {
            return;
        }

        var inputs = new List<NativeMethods.Input>(count * 2 + 2)
        {
            CreateVirtualKeyInput(NativeMethods.VkShift, keyUp: false)
        };

        for (var i = 0; i < count; i++)
        {
            inputs.Add(CreateVirtualKeyInput(NativeMethods.VkLeft, keyUp: false));
            inputs.Add(CreateVirtualKeyInput(NativeMethods.VkLeft, keyUp: true));
        }

        inputs.Add(CreateVirtualKeyInput(NativeMethods.VkShift, keyUp: true));
        Send(inputs);
    }

    private static void SendRepeatedKey(int virtualKey, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var inputs = new List<NativeMethods.Input>(count * 2);
        for (var i = 0; i < count; i++)
        {
            inputs.Add(CreateVirtualKeyInput(virtualKey, keyUp: false));
            inputs.Add(CreateVirtualKeyInput(virtualKey, keyUp: true));
        }

        Send(inputs);
    }

    private static void Send(List<NativeMethods.Input> inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.Input>());
        if (sent != inputs.Count)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не смогла вставить текст полностью.");
        }
    }

    private static NativeMethods.Input CreateUnicodeInput(char character, bool keyUp)
    {
        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                Ki = new NativeMethods.KeyboardInput
                {
                    WVk = 0,
                    WScan = character,
                    DwFlags = NativeMethods.KeyeventfUnicode | (keyUp ? NativeMethods.KeyeventfKeyup : 0),
                    Time = 0,
                    DwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static NativeMethods.Input CreateVirtualKeyInput(int virtualKey, bool keyUp)
    {
        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                Ki = new NativeMethods.KeyboardInput
                {
                    WVk = (ushort)virtualKey,
                    WScan = 0,
                    DwFlags = keyUp ? NativeMethods.KeyeventfKeyup : 0,
                    Time = 0,
                    DwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }
}
