namespace DeepEqual;

/// <summary>The first difference found during a deep comparison.</summary>
/// <param name="Path">The location relative to the root, beginning with <c>$</c>.</param>
/// <param name="Expected">The compared value from the left operand.</param>
/// <param name="Actual">The compared value from the right operand.</param>
/// <param name="Kind">The comparison rule that found the mismatch.</param>
public sealed record DeepMismatch(string Path, object? Expected, object? Actual, DeepMismatchKind Kind = DeepMismatchKind.Value);

public enum DeepMismatchKind
{
    Value,
    Count,
    MissingKey,
    Added,
    Removed,
    Moved,
    Set
}
