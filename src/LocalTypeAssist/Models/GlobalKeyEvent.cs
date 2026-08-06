namespace LocalTypeAssist.Models;

public sealed record GlobalKeyEvent(
    int VirtualKey,
    string Text,
    bool Shift,
    bool Control,
    bool Alt,
    bool Win,
    bool CapsLock)
{
    public bool HasCommandModifier => Control || Alt || Win;
}
