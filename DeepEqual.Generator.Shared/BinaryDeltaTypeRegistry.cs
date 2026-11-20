using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization.Metadata;

namespace DeepEqual.Generator.Shared;

public sealed record BinaryDeltaType(uint Id, JsonTypeInfo Serialization);

/// <summary>An explicit set of additional delta value types and their stable wire IDs.</summary>
public sealed class BinaryDeltaTypeRegistry
{
    private readonly Dictionary<Type, BinaryDeltaType> _byType = new();
    private readonly Dictionary<uint, BinaryDeltaType> _byId = new();

    public BinaryDeltaTypeRegistry(params BinaryDeltaType[] types)
    {
        foreach (var entry in types)
        {
            ArgumentOutOfRangeException.ThrowIfZero(entry.Id);
            _byType.Add(entry.Serialization.Type, entry);
            _byId.Add(entry.Id, entry);
        }
    }

    internal BinaryDeltaType? Find(Type type) => _byType.GetValueOrDefault(type);
    internal BinaryDeltaType? Find(uint id) => _byId.GetValueOrDefault(id);
    internal string Describe() => string.Join("|", _byId.OrderBy(entry => entry.Key)
        .Select(entry => FormattableString.Invariant($"{entry.Key}:{entry.Value.Serialization.Type}")));
}
