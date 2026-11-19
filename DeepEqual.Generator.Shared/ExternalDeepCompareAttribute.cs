using System;

namespace DeepEqual.Generator.Shared;

/// <summary>
///     Assembly-scoped: applies a comparison rule to a readable member of an adopted root.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ExternalDeepCompareAttribute(Type root, string path) : DeepCompareAttribute
{
    /// <summary>The external root type where the path starts. Required.</summary>
    public Type Root { get; } = root ?? throw new ArgumentNullException(nameof(root));

    /// <summary>
    ///     The name of a direct member of the root.
    /// </summary>
    public string Path { get; } =
        !string.IsNullOrWhiteSpace(path) ? path : throw new ArgumentNullException(nameof(path));
}
