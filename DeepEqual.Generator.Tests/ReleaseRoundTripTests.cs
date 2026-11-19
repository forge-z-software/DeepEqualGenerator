using System.Buffers;
using DeepEqual.Generator.Shared;

namespace DeepEqual;

public sealed class ReleaseRoundTripTests
{
    [Fact]
    public void ImmutableModelsProduceAReplacementDiffAndDelta()
    {
        var original = new ImmutableStock("A", 1);
        var desired = new ImmutableStock("A", 2);
        var (hasDiff, diff) = original.GetDeepDiff(desired);

        Assert.True(hasDiff);
        Assert.True(diff.IsReplacement);
        Assert.Same(desired, diff.NewValue);
        var change = Assert.Single(original.ComputeDeepDelta(desired).Operations);
        Assert.Equal(DeltaKind.ReplaceObject, change.Kind);
        Assert.True(original.ComputeDeepDelta(original with { }).IsEmpty);
        Assert.Same(desired, original.ApplyDeepDelta(original.ComputeDeepDelta(desired)));
        Assert.Equal(1, original.Quantity);
    }

    [Fact]
    public void InMemoryDeltaRejectsADifferentRootSchema()
    {
        var delta = new ImmutableStock("A", 1).ComputeDeepDelta(new ImmutableStock("A", 2));
        var other = new MutableStock();
        Assert.Throws<InvalidOperationException>(() => other.ApplyDeepDelta(delta));
    }

    [Fact]
    public void BinaryDeltaChecksTheFingerprintAndRejectsTrailingData()
    {
        var delta = new MutableStock { Quantity = 1 }.ComputeDeepDelta(new MutableStock { Quantity = 2 });
        var profile = new BinaryDeltaOptions { IncludeHeader = true, StableTypeFingerprint = 42 };
        var buffer = new ArrayBufferWriter<byte>();
        BinaryDeltaCodec.Write(delta, buffer, profile);

        var wrongProfile = new BinaryDeltaOptions { IncludeHeader = true, StableTypeFingerprint = 43 };
        Assert.Throws<InvalidOperationException>(() => BinaryDeltaCodec.Read(buffer.WrittenSpan, wrongProfile));
        Assert.Throws<InvalidOperationException>(() => BinaryDeltaCodec.Read([.. buffer.WrittenSpan, 0], profile));
        var decoded = BinaryDeltaCodec.Read(buffer.WrittenSpan, profile);
        Assert.Equal(2, new MutableStock { Quantity = 1 }.ApplyDeepDelta(decoded)?.Quantity);
    }

    [Fact]
    public void CodecRejectsCyclicCollectionsAndEnforcesNestingOnDecode()
    {
        var cycle = new List<object?>();
        cycle.Add(cycle);
        var invalid = new DeltaDocument();
        new DeltaWriter(invalid).WriteSetMember(0, cycle);
        var profile = new BinaryDeltaOptions { IncludeHeader = true };
        var buffer = new ArrayBufferWriter<byte>();
        Assert.Throws<InvalidOperationException>(() => BinaryDeltaCodec.Write(invalid, buffer, profile));
        Assert.Equal(0, buffer.WrittenCount);

        var valid = new DeltaDocument();
        new DeltaWriter(valid).WriteSetMember(0, new List<object?> { new[] { 1, 2 } });
        BinaryDeltaCodec.Write(valid, buffer, profile);
        profile.Safety.MaxNesting = 1;
        Assert.Throws<InvalidOperationException>(() => BinaryDeltaCodec.Read(buffer.WrittenSpan, profile));
    }

    [Fact]
    public void MutableCollectionsRoundTripDifferentValuesAndLengths()
    {
        var random = new Random(17017);
        for (var i = 0; i < 100; i++)
        {
            var original = Next();
            var desired = Next();
            var delta = original.ComputeDeepDelta(desired);
            var actual = original.ApplyDeepDelta(delta);
            Assert.Null(desired.GetDeepMismatch(actual));
        }

        MutableStock Next()
        {
            var stock = new MutableStock
            {
                Quantity = random.Next(0, 6),
                Batches = Enumerable.Range(0, random.Next(0, 8)).Select(_ => random.Next(0, 4)).ToList()
            };
            for (var key = 0; key < 5; key++)
                if (random.Next(0, 2) == 0)
                    stock.Locations[key.ToString()] = random.Next(0, 4);
            return stock;
        }
    }

    [Fact]
    public void FlatDictionaryDeltasKeepRemovalsBeforeWritesAndDiffsKeepTheirOperations()
    {
        var original = new MutableStock
        {
            Locations = new(StringComparer.OrdinalIgnoreCase) { ["Old"] = 1, ["Match"] = 2, ["Other"] = 3 }
        };
        var desired = new MutableStock
        {
            Locations = new(StringComparer.OrdinalIgnoreCase) { ["match"] = 5, ["New"] = 4 }
        };

        var (hasDiff, diff) = original.GetDeepDiff(desired);
        Assert.True(hasDiff);
        var dictionaryDiff = Assert.IsType<DeltaDocument>(Assert.Single(diff.MemberChanges!).ValueOrDiff);
        var (hasEqualDiff, _) = original.GetDeepDiff(new MutableStock
        {
            Locations = new(StringComparer.OrdinalIgnoreCase) { ["old"] = 1, ["match"] = 2, ["other"] = 3 }
        });
        Assert.False(hasEqualDiff);
        AssertOperations(dictionaryDiff);

        var delta = original.ComputeDeepDelta(desired);
        AssertOperations(delta);
        var applied = Assert.IsType<MutableStock>(original.ApplyDeepDelta(delta));
        Assert.Same(StringComparer.OrdinalIgnoreCase, applied.Locations.Comparer);
        Assert.Equal(2, applied.Locations.Count);
        Assert.Equal(5, applied.Locations["match"]);
        Assert.Equal(4, applied.Locations["New"]);

        static void AssertOperations(DeltaDocument document)
        {
            Assert.Collection(document.Operations,
                op => Assert.Equal((DeltaKind.DictRemove, "Old"), (op.Kind, Assert.IsType<string>(op.Key))),
                op => Assert.Equal((DeltaKind.DictRemove, "Other"), (op.Kind, Assert.IsType<string>(op.Key))),
                op => Assert.Equal((DeltaKind.DictSet, "match", 5), (op.Kind, Assert.IsType<string>(op.Key), Assert.IsType<int>(op.Value))),
                op => Assert.Equal((DeltaKind.DictSet, "New", 4), (op.Kind, Assert.IsType<string>(op.Key), Assert.IsType<int>(op.Value))));
        }
    }

    [Fact]
    public void ListAppendFastPathLeavesMixedOperationsForTheGeneralApplyPath()
    {
        var document = new DeltaDocument();
        var writer = new DeltaWriter(document);
        writer.WriteSeqAddAt(1, 1, 2);
        writer.WriteSeqReplaceAt(1, 0, 3);
        var original = new List<int> { 1 };
        object? target = original;

        Assert.False(DeltaHelpers.TryApplyListSeqAddFastLane<int>(ref target, new DeltaReader(document).AsSpan()));
        Assert.Same(original, target);
        Assert.Equal(new[] { 1 }, original);
    }

    [Fact]
    public void KeyedListDeltaPreservesAnUpdateWhenItsElementMoves()
    {
        var original = new PickList { Lines = [new("A", 1), new("B", 1), new("C", 1)] };
        var desired = new PickList { Lines = [new("C", 2), new("B", 1), new("D", 3)] };
        var delta = original.ComputeDeepDelta(desired);
        var actual = original.ApplyDeepDelta(delta);
        Assert.Null(desired.GetDeepMismatch(actual));
        Assert.Null(desired.GetDeepMismatch(actual.ApplyDeepDelta(delta)));
    }

    [DeepComparable(GenerateDelta = true)]
    public sealed class PickList
    {
        [DeepCompare(KeyMembers = [nameof(PickLine.Sku)])]
        public List<PickLine> Lines { get; set; } = [];
    }

    public sealed record PickLine(string Sku, int Quantity);

    [DeepComparable(GenerateDiff = true, GenerateDelta = true)]
    public sealed record ImmutableStock(string Sku, int Quantity);

    [DeepComparable(GenerateDiff = true, GenerateDelta = true)]
    public sealed class MutableStock
    {
        public int Quantity { get; set; }
        public List<int> Batches { get; set; } = [];
        public Dictionary<string, int> Locations { get; set; } = new();
    }
}
