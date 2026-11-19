using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DeepEqual.Generator.Shared;

public static class ComparisonHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreEqualStrings(string? a, string? b, ComparisonContext? context)
    {
        var comp = context?.Options.StringComparison ?? StringComparison.Ordinal;
        if (comp == StringComparison.Ordinal) return a == b;
        return string.Equals(a, b, comp);
    }

    public static bool ArraysEqual<T>(T[]? a, T[]? b)
    {
        if (ReferenceEquals(a, b)) return true;

        if (a is null || b is null) return false;

        if (a.Length != b.Length) return false;

        return a.SequenceEqual(b);
    }

    public static bool AreEqualEnum<T>(T a, T b) where T : struct, Enum
    {
        return EqualityComparer<T>.Default.Equals(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreEqualDateTime(DateTime a, DateTime b)
    {
        return a.Kind == b.Kind && a.Ticks == b.Ticks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreEqualDateTimeOffset(DateTimeOffset a, DateTimeOffset b)
    {
        return a.Offset == b.Offset && a.UtcTicks == b.UtcTicks;
    }

    public static bool AreEqualDateOnly(DateOnly a, DateOnly b)
    {
        return a.DayNumber == b.DayNumber;
    }

    public static bool AreEqualTimeOnly(TimeOnly a, TimeOnly b)
    {
        return a.Ticks == b.Ticks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreEqualSingle(float a, float b, ComparisonContext? context)
    {
        if (float.IsNaN(a) || float.IsNaN(b))
            return context?.Options.TreatNaNEqual == true && float.IsNaN(a) && float.IsNaN(b);

        var eps = context?.Options.FloatEpsilon ?? 0f;
        return a == b || (eps > 0f && float.IsFinite(a) && float.IsFinite(b) && Math.Abs((double)a - b) <= eps);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreEqualDouble(double a, double b, ComparisonContext? context)
    {
        if (double.IsNaN(a) || double.IsNaN(b))
            return context?.Options.TreatNaNEqual == true && double.IsNaN(a) && double.IsNaN(b);

        var eps = context?.Options.DoubleEpsilon ?? 0.0;
        return a == b || (eps > 0d && double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= eps);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreEqualDecimal(decimal a, decimal b, ComparisonContext? context)
    {
        var eps = context?.Options.DecimalEpsilon ?? 0m;
        if (eps <= 0m) return a.Equals(b);

        try
        {
            return Math.Abs(a - b) <= eps;
        }
        catch (OverflowException)
        {
            // The difference is larger than any representable decimal tolerance.
            return false;
        }
    }

    public static bool AreEqualArrayRank1<TElement, TComparer>(TElement[]? a, TElement[]? b, TComparer comparer,
        ComparisonContext context)
        where TComparer : IElementComparer<TElement>
    {
        if (ReferenceEquals(a, b)) return true;

        if (a is null || b is null) return false;

        if (a.Length != b.Length) return context.RecordMismatch(a.Length, b.Length, DeepMismatchKind.Count);

        for (var i = 0; i < a.Length; i++)
            if (!CompareAtIndex(a[i], b[i], i, comparer, context))
                return false;

        return true;
    }

    public static bool AreEqualArray<TElement, TComparer>(Array? a, Array? b, TComparer comparer,
        ComparisonContext context)
        where TComparer : IElementComparer<TElement>
    {
        if (ReferenceEquals(a, b)) return true;

        if (a is null || b is null) return false;

        if (a.Rank != b.Rank) return false;

        var indices = new int[a.Rank];
        for (var d = 0; d < a.Rank; d++)
        {
            indices[d] = a.GetLowerBound(d);
            if (a.GetLength(d) != b.GetLength(d) || indices[d] != b.GetLowerBound(d))
                return false;
        }

        if (a.Length == 0) return true;
        while (true)
        {
            var va = (TElement)a.GetValue(indices)!;
            var vb = (TElement)b.GetValue(indices)!;
            using (context.AtIndices(indices))
                if (!comparer.Invoke(va, vb, context)) return context.RecordMismatch(va, vb);

            var dim = a.Rank - 1;
            while (dim >= 0)
            {
                if (indices[dim] < a.GetUpperBound(dim))
                {
                    indices[dim]++;
                    break;
                }

                indices[dim] = a.GetLowerBound(dim);
                dim--;
            }

            if (dim < 0) break;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool CompareAtIndex<T, TComparer>(T expected, T actual, int index, TComparer comparer,
        ComparisonContext context) where TComparer : IElementComparer<T> =>
        context.IsRecordingMismatch
            ? CompareAndRecordAtIndex(expected, actual, index, comparer, context)
            : comparer.Invoke(expected, actual, context);

    private static bool CompareAndRecordAtIndex<T, TComparer>(T expected, T actual, int index, TComparer comparer,
        ComparisonContext context) where TComparer : IElementComparer<T>
    {
        using var path = context.AtIndex(index);
        return comparer.Invoke(expected, actual, context) || context.RecordMismatch(expected, actual);
    }

    public static bool AreEqualSequencesOrdered<T, TComparer>(
        IEnumerable<T>? a, IEnumerable<T>? b, TComparer comparer, ComparisonContext context)
        where TComparer : IElementComparer<T>
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        if (a is T[] aa && b is T[] bb)
            return AreEqualArrayRank1<T, TComparer>(aa, bb, comparer, context);

        if (a is IList<T> la && b is IList<T> lb)
        {
            if (la.Count != lb.Count) return context.RecordMismatch(la.Count, lb.Count, DeepMismatchKind.Count);
            for (var i = 0; i < la.Count; i++)
                if (!CompareAtIndex(la[i], lb[i], i, comparer, context))
                    return false;
            return true;
        }

        if (a is IReadOnlyList<T> ra && b is IReadOnlyList<T> rb)
        {
            if (ra.Count != rb.Count) return context.RecordMismatch(ra.Count, rb.Count, DeepMismatchKind.Count);
            for (var i = 0; i < ra.Count; i++)
                if (!CompareAtIndex(ra[i], rb[i], i, comparer, context))
                    return false;
            return true;
        }

        using var ea = a.GetEnumerator();
        using var eb = b.GetEnumerator();
        for (var index = 0; ; index++)
        {
            var ma = ea.MoveNext();
            var mb = eb.MoveNext();
            if (ma != mb) return false;
            if (!ma) return true;
            if (!CompareAtIndex(ea.Current, eb.Current, index, comparer, context)) return false;
        }
    }

    public static bool AreEqualSequencesUnordered<T, TComparer>(
        IEnumerable<T>? a, IEnumerable<T>? b, TComparer comparer, ComparisonContext context)
        where TComparer : IElementComparer<T>
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.TryGetNonEnumeratedCount(out var countA) && b.TryGetNonEnumeratedCount(out var countB) && countA != countB)
            return false;
        return MatchUnordered(a as IReadOnlyList<T> ?? a.ToList(), b as IReadOnlyList<T> ?? b.ToList(), comparer, context);
    }

    public static bool AreEqualSequencesByKey<T, TKey, TComparer>(
        IEnumerable<T>? expected, IEnumerable<T>? actual, Func<T, TKey> getKey, TComparer comparer,
        ComparisonContext context) where TComparer : IElementComparer<T>
    {
        if (ReferenceEquals(expected, actual)) return true;
        if (expected is null || actual is null) return false;

        var left = Group(expected, getKey, out var leftNulls);
        var right = Group(actual, getKey, out var rightNulls);
        if (leftNulls != rightNulls || left.Count != right.Count) return false;

        foreach (var (key, leftBucket) in left)
        {
            using var path = context.AtKey(key.Item1);
            if (!right.TryGetValue(key, out var rightBucket))
                return context.RecordMismatch<object?>(leftBucket, null);
            if (leftBucket.Count == 1 && rightBucket.Count == 1)
            {
                if (!comparer.Invoke(leftBucket[0], rightBucket[0], context))
                    return context.RecordMismatch(leftBucket[0], rightBucket[0]);
            }
            else if (!MatchUnordered(leftBucket, rightBucket, comparer, context))
            {
                return context.RecordMismatch(leftBucket, rightBucket);
            }
        }
        return true;

        static Dictionary<ValueTuple<TKey>, List<T>> Group(IEnumerable<T> items, Func<T, TKey> keySelector, out int nulls)
        {
            nulls = 0;
            var groups = new Dictionary<ValueTuple<TKey>, List<T>>();
            foreach (var item in items)
            {
                if (item is null)
                {
                    nulls++;
                    continue;
                }
                var key = ValueTuple.Create(keySelector(item));
                if (!groups.TryGetValue(key, out var bucket))
                    groups[key] = bucket = [];
                bucket.Add(item);
            }
            return groups;
        }
    }

    private static bool MatchUnordered<T, TComparer>(IReadOnlyList<T> a, IReadOnlyList<T> b, TComparer comparer,
        ComparisonContext context) where TComparer : IElementComparer<T>
    {
        var count = a.Count;
        if (count != b.Count) return false;
        if (count == 0) return true;
        using var suppression = context.SuppressMismatch();

        // Reassign earlier matches when the first available candidate blocks a later item.
        // This is required for non-transitive comparisons such as numeric tolerance.
        var leftMatch = new int[count];
        var rightMatch = new int[count];
        Array.Fill(rightMatch, -1);
        var visited = new int[count];
        var parent = new int[count];
        var queue = new int[count];

        for (var left = 0; left < count; left++)
        {
            var head = 0;
            var tail = 1;
            queue[0] = left;
            var free = -1;
            while (head < tail && free < 0)
            {
                var candidate = queue[head++];
                for (var right = 0; right < count; right++)
                {
                    if (visited[right] == left + 1 || !comparer.Invoke(a[candidate], b[right], context)) continue;
                    visited[right] = left + 1;
                    parent[right] = candidate;
                    if (rightMatch[right] < 0)
                    {
                        free = right;
                        break;
                    }
                    queue[tail++] = rightMatch[right];
                }
            }

            if (free < 0) return false;
            while (true)
            {
                var candidate = parent[free];
                var previous = leftMatch[candidate];
                leftMatch[candidate] = free;
                rightMatch[free] = candidate;
                if (candidate == left) break;
                free = previous;
            }
        }

        return true;
    }

    public static bool AreEqualSequencesUnorderedHash<T>(IEnumerable<T>? a, IEnumerable<T>? b,
        IEqualityComparer<T> equalityComparer)
    {
        if (ReferenceEquals(a, b)) return true;

        if (a is null || b is null) return false;

        if (a is ICollection<T> ca && b is ICollection<T> cb && ca.Count != cb.Count) return false;

        #pragma warning disable CS8714
        var counts = new Dictionary<T, int>(equalityComparer);
#pragma warning restore CS8714
        var nullCount = 0;
        foreach (var item in a)
        {
            if (item is null)
            {
                nullCount++;
                continue;
            }
            counts.TryGetValue(item, out var n);
            counts[item] = n + 1;
        }

        foreach (var item in b)
        {
            if (item is null)
            {
                if (--nullCount < 0) return false;
                continue;
            }
            if (!counts.TryGetValue(item, out var n)) return false;

            if (n == 1)
                counts.Remove(item);
            else
                counts[item] = n - 1;
        }

        return nullCount == 0 && counts.Count == 0;
    }

    public static bool AreEqualReadOnlyMemory<T, TComparer>(ReadOnlyMemory<T> a, ReadOnlyMemory<T> b,
        TComparer comparer, ComparisonContext context)
        where TComparer : IElementComparer<T>
    {
        if (a.Length != b.Length) return false;

        var sa = a.Span;
        var sb = b.Span;
        for (var i = 0; i < sa.Length; i++)
            if (!CompareAtIndex(sa[i], sb[i], i, comparer, context))
                return false;

        return true;
    }

    public static bool AreEqualMemory<T, TComparer>(Memory<T> a, Memory<T> b, TComparer comparer,
        ComparisonContext context)
        where TComparer : IElementComparer<T>
    {
        if (a.Length != b.Length) return false;

        var sa = a.Span;
        var sb = b.Span;
        for (var i = 0; i < sa.Length; i++)
            if (!CompareAtIndex(sa[i], sb[i], i, comparer, context))
                return false;

        return true;
    }

    public static bool AreEqualDictionariesAny<TKey, TValue, TValueComparer>(
        object? a, object? b, TValueComparer comparer, ComparisonContext context)
        where TKey : notnull
        where TValueComparer : IElementComparer<TValue>
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is not IEnumerable<KeyValuePair<TKey, TValue>> left ||
            b is not IEnumerable<KeyValuePair<TKey, TValue>> right) return false;

        var leftMap = NormalizeMap(left);
        var rightMap = NormalizeMap(right);
        if (leftMap is null || rightMap is null) return false;
        if (leftMap.Count != rightMap.Count)
            return context.RecordMismatch(leftMap.Count, rightMap.Count, DeepMismatchKind.Count);

        foreach (var pair in leftMap)
        {
            using var path = context.AtKey(pair.Key);
            if (!rightMap.TryGetValue(pair.Key, out var actual))
                return context.RecordMismatch<object?>(pair.Value, null, DeepMismatchKind.MissingKey);
            if (!comparer.Invoke(pair.Value, actual, context))
                return context.RecordMismatch(pair.Value, actual);
        }

        return true;
    }

    private static Dictionary<TKey, TValue>? NormalizeMap<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> pairs)
        where TKey : notnull
    {
        if (pairs is Dictionary<TKey, TValue> map &&
            (ReferenceEquals(map.Comparer, EqualityComparer<TKey>.Default) ||
             (typeof(TKey) == typeof(string) && ReferenceEquals(map.Comparer, StringComparer.Ordinal))))
            return map;

        var result = new Dictionary<TKey, TValue>();
        foreach (var pair in pairs)
            if (!result.TryAdd(pair.Key, pair.Value)) return null;
        return result;
    }

    public static bool DeepComparePolymorphic<T>(T left, T right, ComparisonContext context) =>
        DynamicDeepComparer.AreEqualDynamic(left, right, context);

    public static IEqualityComparer<string> GetStringComparer(ComparisonContext? context)
    {
        var sc = context?.Options.StringComparison ?? StringComparison.Ordinal;
        return sc switch
        {
            StringComparison.Ordinal => StringComparer.Ordinal,
            StringComparison.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase,
            StringComparison.InvariantCulture => StringComparer.InvariantCulture,
            StringComparison.InvariantCultureIgnoreCase => StringComparer.InvariantCultureIgnoreCase,
            StringComparison.CurrentCulture => StringComparer.CurrentCulture,
            StringComparison.CurrentCultureIgnoreCase => StringComparer.CurrentCultureIgnoreCase,
            _ => StringComparer.Ordinal
        };
    }

    public static bool EqualsValueLike(object? a, object? b, ComparisonContext? context)
    {
        if (ReferenceEquals(a, b)) return true;

        if (a is null || b is null) return false;

        var ta = a.GetType();
        var tb = b.GetType();
        if (ta != tb) return false;

        switch (a)
        {
            case string sa:
                return AreEqualStrings(sa, (string)b, context);
            case float fa:
                return AreEqualSingle(fa, (float)b, context);
            case double da:
                return AreEqualDouble(da, (double)b, context);
            case decimal ma:
                return AreEqualDecimal(ma, (decimal)b, context);
            default:
                return a.Equals(b);
        }
    }

    public sealed class StrictDateTimeComparer : IEqualityComparer<DateTime>
    {
        public static readonly StrictDateTimeComparer Instance = new();

        public bool Equals(DateTime x, DateTime y)
        {
            return x.Kind == y.Kind && x.Ticks == y.Ticks;
        }

        public int GetHashCode(DateTime obj)
        {
            return HashCode.Combine((int)obj.Kind, obj.Ticks);
        }
    }

    public sealed class StrictDateTimeOffsetComparer : IEqualityComparer<DateTimeOffset>
    {
        public static readonly StrictDateTimeOffsetComparer Instance = new();

        public bool Equals(DateTimeOffset x, DateTimeOffset y)
        {
            return x.Offset == y.Offset && x.UtcTicks == y.UtcTicks;
        }

        public int GetHashCode(DateTimeOffset obj)
        {
            return HashCode.Combine(obj.Offset, obj.UtcTicks);
        }
    }

    // --- Helpers for polymorphic container comparison (cycle-aware via caller) ---
}
