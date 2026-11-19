using DeepEqual;
using DeepEqual.Generator.Shared;

[assembly: ExternalDeepComparable(typeof(DeepMismatchTests.ThirdPartyModel))]
[assembly: ExternalDeepCompare(typeof(DeepMismatchTests.ThirdPartyModel), nameof(DeepMismatchTests.ThirdPartyModel.Token),
    Kind = CompareKind.Skip)]

public sealed class DeepMismatchTests
{
    [Fact]
    public void OrderedGraphReportsChangedQuantityAfterAnEqualDictionary()
    {
        var expected = new Receipt { Customers = [new() { Orders = [new() { Lines = [new("SKU-9", 2)] }] }] };
        var actual = new Receipt { Customers = [new() { Orders = [new() { Lines = [new("SKU-9", 3)] }] }] };

        Assert.False(expected.AreDeepEqual(actual));
        Assert.Equal(new DeepMismatch("$.Customers[0].Orders[0].Lines[0].Qty", 2, 3),
            expected.GetDeepMismatch(actual));
        Assert.Equal(expected.GetDeepMismatch(actual), DeepOps.GetMismatch(expected, actual));
        Assert.Null(expected.GetDeepMismatch(expected));
    }

    [Fact]
    public void OrderedListFastPathHonorsOptionsAndReportsScalarValues()
    {
        var expected = new OrderedLines { Lines = [new("SKU-9", 2)] };
        var actual = new OrderedLines { Lines = [new("sku-9", 2)] };
        var options = new ComparisonOptions { StringComparison = StringComparison.OrdinalIgnoreCase };

        Assert.False(expected.AreDeepEqual(actual));
        Assert.True(expected.AreDeepEqual(actual, options));
        Assert.Null(expected.GetDeepMismatch(actual, options));

        actual.Lines[0] = new("sku-9", 3);
        Assert.False(expected.AreDeepEqual(actual, options));
        Assert.Equal(new DeepMismatch("$.Lines[0].Qty", 2, 3), expected.GetDeepMismatch(actual, options));

        actual.Lines[0] = null;
        Assert.False(expected.AreDeepEqual(actual, options));
        Assert.Equal(new DeepMismatch("$.Lines[0]", expected.Lines[0], null),
            expected.GetDeepMismatch(actual, options));
    }

    [Fact]
    public void UniqueKeysNameTheItemEvenWhenOrderChanges()
    {
        var expected = new KeyedReceipt { Lines = [new("SKU-8", 1), new("SKU-9", 2)] };
        var actual = new KeyedReceipt { Lines = [new("SKU-9", 3), new("SKU-8", 1)] };

        Assert.Equal(new DeepMismatch("$.Lines[\"SKU-9\"].Qty", 2, 3), expected.GetDeepMismatch(actual));
    }

    [Fact]
    public void DuplicateKeysUseEachCandidateOnlyOnce()
    {
        var expected = new KeyedReceipt { Lines = [new("SKU-9", 2), new("SKU-9", 2)] };
        var actual = new KeyedReceipt { Lines = [new("SKU-9", 2), new("SKU-9", 3)] };

        Assert.False(expected.AreDeepEqual(actual));
        Assert.Equal("$.Lines[\"SKU-9\"]", expected.GetDeepMismatch(actual)?.Path);
    }

    [Fact]
    public void DictionaryKeyRulesAreIndependentOfTheInputComparers()
    {
        var expected = new Receipt { Labels = new(StringComparer.Ordinal) { ["alpha"] = "on" } };
        var actual = new Receipt { Labels = new(StringComparer.OrdinalIgnoreCase) { ["ALPHA"] = "on" } };

        Assert.False(expected.AreDeepEqual(actual));
        Assert.False(actual.AreDeepEqual(expected));
        Assert.Equal(DeepMismatchKind.MissingKey, expected.GetDeepMismatch(actual)?.Kind);
        actual.Labels = new(StringComparer.OrdinalIgnoreCase) { ["alpha"] = "off" };
        Assert.Equal(new DeepMismatch("$.Labels[\"alpha\"]", "on", "off"), expected.GetDeepMismatch(actual));
        actual.Labels.Clear();
        Assert.Equal(new DeepMismatch("$.Labels", 1, 0, DeepMismatchKind.Count), expected.GetDeepMismatch(actual));
    }

    [Fact]
    public void DefaultIncludesInheritedMembersAndTracksDynamicCycles()
    {
        var leftArray = new object[1];
        leftArray[0] = leftArray;
        var rightArray = new object[1];
        rightArray[0] = rightArray;
        var expected = new DerivedModel { Value = leftArray, Inherited = 1 };
        var actual = new DerivedModel { Value = rightArray, Inherited = 1 };

        Assert.True(expected.AreDeepEqual(actual));
        actual.Inherited = 2;
        Assert.Equal(new DeepMismatch("$.Inherited", 1, 2), expected.GetDeepMismatch(actual));
    }

    [Fact]
    public void NestedArraysCompareByContentAndReportBothIndices()
    {
        var expected = new Envelope<int[]> { Entries = [new([1, 2])] };
        var actual = new Envelope<int[]> { Entries = [new([1, 2])] };
        Assert.Null(expected.GetDeepMismatch(actual));
        actual.Entries[0].Value[1] = 3;
        Assert.Equal(new DeepMismatch("$.Entries[0].Value[1]", 2, 3), expected.GetDeepMismatch(actual));
    }

    [Fact]
    public void BoxedRectangularArraysSupportNonzeroBounds()
    {
        var expected = Array.CreateInstance(typeof(int), [2, 2], [1, -1]);
        var actual = Array.CreateInstance(typeof(int), [2, 2], [1, -1]);
        Assert.Null(DeepOps.GetMismatch(expected, actual));
        actual.SetValue(5, 2, 0);
        Assert.Equal(new DeepMismatch("$[2,0]", 0, 5), DeepOps.GetMismatch(expected, actual));
    }

    [Fact]
    public void MultipleRootsCanCompareTheSameChildWithDifferentRules()
    {
        var expected = new ModelWithBase { Child = new() { Inherited = 1 } };
        var actual = new ModelWithBase { Child = new() { Inherited = 2 } };
        Assert.Equal("$.Child.Inherited", expected.GetDeepMismatch(actual)?.Path);
        Assert.True(new ModelWithoutBase { Child = expected.Child }
            .AreDeepEqual(new ModelWithoutBase { Child = actual.Child }));
    }

    [Fact]
    public void TolerantUnorderedMatchingFindsAFullPairingInAnyOrder()
    {
        var expected = new TolerantBag { Values = [1, 0] };
        var actual = new TolerantBag { Values = [0, 2] };
        var options = new ComparisonOptions { DoubleEpsilon = 1 };

        Assert.Null(expected.GetDeepMismatch(actual, options));
        Assert.True(actual.AreDeepEqual(expected, options));
        Array.Reverse(actual.Values);
        Assert.True(expected.AreDeepEqual(actual, options));
        actual.Values[0] = 3;
        Assert.Equal("$.Values", expected.GetDeepMismatch(actual, options)?.Path);
    }

    [Fact]
    public void UnorderedNullableStringsCountDuplicatesAndNulls()
    {
        var left = new TolerantBag { Names = [null, "A", null] };
        var right = new TolerantBag { Names = ["a", null, null] };
        var options = new ComparisonOptions { StringComparison = StringComparison.OrdinalIgnoreCase };
        Assert.True(left.AreDeepEqual(right, options));
        right.Names[1] = "a";
        Assert.False(left.AreDeepEqual(right, options));
    }

    [Fact]
    public void BoxedTimesAndNumericBoundariesUseTheSameRulesAsTypedValues()
    {
        var left = new DerivedModel { Value = new DateTime(100, DateTimeKind.Utc) };
        var right = new DerivedModel { Value = new DateTime(100, DateTimeKind.Unspecified) };
        Assert.Equal("$.Value", left.GetDeepMismatch(right)?.Path);

        var options = new ComparisonOptions { DoubleEpsilon = 1, DecimalEpsilon = 1 };
        Assert.True(DeepOps.AreEqual(double.PositiveInfinity, double.PositiveInfinity, options));
        Assert.False(DeepOps.AreEqual(decimal.MaxValue, decimal.MinValue, options));
        Assert.False(DeepOps.AreEqual(9_007_199_254_740_993L, 9_007_199_254_740_992d));
        Assert.True(DeepOps.AreEqual(9_007_199_254_740_992L, 9_007_199_254_740_992d));
        Assert.True(DeepOps.AreEqual(.5m, .5d));
    }

    [Fact]
    public void ApplyingReplacementDeltasSupportsNullAndUnregisteredRoots()
    {
        object? actual = null;
        var expected = new Receipt { Labels = new() { ["x"] = "y" } };
        DeepOps.ApplyDelta(ref actual, DeepOps.ComputeDelta(actual, expected));
        Assert.Same(expected, actual);

        object? scalar = 1;
        DeepOps.ApplyDelta(ref scalar, DeepOps.ComputeDelta(scalar, 2));
        Assert.Equal(2, scalar);
        DeepOps.ApplyDelta(ref actual, DeepOps.ComputeDelta(actual, null));
        Assert.Null(actual);
    }

    [Fact]
    public void ClosedGenericModelsReportTheElementPath()
    {
        var expected = new Envelope<int> { Entries = [new(1)] };
        var actual = new Envelope<int> { Entries = [new(2)] };
        Assert.Equal(new DeepMismatch("$.Entries[0].Value", 1, 2), expected.GetDeepMismatch(actual));
    }

    [Fact]
    public void ExternalRulesApplyToTheAdoptedModel()
    {
        var expected = new ThirdPartyModel("document", "expected-token");
        var actual = new ThirdPartyModel("document", "actual-token");
        Assert.Null(expected.GetDeepMismatch(actual));
        Assert.Equal("$.Title", expected.GetDeepMismatch(actual with { Title = "different" })?.Path);
    }

    [DeepComparable]
    public sealed class Receipt
    {
        public Dictionary<string, string> Labels { get; set; } = new() { ["status"] = "ok" };
        public List<Customer> Customers { get; init; } = [];
    }

    public sealed record Customer
    {
        public List<Order> Orders { get; init; } = [];
    }

    public sealed record Order
    {
        public List<Line> Lines { get; init; } = [];
    }

    public sealed record Line(string Sku, int Qty);

    [DeepComparable]
    public sealed class KeyedReceipt
    {
        [DeepCompare(OrderInsensitive = true, KeyMembers = [nameof(Line.Sku)])]
        public List<Line> Lines { get; init; } = [];
    }

    [DeepComparable]
    public sealed class OrderedLines
    {
        public List<Line?> Lines { get; init; } = [];
    }

    public class BaseModel
    {
        public int Inherited { get; set; }
    }

    [DeepComparable]
    public sealed class DerivedModel : BaseModel
    {
        public object? Value { get; init; }
    }

    [DeepComparable]
    public sealed class ModelWithBase
    {
        public DerivedModel Child { get; init; } = new();
    }

    [DeepComparable(IncludeBaseMembers = false)]
    public sealed class ModelWithoutBase
    {
        public DerivedModel Child { get; init; } = new();
    }

    [DeepComparable(OrderInsensitiveCollections = true)]
    public sealed class TolerantBag
    {
        public double[] Values { get; init; } = [];
        public List<string?> Names { get; init; } = [];
    }

    [DeepComparable]
    public sealed record Envelope<T>
    {
        public List<Entry<T>> Entries { get; init; } = [];
    }

    public sealed record Entry<T>(T Value);

    public sealed record ThirdPartyModel(string Title, string Token);
}
