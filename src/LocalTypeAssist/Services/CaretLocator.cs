using System.Windows;
using System.Windows.Automation;

namespace LocalTypeAssist.Services;

public readonly record struct CaretAnchor(double X, double Y, double Height);

public static class CaretLocator
{
    public static CaretAnchor GetBestAnchor()
    {
        // Modern browser editors usually expose the most accurate caret through UIA.
        if (TryAutomationCaret(out var anchor))
        {
            return ClampToWorkArea(anchor);
        }

        if (TryWin32Caret(out anchor))
        {
            return ClampToWorkArea(anchor);
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            return ClampToWorkArea(new CaretAnchor(cursor.X + 10, cursor.Y - 10, 22));
        }

        return new CaretAnchor(
            SystemParameters.WorkArea.Left + 30,
            SystemParameters.WorkArea.Bottom - 120,
            22);
    }

    private static CaretAnchor ClampToWorkArea(CaretAnchor anchor)
    {
        var workArea = SystemParameters.WorkArea;
        return new CaretAnchor(
            Math.Clamp(anchor.X, workArea.Left + 8, workArea.Right - 8),
            Math.Clamp(anchor.Y, workArea.Top + 8, workArea.Bottom - 8),
            Math.Clamp(anchor.Height, 12, 52));
    }

    private static bool TryAutomationCaret(out CaretAnchor anchor)
    {
        anchor = default;

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return false;
            }

            if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject))
            {
                var pattern = (TextPattern)patternObject;
                var ranges = pattern.GetSelection();
                for (var rangeIndex = ranges.Length - 1; rangeIndex >= 0; rangeIndex--)
                {
                    // Do not name TextPatternRange explicitly here. Some WindowsDesktop
                    // reference packs expose the returned type for inference but fail to
                    // resolve it when it is used as a method parameter.
                    var rectangles = ranges[rangeIndex].GetBoundingRectangles();
                    for (var rectangleIndex = rectangles.Length - 1; rectangleIndex >= 0; rectangleIndex--)
                    {
                        var rectangle = rectangles[rectangleIndex];
                        if (rectangle.IsEmpty ||
                            double.IsNaN(rectangle.Left) ||
                            double.IsNaN(rectangle.Top) ||
                            double.IsNaN(rectangle.Right) ||
                            double.IsNaN(rectangle.Bottom) ||
                            double.IsInfinity(rectangle.Left) ||
                            double.IsInfinity(rectangle.Top) ||
                            double.IsInfinity(rectangle.Right) ||
                            double.IsInfinity(rectangle.Bottom) ||
                            rectangle.Height <= 0)
                        {
                            continue;
                        }

                        anchor = new CaretAnchor(rectangle.Right + 1, rectangle.Top, rectangle.Height);
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Some applications expose an incomplete UI Automation provider.
        }

        return false;
    }

    private static bool TryWin32Caret(out CaretAnchor anchor)
    {
        anchor = default;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var info = new NativeMethods.GuiThreadInfo
        {
            CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };

        if (!NativeMethods.GetGUIThreadInfo(threadId, ref info))
        {
            return false;
        }

        var target = info.HwndCaret != IntPtr.Zero ? info.HwndCaret : info.HwndFocus;
        if (target == IntPtr.Zero)
        {
            return false;
        }

        var top = new NativeMethods.Point
        {
            X = info.RcCaret.Right,
            Y = info.RcCaret.Top
        };
        var bottom = new NativeMethods.Point
        {
            X = info.RcCaret.Right,
            Y = info.RcCaret.Bottom
        };

        if (!NativeMethods.ClientToScreen(target, ref top) ||
            !NativeMethods.ClientToScreen(target, ref bottom))
        {
            return false;
        }

        var height = bottom.Y - top.Y;
        if (height <= 0 || height > 120)
        {
            return false;
        }

        anchor = new CaretAnchor(top.X + 1, top.Y, Math.Max(16, height));
        return true;
    }
}
