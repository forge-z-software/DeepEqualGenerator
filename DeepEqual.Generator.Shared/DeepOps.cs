#nullable enable
using System;
using DeepEqual.Generator.Shared;

namespace DeepEqual
{
    /// <summary>
    /// Runtime object facade that dispatches by runtime type via the generated registry.
    /// </summary>
    public static class DeepOps
    {
        public static bool AreEqual(object? expected, object? actual, ComparisonOptions? options = null) =>
            DynamicDeepComparer.AreEqualDynamic(expected, actual, new ComparisonContext(options));

        public static DeepMismatch? GetMismatch(object? expected, object? actual, ComparisonOptions? options = null)
        {
            var context = ComparisonContext.ForMismatch(options);
            if (DynamicDeepComparer.AreEqualDynamic(expected, actual, context)) return null;
            context.RecordMismatch(expected, actual);
            return context.Mismatch;
        }

        private static (bool has, DeepEqual.Generator.Shared.IDiff diff)
            GetDiffCore(object? a, object? b, DeepEqual.Generator.Shared.ComparisonContext ctx)
        {
            if (ReferenceEquals(a, b)) return (false, DeepEqual.Generator.Shared.Diff.Empty);

            if (a is null && b is not null) return (true, DeepEqual.Generator.Shared.Diff<object>.Replacement(b));
            if (a is not null && b is null) return (true, DeepEqual.Generator.Shared.Diff<object>.Replacement(b));

            var ta = a!.GetType();
            var tb = b!.GetType();
            if (!ReferenceEquals(ta, tb))
                return (true, DeepEqual.Generator.Shared.Diff<object>.Replacement(b));

            if (DeepEqual.Generator.Shared.GeneratedHelperRegistry.TryGetDiffSameType(ta, a, b, ctx, out var id))
                return (!id.IsEmpty, id);

            var equal = AreEqual(a, b, ctx.Options);
            return equal
                ? (false, DeepEqual.Generator.Shared.Diff.Empty)
                : (true, DeepEqual.Generator.Shared.Diff<object>.Replacement(b));
        }

        public static (bool has, DeepEqual.Generator.Shared.IDiff diff)
            GetDiff(object? a, object? b, DeepEqual.Generator.Shared.ComparisonOptions? opts = null)
        {
            var ctx = opts is null ? new DeepEqual.Generator.Shared.ComparisonContext()
                : new DeepEqual.Generator.Shared.ComparisonContext(opts);
            return GetDiffCore(a, b, ctx);
        }

        public static (bool has, DeepEqual.Generator.Shared.IDiff diff)
            GetDiff(object? a, object? b, DeepEqual.Generator.Shared.ComparisonContext? ctx)
        {
            return GetDiffCore(a, b, ctx ?? new DeepEqual.Generator.Shared.ComparisonContext());
        }


        public static DeltaDocument ComputeDelta(object? a, object? b, ComparisonOptions? opts = null)
        {
            var ctx = opts is null ? new ComparisonContext() : new ComparisonContext(opts);
            var ta = a?.GetType();
            var doc = DeltaDocument.ForRoot(ta);
            var w = new DeltaWriter(doc);

            if (ReferenceEquals(a, b)) return doc;

            var tb = b?.GetType();
            if (ta is null || tb is null || !ReferenceEquals(ta, tb))
            {
                w.WriteReplaceObject(b);
                return doc;
            }

            if (!GeneratedHelperRegistry.TryComputeDeltaSameType(ta, a, b, ctx, ref w) &&
                !DynamicDeepComparer.AreEqualDynamic(a, b, ctx))
                w.WriteReplaceObject(b);
            return doc;
        }

        public static void ApplyDelta(ref object? target, DeltaDocument delta, ComparisonOptions? opts = null)
        {
            var reader = new DeltaReader(delta);
            if (target is not null) reader.RequireRoot(target.GetType());
            var operations = reader.AsSpan();
            if (operations.IsEmpty) return;
            if (operations.Length == 1 && operations[0].Kind == DeltaKind.ReplaceObject)
            {
                target = operations[0].Value;
                return;
            }

            if (target is null || !GeneratedHelperRegistry.TryApplyDeltaSameType(target.GetType(), ref target, ref reader))
                throw new InvalidOperationException("This delta requires a target with a registered patch provider.");
        }
    }
}
