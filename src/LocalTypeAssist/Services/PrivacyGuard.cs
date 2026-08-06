using System.Diagnostics;
using System.Windows.Automation;

namespace LocalTypeAssist.Services;

public readonly record struct FocusSnapshot(string Token, bool Ignore);

public static class PrivacyGuard
{
    private static readonly HashSet<string> SensitiveProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "credentialuibroker",
        "logonui",
        "consent",
        "keepass",
        "keepassxc",
        "1password",
        "bitwarden"
    };

    public static FocusSnapshot GetFocusSnapshot()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return new FocusSnapshot("none", true);
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        var windowToken = $"hwnd:{foreground.ToInt64():X}:pid:{processId}";
        if (processId == Environment.ProcessId)
        {
            return new FocusSnapshot(windowToken, true);
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (SensitiveProcesses.Contains(process.ProcessName))
            {
                return new FocusSnapshot(windowToken, true);
            }
        }
        catch
        {
            // Continue with UI Automation if process metadata is unavailable.
        }

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return new FocusSnapshot(windowToken, false);
            }

            var passwordValue = focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
            var isPassword = passwordValue is bool password && password;

            // Name, RuntimeId and geometry are deliberately excluded. Chromium may
            // recreate them while typing or resize a contenteditable field, which used to
            // reset the accumulated prefix in the middle of a word. These properties remain
            // stable enough to separate the address bar from page editors without changing
            // as the text grows.
            var controlType = SafeProperty(focused, AutomationElement.ControlTypeProperty);
            var className = SafeProperty(focused, AutomationElement.ClassNameProperty);
            var automationId = SafeProperty(focused, AutomationElement.AutomationIdProperty);
            var frameworkId = SafeProperty(focused, AutomationElement.FrameworkIdProperty);
            var nativeHandle = SafeProperty(focused, AutomationElement.NativeWindowHandleProperty);
            var focusToken = $"{windowToken}:ct:{controlType}:cl:{className}:id:{automationId}:fw:{frameworkId}:nh:{nativeHandle}";
            return new FocusSnapshot(focusToken, isPassword);
        }
        catch
        {
            return new FocusSnapshot(windowToken, false);
        }
    }

    private static string SafeProperty(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property, true);
            return value == AutomationElement.NotSupported || value is null
                ? string.Empty
                : value.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
