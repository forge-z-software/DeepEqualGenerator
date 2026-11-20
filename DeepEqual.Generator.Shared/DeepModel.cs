using System;
using System.Collections.Generic;

namespace DeepEqual;

public enum DeepMemberMode
{
    Deep,
    Shallow,
    Reference,
    Skipped,
    Custom
}

public enum DeepCollectionMode
{
    None,
    Ordered,
    Unordered,
    Keyed,
    Dictionary
}

public sealed record DeepModelMember(
    string DeclaringType,
    string Name,
    string ValueType,
    DeepMemberMode Mode,
    DeepCollectionMode Collection,
    string KeyMembers,
    bool CanFallBackAtRuntime);

public sealed class DeepModel
{
    public DeepModel(string rootType, bool tracksCycles, DeepModelMember[] members)
    {
        RootType = rootType;
        TracksCycles = tracksCycles;
        Members = Array.AsReadOnly((DeepModelMember[])members.Clone());
    }

    public string RootType { get; }
    public bool TracksCycles { get; }
    public IReadOnlyList<DeepModelMember> Members { get; }
}

/// <summary>Named comparison options that can be reused across tests and application code.</summary>
public sealed class ComparisonProfile
{
    public ComparisonProfile(string name, Generator.Shared.ComparisonOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Name { get; }
    public Generator.Shared.ComparisonOptions Options { get; }
    public Generator.Shared.ComparisonContext CreateContext() => new(Options);
}
