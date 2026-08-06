using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace LocalTypeAssist.Services;

/// <summary>
/// Reads the complete word immediately before the caret from the focused editor.
/// This is used only as a repair path when browser UI Automation focus changes or
/// destructive editing can desynchronise the low-level keyboard buffer.
/// </summary>
public static class FocusedTextReader
{
    private const int MaxLookBehindCharacters = 160;
    private const int MaxWordLength = 64;

    public static bool TryGetCurrentWordBeforeCaret(out string word)
    {
        word = string.Empty;

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null ||
                !focused.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject))
            {
                return false;
            }

            var pattern = (TextPattern)patternObject;
            var selections = pattern.GetSelection();
            if (selections.Length == 0)
            {
                return false;
            }

            // Use the end of the active selection as the caret. No explicit
            // TextPatternRange type is mentioned here because some WindowsDesktop
            // reference packs expose it only through inferred return types.
            var selection = selections[0];
            var caret = selection.Clone();
            var selectionEnd = selection.Clone();
            caret.MoveEndpointByRange(
                TextPatternRangeEndpoint.Start,
                selectionEnd,
                TextPatternRangeEndpoint.End);
            caret.MoveEndpointByRange(
                TextPatternRangeEndpoint.End,
                selectionEnd,
                TextPatternRangeEndpoint.End);

            caret.MoveEndpointByUnit(
                TextPatternRangeEndpoint.Start,
                TextUnit.Character,
                -MaxLookBehindCharacters);

            var text = caret.GetText(MaxLookBehindCharacters) ?? string.Empty;
            word = ExtractTrailingWord(text);
            return true;
        }
        catch
        {
            // Chromium and some custom editors can temporarily expose an incomplete
            // TextPattern while the DOM is being updated. The caller keeps its current
            // buffer and will retry after the next input event.
            return false;
        }
    }

    private static string ExtractTrailingWord(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var end = text.Length - 1;
        while (end >= 0 && text[end] is '\0' or '\r' or '\n')
        {
            end--;
        }

        if (end < 0 || !IsWordCharacter(text[end]))
        {
            return string.Empty;
        }

        var start = end;
        while (start >= 0 && IsWordCharacter(text[start]))
        {
            start--;
        }

        start++;
        var length = Math.Min(end - start + 1, MaxWordLength);
        start = end - length + 1;
        return text.Substring(start, length);
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetter(character) || character == (char)0x27 || character == (char)0x2019 || character == '-';
}
