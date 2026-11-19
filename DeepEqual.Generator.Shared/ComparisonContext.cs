using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DeepEqual.Generator.Shared;

/// <summary>
///     Holds options and traversal state for one comparison at a time.
/// </summary>
public sealed class ComparisonContext
{
    private const int LinearCycleTrackingLimit = 16;
    private readonly bool _tracking;
    private List<RefPair>? _activePairs;
    private HashSet<RefPair>? _visited;
    private List<PathSegment>? _path;
    private int _suppressionDepth;

    public DeepMismatch? Mismatch { get; private set; }
    public bool IsRecordingMismatch => _path is not null && _suppressionDepth == 0 && Mismatch is null;

    public static ComparisonContext ForMismatch(ComparisonOptions? options, bool trackCycles = true) =>
        new(trackCycles, options) { _path = [] };

    public PathScope AtMember(string name) =>
        IsRecordingMismatch ? PushPath(new PathSegment(PathKind.Member, name)) : default;

    public PathScope AtIndex(int index) =>
        IsRecordingMismatch ? PushPath(new PathSegment(PathKind.Index, Index: index)) : default;

    public PathScope AtKey<TKey>(TKey key) =>
        IsRecordingMismatch ? PushPath(new PathSegment(PathKind.Key, key)) : default;

    internal PathScope AtIndices(int[] indices) =>
        IsRecordingMismatch ? PushPath(new PathSegment(PathKind.Indices, indices)) : default;

    internal PathScope SuppressMismatch()
    {
        if (_path is null) return default;
        _suppressionDepth++;
        return new PathScope(this, suppress: true);
    }

    public bool RecordMismatch<T>(T expected, T actual, DeepMismatchKind kind = DeepMismatchKind.Value)
    {
        if (IsRecordingMismatch)
        {
            var path = new StringBuilder("$");
            foreach (var segment in _path!)
                segment.AppendTo(path);
            Mismatch = new DeepMismatch(path.ToString(), expected, actual, kind);
        }
        return false;
    }

    private PathScope PushPath(PathSegment segment)
    {
        _path!.Add(segment);
        return new PathScope(this, suppress: false);
    }

    private enum PathKind
    {
        Member,
        Index,
        Key,
        Indices
    }

    private readonly record struct PathSegment(PathKind Kind, object? Value = null, int Index = 0)
    {
        public void AppendTo(StringBuilder path)
        {
            switch (Kind)
            {
                case PathKind.Member:
                    path.Append('.').Append(Value);
                    break;
                case PathKind.Index:
                    path.Append('[').Append(Index.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case PathKind.Key:
                    path.Append('[');
                    if (Value is string key)
                        path.Append('"').Append(JsonEncodedText.Encode(key).ToString()).Append('"');
                    else
                        path.Append(Value is null ? "null" : Convert.ToString(Value, CultureInfo.InvariantCulture));
                    path.Append(']');
                    break;
                case PathKind.Indices:
                    path.Append('[').Append(string.Join(",", (int[])Value!)).Append(']');
                    break;
            }
        }
    }

    public readonly struct PathScope : IDisposable
    {
        private readonly ComparisonContext? _context;
        private readonly bool _suppress;

        internal PathScope(ComparisonContext context, bool suppress)
        {
            _context = context;
            _suppress = suppress;
        }

        public void Dispose()
        {
            if (_context is null) return;
            if (_suppress)
                _context._suppressionDepth--;
            else
                _context._path!.RemoveAt(_context._path.Count - 1);
        }
    }

    /// <summary>Creates a context with cycle tracking enabled and default options.</summary>
    public ComparisonContext() : this(true, new ComparisonOptions())
    {
    }

    /// <summary>Creates a context with cycle tracking enabled and explicit options.</summary>
    public ComparisonContext(ComparisonOptions? options) : this(true, options ?? new ComparisonOptions())
    {
    }

    internal ComparisonContext(bool trackCycles, ComparisonOptions? options)
    {
        _tracking = trackCycles;
        Options = options ?? new ComparisonOptions();
    }

    public ComparisonOptions Options { get; }

    /// <summary>A reusable context for models with no cycles and default options.</summary>
    public static ComparisonContext NoTracking { get; } = new(false, null);

    /// <summary>Begins comparing a reference pair, or reports that it is already on the current path.</summary>
    public bool Enter(object left, object right)
    {
        if (!_tracking) return true;

        var active = _activePairs ??= new List<RefPair>(4);
        var pair = new RefPair(left, right);
        if (_visited is null)
        {
            foreach (var existing in active)
                if (RefPair.Comparer.Instance.Equals(existing, pair)) return false;

            if (active.Count == LinearCycleTrackingLimit)
                _visited = new HashSet<RefPair>(active, RefPair.Comparer.Instance);
        }

        if (_visited is not null && !_visited.Add(pair)) return false;
        active.Add(pair);
        return true;
    }

    public void Exit(object left, object right)
    {
        if (!_tracking || _activePairs is not { Count: > 0 } active) return;
        _visited?.Remove(active[^1]);
        active.RemoveAt(active.Count - 1);
    }

    private readonly struct RefPair
    {
        private readonly object _left;
        private readonly object _right;

        public RefPair(object left, object right)
        {
            _left = left;
            _right = right;
        }

        public sealed class Comparer : IEqualityComparer<RefPair>
        {
            public static readonly Comparer Instance = new();

            public bool Equals(RefPair x, RefPair y)
            {
                return ReferenceEquals(x._left, y._left) && ReferenceEquals(x._right, y._right);
            }

            public int GetHashCode(RefPair obj)
            {
                unchecked
                {
                    var a = RuntimeHelpers.GetHashCode(obj._left);
                    var b = RuntimeHelpers.GetHashCode(obj._right);
                    return (a * 397) ^ b;
                }
            }
        }
    }
}

/// <summary>
///     Strongly-typed structural diff for T.
/// </summary>
public readonly struct Diff<T> : IDiff
{
    public bool HasChanges { get; }
    public bool IsReplacement { get; }
    public T? NewValue { get; }
    public IReadOnlyList<MemberChange>? MemberChanges { get; }

    private Diff(bool has, bool replace, T? newValue, IReadOnlyList<MemberChange>? changes)
    {
        HasChanges = has;
        IsReplacement = replace;
        NewValue = newValue;
        MemberChanges = changes;
    }

    public static Diff<T> Empty => new(false, false, default, null);

    public static Diff<T> Replacement(T? newValue)
    {
        return new Diff<T>(true, true, newValue, null);
    }

    public static Diff<T> Members(List<MemberChange> changes)
    {
        if (changes.Count == 0) return Empty;

        return new Diff<T>(true, false, default, changes);
    }

    public bool IsEmpty => !HasChanges;
}
