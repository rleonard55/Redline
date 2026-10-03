namespace Redline.Core.Models;

/// <summary>
/// A single contiguous edit: <see cref="OldLength"/> characters at <see cref="Start"/>
/// were replaced by <see cref="NewLength"/> characters.
/// </summary>
public readonly record struct TextChange(int Start, int OldLength, int NewLength)
{
    public int Delta => NewLength - OldLength;
    public bool IsEmpty => OldLength == 0 && NewLength == 0;

    public TextRange OldRange => new(Start, OldLength);
    public TextRange NewRange => new(Start, NewLength);
}
