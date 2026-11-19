using System;
using System.Collections.Generic;

namespace DeepEqual.Generator.Shared;

/// <summary>
///     In-memory, transport-neutral representation of a delta (patch).
/// </summary>
public sealed class DeltaDocument
{
    public static DeltaDocument Empty => new();

    [ThreadStatic] private static Stack<DeltaDocument>? _pool;
    public readonly List<DeltaOp> Ops = [];
    public IReadOnlyList<DeltaOp> Operations => Ops;
    public bool IsEmpty => Ops.Count == 0;
    internal Type? RootType { get; private set; }

    /// <summary>Creates an in-memory document associated with a generated root schema.</summary>
    public static DeltaDocument ForRoot<T>() => new() { RootType = typeof(T) };

    internal static DeltaDocument ForRoot(Type? type) => new() { RootType = type };

    internal void Clear()
    {
        Ops.Clear();
        RootType = null;
    }

    public static DeltaDocument Rent(int initialCapacity)
    {
        var d = Rent();
        if (d.Ops.Capacity < initialCapacity) d.Ops.Capacity = initialCapacity;
        return d;
    }

    internal static DeltaDocument Rent()
    {
        var p = _pool;
        if (p is not null && p.Count > 0) return p.Pop();

        return new DeltaDocument();
    }

    internal static void Return(DeltaDocument doc)
    {
        doc.Clear();
        (_pool ??= new Stack<DeltaDocument>(4)).Push(doc);
    }
}
