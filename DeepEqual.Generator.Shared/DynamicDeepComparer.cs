using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DeepEqual.Generator.Shared;

public static class DynamicDeepComparer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool AreEqualDynamic(object? left, object? right, ComparisonContext context)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        // Fast value-like cases first (no cycles possible here)
        if (left is string sa && right is string sb) return ComparisonHelpers.AreEqualStrings(sa, sb, context);
        if (left is double da && right is double db) return ComparisonHelpers.AreEqualDouble(da, db, context);
        if (left is float fa && right is float fb) return ComparisonHelpers.AreEqualSingle(fa, fb, context);
        if (left is decimal m1 && right is decimal m2) return ComparisonHelpers.AreEqualDecimal(m1, m2, context);

        if (left is DateTime timeA && right is DateTime timeB)
            return ComparisonHelpers.AreEqualDateTime(timeA, timeB);
        if (left is DateTimeOffset offsetA && right is DateTimeOffset offsetB)
            return ComparisonHelpers.AreEqualDateTimeOffset(offsetA, offsetB);

        // Cross-type numeric comparison (e.g., int vs long, int vs double)
        if (IsNumeric(left) && IsNumeric(right)) return NumericEqual(left, right, context);

        var typeLeft = left.GetType();
        var typeRight = right.GetType();
        if (!ReferenceEquals(typeLeft, typeRight)) return false;

        // Prefer generated same-type comparers for user types (including those that implement IEnumerable).
        // IMPORTANT: Do NOT Enter/Exit here to avoid interfering with the generated helper's own cycle tracking.
        if (GeneratedHelperRegistry.TryCompareSameType(typeLeft, left, right, context, out var eqFromRegistry))
            return eqFromRegistry;

        // Known container shapes — cycles can happen here; guard with Enter/Exit.
        if (left is IDictionary<string, object?> sdictA && right is IDictionary<string, object?> sdictB)
        {
            if (!context.Enter(left, right)) return true;
            try { return EqualStringObjectDictionary(sdictA, sdictB, context); }
            finally { context.Exit(left, right); }
        }

        if (left is IDictionary dictA && right is IDictionary dictB)
        {
            if (!context.Enter(left, right)) return true;
            try { return EqualNonGenericDictionary(dictA, dictB, context); }
            finally { context.Exit(left, right); }
        }

        if (left is Array arrA && right is Array arrB)
        {
            if (arrA.Rank != arrB.Rank) return false;
            if (!context.Enter(left, right)) return true;
            try
            {
                return ComparisonHelpers.AreEqualArray<object?, DeepPolymorphicElementComparer<object?>>(arrA, arrB,
                    new DeepPolymorphicElementComparer<object?>(), context);
            }
            finally { context.Exit(left, right); }
        }

        if (left is IList listA && right is IList listB)
        {
            if (!context.Enter(left, right)) return true;
            try { return EqualNonGenericList(listA, listB, context); }
            finally { context.Exit(left, right); }
        }

        if (left is IEnumerable seqA && right is IEnumerable seqB)
        {
            if (!context.Enter(left, right)) return true;
            try { return EqualNonGenericSequence(seqA, seqB, context); }
            finally { context.Exit(left, right); }
        }

        return left.Equals(right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EqualNonGenericList(IList a, IList b, ComparisonContext context)
    {
        var n = a.Count;
        if (n != b.Count) return false;
        for (var i = 0; i < n; i++)
            if (!ComparisonHelpers.CompareAtIndex(a[i], b[i], i, new DeepPolymorphicElementComparer<object?>(), context))
                return false;
        return true;
    }

    private static bool EqualStringObjectDictionary(
        IDictionary<string, object?> a, IDictionary<string, object?> b, ComparisonContext context) =>
        ComparisonHelpers.AreEqualDictionariesAny<string, object?, DeepPolymorphicElementComparer<object?>>(
            a, b, default, context);

    private static bool EqualNonGenericDictionary(IDictionary a, IDictionary b, ComparisonContext context) =>
        a.Count == b.Count &&
        ComparisonHelpers.AreEqualDictionariesAny<object, object?, DeepPolymorphicElementComparer<object?>>(
            EnumerateEntries(a), EnumerateEntries(b), default, context);

    private static IEnumerable<KeyValuePair<object, object?>> EnumerateEntries(IDictionary map)
    {
        foreach (DictionaryEntry entry in map)
            yield return new(entry.Key, entry.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EqualNonGenericSequence(IEnumerable a, IEnumerable b, ComparisonContext context)
    {
        var ea = a.GetEnumerator();
        IEnumerator? eb = null;
        try
        {
            eb = b.GetEnumerator();
            for (var index = 0; ; index++)
            {
                var ma = ea.MoveNext();
                var mb = eb.MoveNext();
                if (ma != mb) return false;
                if (!ma) return true;
                if (!ComparisonHelpers.CompareAtIndex(ea.Current, eb.Current, index,
                        new DeepPolymorphicElementComparer<object?>(), context)) return false;
            }
        }
        finally
        {
            (ea as IDisposable)?.Dispose();
            (eb as IDisposable)?.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNumeric(object o)
    {
        return o is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NumericEqual(object a, object b, ComparisonContext context)
    {
        var aIsFloating = a is float or double;
        var bIsFloating = b is float or double;
        if (!aIsFloating && !bIsFloating)
            return a is decimal || b is decimal
                ? ComparisonHelpers.AreEqualDecimal(Convert.ToDecimal(a), Convert.ToDecimal(b), context)
                : Convert.ToDecimal(a) == Convert.ToDecimal(b);

        if ((aIsFloating && bIsFloating) || context.Options.DoubleEpsilon > 0)
            return ComparisonHelpers.AreEqualDouble(Convert.ToDouble(a), Convert.ToDouble(b), context);

        return EqualBinaryAndDecimal(Convert.ToDouble(aIsFloating ? a : b), Convert.ToDecimal(aIsFloating ? b : a));
    }

    private static bool EqualBinaryAndDecimal(double floating, decimal exact)
    {
        if (!double.IsFinite(floating)) return false;
        if (floating == 0 || exact == 0) return floating == 0 && exact == 0;
        if ((floating < 0) != (exact < 0)) return false;

        // Compare the two rational values without rounding either operand to the other's precision.
        var bits = BitConverter.DoubleToInt64Bits(floating);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var significand = (BigInteger)(bits & 0x000f_ffff_ffff_ffffL);
        if (exponent != 0) significand += BigInteger.One << 52;
        var powerOfTwo = Math.Max(exponent, 1) - 1075;

        Span<int> parts = stackalloc int[4];
        decimal.GetBits(exact, parts);
        var unscaled = ((BigInteger)(uint)parts[2] << 64) | ((BigInteger)(uint)parts[1] << 32) | (uint)parts[0];
        var scale = (parts[3] >> 16) & 0xff;
        var scaledBinary = significand * BigInteger.Pow(10, scale);
        return powerOfTwo >= 0
            ? (scaledBinary << powerOfTwo) == unscaled
            : scaledBinary == (unscaled << -powerOfTwo);
    }
}
